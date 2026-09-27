"""Disposable generated-fragment browser check; no live Jellyfin calls."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
fragment = (web / 'discovery.html').read_text()
discovery = (web / 'discovery.js').read_text()
host = (web / 'home-tab-host.js').read_text().replace(
    'const VERIFIED_WEB_BUNDLE_SHA256 = null;',
    "const VERIFIED_WEB_BUNDLE_SHA256 = 'test-only';")
assert "const VERIFIED_WEB_BUNDLE_SHA256 = 'test-only';" in host
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True)
    page = browser.new_page()
    page.set_content('<div id="home"><div class="sections">HSS</div></div><div id="favorites"></div>')
    page.add_script_tag(content=discovery)
    page.add_script_tag(content=host)
    page.evaluate("""async fragment => {
        window.discoveryCalls = [];
        window.api = {
            getUrl: (route, params = {}) => `https://example.test/jellyfin/${route}?${new URLSearchParams(params)}`,
            getJSON: async url => { window.discoveryCalls.push(url); return {
                Movies: { Items: [{ TmdbId: 7, MediaType: 'movie', Title: 'Ready' }], TotalPages: 1 },
                Tv: { Items: [], TotalPages: 1 }, Requests: { Items: [], TotalPages: 1 }
            }; }
        };
        window.host = ThreePicFinHomeHost.createHost({ document,
            loadFragment: async () => fragment,
            loadScript: async () => ThreePicFinDiscovery });
        const ok = await window.host.mount({ pane: document.querySelector('#home'),
            favorites: document.querySelector('#favorites'), apiClient: window.api,
            fingerprint: 'test-only', userId: 'alice', enabled: true });
        if (!ok) throw Error('test host failed to mount');
    }""", fragment)
    assert page.evaluate('discoveryCalls.length') == 0
    page.get_by_role('tab', name='3pic Fin', exact=True).click()
    page.get_by_role('tab', name='Home', exact=True).click()
    page.get_by_role('tab', name='3pic Fin', exact=True).click()
    assert page.evaluate('discoveryCalls.length') == 1
    assert page.get_by_label('Discover movies').get_by_role('heading', name='Ready').is_visible()
    assert page.evaluate('discoveryCalls[0].includes("/jellyfin/3picFin/Discovery?")')
    page.evaluate('host.dispose()')
    assert page.evaluate('document.querySelector("#home > .sections") !== null')
    browser.close()
print('generated Discovery fragment: Home 0, Fin rapid switches 1, rendered and disposed')
