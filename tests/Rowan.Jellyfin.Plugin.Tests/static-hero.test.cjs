const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = () => fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/static-hero.js'), 'utf8');
class Element {
    constructor(tag) { this.tagName = tag; this.children = []; this.handlers = {}; this.textContent = ''; this.src = ''; this.alt = ''; }
    setAttribute(key, value) { (this.attrs ??= {})[key] = value; }
    removeAttribute(key) { delete this.attrs?.[key]; if (key === 'src') this.src = ''; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; this.textContent = ''; }
    addEventListener(event, handler) { this.handlers[event] = handler; }
    removeEventListener(event) { delete this.handlers[event]; }
    dispatch(event, props = {}) { this.handlers[event]?.({ preventDefault() {}, ...props }); }
}
const tick = () => new Promise(resolve => setImmediate(resolve));
test('does not request upstream image routes that allow anonymous access', async () => {
    const id = '11111111-1111-1111-1111-111111111111', calls = [], revoked = [];
    const root = new Element('div');
    const context = { document: { createElement: tag => new Element(tag) }, setInterval: () => 1, clearInterval() {}, URL: {
        createObjectURL: blob => { assert.equal(blob, 'binary'); return 'blob:private'; },
        revokeObjectURL: url => revoked.push(url)
    } };
    vm.runInNewContext(source(), context);
    const api = { getUrl: (route, params) => { calls.push([route, params]); return `/jellyfin/${route}`; },
        getJSON: async () => [{ id, imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1b2' }],
        getCurrentUserId: () => 'user-A', accessToken: () => 'session-A',
        fetch: async (request, auth) => { calls.push(['image-fetch', request, auth]); return { ok: true, headers: { get: () => 'image/jpeg' }, blob: async () => 'binary' }; } };
    const cleanup = context.RowanStaticHero.mount(root, api);
    await tick(); await tick();
    assert.equal(calls.some(x => x[0].startsWith('Items/')), false);
    assert.equal(calls.some(x => x[0] === 'Rowan/Home/Hero/Image/' + id), true);
    assert.equal(calls.find(x => x[0] === 'image-fetch')[1].url, `/jellyfin/Rowan/Home/Hero/Image/${id}`);
    assert.equal(root.children[0].children[0].src, 'blob:private');
    cleanup(); assert.deepEqual(revoked, ['blob:private']);
});
test('late image responses do not replace a newer slide or leak blobs', async () => {
    const id = '11111111-1111-1111-1111-111111111111';
    const pending = []; const revoked = []; let created = 0;
    const root = new Element('div');
    const context = { document: { createElement: tag => new Element(tag) }, setInterval: () => 1, clearInterval() {}, URL: {
        createObjectURL: () => `blob:${++created}`, revokeObjectURL: url => revoked.push(url)
    } };
    vm.runInNewContext(source(), context);
    const api = { getUrl: route => route, getJSON: async () => [1, 2].map(() => ({ id, imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1' })),
        getCurrentUserId: () => 'user', accessToken: () => 'token',
        fetch: () => new Promise(resolve => pending.push(resolve)) };
    const cleanup = context.RowanStaticHero.mount(root, api);
    await tick();
    root.children[0].children[4].dispatch('click');
    const response = label => ({ ok: true, headers: { get: () => 'image/jpeg' }, blob: async () => label });
    pending[1](response('new')); await tick();
    assert.equal(root.children[0].children[0].src, 'blob:1');
    pending[0](response('old')); await tick();
    assert.equal(root.children[0].children[0].src, 'blob:1');
    cleanup(); assert.deepEqual(revoked, ['blob:1']);
});

test('cleanup aborts pending image request and never creates a late blob URL', async () => {
    const id = '11111111-1111-1111-1111-111111111111';
    let resolve, signal, created = 0;
    const root = new Element('div');
    const context = { document: { createElement: tag => new Element(tag) }, setInterval: () => 1, clearInterval() {},
        AbortController, URL: { createObjectURL: () => { created++; return 'blob:late'; }, revokeObjectURL() {} } };
    vm.runInNewContext(source(), context);
    const api = { getUrl: route => route, getJSON: async () => [{ id, imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1' }],
        getCurrentUserId: () => 'user', accessToken: () => 'token',
        fetch: request => { signal = request.signal; return new Promise(r => { resolve = r; }); } };
    const cleanup = context.RowanStaticHero.mount(root, api); await tick(); cleanup();
    assert.equal(signal.aborted, true);
    resolve({ ok: true, headers: { get: () => 'image/jpeg' }, blob: async () => 'late' });
    await tick(); assert.equal(created, 0);
});
test('stylesheet fills viewport instead of fixed short height', () => {
    const css = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/static-hero.css'), 'utf8');
    assert.match(css, /min-height:100(?:dvh|vh)/);
    assert.doesNotMatch(css, /56vw|55vh/);
});
test('logout invalidates displayed slides without host remount', async () => {
    const root = new Element('div'); let token = 'session-A', pulse;
    const api = { getUrl: route => route, getJSON: async () => [{ id: '11111111-1111-1111-1111-111111111111', imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1b2' }],
        getCurrentUserId: () => token ? 'user-A' : null, accessToken: () => token };
    const context = { document: { createElement: tag => new Element(tag) }, setInterval: fn => { pulse = fn; return 1; }, clearInterval() {} };
    vm.runInNewContext(source(), context);
    context.RowanStaticHero.mount(root, api); await tick();
    assert.equal(root.children.length, 1);
    token = ''; pulse();
    assert.equal(root.children.length, 0);
});
test('accepts actual PascalCase DTO from disposable Jellyfin', async () => {
    const app = setup(Promise.resolve([{ Id: '11111111-1111-1111-1111-111111111111', Name: 'MovieA', ImageType: 'Backdrop', ImageIndex: 0, ImageTag: 'a1b2' }]));
    await tick();
    assert.equal(app.root.children[0].children[1].textContent, 'MovieA');
});
test('accepts compact GUID from Jellyfin wire response', async () => {
    const id = '71d0d996f1212efab22c235ca251ab79';
    const app = setup(Promise.resolve([{ Id: id, Name: 'Alice', ImageType: 'Backdrop', ImageIndex: 0, ImageTag: 'a1b2' }]));
    await tick();
    assert.equal(app.root.children[0].children[1].textContent, 'Alice');
    assert.equal(app.calls.some(call => call[0] === `Rowan/Home/Hero/Image/${id}`), true);
});
function setup(reply) {
    const root = new Element('div'), calls = [];
    const api = { getUrl(route, params) { calls.push([route, params]); return `/jellyfin/${route}`; }, getJSON(url) { calls.push(['fetch', url]); return reply; }, getCurrentUserId: () => 'user-A', accessToken: () => 'session-A' };
    const context = { document: { createElement: tag => new Element(tag) }, setInterval: () => 1, clearInterval() {} };
    vm.runInNewContext(source(), context);
    return { root, calls, cleanup: context.RowanStaticHero.mount(root, api) };
}
test('still hero requests only scoped endpoint and renders text without unsafe images', async () => {
    const id = '11111111-1111-1111-1111-111111111111';
    const app = setup(Promise.resolve([{ id, name: '<b>Title</b>', overview: 'Synopsis', imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1b2' }]));
    await tick(); await tick();
    assert.equal(app.calls[0][0], 'Rowan/Home/Hero');
    assert.equal(app.calls[1][0], 'fetch');
    assert.equal(app.calls.length, 3);
    assert.equal(app.calls[2][0], `Rowan/Home/Hero/Image/${id}`);
    const [section] = app.root.children;
    assert.equal(section.children[0].src, '');
    assert.equal(section.children[1].textContent, '<b>Title</b>');
    assert.equal(section.children.some(c => Object.hasOwn(c, 'innerHTML')), false);
});
test('navigation wraps and cleanup clears content and suppresses late replies', async () => {
    let resolve; const pending = new Promise(r => { resolve = r; });
    const app = setup(pending);
    app.cleanup(); resolve([{ id: '11111111-1111-1111-1111-111111111111', imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1b2' }]);
    await tick(); assert.equal(app.root.children.length, 0);
    assert.equal(app.calls.length, 2);
});
test('rejects untrusted image paths and caps even a malicious response', async () => {
    const id = '11111111-1111-1111-1111-111111111111';
    const app = setup(Promise.resolve([{ id: '../bad', imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1b2' }, ...Array.from({ length: 30 }, () => ({ id, name: 'Safe', imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1b2' }))]));
    await tick();
    assert.equal(app.calls.filter(([route]) => route.startsWith('Items/')).length, 0);
    const section = app.root.children[0];
    section.children[3].dispatch('click');
    assert.equal(section.children[1].textContent, 'Safe');
});
