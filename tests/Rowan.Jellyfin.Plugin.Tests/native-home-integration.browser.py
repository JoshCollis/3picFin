"""Rendered Home host/rows contract on a populated, signed-in browser fixture."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
item_id = '0123456789abcdef0123456789abcdef'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (320, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 520})
        page.route('**/jellyfin/Items/**/Images/**', lambda route: route.fulfill(content_type='image/svg+xml', body='<svg xmlns="http://www.w3.org/2000/svg" width="480" height="270"><rect width="480" height="270" fill="#58729a"/></svg>'))
        page.set_content('<html><head><base href="https://fixture.invalid/"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div class="skinHeader"><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider"><button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div><div id="indexPage"><div id="homeTab" data-index="0"><div class="sections"><h2>Jellyfin core Home</h2><div class="card">Core card</div></div></div><div id="favoritesTab" data-index="1">Favorites native</div></div></body></html>')
        page.add_style_tag(path=str(web / 'native-home-rows.css'))
        page.add_style_tag(path=str(web / 'home-tab-host.css'))
        page.add_script_tag(path=str(web / 'native-home-rows.js'))
        page.add_script_tag(path=str(web / 'home-tab-host.js'))
        page.evaluate('''id => {
            window.location.hash = '#/home';
            window.user = 'alice'; window.calls = []; window.opened = [];
            window.Emby = { Page: { showItem: item => opened.push(item) } };
            window.observers = [];
            window.IntersectionObserver = class {
                constructor(callback) { this.callback = callback; observers.push(this); }
                observe() {} disconnect() { this.closed = true; }
                fire(target) { this.callback([{ target, isIntersecting: true }]); }
            };
            window.api = {
                getCurrentUserId: () => user, accessToken: () => 'token', serverId: () => 'server',
                getUrl: (path) => '/jellyfin/' + path,
                getJSON: async url => {
                    calls.push(url);
                    if (url.endsWith('/Mode')) return { DiscoveryEnabled: true, HeroEnabled: true,
                        RowsEnabled: true, Rows: ['LatestMovies', 'Collections'] };
                    if (url.endsWith('/Hero')) return [{ Id: id, ImageType: 'Backdrop', ImageIndex: 0, ImageTag: 'a1' }];
                    if (url.endsWith('/Rows/LatestMovies')) return { Kind: 'LatestMovies', Items: [
                        { Id: id, Type: 'Movie', Name: 'The Violet Harbour', ImageTags: { Primary: 'ab12' } }] };
                    if (url.endsWith('/Rows/Collections')) return { Kind: 'Collections', Items: [] };
                    if (url.endsWith('/Items/' + id)) return { Id: id, Type: 'Movie' };
                    throw Error('Unexpected URL ' + url);
                }, fetch: async () => ({ ok: false })
            };
            window.host = ThreePicFinHomeHost.createHost({ document,
                loadFragment: async () => '<div>Fin content</div>',
                loadScript: async url => url.endsWith('native-home-rows.js') ? RowanNativeHomeRows :
                    url.endsWith('static-hero.js') ? { mount: root => {
                        root.textContent = 'Featured sentinel'; return () => root.replaceChildren();
                    } } :
                    { mount: () => { const cleanup = () => {}; cleanup.activate = () => {}; return cleanup; } } });
            window.mount = () => host.mount({ pane: document.querySelector('#homeTab'),
                favorites: document.querySelector('#favoritesTab'), apiClient: api,
                fingerprint: '12.1', userId: user, enabled: true });
        }''', item_id)
        assert page.evaluate('mount()') is True
        assert page.locator('#homeTab > .sections > h2').inner_text() == 'Jellyfin core Home'
        assert page.locator('#homeTab > .rowan-native-rows').count() == 1
        page.wait_for_selector('#homeTab > .threepic-fin-host__hero')
        assert page.locator('#homeTab > .threepic-fin-host__hero').inner_text() == 'Featured sentinel'
        assert page.locator('#homeTab > .threepic-fin-host__hero + .sections').count() == 1
        assert page.locator('#homeTab > .rowan-native-rows > section').count() == 2
        assert page.evaluate('calls.filter(x => x.includes("/Rows/")).length') == 0
        page.evaluate('''() => observers[0].fire(document.querySelector('.rowan-native-row'))''')
        page.wait_for_selector('.rowan-native-row__card img')
        assert page.locator('.rowan-native-row__card').first.inner_text() == 'The Violet Harbour'
        assert '/jellyfin/Items/' in page.locator('.rowan-native-row__card img').get_attribute('src')
        page.locator('.rowan-native-row__card').first.click()
        page.wait_for_function('opened.length === 1')
        assert page.evaluate('opened[0].Id') == item_id
        page.evaluate('''() => observers[0].fire(document.querySelectorAll('.rowan-native-row')[1])''')
        page.wait_for_function('calls.some(x => x.endsWith("/Rows/Collections"))')
        page.locator('.headerTabs > .threepic-fin-host__nav').click()
        assert page.locator('#homeTab > .rowan-native-rows').is_hidden()
        assert page.locator('#homeTab > .threepic-fin-host__hero').count() == 0
        page.locator('.emby-tab-button').first.click()
        page.wait_for_selector('#homeTab > .rowan-native-rows')
        page.wait_for_selector('#homeTab > .threepic-fin-host__hero')
        assert page.locator('#homeTab > .rowan-native-rows').is_visible()
        assert page.locator('#favoritesTab').inner_text() == 'Favorites native'
        assert page.evaluate('document.querySelector(".sections").parentElement.id') == 'homeTab'
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.evaluate('''() => { user = 'bob'; host.dispose(); }''')
        assert page.locator('.rowan-native-rows').count() == 0
        assert page.locator('.threepic-fin-host__hero').count() == 0
        assert page.evaluate('observers[0].closed') is True
        page.evaluate('''() => {
            api.getJSON = async url => {
                calls.push(url);
                if (url.endsWith('/Mode')) return { DiscoveryEnabled: false, HeroEnabled: false,
                    RowsEnabled: true, Rows: ['LatestMovies'] };
                if (url.endsWith('/Rows/LatestMovies')) return { Kind: 'LatestMovies', Items: [
                    { Id: 'abcdef0123456789abcdef0123456789', Type: 'Movie', Name: 'Bob film' }] };
                throw Error('Unexpected Bob URL ' + url);
            };
        }''')
        assert page.evaluate('mount()') is True
        assert page.locator('.threepic-fin-host__nav').count() == 0
        page.evaluate('''() => observers.at(-1).fire(document.querySelector('.rowan-native-row'))''')
        page.wait_for_function('document.querySelector(".rowan-native-row__card")?.textContent === "Bob film"')
        assert page.locator('#homeTab > .sections').count() == 1
        assert page.get_by_text('The Violet Harbour').count() == 0
        page.evaluate('host.dispose()')
        page.close()
    browser.close()
print('Populated native Home rows browser contract: pass')
