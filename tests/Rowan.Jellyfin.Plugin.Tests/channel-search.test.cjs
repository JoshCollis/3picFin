const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('src/Rowan.Jellyfin.Plugin/Web/channel-search.js', 'utf8');
const channels = vm.runInNewContext(source + '; ThreePicFinChannelSearch', { Promise, Date, Number, String, Math, JSON, Set, Error });
const lineup = Array.from({length:1879}, (_,i)=>({Id:(i+1).toString(16).padStart(32,'0'),Name:`Station ${i+1}`,
    ChannelNumber:String(i+1),Type:'TvChannel',ServerId:'server',ImageTags:{Primary:'logo'},MediaSources:[{Path:'must not cache'}]}));
lineup[1851].Name='France 2'; lineup[1852].Name='France 24'; lineup[1853].Name='beIN SPORTS 1 FR';
lineup[1854].Name='DR 1'; lineup[1855].Name='Télé Sport'; lineup[1856].Name='Sky Sports'; lineup[1857].Name='Sky Sports';
function fixture() {
    let user='alice',time=0; const calls=[];
    const api={getCurrentUserId:()=>user,getLiveTvChannels:async q=>{
        calls.push(q); const items=q.IsSports?lineup.filter((_,i)=>i%2===0):lineup;
        // SearchTerm is deliberately ignored, like the actual 12.3 endpoint.
        return {Items:items.slice(q.StartIndex,q.StartIndex+q.Limit),TotalRecordCount:items.length};
    }};
    const index=channels.createCatalogue({api,userId:user,isCurrent:()=>api.getCurrentUserId()===user,now:()=>time});
    return {index,api,calls,setUser:id=>user=id,advance:ms=>time+=ms};
}
test('full native metadata pages find off-page names/numbers without trusting ignored SearchTerm',async()=>{
    const f=fixture(); const result=await f.index.page({},'France2',0,25);
    assert.equal(result.TotalRecordCount,2);assert.equal(result.Items[0].Name,'France 2');
    assert.deepEqual(f.calls.map(q=>q.StartIndex),[0,500,1000,1500]);
    assert(f.calls.every(q=>q.Limit===500 && q.UserId==='alice' && q.AddCurrentProgram===false && !q.SearchTerm));
    assert.equal((await f.index.page({},'1852',0,25)).Items[0].Name,'France 2');
    assert.equal(f.calls.length,4); assert(!JSON.stringify(result).includes('must not cache'));
});
test('case/spacing/diacritics, duplicate names with separate identities, no-match and result paging',async()=>{
    const f=fixture();
    for(const [term,count] of [[' dr1 ',1],['BEIN sports1',1],['tele sport',1],['sKy   Sports',2],['not-a-station',0]]) {
        const result=await f.index.page({},term,0,25);assert.equal(result.TotalRecordCount,count,term);
    }
    const result=await f.index.page({},'SkySports',1,1);assert.equal(result.Items.length,1);assert.equal(result.Items[0].Id,lineup[1857].Id);
    assert.equal(f.calls.length,4);
});
test('filter scope and TTL invalidate the single bounded cache; no query-specific network storm',async()=>{
    const f=fixture();await f.index.page({},'f',0,25);
    for(const term of ['fr','fra','fran','france'])await f.index.page({},term,0,25);
    assert.equal(f.calls.length,4);
    await f.index.page({IsSports:true},'sky',0,25);assert.equal(f.calls.length,6);
    f.advance(61000);await f.index.page({IsSports:true},'sky',0,25);assert.equal(f.calls.length,8);
});
test('anonymous/account/route invalidation and denied native API never return cached cross-user data',async()=>{
    const f=fixture();await f.index.page({},'France',0,25);f.setUser('bob');
    await assert.rejects(f.index.page({},'France',0,25));
    f.setUser('alice');f.index.dispose();await assert.rejects(f.index.page({},'France',0,25));
    const api={getCurrentUserId:()=>null,getLiveTvChannels:()=>assert.fail('anonymous request')};
    await assert.rejects(channels.createCatalogue({api,userId:null,isCurrent:()=>true}).page({},'BBC',0,25));
    const denied={getCurrentUserId:()=> 'denied',getLiveTvChannels:async()=>{throw Error('403');}};
    await assert.rejects(channels.createCatalogue({api:denied,userId:'denied',isCurrent:()=>true}).page({},'BBC',0,25));
});
test('incomplete/changing/oversized snapshots fail honestly, not as a complete empty result',async()=>{
    for(const make of [q=>({Items:[],TotalRecordCount:1879}),q=>({Items:lineup.slice(q.StartIndex,q.StartIndex+500),TotalRecordCount:q.StartIndex?1800:1879}),q=>({Items:lineup.slice(0,500),TotalRecordCount:10001})]) {
        const api={getCurrentUserId:()=> 'alice',getLiveTvChannels:async q=>make(q)};
        await assert.rejects(channels.createCatalogue({api,userId:'alice',isCurrent:()=>true}).page({},'BBC',0,25));
    }
});

test('route/account invalidation during a slow metadata page drops its reply and stops later pages',async()=>{
    let release, reads=0, user='alice';
    const api={getCurrentUserId:()=>user,getLiveTvChannels:()=>{reads++;return new Promise(resolve=>release=resolve);}};
    const index=channels.createCatalogue({api,userId:'alice',isCurrent:()=>true});
    const job=index.page({},'France',0,25);user='bob';
    release({Items:lineup.slice(0,500),TotalRecordCount:1879});
    await assert.rejects(job);assert.equal(reads,1);
});

test('guide category intersects full-catalogue name/number search before paging; All leaves main Search unchanged',async()=>{
    const f=fixture(), allowed=new Set([lineup[1851].Id]);
    const filtered=await f.index.page({},'France2',0,50,allowed);
    assert.equal(filtered.TotalRecordCount,1); assert.equal(filtered.Items[0].Id,lineup[1851].Id);
    assert.equal((await f.index.page({},'',0,50,allowed)).TotalRecordCount,1);
    assert.equal((await f.index.page({},'1853',0,50,allowed)).TotalRecordCount,0);
    assert.equal((await f.index.page({},'France2',0,50)).TotalRecordCount,2);
    assert.equal(f.calls.length,4);
});
