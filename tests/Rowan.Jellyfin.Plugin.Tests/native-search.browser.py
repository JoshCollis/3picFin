"""Synthetic signed-in Jellyfin 12.1 Search DOM; exercises shipped adapter in Chromium."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (390, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.route('http://localhost:8765/**', lambda route: route.fulfill(status=200, content_type='text/html', body='''<html><head></head><body><main id="searchPage">
            <input id="searchTextInput" type="search" value="Alien"><div class="searchResults"><div class="verticalSection">
            <div class="itemsContainer"><div class="card" data-id="native">Native Alien</div></div>
            </div></div></main></body></html>'''))
        page.route('http://localhost:8765/jellyfin/3picFin/Web/discovery.html',
                   lambda route: route.fulfill(status=200, content_type='text/html', body=(web / 'discovery.html').read_text()))
        page.route('https://image.tmdb.org/**', lambda route: route.fulfill(status=200, content_type='image/svg+xml',
                   body='<svg xmlns="http://www.w3.org/2000/svg" width="240" height="360"><rect width="240" height="360" fill="#386b84"/></svg>'))
        page.goto('http://localhost:8765/web/index.html#/search?query=Alien' if width == 1280
                  else 'http://localhost:8765/search?query=Alien')
        page.evaluate("""() => {
          window.identity='alice'; window.calls=[]; window.posts=0;
          window.ApiClient={ logout:()=>{identity=null; return Promise.resolve();}, getCurrentUserId:()=>identity, getUrl:(path,query)=>'/jellyfin/'+path+(query?'?'+new URLSearchParams(query):''),
            getJSON: async url=>{calls.push(url); if(url.includes('System/Info/Public')) return {Version:'12.1.0'};
              if(url.includes('3picFin/Search')) return {Items:[{TmdbId:9,MediaType:'movie',Title:'Seerr title',PosterPath:'/poster.jpg'}],TotalPages:1};
              if(url.includes('RequestOptions')) return {CanRequest:true,CanRequest4k:false,MediaStatus:1,Seasons:[]};
              if(url.includes('3picFin/Discovery')) return {Requests:{Items:[{Id:17,TmdbId:9,MediaType:'movie',Status:1,Is4k:false,Seasons:[]}]}};
              if(url.includes('TitleDetails')) return {TmdbId:9,MediaType:'movie',Title:'Seerr title',PosterPath:'/poster.jpg',CanRequest:true};
              throw Error(url);}, ajax:()=>{posts++; return Promise.resolve({Id:17});} };
        }""")
        page.add_style_tag(content=(web / 'global-search-addon.css').read_text())
        page.add_script_tag(content=(web / 'global-search-addon.js').read_text())
        page.add_script_tag(content=(web / 'discovery.js').read_text())
        page.add_script_tag(content=(web / 'search-adapter.js').read_text())
        page.wait_for_selector('.threepic-fin-search .cardText')
        assert page.locator('.card[data-id=native]').inner_text() == 'Native Alien'
        assert page.locator('.threepic-fin-search .cardText').inner_text() == 'Seerr title'
        assert page.locator('.threepic-fin-search img').get_attribute('src').endswith('/poster.jpg')
        assert page.evaluate("calls.some(x=>x.includes('/jellyfin/3picFin/Search?'))")
        page.evaluate("""() => { window.staleButton=document.querySelector('.threepic-fin-search article button');
            document.querySelector('#searchTextInput').value='New';
            history.replaceState({}, '', location.hash.startsWith('#') ? '#/search?query=New' : '/search?query=New');
            staleButton.click(); }""")
        assert page.locator('#threepic-fin-details-dialog[open]').count() == 0
        assert page.evaluate('posts') == 0
        page.wait_for_function("!document.querySelector('.threepic-fin-search article button')")
        page.wait_for_function("calls.some(x=>x.includes('query=New'))")
        page.wait_for_selector('.threepic-fin-search article button')
        page.evaluate("""() => { window.staleButton=document.querySelector('.threepic-fin-search article button');
            document.querySelector('#searchTextInput').value='Different';
            document.querySelector('#searchTextInput').dispatchEvent(new Event('input',{bubbles:true})); }""")
        page.wait_for_function("!document.querySelector('.threepic-fin-search')")
        page.evaluate("staleButton.click()")
        assert page.evaluate('posts') == 0
        page.evaluate("""() => { document.querySelector('#searchTextInput').value='Alien';
            history.replaceState({}, '', location.hash.startsWith('#') ? '#/search?query=Alien' : '/search?query=Alien');
            document.querySelector('#searchTextInput').dispatchEvent(new Event('input',{bubbles:true})); }""")
        page.wait_for_selector('.threepic-fin-search .cardText')
        assert page.locator('.threepic-fin-search article button').count() == 1
        assert page.locator('.threepic-fin-search article button').get_attribute('aria-label').startswith('Details for ')
        art_box = page.locator('.threepic-fin-search article').bounding_box()
        assert art_box and art_box['width'] <= 175 and art_box['height'] <= 320
        page.locator('.threepic-fin-search article button').focus()
        assert page.locator('.threepic-fin-search article button').evaluate('(el) => document.activeElement === el')
        page.keyboard.press('Enter')
        page.wait_for_selector('#threepic-fin-details-dialog[open]')
        assert page.locator('#threepic-fin-details-title').inner_text() == 'Seerr title'
        assert page.locator('#threepic-fin-details-body img').get_attribute('src').endswith('/poster.jpg')
        assert page.evaluate('posts') == 0
        page.locator('#threepic-fin-details-request').click()
        page.wait_for_selector('#threepic-fin-request-dialog[open]')
        assert page.evaluate('posts') == 0
        page.evaluate("""() => { history.pushState({}, '', '/home');
            document.querySelector('#threepic-fin-request-form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));
            history.replaceState({}, '', '/search?query=Alien'); }""")
        assert page.evaluate('posts') == 0
        page.locator('#threepic-fin-request-submit').click()
        page.evaluate("document.querySelector('#threepic-fin-request-form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}))")
        page.wait_for_function('posts === 1')
        assert 'Pending' in page.locator('#threepic-fin-request-status').inner_text()
        page.evaluate("history.pushState({}, '', '/home'); dispatchEvent(new PopStateEvent('popstate'))")
        page.wait_for_function("!document.querySelector('.threepic-fin-search')")
        page.evaluate("history.pushState({}, '', '/search?query=Alien&parentId=x'); dispatchEvent(new PopStateEvent('popstate'))")
        assert page.locator('.threepic-fin-search').count() == 0
        page.evaluate("history.pushState({}, '', '/search?query=Alien&collectionType='); dispatchEvent(new PopStateEvent('popstate'))")
        assert page.locator('.threepic-fin-search').count() == 0
        page.evaluate("history.pushState({}, '', '/search?query=Alien'); identity='bob'; dispatchEvent(new PopStateEvent('popstate'))")
        page.wait_for_selector('.threepic-fin-search .cardText')
        page.evaluate("window.oldSection=document.querySelector('.threepic-fin-search'); identity='alice'; dispatchEvent(new PopStateEvent('popstate'))")
        page.wait_for_function("document.querySelector('.threepic-fin-search .cardText') && document.querySelector('.threepic-fin-search') !== oldSection")
        assert page.evaluate('posts') == 1
        assert page.evaluate("""() => { ApiClient.logout(); return !document.querySelector('.threepic-fin-search'); }""")
        page.wait_for_function("!document.querySelector('.threepic-fin-search')")
        assert page.evaluate('posts') == 1
        page.close()
    browser.close()
