const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/home-tab-host.js'), 'utf8');
// The public host is exercised as shipped; no lab pin is substituted.
function testBuild(browser = { location: { hash: '#/home' } }) {
    vm.runInNewContext(source, browser);
    return browser.ThreePicFinHomeHost.createHost;
}
const createHost = options => testBuild()(options);

class Node {
    constructor(tag = 'div') { this.tagName = tag; this.children = []; this.handlers = {}; this.className = ''; this.hidden = false; this.parentNode = null; this.attributes = {}; this.textContent = ''; this.style = {}; }
    appendChild(node) { node.remove(); this.children.push(node); node.parentNode = this; return node; }
    insertBefore(node, before) { node.remove(); const at = this.children.indexOf(before); this.children.splice(at < 0 ? this.children.length : at, 0, node); node.parentNode = this; return node; }
    insertAdjacentElement(position, node) { assert.equal(position, 'afterend'); this.parentNode.insertBefore(node, this.parentNode.children[this.parentNode.children.indexOf(this) + 1]); }
    get parentElement() { return this.parentNode; }
    get classList() { return { contains: name => this.className.split(/\s+/).includes(name) }; }
    getBoundingClientRect() { return { width: 100 }; }
    remove() { if (this.parentNode) this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; }
    setAttribute(k, v) { this.attributes[k] = String(v); }
    removeAttribute(k) { delete this.attributes[k]; }
    getAttribute(k) { return this.attributes[k]; }
    addEventListener(k, fn) { (this.handlers[k] ??= []).push(fn); }
    removeEventListener(k, fn) { this.handlers[k] = (this.handlers[k] || []).filter(f => f !== fn); }
    click() { for (const fn of this.handlers.click || []) fn(); }
    focus() { this.focused = true; }
    keydown(key) { const event = { key, prevented: false, preventDefault() { this.prevented = true; } }; for (const fn of this.handlers.keydown || []) fn(event); return event; }
    contains(n) { return this === n || this.children.some(c => c.contains(n)); }
    querySelectorAll(k) {
        if (k === '.skinHeader .headerTabs') return this.querySelectorAll('.skinHeader').flatMap(n => n.querySelectorAll('.headerTabs'));
        const parts = k.replace(/^:scope > /, '').split(' > ');
        const match = (node, part) => {
            const tag = part.match(/^[a-z-]+/i)?.[0];
            const cls = part.match(/\.([\w-]+)/)?.[1];
            const attr = part.match(/\[([\w-]+)="([^"]+)"\]/);
            return (!tag || node.tagName === tag) && (!cls || node.classList.contains(cls)) &&
                (!attr || node.getAttribute(attr[1]) === attr[2]);
        };
        if (parts.length > 1) return this.children.filter(n => match(n, parts[0])).flatMap(n => n.querySelectorAll(':scope > ' + parts.slice(1).join(' > ')));
        if (k.startsWith(':scope > ')) return this.children.filter(n => match(n, parts[0]));
        return this.children.flatMap(n => [...(match(n, parts[0]) ? [n] : []), ...n.querySelectorAll(k)]);
    }
    querySelector(k) { return this.querySelectorAll(k)[0] || null; }
}
function fixture() {
    const root = new Node('html'), head = new Node('head'), body = new Node('body');
    root.appendChild(head); root.appendChild(body);
    const document = { head, documentElement: root, createElement: tag => new Node(tag),
        querySelectorAll: selector => root.querySelectorAll(selector), querySelector: selector => root.querySelector(selector) };
    const header = new Node(); header.className = 'headerTabs'; const skin = new Node(); skin.className = 'skinHeader';
    const native = new Node(); native.setAttribute('is', 'emby-tabs'); const slider = new Node(); slider.className = 'emby-tabs-slider';
    const home = new Node('button'), favoriteTab = new Node('button');
    home.className = 'emby-tab-button emby-tab-button-active'; home.textContent = 'Home'; home.setAttribute('data-index', '0');
    favoriteTab.className = 'emby-tab-button'; favoriteTab.textContent = 'Favorites'; favoriteTab.setAttribute('data-index', '1');
    slider.appendChild(home); slider.appendChild(favoriteTab); native.appendChild(slider); header.appendChild(native); skin.appendChild(header); body.appendChild(skin);
    const toolbar = new Node(); toolbar.className = 'MuiToolbar-root'; const stack = new Node(); stack.className = 'MuiStack-root';
    const logo = new Node('a'), favoritesLink = new Node('a'); logo.setAttribute('href', '#/'); favoritesLink.setAttribute('href', '#/home?tab=1');
    stack.appendChild(logo); stack.appendChild(favoritesLink); toolbar.appendChild(stack); body.appendChild(toolbar);
    const pane = new Node(), favorites = new Node(), sections = new Node(); sections.className = 'sections'; pane.appendChild(sections);
    body.appendChild(pane); body.appendChild(favorites);
    const calls = [], disposed = [];
    const api = { getUrl: route => `https://test.invalid/jellyfin/${route}`, getJSON: url => { calls.push(url); return Promise.resolve({}); } };
    const host = createHost({ document, loadFragment: async url => { calls.push(url); return '<div>Discovery</div>'; }, loadScript: async url => { calls.push(url); return { mount: (root, client) => { client.getJSON(client.getUrl('3picFin/Discovery')); return () => disposed.push(root); } }; } });
    const mount = (overrides = {}) => host.mount({ pane, favorites, apiClient: api, fingerprint: '12.1', userId: 'alice', ...overrides });
    return { document, pane, favorites, sections, calls, disposed, host, mount, home, favoriteTab, favoritesLink, toolbar, stack,
        nav: () => stack.querySelector('.threepic-fin-host__nav') };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test('mismatch and disabled gate leave native Home, Favorites and HSS untouched', async () => {
    const f = fixture();
    assert.equal(await f.mount({ fingerprint: 'wrong' }), false);
    assert.equal(await f.mount({ enabled: false }), false);
    assert.deepEqual(f.pane.children, [f.sections]);
    assert.deepEqual(f.favorites.children, []);
    assert.deepEqual(f.calls, []);
});
test('host refuses a caller fingerprint for an unsupported version', async () => {
    const browser = { module: { exports: {} } };
    vm.runInNewContext(source, browser);
    const f = fixture();
    let loaderCalled = false;
    const host = browser.ThreePicFinHomeHost.createHost({ document: f.document, expectedFingerprint: 'tested-hash',
        loadFragment: () => { loaderCalled = true; throw Error('must fail closed'); } });
    const api = { getUrl: x => x, getJSON: async () => ({}) };
    assert.equal(await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        userId: 'alice', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false }, fingerprint: '13.0' }), false);
    assert.equal(loaderCalled, false);
    assert.deepEqual(f.pane.children, [f.sections]);
});
test('native tabs retain indexes and Fin is a separate accessible action', async () => {
    const f = fixture(); assert.equal(await f.mount({ enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } }), true);
    const nav = f.nav(), panel = f.pane.children[1];
    assert.equal(nav.tagName, 'button'); assert.equal(nav.type, 'button');
    assert.equal(nav.textContent, '3pic Fin'); assert.equal(nav.getAttribute('aria-pressed'), 'false');
    assert.equal(f.home.getAttribute('data-index'), '0');
    assert.equal(f.favoriteTab.getAttribute('data-index'), '1');
    assert.equal(f.home.getAttribute('tabindex'), undefined);
    assert.equal(f.sections.getAttribute('role'), undefined);
    assert.equal(panel.getAttribute('aria-label'), '3pic Fin');
    nav.click(); assert.equal(nav.getAttribute('aria-pressed'), 'true');
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'discovery');
    f.stack.children[0].click(); assert.equal(nav.getAttribute('aria-pressed'), 'false');
    f.host.dispose();
    assert.equal(f.sections.getAttribute('id'), undefined);
    assert.equal(f.sections.getAttribute('role'), undefined);
    assert.equal(f.sections.getAttribute('aria-labelledby'), undefined);
    assert.equal(f.stack.children.length, 2);
});
test('Fin URL is canonical, remountable, and Back restores Home without a second panel', async () => {
    const f = fixture(), browser = { location: { hash: '#/home' } };
    const host = testBuild(browser)({ document: f.document, loadFragment: async () => '<div>Fin</div>',
        loadScript: async () => ({ mount: () => () => {} }) });
    const options = { pane: f.pane, favorites: f.favorites,
        apiClient: { getUrl: x => x, getJSON: async () => ({}) },
        fingerprint: '12.1', userId: 'alice', enabled: true,
        mode: { DiscoveryEnabled: true, HeroEnabled: false } };
    assert.equal(await host.mount(options), true);
    f.nav().click();
    assert.equal(browser.location.hash, '#/home?fin=1');
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'discovery');
    host.dispose();
    assert.equal(await host.mount(options), true);
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'discovery');
    assert.equal(f.nav().getAttribute('aria-pressed'), 'true');
    browser.location.hash = '#/home'; host.sync();
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'home');
    browser.location.hash = '#/home?fin=1'; host.sync();
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'discovery');
    f.nav().click(); assert.equal(browser.location.hash, '#/home');
    host.dispose();
});
test('hidden legacy tab header never becomes the stock navigation seam', async () => {
    const f = fixture();
    f.document.querySelector('.skinHeader').getBoundingClientRect = () => ({ width: 0 });
    f.toolbar.remove();
    assert.equal(await f.mount({ enabled: true, mode: { DiscoveryEnabled: true } }), false);
    assert.equal(f.document.querySelector('.threepic-fin-host__nav'), null);
});
test('failed nav seam removes an otherwise misleading Fin URL', async () => {
    const f = fixture(), browser = { location: { hash: '#/home?fin=1' } };
    f.toolbar.remove();
    f.document.querySelector('.skinHeader').getBoundingClientRect = () => ({ width: 0 });
    const host = testBuild(browser)({ document: f.document, loadFragment: async () => '<div>Fin</div>',
        loadScript: async () => ({ mount: () => () => {} }) });
    assert.equal(await host.mount({ pane: f.pane, favorites: f.favorites,
        apiClient: { getUrl: x => x, getJSON: async () => ({}) }, fingerprint: '12.1',
        userId: 'alice', enabled: true, mode: { DiscoveryEnabled: true } }), false);
    assert.equal(browser.location.hash, '#/home');
});
test('hero-only mode rejects a stale Fin deep link instead of showing Home under it', async () => {
    const f = fixture(), browser = { location: { hash: '#/home?fin=1' } };
    const host = testBuild(browser)({ document: f.document });
    const api = { getUrl: x => x, getJSON: async () => [], getCurrentUserId: () => 'alice',
        accessToken: () => 'token', fetch: async () => ({ok:false}) };
    assert.equal(await host.mount({pane:f.pane, favorites:f.favorites, apiClient:api,
        fingerprint:'12.1', userId:'alice', enabled:true, mode:{HeroEnabled:true}}), true);
    assert.equal(browser.location.hash, '#/home');
    host.dispose();
});
test('failed Fin fragment does not leave a misleading deep link', async () => {
    const f = fixture(), browser = { location: { hash: '#/home?fin=1' } };
    const host = testBuild(browser)({ document:f.document, loadFragment:async()=>{throw Error('offline');} });
    assert.equal(await host.mount({pane:f.pane, favorites:f.favorites,
        apiClient:{getUrl:x=>x, getJSON:async()=>({})}, fingerprint:'12.1',
        userId:'alice', enabled:true, mode:{DiscoveryEnabled:true}}), false);
    assert.equal(browser.location.hash, '#/home');
});
test('enabled hero reserves a slot before slide fetch settles and releases empty results', async () => {
    const f = fixture(); let release;
    const browser = { location: { hash: '#/home' } };
    const host = testBuild(browser)({ document: f.document, loadScript: async () => ({ mount: () => () => {} }) });
    const api = { getUrl: x => x, getJSON: () => new Promise(resolve => release = resolve),
        getCurrentUserId: () => 'alice', accessToken: () => 'token', fetch: async () => ({ ok: false }) };
    assert.equal(await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        fingerprint: '12.1', userId: 'alice', enabled: true, mode: { HeroEnabled: true } }), true);
    assert.equal(f.pane.children[0].className, 'threepic-fin-host__hero');
    assert.equal(f.pane.children[1], f.sections);
    release([]); await tick();
    assert.deepEqual(f.pane.children, [f.sections]);
    host.dispose();
});

test('host reserves the same responsive block size as the rendered hero before data arrives', async () => {
    const css = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/static-hero.css'), 'utf8');
    const desktop = css.match(/\.rowan-static-hero\{[^}]*min-height:([^;]+)/)?.[1];
    const mobile = css.match(/@media\(max-width:600px\)\{\.rowan-static-hero\{[^}]*min-height:([^;]+)/)?.[1];
    assert.ok(desktop && mobile, 'hero CSS must define both responsive reservations');
    for (const [width, expected] of [[320, mobile], [390, mobile], [1280, desktop]]) {
        const f = fixture(); let release;
        const browser = { location: { hash: '#/home' }, matchMedia: () => ({ matches: width <= 600 }) };
        const host = testBuild(browser)({ document: f.document,
            loadScript: async () => ({ mount: () => () => {} }) });
        const api = { getUrl: x => x, getJSON: () => new Promise(resolve => release = resolve),
            getCurrentUserId: () => 'alice', accessToken: () => 'token', fetch: async () => ({ ok: false }) };
        await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
            fingerprint: '12.1', userId: 'alice', enabled: true, mode: { HeroEnabled: true } });
        const slot = f.pane.children[0];
        assert.equal(slot.style.height.replaceAll(' ', ''), expected.replaceAll(' ', ''), `${width}px pending slot`);
        assert.equal(slot.style.minHeight.replaceAll(' ', ''), expected.replaceAll(' ', ''), `${width}px pending minimum`);
        release([]); await tick();
        host.dispose();
    }
});

test('dispose while fragment pending prevents late script or DOM injection', async () => {
    const f = fixture(); let release; const pending = new Promise(r => release = r);
    const calls = [];
    const host = createHost({ document: f.document, loadFragment: () => { calls.push('fragment'); return pending; },
        loadScript: () => { calls.push('script'); return { mount() { calls.push('mount'); } }; } });
    const promise = host.mount({ pane: f.pane, favorites: f.favorites, apiClient: { getUrl: x => x, getJSON: async () => ({}) },
        fingerprint: '12.1', userId: 'bob', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } });
    assert.deepEqual(calls, ['fragment']);
    host.dispose(); release('<div>late</div>');
    assert.equal(await promise, false);
    assert.deepEqual(calls, ['fragment']);
    assert.deepEqual(f.pane.children, [f.sections]);
    assert.equal(f.document.head.children.length, 0);
});
test('concurrent mounts leave only latest view and cleanup', async () => {
    const f = fixture(); let release; const pending = new Promise(r => release = r);
    const host = createHost({ document: f.document, loadFragment: () => pending,
        loadScript: () => ({ mount: () => () => f.disposed.push('latest') }) });
    const opts = { pane: f.pane, favorites: f.favorites, apiClient: { getUrl: x => x, getJSON: async () => ({}) },
        fingerprint: '12.1', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } };
    const first = host.mount({ ...opts, userId: 'alice' });
    const second = host.mount({ ...opts, userId: 'bob' });
    release('<div>Discovery</div>');
    assert.equal(await first, false); assert.equal(await second, true);
    assert.equal(f.pane.children.length, 2);
    host.dispose(); assert.deepEqual(f.disposed, ['latest']);
    assert.deepEqual(f.pane.children, [f.sections]);
});
test('teardown restores preexisting native section attributes across user switch', async () => {
    const f = fixture();
    f.sections.setAttribute('id', 'native-hss');
    f.sections.setAttribute('role', 'region');
    f.sections.setAttribute('aria-labelledby', 'native-heading');
    await f.mount({ enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } });
    assert.equal(f.sections.getAttribute('id'), 'native-hss');
    assert.equal(f.nav().getAttribute('aria-pressed'), 'false');
    await f.mount({ enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false }, userId: 'bob' });
    assert.equal(f.disposed.length, 1);
    f.host.dispose();
    assert.equal(f.sections.getAttribute('id'), 'native-hss');
    assert.equal(f.sections.getAttribute('role'), 'region');
    assert.equal(f.sections.getAttribute('aria-labelledby'), 'native-heading');
});
test('throwing discovery cleanup cannot leave host content on user switch', async () => {
    const f = fixture();
    f.sections.setAttribute('id', 'native-hss');
    f.sections.setAttribute('role', 'region');
    f.sections.setAttribute('aria-labelledby', 'native-heading');
    let mounts = 0, oldNav, oldPanel, oldStyles;
    const host = createHost({ document: f.document,
        loadFragment: async () => {
            if (mounts === 1) {
                assert.deepEqual(f.pane.children, [f.sections], 'old user DOM removed before loading new user');
                assert.deepEqual(f.document.head.children, [], 'old user styles removed before loading new user');
                assert.equal(f.sections.getAttribute('id'), 'native-hss');
                assert.equal(f.sections.getAttribute('role'), 'region');
                assert.equal(f.sections.getAttribute('aria-labelledby'), 'native-heading');
                assert.equal(f.pane.getAttribute('data-threepic-fin-view'), undefined);
            }
            return '<div>Discovery</div>';
        },
        loadScript: async () => ({ mount: () => {
            ++mounts;
            return () => { throw Error('discovery cleanup failed'); };
        } }) });
    const opts = { pane: f.pane, favorites: f.favorites,
        apiClient: { getUrl: x => x, getJSON: async () => ({}) },
        fingerprint: '12.1', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } };
    assert.equal(await host.mount({ ...opts, userId: 'alice' }), true);
    oldNav = f.nav(); oldPanel = f.pane.children[1];
    oldStyles = [...f.document.head.children];
    assert.equal(await host.mount({ ...opts, userId: 'bob' }), true);
    assert.equal(mounts, 2);
    assert.equal(oldNav.parentNode, null);
    assert.equal(oldPanel.parentNode, null);
    assert.ok(oldStyles.every(style => style.parentNode === null));
    assert.equal(f.stack.children[0].handlers.click.length, 1, 'only the new user Home listener remains');
    assert.doesNotThrow(() => host.dispose());
    assert.deepEqual(f.pane.children, [f.sections]);
    assert.deepEqual(f.document.head.children, []);
    assert.equal(f.sections.getAttribute('id'), 'native-hss');
    assert.equal(f.sections.getAttribute('role'), 'region');
    assert.equal(f.sections.getAttribute('aria-labelledby'), 'native-heading');
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), undefined);
    assert.equal(await host.mount({ ...opts, userId: 'charlie' }), true);
    assert.equal(mounts, 3);
    assert.doesNotThrow(() => host.dispose());
});
test('mount and remount own a nav action, panel and one HSS sections node', async () => {
    const f = fixture();
    assert.equal(await f.mount({ enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } }), true);
    assert.equal(await f.mount({ enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } }), true);
    assert.equal(f.pane.querySelectorAll('.sections').length, 1);
    assert.equal(f.sections.parentNode, f.pane);
    assert.equal(f.favorites.children.length, 0);
    assert.equal(f.pane.children.length, 2);
    assert.equal(f.pane.children[0], f.sections);
    assert.equal(f.stack.children.length, 3);
    assert.equal(f.nav().textContent, '3pic Fin');
    f.nav().click();
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'discovery');
    assert.equal(f.sections.hidden, false);
    f.stack.children[0].click();
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'home');
    assert.equal(f.nav().getAttribute('aria-pressed'), 'false');
    assert.ok(f.document.head.children.some(x => x.href === 'https://test.invalid/jellyfin/3picFin/Web/discovery.css'));
    assert.ok(f.document.head.children.some(x => x.href === 'https://test.invalid/jellyfin/3picFin/Web/home-tab-host.css'));
    assert.ok(f.calls.includes('https://test.invalid/jellyfin/3picFin/Discovery'));
    f.host.dispose();
    assert.deepEqual(f.pane.children, [f.sections]);
    assert.equal(f.document.head.children.length, 0);
    assert.equal(f.disposed.length, 1);
});
test('Home mount defers the bundled Discovery request until Fin selection', async () => {
    const f = fixture();
    const requests = [];
    const api = { getUrl: x => x, getJSON: url => { requests.push(url); return Promise.resolve({}); } };
    const host = createHost({ document: f.document, loadFragment: async () => '<div>Discovery</div>',
        loadScript: async () => ({ mount(root, client, options) {
            assert.equal(options.deferInitialLoad, true);
            const cleanup = () => {};
            cleanup.activate = () => client.getJSON(client.getUrl('3picFin/Discovery'));
            return cleanup;
        } }) });
    assert.equal(await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        fingerprint: '12.1', userId: 'alice', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } }), true);
    assert.deepEqual(requests, []);
    f.nav().click();
    assert.deepEqual(requests, ['3picFin/Discovery']);
    f.stack.children[0].click(); f.nav().click();
    assert.deepEqual(requests, ['3picFin/Discovery'], 'host does not re-activate on each selection');
    host.dispose();
});
test('route teardown, user switch and late assets cannot leak a previous user', async () => {
    const f = fixture(); await f.mount({ enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } });
    await f.mount({ enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false }, userId: 'bob' });
    assert.equal(f.disposed.length, 1);
    assert.equal(f.pane.children.length, 2);
    f.host.dispose();
    let release; const pending = new Promise(r => release = r);
    const late = createHost({ document: f.document, loadFragment: () => pending, loadScript: () => { throw Error('late script'); } });
    const promise = late.mount({ pane: f.pane, favorites: f.favorites, apiClient: { getUrl: p => p, getJSON: async () => ({}) }, fingerprint: '12.1', userId: 'bob', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } });
    assert.ok(release, 'loader must actually be pending');
    late.dispose(); release('<div>late</div>');
    assert.equal(await promise, false); await tick();
    assert.deepEqual(f.pane.children, [f.sections]);
});
test('host rechecks title before native item navigation and rejects stale user, route or match', async () => {
    const f = fixture(), pending = (() => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; })();
    const guid = '01234567-89ab-cdef-0123-456789abcdef';
    let user = 'alice', hash = '#/home', callback, result = pending.promise;
    const shown = [];
    const browser = { location: { get hash() { return hash; }, set hash(value) { hash = value; } }, Emby: { Page: { showItem: item => shown.push(item) } } };
    const host = testBuild(browser)({ document: { ...f.document, documentElement: { contains: node => f.pane.contains(node) } },
        loadFragment: async () => '<div>Discovery</div>', loadScript: async () => ({ mount: (_root, _api, bridge) => { callback = bridge.openItem; return () => {}; } }) });
    const api = { getUrl: (route, params) => `https://test.invalid/jellyfin/${route}?${new URLSearchParams(params)}`,
        getJSON: () => result, getCurrentUserId: () => user, serverId: () => 'server-a' };
    const options = { pane: f.pane, favorites: f.favorites, apiClient: api, fingerprint: '12.1', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: false } };
    assert.equal(await host.mount({ ...options, userId: user }), true);
    f.nav().click();
    const attempt = callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid });
    user = 'bob'; pending.resolve({ MediaType: 'movie', TmdbId: 9, LibraryItemId: guid });
    assert.equal(await attempt, false); assert.equal(shown.length, 0);
    hash = '#/home'; await host.mount({ ...options, userId: user }); f.nav().click();
    result = Promise.resolve({ MediaType: 'movie', TmdbId: 9, LibraryItemId: guid });
    assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), true);
    assert.equal(shown[0].Id, guid); assert.equal(shown[0].Type, 'Movie'); assert.equal(shown[0].ServerId, 'server-a');
    result = Promise.resolve({ MediaType: 'movie', TmdbId: 9, LibraryItemId: 'fedcba98-7654-3210-fedc-ba9876543210' });
    assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), false);
    hash = '#/favorites'; assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), false);
    hash = '#/home'; host.dispose(); assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), false);
    assert.equal(shown.length, 1);
});

test('optional hero waits for slides on Home and cleans up without moving HSS or Favorites', async () => {
    const f = fixture(), events = [], id = '01234567-89ab-cdef-0123-456789abcdef';
    let release;
    const pending = new Promise(resolve => release = resolve);
    const api = { getUrl: route => route, getJSON: url => { events.push(url); return url === 'Rowan/Home/Hero' ? pending : Promise.resolve({}); },
        getCurrentUserId: () => 'alice', accessToken: () => 'token', fetch: async () => ({ ok: false }) };
    const browser = { location: { hash: '#/home' } };
    const host = testBuild(browser)({ document: { ...f.document, documentElement: { contains: x => f.pane.contains(x) } },
        loadFragment: async () => '', loadScript: async url => {
        events.push(url);
        return url.endsWith('static-hero.js') ? { mount(root, client, options) {
            events.push(['hero-mount', options.slides]); root.appendChild(new Node('section'));
            return () => { events.push('hero-dispose'); root.children[0]?.remove(); };
        } } : { mount: () => () => {} };
    } });
    assert.equal(await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        fingerprint: '12.1', userId: 'alice', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: true } }), true);
    assert.equal(events.includes('Rowan/Home/Hero'), true);
    assert.equal(events.some(x => String(x).endsWith('static-hero.js')), false);
    f.nav().click();
    release([{ Id: id, ImageType: 'Backdrop', ImageIndex: 0, ImageTag: 'a1' }]); await tick();
    assert.equal(events.some(x => String(x).endsWith('static-hero.js')), false);
    f.stack.children[0].click(); await tick();
    assert.equal(events.some(x => String(x).endsWith('static-hero.js')), true);
    assert.equal(f.pane.children[0].className, 'threepic-fin-host__hero');
    assert.equal(f.pane.children[1], f.sections);
    assert.equal(f.sections.parentNode, f.pane);
    assert.equal(f.favorites.children.length, 0);
    assert.equal(f.document.head.children.some(x => x.href?.endsWith('static-hero.css')), true);
    f.nav().click();
    assert.equal(events.includes('hero-dispose'), true);
    assert.equal(f.pane.children.length, 2);
    host.dispose(); assert.deepEqual(f.pane.children, [f.sections]);
});

test('hero fails closed for empty, malformed and stale-user replies without loading assets', async () => {
    for (const slides of [[], [{ Id: '../bad', ImageTag: 'a1', ImageType: 'Backdrop', ImageIndex: 0 }]]) {
        const f = fixture(), assets = [];
        const host = createHost({ document: f.document, loadFragment: async () => '',
            loadScript: async url => { assets.push(url); return { mount: () => () => {} }; } });
        const api = { getUrl: x => x, getJSON: async () => slides, getCurrentUserId: () => 'alice', accessToken: () => 'token' };
        await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
            fingerprint: '12.1', userId: 'alice', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: true } }); await tick();
        assert.equal(assets.some(x => x.endsWith('static-hero.js')), false);
        assert.equal(f.pane.children.length, 2); host.dispose();
    }
});

test('hero open uses fresh user-scoped item and refuses stale route, user and mismatched response', async () => {
    const f = fixture(), id = '01234567-89ab-cdef-0123-456789abcdef', shown = [];
    let hash = '#/home', user = 'alice', callback, fresh = { Id: id, Type: 'Movie' };
    const browser = { location: { get hash() { return hash; } }, Emby: { Page: { showItem: x => shown.push(x) } } };
    const host = testBuild(browser)({ document: { ...f.document, documentElement: { contains: x => f.pane.contains(x) } },
        loadFragment: async () => '', loadScript: async url => url.endsWith('static-hero.js')
            ? { mount: (_root, _api, options) => { callback = options.openItem; return () => {}; } }
            : { mount: () => () => {} } });
    const api = { getUrl: route => route, getJSON: async route => route === 'Rowan/Home/Hero'
        ? [{ Id: id, ImageType: 'Backdrop', ImageIndex: 0, ImageTag: 'a1' }] : fresh,
        getCurrentUserId: () => user, accessToken: () => 'token', fetch: async () => ({ ok: false }), serverId: () => 'server-a' };
    await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        fingerprint: '12.1', userId: user, enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: true } }); await tick();
    assert.equal(await callback('../bad'), false);
    assert.equal(await callback(id), true);
    assert.equal(shown[0].Id, id); assert.equal(shown[0].ServerId, 'server-a');
    fresh = { Id: id, Type: 'Series' }; assert.equal(await callback(id), true);
    assert.equal(shown[1].Type, 'Series');
    fresh = { Id: 'fedcba98-7654-3210-fedc-ba9876543210', Type: 'Movie' };
    assert.equal(await callback(id), false);
    hash = '#/favorites'; assert.equal(await callback(id), false);
    hash = '#/home'; user = 'bob'; assert.equal(await callback(id), false);
    host.dispose(); assert.equal(await callback(id), false);
    assert.equal(shown.length, 2);
});

test('pending hero script cannot mount after teardown or user switch', async () => {
    const f = fixture(), id = '01234567-89ab-cdef-0123-456789abcdef';
    let release, user = 'alice', mounts = 0;
    const pending = new Promise(resolve => release = resolve);
    const browser = { location: { hash: '#/home' } };
    const host = testBuild(browser)({ document: { ...f.document, documentElement: { contains: x => f.pane.contains(x) } },
        loadFragment: async () => '', loadScript: async url => url.endsWith('static-hero.js') ? pending : { mount: () => () => {} } });
    const api = { getUrl: x => x, getJSON: async () => [{ Id: id, ImageType: 'Backdrop', ImageIndex: 0, ImageTag: 'a1' }],
        getCurrentUserId: () => user, accessToken: () => 'token', fetch: async () => ({ ok: false }) };
    const options = { pane: f.pane, favorites: f.favorites, apiClient: api,
        fingerprint: '12.1', enabled: true, mode: { DiscoveryEnabled: true, HeroEnabled: true } };
    await host.mount({ ...options, userId: user }); await tick();
    user = 'bob'; await host.mount({ ...options, userId: user });
    host.dispose();
    release({ mount: () => { mounts++; return () => {}; } }); await tick();
    assert.equal(mounts, 0);
    assert.deepEqual(f.pane.children, [f.sections]);
    assert.deepEqual(f.document.head.children, []);
});

test('narrow layout contract keeps buttons accessible without native tab indexes', () => {
    const css = require('node:fs').readFileSync(require('node:path').join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/home-tab-host.css'), 'utf8');
    assert.match(css, /max-width:\s*360px/);
    assert.match(css, /min-height:\s*44px/);
    assert.match(css, /:focus-visible/);
    assert.match(css, /\[data-threepic-fin-view="discovery"\] > \.sections/);
    assert.doesNotMatch(css, /\.tabContent|nth-child|^\.sections\s*\{/m);
});

for (const discovery of [false, true]) test(`hero-only mode discovery=${discovery} respects native Home and transitions`, async () => {
    const f = fixture(), calls = [], id = '01234567-89ab-cdef-0123-456789abcdef';
    let user = 'alice', cleanup = 0;
    const api = { getUrl: route => route, getJSON: async url => {
        calls.push(url);
        if (url === 'Rowan/Home/Mode') return { DiscoveryEnabled: discovery, HeroEnabled: true };
        if (url === 'Rowan/Home/Hero') return [{ Id: id, ImageType: 'Backdrop', ImageIndex: 0, ImageTag: 'ab' }];
        return {};
    }, getCurrentUserId: () => user, accessToken: () => 'token', fetch: async () => ({ ok: false }) };
    const browser = { location: { hash: '#/home' } };
    const host = testBuild(browser)({ document: { ...f.document, documentElement: { contains: x => f.pane.contains(x) } },
        loadFragment: async url => { calls.push(url); return '<div>Discovery</div>'; },
        loadScript: async url => { calls.push(url); return url.endsWith('static-hero.js')
            ? { mount: root => { root.appendChild(new Node()); return () => { cleanup++; }; } }
            : { mount: () => () => { cleanup++; } }; } });
    const options = { pane: f.pane, favorites: f.favorites, apiClient: api, fingerprint: '12.1', userId: 'alice', enabled: true };
    assert.equal(await host.mount(options), true); await tick();
    assert.equal(calls.includes('Rowan/Home/Hero'), true);
    assert.equal(calls.some(x => String(x).endsWith('static-hero.js')), true);
    assert.equal(calls.some(x => String(x).includes('discovery.')), discovery);
    assert.equal(f.sections.parentNode, f.pane);
    assert.equal(f.favorites.children.length, 0);
    assert.equal(!!f.nav(), discovery);
    if (discovery) {
        f.nav().click(); assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'discovery');
        assert.equal(cleanup, 1);
        f.stack.children[0].click(); await tick(); assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'home');
    }
    user = 'bob'; host.dispose();
    assert.deepEqual(f.pane.children, [f.sections]);
    assert.equal(f.document.head.children.length, 0);
});

test('disabled mode fails closed before loading any feature assets', async () => {
    const f = fixture(), calls = [];
    const api = { getUrl: x => x, getJSON: async x => { calls.push(x); return { DiscoveryEnabled: false, HeroEnabled: false }; }, getCurrentUserId: () => 'alice' };
    const host = createHost({ document: f.document, loadFragment: async x => { calls.push(x); return ''; }, loadScript: async x => { calls.push(x); return {}; } });
    assert.equal(await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        userId: 'alice', fingerprint: '12.1', enabled: true }), false);
    assert.deepEqual(calls, ['Rowan/Home/Mode']);
    assert.deepEqual(f.pane.children, [f.sections]);
});

test('late mode reply from previous user cannot mount a hero', async () => {
    const f = fixture(), calls = []; let release, user = 'alice';
    const api = { getUrl: x => x, getJSON: x => { calls.push(x); return new Promise(r => release = r); }, getCurrentUserId: () => user };
    const host = createHost({ document: f.document, loadFragment: async x => { calls.push(x); return ''; }, loadScript: async x => { calls.push(x); return {}; } });
    const pending = host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        userId: user, fingerprint: '12.1', enabled: true });
    user = 'bob'; release({ DiscoveryEnabled: false, HeroEnabled: true });
    assert.equal(await pending, false);
    assert.deepEqual(calls, ['Rowan/Home/Mode']);
    assert.deepEqual(f.pane.children, [f.sections]);
});

test('mode changes across route teardown replace hero-only with Home/Fin and back', async () => {
    const f = fixture(), calls = []; let discovery = false;
    const api = { getUrl: x => x, getJSON: async x => {
        calls.push(x);
        return x === 'Rowan/Home/Mode' ? { DiscoveryEnabled: discovery, HeroEnabled: true } : [];
    }, getCurrentUserId: () => 'alice', accessToken: () => 'token' };
    const host = createHost({ document: f.document, loadFragment: async x => { calls.push(x); return '<div>Fin</div>'; },
        loadScript: async x => { calls.push(x); return { mount: () => () => {} }; } });
    const options = { pane: f.pane, favorites: f.favorites, apiClient: api, userId: 'alice', fingerprint: '12.1', enabled: true };
    assert.equal(await host.mount(options), true);
    assert.equal(!!f.nav(), false);
    host.dispose(); discovery = true;
    assert.equal(await host.mount(options), true);
    assert.equal(!!f.nav(), true);
    assert.equal(calls.filter(x => x.endsWith('discovery.html')).length, 1);
    host.dispose(); discovery = false;
    assert.equal(await host.mount(options), true);
    assert.equal(!!f.nav(), false);
    assert.equal(calls.filter(x => x.endsWith('discovery.html')).length, 1);
    host.dispose(); assert.deepEqual(f.pane.children, [f.sections]);
});
