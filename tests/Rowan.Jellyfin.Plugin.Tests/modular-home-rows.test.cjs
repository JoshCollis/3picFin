const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRows } = require('../../src/Rowan.Jellyfin.Plugin/Web/native-home-rows.js');
const id = '0123456789abcdef0123456789abcdef';
class Node {
    constructor(tag) { this.tagName = tag?.toUpperCase(); this.children = []; this.textContent = ''; this.parent = null; this.className = ''; this.listeners = {}; }
    appendChild(node) { this.children.push(node); node.parent = this; return node; }
    append(...nodes) { nodes.forEach(node => this.appendChild(node)); }
    remove() { if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; }
    replaceChildren(...nodes) { this.children = []; this.textContent = ''; this.append(...nodes); }
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
test('Because You Watched is one lazy bounded fetch with separate seed headings and landscape cards', async () => {
    const seen = [], f = fixture(['BecauseYouWatched'], item => seen.push(item));
    f.api.getJSON = url => { f.calls.push(url); return [{ Heading: 'Because You Watched <Movie>',
        Items: [{ Id: id, Type: 'Movie', Name: 'Next <Film>', BackdropImageTags: ['abcd'] }] }]; };
    f.rows.mount(f.root, f.api, 'alice');
    assert.equal(f.root.children.length, 1);
    assert.deepEqual(f.calls, []);
    f.observers[0].fire(f.root.children[0]); await tick();
    assert.deepEqual(f.calls, ['/base/Rowan/Home/BecauseYouWatched']);
    const seed = f.root.children[0].children[0].children[0];
    assert.equal(seed.children[0].tagName, 'H3');
    assert.equal(seed.children[0].textContent, 'Because You Watched <Movie>');
    const card = seed.children[1].children[0];
    assert.equal(card.tagName, 'BUTTON');
    assert.match(card.className, /landscape/);
    assert.equal(card.children[0].src, `/base/Items/${id}/Images/Backdrop/0`);
    assert.equal(card.children[1].textContent, 'Next <Film>');
    card.click(); assert.equal(seen[0].Id, id);
    f.rows.dispose(); assert.equal(f.root.children.length, 0);
});
test('unsupported household and Seerr candidate kinds never mount or fetch', () => {
    const f = fixture(['Discover', 'DiscoverMovies', 'DiscoverTV', 'LiveTV', 'UpcomingMovies']);
    assert.equal(f.rows.mount(f.root, f.api, 'alice'), false);
    assert.deepEqual(f.calls, []); assert.equal(f.root.children.length, 0);
});
test('excessive recommendation groups fail closed without partial cards', async () => {
    const f = fixture(['BecauseYouWatched']);
    f.api.getJSON = () => Array.from({ length: 6 }, () => ({ Heading: 'Because You Watched A', Items: [{ Id: id, Type: 'Movie' }] }));
    f.rows.mount(f.root, f.api, 'alice'); f.observers[0].fire(f.root.children[0]); await tick();
    assert.equal(f.root.children[0].children[0].textContent, 'Row unavailable');
});
test('disposal before queued work prevents even a request', async () => {
    const f = fixture(['BecauseYouWatched']);
    f.rows.mount(f.root, f.api, 'alice'); f.observers[0].fire(f.root.children[0]);
    f.rows.dispose(); await tick(); assert.deepEqual(f.calls, []);
});
