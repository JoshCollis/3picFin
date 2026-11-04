const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('src/Rowan.Jellyfin.Plugin/Web/native-guide-controls.js', 'utf8');
const create = vm.runInNewContext(source + '; finCreateGuideController', { Promise, Map, Number, Math, JSON });
const deferred = () => { let resolve, reject; const promise = new Promise((a,b) => { resolve=a; reject=b; }); return {promise,resolve,reject}; };
const flush = async () => { for(let i=0;i<6;i++) await new Promise(setImmediate); };

function fixture() {
    let user='alice', errors=0; const calls=[], jumps=[];
    const grid={inert:false}, channels={inert:false};
    const root={isConnected:true,querySelector:s=>s==='.programGrid'?grid:s==='.channelsContainer'?channels:null,
        setAttribute(){},removeAttribute(){}};
    const api={getCurrentUserId:()=>user, load:query=>{ const result=deferred(); calls.push({query,...result}); return result.promise; }};
    const scope=create({element:root,getApi:()=>api,jump:offset=>jumps.push(offset),limit:25,onError(){errors++;}});
    return {scope,calls,jumps,grid,api,errorCount:()=>errors,setUser:id=>user=id};
}

test('one in-flight read, latest pending intent, stale replies never reach native render',async()=>{
    const f=fixture(), rendered=[];
    const first=f.scope.begin(); f.scope.read(f.api,'load',{page:1},first).then(x=>rendered.push(x));
    for(let page=2;page<=30;page++) {
        const ticket=f.scope.begin(); f.scope.read(f.api,'load',{page},ticket).then(x=>rendered.push(x));
    }
    assert.equal(f.calls.length,1); assert.equal(f.grid.inert,true);
    f.calls[0].resolve('stale'); await flush();
    assert.equal(f.calls.length,2); assert.equal(f.calls[1].query.page,30); assert.deepEqual(rendered,[]);
    f.calls[1].resolve('latest'); await flush(); assert.deepEqual(rendered,['latest']);
});

test('duplicate scoped reads share a native API call; finish unlocks only current data',async()=>{
    const f=fixture(), ticket=f.scope.begin();
    f.scope.read(f.api,'load',{page:1},ticket).then(()=>{});
    f.scope.read(f.api,'load',{page:1},ticket).then(()=>{});
    assert.equal(f.calls.length,1);
    const newer=f.scope.begin(); f.scope.finish(ticket); assert.equal(f.grid.inert,true);
    f.calls[0].resolve({}); await flush(); f.scope.finish(newer); assert.equal(f.grid.inert,false);
});

test('pause/account switch drops pending work and old user results without claiming transport abort',async()=>{
    const f=fixture(); let rendered=0;
    const ticket=f.scope.begin(); f.scope.read(f.api,'load',{},ticket).then(()=>rendered++);
    f.scope.pause(); f.setUser('bob'); assert.equal(f.scope.needsRefresh(),true);
    const next=f.scope.begin(); f.scope.read(f.api,'load',{page:2},next).then(()=>rendered++);
    assert.equal(f.calls.length,1); f.calls[0].resolve({}); await flush();
    assert.equal(rendered,0); assert.equal(f.calls.length,2);
    f.calls[1].resolve({}); await flush(); assert.equal(rendered,1);
});

test('page jump is bounded by current authenticated result, supports direct tail, rejects invalid values',()=>{
    const f=fixture(); f.scope.update(1879,0,25);
    assert.equal(f.scope.go('76'),true); assert.deepEqual(f.jumps,[1875]);
    for(const value of ['0','77','-1','1.5','NaN','1e2','', '999999999999999999999']) assert.equal(f.scope.go(value),false,value);
    f.scope.update(17,0,17); assert.equal(f.scope.go('76'),false); assert.equal(f.scope.go('1'),false);
});

test('nested GuideInfo/channel begin retains inert-blurred focus, but pause clears it',()=>{
    let restored=0;
    const body={}, cell={isConnected:true,closest:()=>true,focus:()=>restored++};
    const document={activeElement:cell,body};
    const createFocused=vm.runInNewContext(source+'; finCreateGuideController',{Promise,Number,Math,JSON,document});
    const api={getCurrentUserId:()=> 'alice'};
    const root={contains:el=>el===cell,querySelector:()=>null,setAttribute(){}};
    const scope=createFocused({element:root,getApi:()=>api,limit:25});
    scope.begin(); document.activeElement=body;
    scope.finish(scope.begin()); assert.equal(restored,1);
    document.activeElement=cell; scope.begin(); document.activeElement=body;
    scope.pause(); scope.finish(scope.begin()); assert.equal(restored,1);
});

test('stale errors do not unlock/report old actions; a current error fails inert',async()=>{
    const f=fixture(); let rendered=0;
    f.scope.read(f.api,'load',{page:1},f.scope.begin()).then(()=>rendered++);
    f.scope.read(f.api,'load',{page:2},f.scope.begin()).then(()=>rendered++);
    f.calls[0].reject(new Error('stale')); await flush();
    assert.equal(f.errorCount(),0); assert.equal(f.calls.length,2); assert.equal(f.grid.inert,true);
    f.calls[1].reject(new Error('current')); await flush();
    assert.equal(f.errorCount(),1); assert.equal(f.grid.inert,true); assert.equal(rendered,0);
});

test('search and Clear invalidate old replies immediately and reset the native offset',async()=>{
    const f=fixture();let rendered=0;
    const old=f.scope.begin();f.scope.read(f.api,'load',{},old).then(()=>rendered++);
    f.scope.update(1879,1875,4);f.scope.setSearch(' France2 ',true);
    assert.deepEqual(f.jumps,[0]);assert.equal(f.grid.inert,true);
    assert.equal(f.scope.begin().search,'France2');
    f.calls[0].resolve({});await flush();assert.equal(rendered,0);
    f.scope.setSearch('',true);assert.equal(f.scope.begin().search,'');assert.deepEqual(f.jumps,[0,0]);
});

test('a catalogue shrinking under a late page corrects the native offset instead of showing an invalid range',()=>{
    const f=fixture();f.scope.update(1879,1875,4);
    f.scope.update(1819,1875,0);assert.deepEqual(f.jumps,[1800]);
    f.scope.update(0,1800,0);assert.deepEqual(f.jumps,[1800,0]);
});

test('assigned category changes invalidate pending work and reset page without clearing channel search',()=>{
    const f=fixture(); f.scope.setSearch('France2',true);
    const old=f.scope.begin(); f.scope.setCategory('group-123');
    assert.equal(f.scope.valid(old),false);
    assert.equal(f.scope.begin().category,'group-123');
    assert.equal(f.scope.begin().search,'France2');
    assert.deepEqual(f.jumps,[0,0]);
    f.scope.setCategory(''); assert.equal(f.scope.begin().category,'');
});

test('missing selected category fails closed, but All can still use the native catalogue',async()=>{
    let reads=0, errors=0, rendered=0;
    const api={getCurrentUserId:()=> 'alice', getUrl:path=>path,
        getJSON:async()=>({Available:false,Source:'jellyfin-m3u-group-title-cache',Items:[]}),
        getLiveTvChannels:async()=>{reads++;return {Items:[],TotalRecordCount:0};}};
    const root={isConnected:true,querySelector:()=>null,setAttribute(){}};
    const scope=create({element:root,getApi:()=>api,jump(){},limit:50,onError(){errors++;}});
    scope.setCategory('group-'+'a'.repeat(64));
    scope.read(api,'getLiveTvChannels',{StartIndex:0,Limit:50},scope.begin()).then(()=>rendered++);
    await flush();assert.equal(errors,1);assert.equal(reads,0);assert.equal(rendered,0);
    scope.setCategory('');scope.read(api,'getLiveTvChannels',{StartIndex:0,Limit:50},scope.begin()).then(()=>rendered++);
    await flush();assert.equal(reads,1);assert.equal(rendered,1);
});

test('category source responses cannot commit after account switch; new identity defaults to All',async()=>{
    let user='alice',reads=0;const response=deferred();
    const api={getCurrentUserId:()=>user,getUrl:path=>path,getJSON:()=>response.promise,
        getLiveTvChannels:async()=>{reads++;return {Items:[],TotalRecordCount:0};}};
    const root={isConnected:true,querySelector:()=>null,setAttribute(){}};
    const scope=create({element:root,getApi:()=>api,jump(){},limit:50,onError(){assert.fail('stale error');}});
    scope.setCategory('group-'+'a'.repeat(64));let rendered=0;
    scope.read(api,'getLiveTvChannels',{StartIndex:0,Limit:50},scope.begin()).then(()=>rendered++);
    user='bob';assert.equal(scope.begin().category,'');
    response.resolve({Available:true,Source:'jellyfin-m3u-group-title-cache',Items:[]});await flush();
    assert.equal(reads,0);assert.equal(rendered,0);
});
