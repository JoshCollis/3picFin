"""Offline regressions for Search request limits and navigation churn."""
import json
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
from playwright.sync_api import sync_playwright

WEB = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
ORIGIN = 'http://localhost:8765'
HTML = '''<!doctype html><html><head><style>
.MuiStack-root { display:flex; gap:10px } .hide { display:none }
.searchResults .itemsContainer { display:flex; overflow:hidden }
.card { flex:none; width:120px } .cardBox { min-height:40px }
</style></head><body>
<header class="MuiToolbar-root"><button aria-label="Open Menu">Menu</button><div class="MuiStack-root">
<a href="#/">Home</a><a href="#/home?tab=1"><span class="MuiTypography-root">Favorites</span></a>
</div></header>
<aside class="MuiDrawer-root"><ul><li class="MuiListItem-root"><a href="#/">Home</a></li>
<li class="MuiListItem-root"><a href="#/home?tab=1"><span class="MuiListItemText-root">Favorites</span></a></li></ul></aside>
<main id="searchPage"><div><input id="searchTextInput"></div><div class="searchResults"></div></main>
</body></html>'''


def run(browser, version, width):
    page = browser.new_page(viewport={'width': width, 'height': 800})
    errors, requests = [], []
    page.on('pageerror', lambda error: errors.append(str(error)))

    def route(request):
        url = urlsplit(request.request.url)
        if url.path == '/jellyfin/Items':
            query = parse_qs(url.query)
            limit = int(query['limit'][0])
            requests.append({'limit': limit, 'query': query['searchTerm'][0],
                             'fields': query.get('fields'),
                             'fixture_header': request.request.headers.get('x-fixture')})
            request.fulfill(status=200, content_type='application/json', body=json.dumps({
                'Items': [{'Type': 'Movie', 'Name': f'Fixture {i}'} for i in range(limit)]}))
        elif url.path.endswith('/global-search-addon.css'):
            request.fulfill(status=200, content_type='text/css', body=(WEB / 'global-search-addon.css').read_text())
        else:
            request.fulfill(status=200, content_type='text/html', body=HTML)

    page.route('**/*', route)
    page.goto(ORIGIN + '/jellyfin/web/index.html#/search')
    page.evaluate('''version => {
        window.user='alice'; window.navReads=0; window.cardScans=0;
        const rect=Element.prototype.getBoundingClientRect;
        Element.prototype.getBoundingClientRect=function(){
            if(this.matches('.MuiStack-root'))navReads++;
            return rect.call(this);
        };
        const queryAll=Element.prototype.querySelectorAll;
        Element.prototype.querySelectorAll=function(selector){
            if(selector==='.itemsContainer .card')cardScans++;
            return queryAll.call(this,selector);
        };
        window.ApiClient={getCurrentUserId:()=>user,getUrl:(p,q)=>'/jellyfin/'+p+(q?'?'+new URLSearchParams(q):''),
            getJSON:async url=>url.includes('System/Info/Public')?{Version:version}:
                url.includes('Rowan/Home/Mode')?{DiscoveryEnabled:true}:{Items:[],TotalPages:1},
            logout:()=>Promise.resolve()};
        window.originalFetch=fetch;window.originalOpen=XMLHttpRequest.prototype.open;
        const input=document.querySelector('#searchTextInput');let timer;
        input.addEventListener('input',()=>{
            history.replaceState({},'', '#/search?query='+encodeURIComponent(input.value));
            clearTimeout(timer);
            timer=setTimeout(async()=>{
                const q=new URLSearchParams({userId:user,searchTerm:input.value,limit:800,recursive:true});
                q.append('fields','PrimaryImageAspectRatio');q.append('fields','CanDelete');
                const a=await fetch(new Request('/jellyfin/Items?'+q,{headers:{'X-Fixture':'fetch'}})).then(r=>r.json());
                q.set('limit',100);
                const b=await new Promise(resolve=>{const xhr=new XMLHttpRequest();
                    xhr.open('GET','/jellyfin/Items?'+q);xhr.setRequestHeader('X-Fixture','xhr');
                    xhr.onload=()=>resolve(JSON.parse(xhr.responseText));xhr.send();});
                const results=document.querySelector('.searchResults');
                results.innerHTML=[a,b].map((data,index)=>'<div class="verticalSection"><h2>Native</h2><div class="itemsContainer">'+
                    data.Items.map((item,i)=>'<div class="card" data-type="'+(index?'Episode':'Movie')+'"><div class="cardBox">'+i+'</div></div>').join('')+'</div></div>').join('');
                window.nativeComplete=true;
            },500);
        });
    }''', version)
    for asset in ['global-search-addon.js', 'global-fin-nav.js', 'search-adapter.js']:
        page.add_script_tag(content=(WEB / asset).read_text())
    page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length===2")
    page.locator('#searchTextInput').press_sequentially('big buck bunny', delay=70)
    page.wait_for_function('window.nativeComplete===true')
    page.wait_for_function("document.querySelector('.threepic-fin-search')?.dataset.nativeBefore==='1'")
    fields = ['PrimaryImageAspectRatio', 'CanDelete']
    assert requests == [{'limit': 50, 'query': 'big buck bunny', 'fields': fields, 'fixture_header': 'fetch'},
                        {'limit': 50, 'query': 'big buck bunny', 'fields': fields, 'fixture_header': 'xhr'}], requests
    assert page.locator('.searchResults .card').count() == 100
    assert page.locator('.threepic-fin-search__limit-note').is_visible()
    assert '50 results per request' in page.locator('.threepic-fin-search__limit-note').inner_text()

    # Card-internal upgrades must not rescan results or measure navigation.
    page.evaluate('''() => {
        const container=document.querySelector('.searchResults .itemsContainer');
        const fragment=document.createDocumentFragment();
        for(let i=0;i<700;i++){const card=document.createElement('div');card.className='card';
            card.dataset.type='Movie';card.innerHTML='<div class="cardBox"><span>Before</span></div>';fragment.append(card);}
        container.append(fragment);
    }''')
    page.wait_for_timeout(250)
    churn = page.evaluate('''async () => {
        navReads=cardScans=0;let heartbeats=0;
        const heartbeat=setInterval(()=>heartbeats++,50);
        const cards=[...document.querySelectorAll('.searchResults .cardBox')];
        await new Promise(resolve=>{let i=0;function upgrade(){
            cards[i].replaceChildren(document.createTextNode('After'));i++;
            if(i<cards.length)queueMicrotask(upgrade);else resolve();}upgrade();});
        await new Promise(resolve=>setTimeout(resolve,650));clearInterval(heartbeat);
        return {navReads,cardScans,heartbeats};
    }''')
    assert churn['navReads'] == 0, churn
    assert churn['cardScans'] == 0, churn
    assert churn['heartbeats'] >= 3, churn

    # React replacing the whole shell must replace cloned, listener-less links.
    page.locator('.MuiToolbar-root').evaluate('node=>node.outerHTML=node.outerHTML')
    page.wait_for_timeout(750)
    page.locator('.MuiStack-root .threepic-fin-global-nav').click()
    page.wait_for_function("location.hash==='#/home?fin=1'")
    page.wait_for_function("document.querySelector('.MuiStack-root .threepic-fin-global-nav')?.getAttribute('aria-current')==='page'")
    page.evaluate('user=null')
    page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length===0")
    page.evaluate('user="bob"')
    page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length===2")
    page.evaluate('__threePicFinGlobalNav.dispose();__threePicFinSearchAdapter.dispose()')
    assert page.evaluate('fetch===originalFetch && XMLHttpRequest.prototype.open===originalOpen')
    assert page.locator('.threepic-fin-global-nav,.threepic-fin-search').count() == 0
    assert not errors, errors
    page.close()
    return {'version': version, 'width': width, 'native_requests': requests, 'churn': churn}


def cold_search(browser, version, prefix):
    """An initial SDK query can precede both Search DOM and the addon preflight."""
    page = browser.new_page()
    requests, errors = [], []
    page.on('pageerror', lambda error: errors.append(str(error)))

    def route(request):
        url = urlsplit(request.request.url)
        if url.path == prefix + '/Items':
            query = parse_qs(url.query)
            requests.append({'limit': int(query['limit'][0]),
                             'transport': request.request.resource_type,
                             'header': request.request.headers.get('x-fixture')})
            request.fulfill(content_type='application/json', body='{"Items":[]}')
        else:
            request.fulfill(content_type='text/html', body='<!doctype html><body></body>')

    page.route('**/*', route)
    url = ORIGIN + prefix + '/web/index.html#/search?query=fixture'
    # Exercise a new document as well as reload of the same search deep link.
    for reload in [False, True]:
        if reload:
            page.reload()
        else:
            page.goto(url)
        page.evaluate('''({version,prefix,reload}) => {
            window.publicChecks=0;window.publicResolved=false;
            window.connectionVersion=reload?undefined:version;
            window.ApiClient={getCurrentUserId:()=> 'alice',getUrl:p=>prefix+'/'+p,
                // The SDK sets connection metadata before dispatching signed-in queries.
                serverVersion:()=>connectionVersion,
                getJSON:path=>{if(!path.includes('System/Info/Public'))return Promise.resolve({Items:[],TotalPages:1});
                    publicChecks++;return new Promise(resolve=>window.resolvePublic=()=>{
                        publicResolved=true;resolve({Version:version});});}};
            window.originalFetch=fetch;window.originalOpen=XMLHttpRequest.prototype.open;
            if(reload)document.body.innerHTML='<main id="searchPage"><input id="searchTextInput" value="fixture"><div class="searchResults"></div></main>';
        }''', {'version': version, 'prefix': prefix, 'reload': reload})
        for asset in ['global-search-addon.js', 'search-adapter.js']:
            page.add_script_tag(content=(WEB / asset).read_text())
        if reload:
            # The addon's HTTP check has already started but is still slow when
            # the native connection handshake publishes its version and queries.
            page.wait_for_function('publicChecks===1')
            page.evaluate('version=>connectionVersion=version', version)
        else:
            page.evaluate("void ApiClient.getJSON(ApiClient.getUrl('System/Info/Public'))")
        page.evaluate('''async prefix => {
            const q=new URLSearchParams({userId:'alice',searchTerm:'fixture',limit:800});
            const request=new Request(location.origin+prefix+'/Items?'+q,
                {headers:{'X-Fixture':'cold-fetch'},credentials:'include'});
            await fetch(request);
            q.set('limit',100);
            await new Promise(resolve=>{const xhr=new XMLHttpRequest();
                xhr.open('GET',prefix+'/Items?'+q);xhr.setRequestHeader('X-Fixture','cold-xhr');
                xhr.onload=resolve;xhr.send();});
        }''', prefix)
        assert page.evaluate('publicResolved') is False
        expected = [50, 50] if version in ['12.1.0', '12.3.0'] else [800, 100]
        assert [r['limit'] for r in requests[-2:]] == expected, requests
        assert [r['header'] for r in requests[-2:]] == ['cold-fetch', 'cold-xhr']
        page.evaluate('resolvePublic();__threePicFinSearchAdapter.dispose()')
        assert page.evaluate('fetch===originalFetch && XMLHttpRequest.prototype.open===originalOpen')
    assert not errors, errors
    page.close()
    return {'version': version, 'prefix': prefix, 'requests': requests}


def toolbar_visibility(browser):
    page = browser.new_page()
    errors = []
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.set_content(HTML)
    page.locator('.MuiStack-root').evaluate("node=>node.style.display='none'")
    page.evaluate('''() => {window.ApiClient={getCurrentUserId:()=> 'alice',getUrl:p=>'/'+p,
        getJSON:async()=>({Version:'12.3.0',DiscoveryEnabled:true})};}''')
    page.add_script_tag(content=(WEB / 'global-fin-nav.js').read_text())
    page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length===1")
    assert page.locator('.MuiStack-root .threepic-fin-global-nav').count() == 0
    page.locator('.MuiStack-root').evaluate("node=>node.style.display='flex'")
    # No resize, route change or shell replacement: style observation must repair it.
    page.wait_for_function("document.querySelectorAll('.MuiStack-root .threepic-fin-global-nav').length===1", timeout=1500)
    assert page.locator('.MuiDrawer-root .threepic-fin-global-nav').count() == 1
    page.wait_for_timeout(200)
    assert page.locator('.threepic-fin-global-nav').count() == 2
    page.locator('.MuiStack-root').evaluate("node=>node.style.display='none'")
    page.wait_for_function("document.querySelectorAll('.MuiStack-root .threepic-fin-global-nav').length===0", timeout=1500)
    page.locator('.MuiStack-root').evaluate("node=>node.style.display='flex'")
    page.wait_for_function("document.querySelectorAll('.MuiStack-root .threepic-fin-global-nav').length===1", timeout=1500)
    assert page.locator('.threepic-fin-global-nav').count() == 2
    page.evaluate('__threePicFinGlobalNav.dispose()')
    assert not errors, errors
    page.close()
    return {'toolbar_reappeared': True}


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    try:
        result = [run(browser, version, width) for version, width in [('12.1.0', 390), ('12.3.0', 1280)]]
        cold = [cold_search(browser, version, prefix) for version in ['12.1.0','12.3.0','12.2.0'] for prefix in ['', '/jellyfin']]
        visibility = toolbar_visibility(browser)
    finally:
        browser.close()
print(json.dumps({'render_churn':result,'cold_search':cold,'visibility':visibility}, indent=2))
