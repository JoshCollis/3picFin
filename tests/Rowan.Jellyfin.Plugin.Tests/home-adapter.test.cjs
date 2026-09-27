const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const crypto = require('node:crypto');
const { createAdapter, resolveHomeNodes } = require('../../src/Rowan.Jellyfin.Plugin/Web/home-adapter.js');

const body = Buffer.from('tested Home distribution');
const digest = crypto.createHash('sha256').update(body).digest('hex');
function runtime({ timing = ['https://example.test/jellyfin/web/hometab.abc.chunk.js'],
    bytes = body, hash = digest, route = '#/home', children, user = 'alice' } = {}) {
    const source = fs.readFileSync(require.resolve('../../src/Rowan.Jellyfin.Plugin/Web/home-adapter.js'), 'utf8')
        .replace('VERIFIED_HOME_DISTRIBUTION = null',
            `VERIFIED_HOME_DISTRIBUTION = { chunk: 'hometab.abc.chunk.js', sha256: '${hash}' }`);
    let appended = 0, mounts = 0, disposals = 0, stableMounts = 0, stableDisposals = 0;
    let fetches = [], listeners = {}, observer, ticks, watchReady = false, schedules = 0;
    // Publish host events after the adapter's awaited mount/refresh continuation has run.
    const publish = () => setImmediate(() => { stableMounts = mounts; stableDisposals = disposals; signal(); });
    const waiters = [];
    const signal = () => {
        for (const waiter of [...waiters]) if (waiter.ready()) {
            waiters.splice(waiters.indexOf(waiter), 1);
            clearTimeout(waiter.timer);
            waiter.resolve();
        }
    };
    const waitFor = (ready, label) => ready() ? Promise.resolve() : new Promise((resolve, reject) => {
        const waiter = { ready, resolve, timer: setTimeout(() => {
            waiters.splice(waiters.indexOf(waiter), 1);
            reject(Error(`timed out waiting for ${label}`));
        }, 2000) };
        waiters.push(waiter);
    });
    const pane = { id: 'homeTab', getAttribute: () => '0', querySelector: () => ({}), getClientRects: () => [{}] };
    const favorites = { id: 'favoritesTab', getAttribute: () => '1' };
    const document = { documentElement: {}, baseURI: 'https://example.test/jellyfin/web/index.html',
        querySelector: () => ({ children: children || [pane, favorites] }),
        createElement: () => ({ remove() {}, set src(value) { this.url = value; }, get src() { return this.url; } }),
        head: { appendChild(script) { appended++; signal(); script.onload?.(); } } };
    const context = { document, location: { hash: route },
        ApiClient: { getCurrentUserId: () => user, getUrl: path => `/jellyfin/${path}`, getJSON() {}, logout() { user = null; } },
        getComputedStyle: () => ({ display: 'block', visibility: 'visible' }),
        performance: { getEntriesByType: () => timing.map(name => ({ name, initiatorType: 'script' })) },
        fetch: async (url, options) => { fetches.push([url, options]); return { ok: true, url, arrayBuffer: async () => bytes }; },
        MutationObserver: class { constructor(callback) { observer = callback; } observe() {} disconnect() {} },
        addEventListener: (name, handler) => listeners[name] = handler,
        removeEventListener: name => delete listeners[name],
        queueMicrotask: callback => queueMicrotask(() => { callback(); schedules++; signal(); }), URL,
        setInterval: callback => { ticks = callback; watchReady = true; signal(); return 1; }, clearInterval: () => { ticks = null; },
        crypto: crypto.webcrypto,
        ThreePicFinHomeHost: { createHost: () => ({ mount: async () => { mounts++; publish(); return true; }, dispose() { disposals++; publish(); } }) } };
    vm.runInNewContext(source, context);
    const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
    return { context, pane, favorites, settle, tick: () => ticks?.(),
        waitForWatch: () => waitFor(() => watchReady, 'identity watcher'),
        nextSchedule: () => { const target = schedules + 1; return waitFor(() => schedules >= target, 'scheduled refresh'); },
        waitForMounts: count => waitFor(() => stableMounts >= count, `${count} mounts`),
        waitForDisposals: count => waitFor(() => stableDisposals >= count, `${count} disposals`),
        get appended() { return appended; },
        get mounts() { return mounts; },
        get disposals() { return disposals; }, get fetches() { return fetches; },
        change: (hash, id) => { context.location.hash = hash; if (id !== undefined) context.ApiClient.getCurrentUserId = () => id;
            listeners.hashchange?.(); observer?.(); },
        dispose: () => context.__threePicFinHomeAdapter?.dispose() };
}

test('native v12.1 DOM has distinct homeTab and favoritesTab children', () => {
    const home = { id: 'homeTab', getAttribute: () => '0', querySelector: () => ({}) };
    const favorites = { id: 'favoritesTab', getAttribute: () => '1' };
    assert.deepEqual(resolveHomeNodes({ children: [home, favorites] }), { pane: home, favorites });
    assert.deepEqual(resolveHomeNodes({ children: [home, favorites, favorites] }), { pane: null, favorites: null });
});

test('distribution-gated active Home loads from exact URL with matching refetched body under base path', async () => {
    const app = runtime(); await app.waitForMounts(1);
    assert.equal(app.appended, 1);
    assert.equal(app.fetches.length, 1);
    assert.equal(app.fetches[0][0], 'https://example.test/jellyfin/web/hometab.abc.chunk.js');
    app.dispose();
});
for (const [name, options] of [
    ['wrong body at same URL', { bytes: Buffer.from('different') }],
    ['wrong loaded URL', { timing: ['https://example.test/jellyfin/web/hometab.other.chunk.js'] }],
    ['missing resource timing', { timing: [] }],
    ['ambiguous duplicate resource timing', { timing: ['https://example.test/jellyfin/web/hometab.abc.chunk.js', 'https://example.test/jellyfin/web/hometab.abc.chunk.js'] }],
    ['cross-origin script URL', { timing: ['https://evil.test/jellyfin/web/hometab.abc.chunk.js'] }],
    ['Favorites route', { route: '#/home?tab=1' }],
    ['signed-out identity', { user: null }],
]) test(`fails closed on ${name}`, async () => {
    const app = runtime(options); await app.settle();
    assert.equal(app.appended, 0); app.dispose();
});
test('ambiguous native Home nodes fail closed', async () => {
    const app = runtime({ children: [{ id: 'homeTab', getAttribute: () => '0', querySelector: () => ({}) },
        { id: 'homeTab', getAttribute: () => '0', querySelector: () => ({}) }] });
    await app.settle(); assert.equal(app.appended, 0); app.dispose();
});
test('missing production manifest is inert', async () => {
    const source = fs.readFileSync(require.resolve('../../src/Rowan.Jellyfin.Plugin/Web/home-adapter.js'), 'utf8');
    const app = runtime(); app.dispose();
    assert.match(source, /VERIFIED_HOME_DISTRIBUTION = null/);
});

test('route exit and user switch tear down and remount only verified Home', async () => {
    let state = { route: '#/home', userId: 'alice', pane: {}, favorites: {}, activePane: true, compatibilityVerified: true,
        apiClient: { getUrl() {}, getJSON() {}, getCurrentUserId: () => state.userId }, bundleHash: 'bundle' };
    const events = [];
    const host = { mount: async ({ userId }) => { events.push(`mount:${userId}`); return true; }, dispose: () => events.push('dispose') };
    const adapter = createAdapter({ bundleHash: 'bundle', getState: () => state, loadHost: async () => host });
    await adapter.refresh(); await adapter.refresh();
    state = { ...state, userId: 'bob' }; await adapter.refresh();
    state = { ...state, route: '#/home?tab=1' }; await adapter.refresh();
    state = { ...state, route: '#/home', userId: null }; await adapter.refresh();
    assert.deepEqual(events, ['mount:alice', 'dispose', 'mount:bob', 'dispose']);
    adapter.dispose();
});
test('observer refresh while host script pending does not cancel its own load', async () => {
    let release, loads = 0, mounts = 0;
    const pending = new Promise(resolve => release = resolve);
    const state = { route: '#/home', userId: 'alice', pane: {}, favorites: {}, activePane: true, compatibilityVerified: true,
        apiClient: { getUrl() {}, getJSON() {}, getCurrentUserId: () => state.userId }, bundleHash: 'bundle' };
    const host = { mount: async () => { mounts++; return true; }, dispose() {} };
    const adapter = createAdapter({ bundleHash: 'bundle', getState: () => state,
        loadHost: () => { loads++; return pending; } });
    const first = adapter.refresh(); await adapter.refresh(); release(host); await first;
    assert.equal(loads, 1); assert.equal(mounts, 1); adapter.dispose();
});
test('logout synchronously tears down mounted Home without hash or DOM mutation', async () => {
    const app = runtime(); await app.waitForMounts(1);
    assert.equal(app.appended, 1);
    const logout = app.context.ApiClient.logout;
    app.context.ApiClient.logout();
    assert.equal(app.context.location.hash, '#/home');
    assert.equal(app.context.ApiClient.getCurrentUserId(), null);
    assert.equal(app.disposals, 1, 'logout must dispose before any observer tick');
    assert.notEqual(app.context.ApiClient.logout, logout, 'restore original logout after shutdown');
    app.dispose();
});
test('a new identity after logout can remount without leaving Home', async () => {
    const app = runtime(); await app.waitForMounts(1);
    app.context.ApiClient.logout();
    app.context.ApiClient.getCurrentUserId = () => 'bob'; app.tick();
    app.change('#/home'); await app.waitForMounts(2);
    assert.equal(app.appended, 1, 'host script is cached');
    assert.equal(app.mounts, 2);
    assert.equal(app.disposals, 1, 'new user must not dispose the old host again');
    assert.notEqual(app.context.ApiClient.logout, undefined);
    app.dispose();
});
test('identity change is detected while Home stays mounted without mutation', async () => {
    const app = runtime(); await app.waitForMounts(1);
    app.context.ApiClient.getCurrentUserId = () => 'bob';
    app.tick(); await app.waitForDisposals(1);
    assert.equal(app.disposals, 1);
    app.dispose();
});
test('pending host load cannot mount after logout', async () => {
    let release, state = { route: '#/home', userId: 'alice', pane: {}, favorites: {}, activePane: true,
        compatibilityVerified: true, apiClient: { getUrl() {}, getJSON() {}, getCurrentUserId: () => state.userId }, bundleHash: 'bundle' };
    let mounts = 0;
    const adapter = createAdapter({ bundleHash: 'bundle', getState: () => state,
        loadHost: () => new Promise(resolve => release = resolve) });
    const pending = adapter.refresh(); state = { ...state, userId: null }; await adapter.refresh();
    release({ mount: async () => { mounts++; return true; }, dispose() {} }); await pending;
    assert.equal(mounts, 0); adapter.dispose();
});
test('failed and no-op logout remount signed-in Home without mutation', async () => {
    for (const logout of [() => {}, () => Promise.reject(Error('offline'))]) {
        const app = runtime(); await app.waitForMounts(1);
        app.context.ApiClient.logout = logout;
        // Re-arm the hook against the supplied logout implementation.
        app.change('#/home?tab=1'); await app.waitForDisposals(1);
        app.change('#/home'); await app.waitForMounts(2);
        const result = app.context.ApiClient.logout();
        assert.equal(app.disposals, 2, 'teardown is synchronous');
        await result?.catch?.(() => {});
        await app.waitForMounts(3);
        assert.equal(app.mounts, 3, 'still signed in: remount');
        app.dispose();
    }
});
for (const outcome of ['resolve', 'reject']) test(`deferred logout ${outcome} keeps Home disposed until authentication is rechecked`, async () => {
    const app = runtime(); await app.waitForMounts(1);
    let finish;
    const pending = new Promise((resolve, reject) => { finish = outcome === 'resolve' ? resolve : reject; });
    app.context.ApiClient.logout = () => pending;
    app.change('#/home?tab=1'); await app.waitForDisposals(1);
    app.change('#/home'); await app.waitForMounts(2);
    const result = app.context.ApiClient.logout();
    assert.equal(result, pending, 'preserve the original logout result');
    assert.equal(app.disposals, 2, 'dispose synchronously before logout resolves');
    const whilePending = app.nextSchedule();
    app.change('#/home'); app.tick(); await whilePending;
    assert.equal(app.mounts, 2, 'still-authenticated user must not remount during logout');
    finish(outcome === 'reject' ? Error('offline') : undefined);
    await result.catch(() => {}); await app.waitForMounts(3);
    assert.equal(app.mounts, 3, 'recheck auth and remount if still authenticated');
    app.dispose();
});
test('deferred logout does not rearm for a signed-out identity, but same-user reauth mounts', async () => {
    const app = runtime(); await app.waitForMounts(1);
    let finish;
    const pending = new Promise(resolve => finish = resolve);
    app.context.ApiClient.logout = () => pending;
    app.change('#/home?tab=1'); await app.waitForDisposals(1);
    app.change('#/home'); await app.waitForMounts(2);
    app.context.ApiClient.logout();
    app.context.ApiClient.getCurrentUserId = () => null;
    const whilePending = app.nextSchedule();
    app.tick(); app.change('#/home'); await whilePending;
    assert.equal(app.mounts, 2, 'no mount while logout is pending');
    const afterLogout = app.nextSchedule();
    finish(); await pending; await afterLogout;
    assert.equal(app.mounts, 2, 'no mount after completed sign-out');
    app.context.ApiClient.getCurrentUserId = () => 'alice';
    app.tick(); await app.waitForMounts(3);
    assert.equal(app.mounts, 3, 'same user may reauthenticate');
    app.dispose();
});
test('disposing adapter during deferred logout prevents rearm on settlement', async () => {
    const app = runtime(); await app.waitForMounts(1);
    let finish;
    const pending = new Promise(resolve => finish = resolve);
    app.context.ApiClient.logout = () => pending;
    app.change('#/home?tab=1'); await app.waitForDisposals(1);
    app.change('#/home'); await app.waitForMounts(2);
    const hooked = app.context.ApiClient.logout;
    hooked(); app.dispose(); finish(); await pending;
    assert.equal(hooked.active, false);
    assert.equal(app.mounts, 2);
    assert.equal(app.context.ApiClient.logout === hooked, false, 'hook removed');
});
test('synchronous logout throw rearms Home after synchronous teardown', async () => {
    const app = runtime(); await app.waitForMounts(1);
    app.context.ApiClient.logout = () => { throw Error('offline'); };
    app.change('#/home?tab=1'); await app.waitForDisposals(1);
    app.change('#/home'); await app.waitForMounts(2);
    assert.throws(() => app.context.ApiClient.logout(), /offline/);
    assert.equal(app.disposals, 2);
    await app.waitForMounts(3);
    assert.equal(app.mounts, 3);
    app.dispose();
});
test('same user reauth after an observed signed-out interval remounts Home', async () => {
    const app = runtime(); await app.waitForMounts(1);
    const signedOut = app.nextSchedule();
    app.context.ApiClient.logout(); await signedOut;
    app.context.ApiClient.getCurrentUserId = () => 'alice';
    app.tick(); await app.waitForMounts(2);
    assert.equal(app.mounts, 2);
    app.dispose();
});
test('pending host discarded on invalidation is disposed', async () => {
    let release, disposals = 0, mounts = 0;
    const state = { route: '#/home', userId: 'alice', pane: {}, favorites: {}, activePane: true,
        compatibilityVerified: true, apiClient: { getUrl() {}, getJSON() {}, getCurrentUserId: () => state.userId }, bundleHash: 'bundle' };
    const adapter = createAdapter({ bundleHash: 'bundle', getState: () => state,
        loadHost: () => new Promise(resolve => release = resolve) });
    const pending = adapter.refresh(); adapter.invalidate();
    release({ mount: async () => { mounts++; return true; }, dispose() { disposals++; } });
    await pending;
    assert.equal(mounts, 0); assert.equal(disposals, 1);
    adapter.dispose();
});
test('outer logout wrapper retaining hook is harmless after adapter disposal', async () => {
    const app = runtime(); await app.waitForMounts(1);
    const inner = app.context.ApiClient.logout;
    app.context.ApiClient.logout = function (...args) { return inner.apply(this, args); };
    app.dispose();
    assert.equal(inner.active, false, 'retained closure is disabled');
    const before = app.disposals;
    app.context.ApiClient.logout();
    assert.equal(app.disposals, before);
    assert.equal(app.context.ApiClient.getCurrentUserId(), null);
});
test('signed-in browser state mounts after starting signed out, without navigation', async () => {
    const app = runtime({ user: null }); await app.waitForWatch();
    assert.equal(app.mounts, 0);
    app.context.ApiClient.getCurrentUserId = () => 'alice';
    app.tick(); await app.waitForMounts(1);
    assert.equal(app.mounts, 1);
    assert.equal(app.context.location.hash, '#/home');
    app.dispose();
});
