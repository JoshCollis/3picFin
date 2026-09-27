"""Native Home/Favorites header seam: exercise the shipped host in Chromium."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (390, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 700})
        page.set_content('''<html><head></head><body><div class="skinHeader"><div class="headerTabs">
        <div is="emby-tabs" class="tabs-viewmenubar"><div class="emby-tabs-slider">
        <button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button>
        <button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div>
        <div class="MuiToolbar-root"><div class="MuiStack-root">
        <a href="#/">Logo</a><a href="#/home?tab=1">Favorites</a></div></div>
        <div id="indexPage"><div id="homeTab" class="tabContent is-active" data-index="0">
        <div class="sections" style="height:1200px">HSS sentinel</div></div>
        <div id="favoritesTab" class="tabContent" data-index="1">Favorites sentinel</div></div></body></html>''')
        page.evaluate("location.hash = '#/home'")
        page.add_style_tag(content=(web / 'home-tab-host.css').read_text())
        page.add_script_tag(content=(web / 'home-tab-host.js').read_text())
        page.evaluate('''async () => {
            window.user = 'alice'; window.activations = 0;
            window.api = {getCurrentUserId: () => user, getUrl: path => '/jellyfin/' + path,
              getJSON: async () => ({DiscoveryEnabled:true, HeroEnabled:false})};
            window.host = ThreePicFinHomeHost.createHost({document,
              loadFragment: async () => '<div>Fin sentinel</div>',
              loadScript: async () => ({mount: () => ({activate: () => activations++})})});
            if (!await host.mount({pane:document.querySelector('#homeTab'),
              favorites:document.querySelector('#favoritesTab'), apiClient:api,
              fingerprint:'12.1', userId:user, enabled:true})) throw Error('mount failed');
        }''')
        nav = page.locator('.MuiStack-root > .threepic-fin-host__nav')
        assert nav.count() == 1
        assert page.locator('#homeTab > .threepic-fin-host__tabs').count() == 0
        assert page.locator('.emby-tab-button').count() == 2
        assert page.locator('#indexPage .tabContent').count() == 2
        assert page.locator('#homeTab > .sections').is_visible()
        assert nav.bounding_box()['y'] < page.locator('#homeTab').bounding_box()['y']
        nav.click()
        assert page.locator('#homeTab > .sections').is_hidden()
        assert page.locator('#homeTab > .threepic-fin-host__panel').is_visible()
        assert page.evaluate('activations') == 1
        assert nav.get_attribute('aria-pressed') == 'true'
        page.locator('.MuiStack-root > a[href="#/"]').click()
        assert page.locator('#homeTab > .sections').is_visible()
        assert nav.get_attribute('aria-pressed') == 'false'
        nav.click()
        page.evaluate('''() => {
            document.querySelector('.MuiStack-root').outerHTML =
              '<div class="MuiStack-root"><a href="#/">Logo</a>' +
              '<a href="#/home?tab=1">Favorites</a></div>';
        }''')
        page.evaluate('''async () => host.mount({pane:document.querySelector('#homeTab'),
            favorites:document.querySelector('#favoritesTab'), apiClient:api,
            fingerprint:'12.1', userId:user, enabled:true})''')
        page.evaluate('host.sync()')
        if nav.get_attribute('aria-pressed') != 'true':
            nav.click()
        assert page.locator('#homeTab > .sections').is_hidden()
        page.evaluate('''() => {
            const duplicate = document.querySelector('.MuiToolbar-root').cloneNode(true);
            duplicate.querySelector('.threepic-fin-host__nav')?.remove();
            duplicate.id = 'duplicate-toolbar'; document.body.appendChild(duplicate);
            host.sync();
        }''')
        assert page.locator('.threepic-fin-host__nav').count() == 0
        assert page.locator('#homeTab > .sections').is_visible()
        page.evaluate("document.querySelector('#duplicate-toolbar').remove(); host.sync()")
        assert nav.count() == 1
        page.evaluate('host.dispose()')
        assert nav.count() == 0
        assert page.locator('#homeTab > .sections').is_visible()
        assert page.locator('#favoritesTab').inner_text() == 'Favorites sentinel'
        page.close()
    browser.close()
print('native header seam at mobile/desktop: pass')
