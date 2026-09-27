"""Disposable browser geometry check for the isolated inner tab CSS."""
from pathlib import Path
from playwright.sync_api import sync_playwright

css = (Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web/home-tab-host.css').read_text()
html = '''<html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head>
<body style="margin:0"><div id="home"><div class="threepic-fin-host__tabs" role="tablist">
<button class="threepic-fin-host__tab">Home</button><button class="threepic-fin-host__tab">3pic Fin</button></div>
<div class="sections">Native HSS rows</div><div class="threepic-fin-host__panel">Discovery
<div class="sections" id="discovery-sections">Nested Discovery cards</div></div></div>
<div id="favorites">Native Favorites</div></body></html>'''
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (360, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_content(html)
        page.add_style_tag(content=css)
        buttons = page.locator('.threepic-fin-host__tab')
        assert buttons.count() == 2
        boxes = [buttons.nth(i).bounding_box() for i in range(2)]
        assert all(box is not None and box['height'] >= 44 for box in boxes)
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        assert page.locator('#home > .sections').is_visible()
        page.locator('#home').evaluate("el => el.setAttribute('data-threepic-fin-view', 'discovery')")
        assert not page.locator('#home > .sections').is_visible()
        assert page.locator('#discovery-sections').is_visible()
        assert page.locator('#favorites').is_visible()
        page.close()
    # Exercise the actual shipped host and fragment in Chromium.
    web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
    host_js = (web / 'home-tab-host.js').read_text()
    fragment_js = (web / 'discovery.js').read_text()
    fragment_html = (web / 'discovery.html').read_text()
    page = browser.new_page()
    page.set_content('<html><head></head><body><div id="home"><div class="sections">Native rows</div></div><div id="favorites"></div></body></html>')
    page.evaluate("location.hash = '#/home'")
    page.add_script_tag(content=fragment_js)
    page.add_script_tag(content=host_js)
    page.evaluate("""async html => {
      const pane = document.querySelector('#home'), favorites = document.querySelector('#favorites');
      window.user = 'alice'; window.shown = []; window.reads = 0; window.freshId = '01234567-89ab-cdef-0123-456789abcdef';
      window.Emby = { Page: { showItem: item => shown.push(item) } };
      const movie = {Title:'Film', MediaType:'movie', TmdbId:9};
      const api = {getCurrentUserId: () => user, serverId: () => 'server-a',
        getUrl: (route, params) => '/jellyfin/' + route + (params ? '?' + new URLSearchParams(params) : ''),
        getJSON: url => url.includes('Rowan/Home/Mode') ? Promise.resolve({DiscoveryEnabled:true, HeroEnabled:false}) :
          url.includes('TitleDetails') ? (reads++, Promise.resolve({Title:'Film', MediaType:'movie', TmdbId:9, LibraryItemId:freshId, CanRequest:false})) :
          Promise.resolve({Movies:{Items:[movie]}, Tv:{Items:[]}, Requests:{Items:[]}})};
      window.host = ThreePicFinHomeHost.createHost({document, loadFragment: async () => html,
        loadScript: async () => ThreePicFinDiscovery});
      if (!await host.mount({pane, favorites, apiClient:api, fingerprint:'12.1', userId:user, enabled:true})) throw Error('host did not mount');
      pane.querySelectorAll('.threepic-fin-host__tab')[1].click();
    }""", fragment_html)
    page.locator('#threepic-fin-movies .threepic-fin-discovery__title-button').click()
    page.locator('#threepic-fin-details-open').click()
    page.wait_for_function('shown.length === 1')
    assert page.evaluate('shown[0].Id') == '01234567-89ab-cdef-0123-456789abcdef'
    assert page.evaluate('reads') == 2
    page.evaluate("freshId = null")
    page.locator('#threepic-fin-movies .threepic-fin-discovery__title-button').click()
    assert not page.locator('#threepic-fin-details-open').is_visible()
    page.evaluate('host.dispose()')
    assert not page.locator('.threepic-fin-host__panel').count()
    page.close()
    # Exercise the real hero in the generated Home DOM; no live server is changed.
    hero_js = (web / 'static-hero.js').read_text()
    hero_css = (web / 'static-hero.css').read_text()
    for width in (360, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_content('''<html><head></head><body style="margin:0"><div id="home">
          <div class="sections" style="height:1200px">HSS sentinel</div></div>
          <div id="favorites">Favorites sentinel</div></body></html>''')
        page.evaluate("location.hash = '#/home'")
        page.add_style_tag(content=css)
        page.add_style_tag(content=hero_css)
        page.add_script_tag(content=hero_js)
        page.add_script_tag(content=host_js)
        page.evaluate("""async () => {
          const pane = document.querySelector('#home'), favorites = document.querySelector('#favorites');
          const id = '01234567-89ab-cdef-0123-456789abcdef';
          window.identity = 'alice'; window.token = 'token'; window.shown = [];
          window.Emby = {Page: {showItem: item => shown.push(item)}};
          window.api = {getUrl: route => route, getCurrentUserId: () => identity,
            accessToken: () => token, serverId: () => 'server-a',
            getJSON: async route => route === 'Rowan/Home/Mode'
              ? {DiscoveryEnabled:true, HeroEnabled:true}
              : route === 'Rowan/Home/Hero'
              ? [{Id:id, Name:'Featured', Overview:'Overview', ImageType:'Backdrop', ImageIndex:0, ImageTag:'a1'}]
              : {Id:id, Type:'Movie'},
            fetch: async () => ({ok:false})};
          window.host = ThreePicFinHomeHost.createHost({document,
            loadFragment: async () => '<div>Discovery</div>',
            loadScript: async route => route.endsWith('static-hero.js')
              ? RowanStaticHero : {mount: () => () => {}}});
          if (!await host.mount({pane, favorites, apiClient:api, fingerprint:'12.1',
              userId:identity, enabled:true, heroEnabled:true})) throw Error('mount failed');
        }""")
        page.wait_for_selector('#home > .threepic-fin-host__hero .rowan-static-hero')
        for selector in ('.rowan-hero-previous', '.rowan-hero-next', '.rowan-hero-pagination', '.rowan-hero-progress'):
            assert page.locator('#home > .threepic-fin-host__hero ' + selector).is_hidden()
        assert page.locator('#home > .sections').count() == 1
        assert page.locator('#home > .sections').evaluate('(el) => el.parentNode.id') == 'home'
        assert page.locator('#favorites').inner_text() == 'Favorites sentinel'
        assert page.locator('.rowan-static-hero').get_attribute('aria-label') == 'Featured media'
        assert page.locator('.rowan-static-hero .rowan-hero-open').bounding_box()['height'] >= 44
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.locator('.rowan-hero-open').click()
        page.wait_for_function('shown.length === 1')
        page.evaluate('scrollTo(0, 500)')
        page.locator('.threepic-fin-host__tab').nth(1).click()
        assert page.locator('#home > .threepic-fin-host__hero').count() == 0
        assert not page.locator('#home > .sections').is_visible()
        page.locator('.threepic-fin-host__tab').nth(0).click()
        page.wait_for_selector('#home > .threepic-fin-host__hero .rowan-static-hero')
        assert page.evaluate('document.scrollingElement.scrollHeight > innerHeight')
        page.evaluate('scrollTo(0, 500)')
        assert page.evaluate('scrollY') > 0
        page.evaluate('identity = null; token = null; host.dispose()')
        assert page.locator('#home > .threepic-fin-host__hero').count() == 0
        assert page.locator('#home > .sections').count() == 1
        assert page.locator('#favorites').inner_text() == 'Favorites sentinel'
        page.evaluate("""async () => {
          identity = 'bob'; token = 'new-token'; api.getJSON = async route => route === 'Rowan/Home/Hero' ? [] : {};
          await host.mount({pane:document.querySelector('#home'), favorites:document.querySelector('#favorites'),
            apiClient:api, fingerprint:'12.1', userId:identity, enabled:true, heroEnabled:true});
        }""")
        assert page.locator('#home > .threepic-fin-host__hero').count() == 0
        assert page.locator('link[href="static-hero.css"]').count() == 0
        page.evaluate('host.dispose()')
        page.close()
    browser.close()
print('360px and 1280px CSS geometry, hero lifecycle, HSS scroll and navigation: pass')
