const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRows } = require('../../src/Rowan.Jellyfin.Plugin/Web/native-home-rows.js');
class Node {
    constructor(tag) { this.tagName = tag?.toUpperCase(); this.children = []; this.textContent = ''; this.parent = null; this.className = ''; this.listeners = {}; }
    appendChild(node) { this.children.push(node); node.parent = this; return node; }
    remove() { if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; }
    replaceChildren(...nodes) { this.children = []; nodes.forEach(n => this.appendChild(n)); this.textContent = ''; }
    addEventListener(name, fn) { (this.listeners[name] ||= []).push(fn); }
    click() { for (const fn of this.listeners.click || []) fn(); }
}
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture(enabledRows, openItem) {
    const root = new Node('main'), calls = [], observers = [];
    class Observer { constructor(cb) { this.cb = cb; observers.push(this); } observe() {} disconnect() { this.closed = true; } fire(target) { this.cb([{ target, isIntersecting: true }]); } }
    const rows = createRows({ document: { createElement: tag => new Node(tag) }, IntersectionObserver: Observer, enabledRows, openItem });
    const api = { getUrl: path => `/base/${path}`, getJSON: url => { calls.push(url); return {}; } };
    return { root, calls, observers, rows, api };
}
test('Because You Watched is one lazy bounded fetch with per-seed landscape headings and playable identities', async () => {
    const seen = [], f = fixture(['BecauseYouWatched'], item => seen.push(item));
    f.api.getJSON = url => { f.calls.push(url); return [{ SeedId: 'seed', Heading: 'Because You Watched <Movie>',
        Seed: { Id: 'seed', Name: 'Movie' }, Items: [{ Id: 'film', Name: 'Next <Film>' }] }]; };
    f.rows.mount(f.root, f.api, 'alice');
    assert.equal(f.root.children.length, 6);
    assert.deepEqual(f.calls, []);
    f.observers[0].fire(f.root.children[5]); await tick();
    assert.deepEqual(f.calls, ['/base/Rowan/Home/BecauseYouWatched']);
    assert.equal(f.root.children[5].children[1].className, 'rowan-native-row__seeds');
    const seed = f.root.children[5].children[1].children[0];
    assert.equal(seed.children[0].tagName, 'H2');
    assert.equal(seed.children[0].textContent, 'Because You Watched <Movie>');
    const card = seed.children[1].children[0];
    assert.equal(card.tagName, 'BUTTON');
    assert.match(card.className, /landscape/);
    assert.equal(card.textContent, 'Next <Film>');
    card.click(); assert.equal(seen[0].Id, 'film');
    f.rows.dispose(); assert.equal(f.root.children.length, 0);
});
test('Discover sources fetch independently and render candidate-only portrait cards', async () => {
    const opened = [], f = fixture(['Discover', 'DiscoverMovies', 'DiscoverTV'], item => opened.push(item));
    f.api.getJSON = url => { f.calls.push(url); return { Items: [{ TmdbId: 7, MediaType: 'movie', Title: 'Candidate', PosterPath: '/poster.jpg', Id: 'not-playable' }] }; };
    f.rows.mount(f.root, f.api, 'alice');
    assert.equal(f.root.children.length, 8);
    f.observers[0].fire(f.root.children[6]); await tick();
    assert.deepEqual(f.calls, ['/base/3picFin/HomeDiscover/DiscoverMovies']);
    const card = f.root.children[6].children[1].children[0];
    assert.equal(card.tagName, 'ARTICLE');
    assert.match(card.className, /portrait/);
    assert.equal(card.children.at(-1).textContent, 'Candidate · Seerr candidate');
    assert.equal(card.listeners.click, undefined);
    assert.deepEqual(opened, []);
    f.observers[0].fire(f.root.children[5]); f.observers[0].fire(f.root.children[7]); await tick();
    assert.equal(f.calls.length, 3);
    f.rows.dispose();
});
test('malformed, excessive and stale modular responses fail closed', async () => {
    const f = fixture(['BecauseYouWatched', 'Discover']);
    let finish;
    f.api.getJSON = url => url.includes('BecauseYouWatched') ? new Promise(resolve => { finish = resolve; }) : { Items: Array.from({ length: 21 }, () => ({ Title: 'Too many' })) };
    f.rows.mount(f.root, f.api, 'alice');
    const old = f.root.children[5];
    f.observers[0].fire(old); f.observers[0].fire(f.root.children[6]); await tick();
    assert.equal(f.root.children[6].children[1].textContent, 'Row unavailable');
    f.rows.mount(f.root, f.api, 'bob');
    finish([{ SeedId: 'seed', Heading: 'Private', Items: [{ Id: 'x', Name: 'secret' }] }]); await tick();
    assert.equal(old.children[1].children.length, 0);
    assert.equal(f.observers[0].closed, true);
    f.rows.dispose();
});
test('dispose before queued work prevents even the request', async () => {
    const f = fixture(['Discover']);
    f.rows.mount(f.root, f.api, 'alice');
    f.observers[0].fire(f.root.children[5]);
    f.rows.dispose(); await tick();
    assert.deepEqual(f.calls, []);
});
test('mixed valid and malformed candidates do not disclose partial row', async () => {
    const f = fixture(['Discover']);
    f.api.getJSON = () => ({ Items: [{ TmdbId: 7, MediaType: 'movie', Title: 'Valid' }, { TmdbId: 0, MediaType: 'tv', Title: 'Invalid' }] });
    f.rows.mount(f.root, f.api, 'alice'); f.observers[0].fire(f.root.children[5]); await tick();
    assert.equal(f.root.children[5].children[1].textContent, 'Row unavailable');
    assert.equal(f.root.children[5].children[1].children.length, 0);
    f.rows.dispose();
});
test('poster URLs are constrained and candidate errors do not leak partial items', async () => {
    const f = fixture(['DiscoverTV']);
    f.api.getJSON = () => ({ Items: [{ TmdbId: 7, MediaType: 'tv', Title: 'Safe', PosterPath: '//evil.test/p.jpg' }], Error: 'UpstreamUnavailable' });
    f.rows.mount(f.root, f.api, 'alice'); f.observers[0].fire(f.root.children[5]); await tick();
    assert.equal(f.root.children[5].children[1].textContent, 'Row unavailable');
    f.rows.dispose();
    f.api.getJSON = () => ({ Items: [{ TmdbId: 7, MediaType: 'tv', Title: 'Safe', PosterPath: '/legit.jpg' }] });
    f.rows.mount(f.root, f.api, 'alice'); f.observers[1].fire(f.root.children[5]); await tick();
    assert.equal(f.root.children[5].children[1].children[0].children[0].src, 'https://image.tmdb.org/t/p/w342/legit.jpg');
    f.rows.dispose();
});
