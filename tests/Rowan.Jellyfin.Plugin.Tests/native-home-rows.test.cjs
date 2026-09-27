const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRows } = require('../../src/Rowan.Jellyfin.Plugin/Web/native-home-rows.js');

class Node {
    constructor() { this.children = []; this.textContent = ''; this.parent = null; this.className = ''; }
    appendChild(n) { this.children.push(n); n.parent = this; return n; }
    remove() { if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; }
    replaceChildren(...nodes) { for (const n of this.children) n.parent = null; this.children = []; nodes.forEach(n => this.appendChild(n)); }
}
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture() {
    const root = new Node(), calls = [], observers = [];
    const document = { createElement: () => new Node() };
    const api = { getUrl: p => `/base/${p}`, getJSON: url => { calls.push(url); return Promise.resolve({ Kind: 'ContinueWatching', Items: [{ Id: 'a', Name: 'Own film' }] }); } };
    class Observer { constructor(callback) { this.callback = callback; observers.push(this); } observe() {} disconnect() { this.closed = true; } fire(target) { this.callback([{ target, isIntersecting: true }]); } }
    const rows = createRows({ document, IntersectionObserver: Observer });
    return { root, calls, observers, api, rows };
}
test('personal My Requests is opt-in and lazy with user teardown', async () => {
    const f = fixture();
    const observers = [];
    class Observer { constructor(cb) { this.cb = cb; observers.push(this); } observe() {} disconnect() {} fire(target) { this.cb([{ target, isIntersecting: true }]); } }
    const rows = createRows({ document: { createElement: () => new Node() }, IntersectionObserver: Observer, enabledRows: ['MyRequests'] });
    rows.mount(f.root, f.api, 'alice');
    assert.equal(f.root.children.length, 6);
    observers[0].fire(f.root.children[5]); await tick();
    assert.deepEqual(f.calls, ['/base/Rowan/Home/Rows/MyRequests']);
    rows.dispose(); assert.equal(f.root.children.length, 0);
});

test('loads each row only when visible using a base-path URL', async () => {
    const f = fixture(); f.rows.mount(f.root, f.api, 'alice');
    assert.deepEqual(f.calls, []);
    const sections = f.root.children;
    f.observers[0].fire(sections[0]); await tick();
    assert.deepEqual(f.calls, ['/base/Rowan/Home/Rows/ContinueWatching']);
    assert.equal(sections[0].children[1].children[0].textContent, 'Own film');
    f.observers[0].fire(sections[0]); await tick(); assert.equal(f.calls.length, 1);
});
test('disposing or changing user drops pending responses and disconnects observation', async () => {
    const f = fixture(); let finish;
    f.api.getJSON = () => new Promise(resolve => { finish = resolve; });
    f.rows.mount(f.root, f.api, 'alice');
    const old = f.root.children[0]; f.observers[0].fire(old); await tick();
    f.rows.dispose(); finish({ Items: [{ Name: 'Alice secret' }] }); await tick();
    assert.deepEqual(f.root.children, []); assert.equal(f.observers[0].closed, true);
    f.rows.mount(f.root, f.api, 'bob'); assert.notEqual(f.root.children[0], old);
});
test('fails closed without a signed-in user or API client', () => {
    const f = fixture(); assert.equal(f.rows.mount(f.root, f.api, ''), false);
    assert.equal(f.rows.mount(f.root, {}, 'alice'), false);
    assert.deepEqual(f.root.children, []);
});
test('Live TV opt-in lazily renders program and channel labels without stream URL', async () => {
    const f = fixture(); const observers = [];
    class Observer { constructor(cb) { this.cb = cb; observers.push(this); } observe() {} disconnect() {} fire(target) { this.cb([{ target, isIntersecting: true }]); } }
    const opted = createRows({ document: { createElement: () => new Node() }, IntersectionObserver: Observer, enabledRows: ['LiveTV'] });
    f.api.getJSON = url => { f.calls.push(url); return Promise.resolve({ Kind: 'LiveTV', Items: [
        { ProgramId: 'a', ChannelId: 'b', Title: 'The Match', ChannelName: 'Sport One', ChannelNumber: '5', StreamUrl: 'not-to-render' }
    ] }); };
    opted.mount(f.root, f.api, 'alice');
    assert.equal(f.root.children.length, 6);
    observers[0].fire(f.root.children[5]); await tick();
    assert.deepEqual(f.calls, ['/base/Rowan/Home/Rows/LiveTV']);
    assert.equal(f.root.children[5].children[0].textContent, 'Live TV');
    assert.match(f.root.children[5].children[1].children[0].textContent, /The Match.*Sport One/);
    assert.doesNotMatch(f.root.children[5].children[1].children[0].textContent, /not-to-render/);
    opted.dispose();
});

test('new rows are opt-in, lazy and independently fetched', async () => {
    const f = fixture();
    assert.equal(f.rows.mount(f.root, f.api, 'alice'), true);
    assert.equal(f.root.children.length, 5);
    f.rows.dispose();
    const observers = [];
    class Observer { constructor(cb) { this.cb = cb; observers.push(this); } observe() {} disconnect() {} fire(target) { this.cb([{ target, isIntersecting: true }]); } }
    const opted = createRows({ document: { createElement: () => new Node() }, IntersectionObserver: Observer,
        enabledRows: ['ContinueWatchingNextUp', 'Collections'] });
    assert.equal(opted.mount(f.root, f.api, 'alice'), true);
    assert.equal(f.root.children.length, 7);
    assert.equal(f.root.children[5].children[0].textContent, 'Continue Watching / Next Up');
    assert.equal(f.root.children[6].children[0].textContent, 'Collections');
    observers[0].fire(f.root.children[6]); await tick();
    assert.deepEqual(f.calls, ['/base/Rowan/Home/Rows/Collections']);
    opted.dispose();
});

test('upcoming source failure is not represented as an empty successful row', async () => {
    const f = fixture(), observers = [];
    class Observer { constructor(cb) { this.cb = cb; observers.push(this); } observe() {} disconnect() {} fire(target) { this.cb([{ target, isIntersecting: true }]); } }
    const rows = createRows({ document: { createElement: () => new Node() }, IntersectionObserver: Observer, enabledRows: ['UpcomingMovies'] });
    f.api.getJSON = () => Promise.resolve({ Items: [], Error: 'UpstreamUnavailable' });
    rows.mount(f.root, f.api, 'alice'); observers[0].fire(f.root.children[5]); await tick();
    assert.equal(f.root.children[5].children[1].textContent, 'Row unavailable');
    rows.dispose();
});

test('upcoming cards are separately opt-in and render calendar identity without playability', async () => {
    const f = fixture(), observers = [];
    class Observer { constructor(cb) { this.cb = cb; observers.push(this); } observe() {} disconnect() {} fire(target) { this.cb([{ target, isIntersecting: true }]); } }
    const opted = createRows({ document: { createElement: () => new Node() }, IntersectionObserver: Observer,
        enabledRows: ['UpcomingMovies', 'UpcomingShows'] });
    f.api.getJSON = url => { f.calls.push(url); return Promise.resolve({ Items: [{ Title: 'Soon', Date: '2026-10-02T00:00:00Z',
        EpisodeTitle: 'Pilot', SeasonNumber: 1, EpisodeNumber: 2, Path: '/secret', PlaybackUrl: '/play' }] }); };
    opted.mount(f.root, f.api, 'alice');
    assert.equal(f.root.children.length, 7);
    observers[0].fire(f.root.children[5]); observers[0].fire(f.root.children[6]); await tick();
    assert.deepEqual(f.calls, ['/base/Rowan/Home/Rows/UpcomingMovies', '/base/Rowan/Home/Rows/UpcomingShows']);
    assert.match(f.root.children[5].children[1].children[0].textContent, /Soon.*2026-10-02/);
    assert.match(f.root.children[6].children[1].children[0].textContent, /Soon.*Pilot/);
    assert.doesNotMatch(f.root.children[6].children[1].children[0].textContent, /secret|play/i);
    opted.dispose();
});
