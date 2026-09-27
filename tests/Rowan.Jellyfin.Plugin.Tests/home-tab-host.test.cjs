const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/home-tab-host.js'), 'utf8');
// Explicit test-only build; production source remains unpinned.
function testBuild(browser = {}) {
    const pinned = source.replace('const VERIFIED_WEB_BUNDLE_SHA256 = null;',
        "const VERIFIED_WEB_BUNDLE_SHA256 = 'tested-hash';");
    assert.notEqual(pinned, source);
    vm.runInNewContext(pinned, browser);
    return browser.ThreePicFinHomeHost.createHost;
}
const createHost = testBuild();

class Node {
    constructor(tag = 'div') { this.tagName = tag; this.children = []; this.handlers = {}; this.className = ''; this.hidden = false; this.parentNode = null; this.attributes = {}; this.textContent = ''; }
    appendChild(node) { node.remove(); this.children.push(node); node.parentNode = this; return node; }
    insertBefore(node, before) { node.remove(); const at = this.children.indexOf(before); this.children.splice(at < 0 ? this.children.length : at, 0, node); node.parentNode = this; return node; }
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
    querySelectorAll(k) { return this.children.flatMap(c => [...(k === '.sections' && c.className === 'sections' ? [c] : []), ...c.querySelectorAll(k)]); }
}
function fixture() {
    const document = { head: new Node('head'), createElement: tag => new Node(tag) };
    const pane = new Node(), favorites = new Node(), sections = new Node(); sections.className = 'sections'; pane.appendChild(sections);
    const calls = [], disposed = [];
    const api = { getUrl: route => `https://test.invalid/jellyfin/${route}`, getJSON: url => { calls.push(url); return Promise.resolve({}); } };
    const host = createHost({ document, loadFragment: async url => { calls.push(url); return '<div>Discovery</div>'; }, loadScript: async url => { calls.push(url); return { mount: (root, client) => { client.getJSON(client.getUrl('3picFin/Discovery')); return () => disposed.push(root); } }; } });
    const mount = (overrides = {}) => host.mount({ pane, favorites, apiClient: api, fingerprint: 'tested-hash', userId: 'alice', ...overrides });
    return { document, pane, favorites, sections, calls, disposed, host, mount };
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
test('browser build cannot override the unpinned production fingerprint', async () => {
    const browser = { module: { exports: {} } };
    vm.runInNewContext(source, browser);
    const f = fixture();
    let loaderCalled = false;
    const host = browser.ThreePicFinHomeHost.createHost({ document: f.document, expectedFingerprint: 'tested-hash',
        loadFragment: () => { loaderCalled = true; throw Error('must fail closed'); } });
    const api = { getUrl: x => x, getJSON: async () => ({}) };
    assert.equal(await host.mount({ pane: f.pane, favorites: f.favorites, apiClient: api,
        userId: 'alice', enabled: true, fingerprint: 'tested-hash' }), false);
    assert.equal(loaderCalled, false);
    assert.deepEqual(f.pane.children, [f.sections]);
});
test('tabs expose panels and roving arrow/home/end keyboard navigation', async () => {
    const f = fixture(); await f.mount({ enabled: true });
    const [home, fin] = f.pane.children[0].children;
    const panel = f.pane.children[2];
    assert.equal(home.getAttribute('aria-controls'), f.sections.getAttribute('id'));
    assert.equal(fin.getAttribute('aria-controls'), panel.getAttribute('id'));
    assert.ok(home.getAttribute('id'));
    assert.ok(fin.getAttribute('id'));
    assert.equal(f.sections.getAttribute('aria-labelledby'), home.getAttribute('id'));
    assert.equal(panel.getAttribute('aria-labelledby'), fin.getAttribute('id'));
    assert.equal(f.sections.getAttribute('role'), 'tabpanel');
    assert.equal(home.getAttribute('tabindex'), '0');
    assert.equal(fin.getAttribute('tabindex'), '-1');
    assert.equal(home.keydown('ArrowRight').prevented, true);
    assert.equal(fin.getAttribute('aria-selected'), 'true');
    assert.equal(fin.getAttribute('tabindex'), '0');
    assert.ok(fin.focused);
    fin.keydown('ArrowRight'); assert.equal(home.getAttribute('aria-selected'), 'true');
    fin.keydown('Home'); assert.equal(home.getAttribute('aria-selected'), 'true');
    home.keydown('End'); assert.equal(fin.getAttribute('aria-selected'), 'true');
    fin.keydown('ArrowLeft'); assert.equal(home.getAttribute('aria-selected'), 'true');
    f.host.dispose();
    assert.equal(f.sections.getAttribute('id'), undefined);
    assert.equal(f.sections.getAttribute('role'), undefined);
    assert.equal(f.sections.getAttribute('aria-labelledby'), undefined);
});
test('dispose while fragment pending prevents late script or DOM injection', async () => {
    const f = fixture(); let release; const pending = new Promise(r => release = r);
    const calls = [];
    const host = createHost({ document: f.document, loadFragment: () => { calls.push('fragment'); return pending; },
        loadScript: () => { calls.push('script'); return { mount() { calls.push('mount'); } }; } });
    const promise = host.mount({ pane: f.pane, favorites: f.favorites, apiClient: { getUrl: x => x, getJSON: async () => ({}) },
        fingerprint: 'tested-hash', userId: 'bob', enabled: true });
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
        fingerprint: 'tested-hash', enabled: true };
    const first = host.mount({ ...opts, userId: 'alice' });
    const second = host.mount({ ...opts, userId: 'bob' });
    release('<div>Discovery</div>');
    assert.equal(await first, false); assert.equal(await second, true);
    assert.equal(f.pane.children.length, 3);
    host.dispose(); assert.deepEqual(f.disposed, ['latest']);
    assert.deepEqual(f.pane.children, [f.sections]);
});
test('teardown restores preexisting native section attributes across user switch', async () => {
    const f = fixture();
    f.sections.setAttribute('id', 'native-hss');
    f.sections.setAttribute('role', 'region');
    f.sections.setAttribute('aria-labelledby', 'native-heading');
    await f.mount({ enabled: true });
    assert.equal(f.pane.children[0].children[0].getAttribute('aria-controls'), 'native-hss');
    await f.mount({ enabled: true, userId: 'bob' });
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
    let mounts = 0, oldTabs, oldPanel, oldStyles;
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
        fingerprint: 'tested-hash', enabled: true };
    assert.equal(await host.mount({ ...opts, userId: 'alice' }), true);
    [oldTabs, , oldPanel] = f.pane.children;
    oldStyles = [...f.document.head.children];
    assert.equal(await host.mount({ ...opts, userId: 'bob' }), true);
    assert.equal(mounts, 2);
    assert.equal(oldTabs.parentNode, null);
    assert.equal(oldPanel.parentNode, null);
    assert.ok(oldStyles.every(style => style.parentNode === null));
    assert.deepEqual(oldTabs.children[0].handlers.click, []);
    assert.deepEqual(oldTabs.children[1].handlers.keydown, []);
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
test('mount and remount own only inner tabs and one HSS sections node', async () => {
    const f = fixture();
    assert.equal(await f.mount({ enabled: true }), true);
    assert.equal(await f.mount({ enabled: true }), true);
    assert.equal(f.pane.querySelectorAll('.sections').length, 1);
    assert.equal(f.sections.parentNode, f.pane);
    assert.equal(f.favorites.children.length, 0);
    assert.equal(f.pane.children.length, 3);
    const tabs = f.pane.children[0];
    assert.equal(tabs.children.map(x => x.textContent).join(','), 'Home,3pic Fin');
    tabs.children[1].click();
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'discovery');
    assert.equal(f.sections.hidden, false);
    tabs.children[0].click();
    assert.equal(f.pane.getAttribute('data-threepic-fin-view'), 'home');
    assert.ok(tabs.children[0].focused);
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
        fingerprint: 'tested-hash', userId: 'alice', enabled: true }), true);
    assert.deepEqual(requests, []);
    const [home, fin] = f.pane.children[0].children;
    fin.click();
    assert.deepEqual(requests, ['3picFin/Discovery']);
    home.click(); fin.click();
    assert.deepEqual(requests, ['3picFin/Discovery'], 'host does not re-activate on each selection');
    host.dispose();
});
test('route teardown, user switch and late assets cannot leak a previous user', async () => {
    const f = fixture(); await f.mount({ enabled: true });
    await f.mount({ enabled: true, userId: 'bob' });
    assert.equal(f.disposed.length, 1);
    assert.equal(f.pane.children.length, 3);
    f.host.dispose();
    let release; const pending = new Promise(r => release = r);
    const late = createHost({ document: f.document, loadFragment: () => pending, loadScript: () => { throw Error('late script'); } });
    const promise = late.mount({ pane: f.pane, favorites: f.favorites, apiClient: { getUrl: p => p, getJSON: async () => ({}) }, fingerprint: 'tested-hash', userId: 'bob', enabled: true });
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
    const browser = { location: { get hash() { return hash; } }, Emby: { Page: { showItem: item => shown.push(item) } } };
    const host = testBuild(browser)({ document: { ...f.document, documentElement: { contains: node => f.pane.contains(node) } },
        loadFragment: async () => '<div>Discovery</div>', loadScript: async () => ({ mount: (_root, _api, bridge) => { callback = bridge.openItem; return () => {}; } }) });
    const api = { getUrl: (route, params) => `https://test.invalid/jellyfin/${route}?${new URLSearchParams(params)}`,
        getJSON: () => result, getCurrentUserId: () => user, serverId: () => 'server-a' };
    const options = { pane: f.pane, favorites: f.favorites, apiClient: api, fingerprint: 'tested-hash', enabled: true };
    assert.equal(await host.mount({ ...options, userId: user }), true);
    f.pane.children[0].children[1].click();
    const attempt = callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid });
    user = 'bob'; pending.resolve({ MediaType: 'movie', TmdbId: 9, LibraryItemId: guid });
    assert.equal(await attempt, false); assert.equal(shown.length, 0);
    await host.mount({ ...options, userId: user }); f.pane.children[0].children[1].click();
    result = Promise.resolve({ MediaType: 'movie', TmdbId: 9, LibraryItemId: guid });
    assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), true);
    assert.equal(shown[0].Id, guid); assert.equal(shown[0].Type, 'Movie'); assert.equal(shown[0].ServerId, 'server-a');
    result = Promise.resolve({ MediaType: 'movie', TmdbId: 9, LibraryItemId: 'fedcba98-7654-3210-fedc-ba9876543210' });
    assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), false);
    hash = '#/favorites'; assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), false);
    hash = '#/home'; host.dispose(); assert.equal(await callback({ mediaType: 'movie', mediaId: 9, libraryItemId: guid }), false);
    assert.equal(shown.length, 1);
});

test('narrow layout contract keeps buttons accessible without native tab indexes', () => {
    const css = require('node:fs').readFileSync(require('node:path').join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/home-tab-host.css'), 'utf8');
    assert.match(css, /max-width:\s*360px/);
    assert.match(css, /min-height:\s*44px/);
    assert.match(css, /:focus-visible/);
    assert.match(css, /\[data-threepic-fin-view="discovery"\] > \.sections/);
    assert.doesNotMatch(css, /\.tabContent|nth-child|^\.sections\s*\{/m);
});
