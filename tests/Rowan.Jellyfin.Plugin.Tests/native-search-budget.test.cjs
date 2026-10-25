const {test} = require('node:test');
const assert = require('node:assert/strict');
const {boundedSearchUrl, installSearchBudget, cachedSearchCompatibility} = require('../../src/Rowan.Jellyfin.Plugin/Web/search-adapter.js');

function fixture(prefix = '/jellyfin') {
    const calls = [], opened = [];
    class XHR { open(...args) { opened.push({receiver:this, args}); return 'opened'; } }
    const world = {location:{href:`https://example.test${prefix}/web/index.html#/search?query=big%20buck%20bunny`},
        Request, XMLHttpRequest:XHR, fetch:(...args)=>{calls.push(args); return Promise.resolve('response');},
        ApiClient:{getCurrentUserId:()=> 'alice', getUrl:path=>`https://example.test${prefix}/${path}`}};
    const url = (path='Items', params={}) => `${world.ApiClient.getUrl(path)}?${new URLSearchParams({
        userId:'alice',searchTerm:'big buck bunny',limit:'800',recursive:'true',enableTotalRecordCount:'false',...params})}`;
    return {world, calls, opened, url};
}

test('connection metadata verifies supported versions synchronously and distinguishes unknown from unsupported',()=>{
    for (const version of ['12.1.0','12.3.0','12.3.1.0']) {
        assert.equal(cachedSearchCompatibility({serverVersion:()=>version}),true);
    }
    for (const version of ['12.2.0','13.0.0','12.3-preview']) {
        assert.equal(cachedSearchCompatibility({serverVersion:()=>version}),false);
    }
    for (const api of [undefined,{}, {serverVersion:()=>undefined},{serverVersion:()=>''},
        {serverVersion:()=>{throw Error('unavailable');}}]) {
        assert.equal(cachedSearchCompatibility(api),null);
    }
});

for (const prefix of ['', '/jellyfin']) {
    for (const endpoint of ['Items','Persons','Studios','Artists','Artists/AlbumArtists']) {
        test(`caps only the requested count for ${prefix}/${endpoint}`,()=>{
            const f=fixture(prefix), original=f.url(endpoint);
            const result=boundedSearchUrl(f.world,original,'GET',f.world.ApiClient);
            const expected=new URL(original); expected.searchParams.set('limit','50');
            assert.equal(result.url,expected.href); assert.equal(result.original,original);
            assert.equal(result.api,f.world.ApiClient);
        });
    }
}
test('accepts native parameter casing, URL objects, relative URLs and matching case-insensitive user ID',()=>{
    const f=fixture('');
    const url='/Items?UserId=ALICE&SearchTerm=big+buck+bunny&Limit=100';
    assert.equal(new URL(boundedSearchUrl(f.world,url,'get',f.world.ApiClient).url).searchParams.get('Limit'),'50');
    assert.equal(new URL(boundedSearchUrl(f.world,new URL(f.url()),'GET',f.world.ApiClient).url).searchParams.get('limit'),'50');
});
test('does not change other queries, users, endpoints, origins, page sizes or pagination',()=>{
    const f=fixture();
    const urls=[f.url('Items',{userId:'bob'}),f.url('Items',{userId:''}),f.url('Items',{searchTerm:'different'}),
        f.url('Items',{parentId:'library'}),f.url('Items',{collectionType:''}),f.url('Items',{startIndex:'50'}),
        ...['20','50','-1','1000','NaN',''].map(limit=>f.url('Items',{limit})),
        f.url('Items')+'&Limit=800',f.url('Items')+'&userId=alice',f.url('Items')+'&SearchTerm=big+buck+bunny',
        f.url('Items/Resume'),f.url('Items/item/Images/Primary'),f.url('Videos/item/stream'),f.url('3picFin/Search'),
        f.url().replace('example.test','other.test'),f.url().replace('/jellyfin/','/other/'),
        'https://name:password@example.test/jellyfin/Items?userId=alice&searchTerm=big+buck+bunny&limit=800'];
    for (const url of urls) assert.equal(boundedSearchUrl(f.world,url,'GET',f.world.ApiClient),null,url);
    for (const method of ['POST','PUT','DELETE','HEAD']) assert.equal(boundedSearchUrl(f.world,f.url(),method,f.world.ApiClient),null);
    f.world.ApiClient.getCurrentUserId=()=>{throw Error('identity unavailable');};
    assert.equal(boundedSearchUrl(f.world,f.url(),'GET',f.world.ApiClient),null);
});
test('only unscoped active global search can apply the cap',()=>{
    const f=fixture();
    for (const hash of ['#/home','#/search?query=big+buck+bunny&parentId=',
        '#/search?query=big+buck+bunny&collectionType=', '#/search?query=other',
        '#/search?query=big+buck+bunny&query=big+buck+bunny']) {
        f.world.location.href='https://example.test/jellyfin/web/index.html'+hash;
        assert.equal(boundedSearchUrl(f.world,f.url(),'GET',f.world.ApiClient),null,hash);
    }
});
test('preserves repeated native SDK array parameters while rejecting ambiguous controls',()=>{
    const f=fixture();
    const original=f.url()+'&fields=PrimaryImageAspectRatio&fields=CanDelete&includeItemTypes=Movie&includeItemTypes=Series';
    const result=new URL(boundedSearchUrl(f.world,original,'GET',f.world.ApiClient).url);
    assert.equal(result.searchParams.get('limit'),'50');
    assert.deepEqual(result.searchParams.getAll('fields'),['PrimaryImageAspectRatio','CanDelete']);
    assert.deepEqual(result.searchParams.getAll('includeItemTypes'),['Movie','Series']);
    assert.equal(boundedSearchUrl(f.world,original+'&Limit=100','GET',f.world.ApiClient),null);
});
test('fetch string/URL calls preserve init including headers, credentials and cancellation',async()=>{
    const f=fixture(); let applied=0;
    const original=f.world.fetch, originalOpen=f.world.XMLHttpRequest.prototype.open;
    const budget=installSearchBudget(f.world,()=>true,()=>applied++);
    const controller=new AbortController(), init={headers:{'X-Fixture':'test-only'},credentials:'include',signal:controller.signal};
    assert.equal(await f.world.fetch(new URL(f.url()),init),'response');
    assert.equal(new URL(f.calls[0][0]).searchParams.get('limit'),'50');
    assert.equal(f.calls[0][1],init); assert.equal(applied,1); assert.equal(budget.limit(),50);
    const xhr=new f.world.XMLHttpRequest();
    assert.equal(xhr.open('GET',f.url(),false),'opened');
    assert.equal(f.opened[0].receiver,xhr); assert.equal(f.opened[0].args[2],false);
    assert.equal(new URL(f.opened[0].args[1]).searchParams.get('limit'),'50');
    f.world.location.href=f.world.location.href.replace('big%20buck%20bunny','different');
    assert.equal(budget.limit(),undefined);
    budget.dispose(); assert.equal(f.world.fetch,original); assert.equal(f.world.XMLHttpRequest.prototype.open,originalOpen);
});
test('fetch Request copies preserve headers, options and the original abort signal semantics',async()=>{
    const f=fixture(), controller=new AbortController();
    const budget=installSearchBudget(f.world,()=>true,()=>{});
    const request=new Request(f.url(),{headers:{Authorization:'test-only-sentinel'},credentials:'include',cache:'no-store',signal:controller.signal});
    await f.world.fetch(request);
    const forwarded=f.calls[0][0];
    assert.equal(forwarded.headers.get('Authorization'),'test-only-sentinel');
    assert.equal(forwarded.credentials,'include'); assert.equal(forwarded.cache,'no-store');
    assert.equal(new URL(forwarded.url).searchParams.get('limit'),'50');
    assert.equal(new URL(request.url).searchParams.get('limit'),'800');
    controller.abort(); assert.equal(forwarded.signal.aborted,true);
    const init={method:'POST'}; await f.world.fetch(request,init);
    assert.equal(f.calls[1][0],request); assert.equal(f.calls[1][1],init);
    budget.dispose();
});
test('unsupported servers, logout and disposal leave native requests untouched',async()=>{
    const f=fixture(); let supported=false, count=0;
    const budget=installSearchBudget(f.world,()=>supported,()=>count++);
    await f.world.fetch(f.url()); assert.equal(f.calls.at(-1)[0],f.url()); assert.equal(count,0);
    supported=true; await f.world.fetch(f.url()); assert.equal(budget.limit(),50);
    f.world.ApiClient.getCurrentUserId=()=>null;
    await f.world.fetch(f.url()); assert.equal(f.calls.at(-1)[0],f.url()); assert.equal(budget.limit(),undefined);
    f.world.ApiClient.getCurrentUserId=()=> 'alice';
    const inner=f.world.fetch, outer=(...args)=>inner(...args); f.world.fetch=outer;
    budget.dispose(); assert.equal(f.world.fetch,outer); await f.world.fetch(f.url());
    assert.equal(f.calls.at(-1)[0],f.url()); // Another plugin's wrapper is not overwritten.
});
