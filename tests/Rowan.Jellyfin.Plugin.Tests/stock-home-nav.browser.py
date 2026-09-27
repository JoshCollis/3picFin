"""Stock-style 12.1 Home header without distribution-specific MUI navigation."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (320, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 700})
        page.set_content('''<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body>
        <div class="skinHeader"><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider">
        <button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button>
        <button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div>
        <div id="indexPage"><div id="homeTab" data-index="0"><div class="sections">HSS sentinel</div></div>
        <div id="favoritesTab" data-index="1">Favorites sentinel</div></div></body></html>''')
        page.evaluate("location.hash = '#/home'")
        page.add_style_tag(content=(web / 'home-tab-host.css').read_text())
        page.add_script_tag(content=(web / 'home-tab-host.js').read_text())
        assert page.evaluate('''async () => {
            window.user = 'alice'; window.activations = 0;
            window.host = ThreePicFinHomeHost.createHost({document,
                loadFragment: async () => '<div>Fin sentinel</div>',
                loadScript: async () => ({mount: () => {
                    const cleanup = () => {}; cleanup.activate = () => activations++; return cleanup;
                }})});
            window.api = {getCurrentUserId: () => user, getUrl: path => '/jellyfin/' + path,
                getJSON: async () => ({DiscoveryEnabled:true, HeroEnabled:false})};
            return host.mount({pane:document.querySelector('#homeTab'), favorites:document.querySelector('#favoritesTab'),
                apiClient:api, fingerprint:'12.1', userId:user, enabled:true});
        }''') is True
        nav = page.locator('.headerTabs > .threepic-fin-host__nav')
        assert nav.count() == 1
        assert nav.bounding_box()['height'] >= 44
        assert page.locator('.emby-tabs-slider > .emby-tab-button').count() == 2
        assert page.locator('#homeTab > .sections').is_visible()
        nav.click()
        assert page.locator('#homeTab > .sections').is_hidden()
        assert page.locator('#homeTab > .threepic-fin-host__panel').is_visible()
        assert page.evaluate('activations') == 1
        page.locator('.emby-tab-button').first.click()
        assert page.locator('#homeTab > .sections').is_visible()
        assert nav.get_attribute('aria-pressed') == 'false'
        nav.click()
        assert page.evaluate('activations') == 1
        assert page.locator('#favoritesTab').inner_text() == 'Favorites sentinel'
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.evaluate('host.dispose()')
        assert nav.count() == 0
        assert page.locator('.emby-tabs-slider > .emby-tab-button').count() == 2
        assert page.locator('#homeTab > .sections').is_visible()
        # A changed native tab contract must not silently borrow the fallback.
        page.locator('.emby-tab-button').first.evaluate("element => element.classList.remove('emby-tab-button-active')")
        assert page.evaluate('''() => host.mount({pane:document.querySelector('#homeTab'),
            favorites:document.querySelector('#favoritesTab'), apiClient:api,
            fingerprint:'12.1', userId:user, enabled:true})''') is False
        assert page.locator('.threepic-fin-host__nav').count() == 0
        assert page.locator('#homeTab > .sections').is_visible()
        page.close()
    browser.close()
print('stock-style toolbar-free Home navigation: pass')
