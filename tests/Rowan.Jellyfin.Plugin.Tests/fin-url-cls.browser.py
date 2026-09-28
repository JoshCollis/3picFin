"""Shipped host/hero in Chromium: URL navigation and post-mount hero geometry."""
from pathlib import Path
from tempfile import gettempdir
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (360, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_content('''<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0">
          <div class="skinHeader" style="display:none"><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider">
          <button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div>
          <div class="MuiToolbar-root"><div class="MuiStack-root"><a href="#/">Home</a><a href="#/home?tab=1">Favorites</a></div></div>
          <div id="indexPage"><div id="homeTab" data-index="0"><div class="sections" style="height:1200px">HSS</div></div>
          <div id="favoritesTab" data-index="1">Favorites</div></div></body></html>''')
        page.evaluate("location.hash = '#/home'")
        for asset in ('home-tab-host.css', 'static-hero.css'):
            page.add_style_tag(content=(web / asset).read_text())
        for asset in ('static-hero.js', 'home-tab-host.js'):
            page.add_script_tag(content=(web / asset).read_text())
        page.evaluate('''async () => {
          window.shifts = [];
          new PerformanceObserver(list => shifts.push(...list.getEntries().filter(e => !e.hadRecentInput).map(e => e.value)))
            .observe({type:'layout-shift', buffered:true});
          window.user = 'alice'; window.token = 'token';
          window.slide = {Id:'01234567-89ab-cdef-0123-456789abcdef', Name:'Featured',
            ImageType:'Backdrop', ImageIndex:0, ImageTag:'a1'};
          window.api = {getUrl:x => '/jellyfin/' + x, getCurrentUserId:()=>user, accessToken:()=>token,
            getJSON:route => route.includes('Hero') ? new Promise(resolve => window.release = resolve) : Promise.resolve({}),
            fetch: async () => ({ok:false})};
          window.host = ThreePicFinHomeHost.createHost({document, loadFragment:async()=>'<div>Fin</div>',
            loadScript:async path => path.endsWith('static-hero.js') ? RowanStaticHero :
              {mount:()=>{const cleanup=()=>{}; cleanup.activate=()=>{}; return cleanup;}}});
          if (!await host.mount({pane:document.querySelector('#homeTab'), favorites:document.querySelector('#favoritesTab'),
            apiClient:api, fingerprint:'12.1',userId:user,enabled:true,mode:{DiscoveryEnabled:true,HeroEnabled:true}})) throw Error('mount');
          window.addEventListener('hashchange', () => host.sync());
          window.beforeY = document.querySelector('.sections').getBoundingClientRect().top;
        }''')
        assert page.locator('.MuiStack-root > .threepic-fin-host__nav').count() == 1
        page.evaluate('release([slide])')
        page.wait_for_selector('.rowan-static-hero h2:text("Featured")')
        page.evaluate('''() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))''')
        metrics = page.evaluate('''() => ({before:beforeY, after:document.querySelector('.sections').getBoundingClientRect().top,
            heroHeight: document.querySelector('.threepic-fin-host__hero').getBoundingClientRect().height,
            shift:shifts.reduce((a,b)=>a+b,0), width:document.documentElement.scrollWidth})''')
        expected = min(0.78 * 800, 800) if width > 600 else 0.65 * 800
        assert abs(metrics['after'] - metrics['before']) < 1, metrics
        assert abs(metrics['heroHeight'] - expected) < 2, metrics
        assert metrics['width'] <= width, metrics
        page.screenshot(path=str(Path(gettempdir()) / f'fin-home-{width}.png'))
        nav = page.locator('.MuiStack-root > .threepic-fin-host__nav')
        nav.click()
        assert page.evaluate('location.hash') == '#/home?fin=1'
        assert page.locator('#homeTab > .threepic-fin-host__panel').is_visible()
        page.go_back()
        page.wait_for_function("location.hash === '#/home' && document.querySelector('#homeTab').getAttribute('data-threepic-fin-view') === 'home'")
        page.evaluate('host.sync()')
        nav.click()
        page.evaluate('host.dispose()')
        assert page.evaluate('''async () => host.mount({pane:document.querySelector('#homeTab'),
          favorites:document.querySelector('#favoritesTab'),apiClient:api,fingerprint:'12.1',
          userId:user,enabled:true,mode:{DiscoveryEnabled:true,HeroEnabled:true}})''') is True
        assert page.locator('#homeTab > .threepic-fin-host__panel').is_visible()
        page.evaluate('''() => { document.querySelector('.MuiStack-root').outerHTML =
          '<div class="MuiStack-root"><a href="#/">Home</a><a href="#/home?tab=1">Favorites</a></div>'; host.sync(); }''')
        assert page.locator('.MuiStack-root > .threepic-fin-host__nav').get_attribute('aria-pressed') == 'true'
        page.evaluate("location.hash = '#/home?tab=1'; host.dispose()")
        assert page.locator('#homeTab > .threepic-fin-host__panel').count() == 0
        assert page.locator('#favoritesTab').inner_text() == 'Favorites'
        page.evaluate("user = 'bob'; token = 'new-token'")
        page.close()
        print(f'{width}px: geometry={metrics}, Fin URL and Back pass')
    browser.close()
