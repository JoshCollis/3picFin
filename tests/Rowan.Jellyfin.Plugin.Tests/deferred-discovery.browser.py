"""Disposable generated-fragment browser check; no live Jellyfin calls."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
fragment = (web / 'discovery.html').read_text()
discovery = (web / 'continuous-rail.js').read_text() + '\n' + (web / 'discovery.js').read_text()
host = (web / 'home-tab-host.js').read_text()
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True)
    page = browser.new_page()
    page.set_content('<div class="skinHeader"><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider"><button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div><div id="home"><div class="sections">HSS</div></div><div id="favorites"></div>')
    page.evaluate("location.hash = '#/home'")
    page.add_script_tag(content=discovery)
    page.add_script_tag(content=host)
    page.evaluate("""async fragment => {
        window.discoveryCalls = [];
        window.api = {
            getCurrentUserId: () => 'alice',
            getUrl: (route, params = {}) => `https://example.test/jellyfin/${route}?${new URLSearchParams(params)}`,
            getJSON: async url => { if (url.includes('Rowan/Home/Mode')) return { DiscoveryEnabled: true, HeroEnabled: false }; window.discoveryCalls.push(url); return {
                Movies: { Items: [{ TmdbId: 7, MediaType: 'movie', Title: 'Ready' }], TotalPages: 1 },
                Tv: { Items: [], TotalPages: 1 }, Requests: { Items: [], TotalPages: 1 }
            }; }
        };
        window.host = ThreePicFinHomeHost.createHost({ document,
            loadFragment: async () => fragment,
            loadScript: async () => ThreePicFinDiscovery });
        const ok = await window.host.mount({ pane: document.querySelector('#home'),
            favorites: document.querySelector('#favorites'), apiClient: window.api,
            fingerprint: '12.3', userId: 'alice', enabled: true });
        if (!ok) throw Error('test host failed to mount');
    }""", fragment)
    assert page.evaluate('discoveryCalls.length') == 0
    page.locator('.headerTabs > .threepic-fin-host__nav').click()
    page.locator('.emby-tab-button').first.click()
    page.locator('.headerTabs > .threepic-fin-host__nav').click()
    assert page.evaluate('discoveryCalls.filter(url=>url.includes("/3picFin/Discovery?")).length') == 1
    page.locator('#threepic-fin-movies').get_by_role('button', name='Ready', exact=True).wait_for()
    assert page.evaluate('discoveryCalls[0].includes("/jellyfin/3picFin/Discovery?")')
    page.evaluate('host.dispose()')
    assert page.evaluate('document.querySelector("#home > .sections") !== null')
    browser.close()
print('generated Discovery fragment: Home 0, Fin rapid switches 1, rendered and disposed')
