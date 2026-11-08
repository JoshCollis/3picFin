const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const web = path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web');
class Node {
    constructor(tag = 'div') { this.tagName = tag; this.children = []; this.attrs = {}; this.style = {}; this.handlers = {}; this.scrollLeft = 0; this.clientWidth = 400; this.classList = { add() {} }; }
    setAttribute(key, value) { this.attrs[key] = String(value); }
    getAttribute(key) { return this.attrs[key] ?? null; }
    hasAttribute(key) { return Object.hasOwn(this.attrs, key); }
    addEventListener(key, fn) { (this.handlers[key] ??= []).push(fn); }
    removeEventListener(key, fn) { this.handlers[key] = (this.handlers[key] || []).filter(handler => handler !== fn); }
    appendChild(node) { return this.insertBefore(node, null); }
    insertBefore(node, before) { node.remove(); this.children.splice(before ? this.children.indexOf(before) : this.children.length, 0, node); node.parentNode = this; return node; }
    remove() { if (this.parentNode) this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; }
    replaceChildren(...nodes) { for (const node of [...this.children]) node.remove(); nodes.forEach(node => this.appendChild(node)); }
    contains(node) { return node === this || this.children.some(child => child.contains(node)); }
    get firstElementChild() { return this.children[0] || null; }
    get nextElementSibling() { return this.parentNode?.children[this.parentNode.children.indexOf(this) + 1] || null; }
    getClientRects() { return []; }
}
const flush = async () => { for (let i = 0; i < 4; i++) await new Promise(setImmediate); };
function setup() {
    const context = { document: { createElement: tag => new Node(tag) }, AbortController,
        requestAnimationFrame: setImmediate, cancelAnimationFrame: clearImmediate, addEventListener() {}, removeEventListener() {} };
    vm.runInNewContext(fs.readFileSync(path.join(web, 'continuous-rail.js'), 'utf8'), context);
    const rail = new Node(), status = new Node(), calls = [];
    const feed = context.ThreePicFinContinuousRail.create({ rail, status, title: 'Catalog',
        renderItem: item => { const card = new Node('article'); card.textContent = item.Title; return card; },
        loadPage: (page, signal) => new Promise((resolve, reject) => calls.push({ page, signal, resolve, reject })) });
    return { feed, rail, status, calls };
}
const source = (page, items, total = 5) => ({ Page: page, Items: items.map(id => ({ MediaType: 'movie', TmdbId: id, Title: String(id) })), TotalPages: total });
test('one in-flight page, ordered append and cross-page identity deduplication', async () => {
    const app = setup(); app.feed.load(); app.feed.load(); app.feed.load();
    assert.equal(app.calls.length, 1);
    app.calls[0].resolve(source(1, [1, 2])); await flush();
    app.feed.load(); app.feed.load(); assert.equal(app.calls.length, 2);
    app.calls[1].resolve(source(2, [2, 3])); await flush();
    assert.deepEqual(Array.from(app.feed.snapshot().items, item => item.TmdbId), [1, 2, 3]);
    app.feed.dispose();
});
test('reset aborts and rejects stale fulfillment even when transport ignores cancellation', async () => {
    const app = setup(); app.feed.load(); app.feed.reset(); app.feed.load();
    assert.equal(app.calls[0].signal.aborted, true);
    app.calls[1].resolve(source(1, [20])); await flush();
    app.calls[0].resolve(source(1, [10])); await flush();
    assert.deepEqual(Array.from(app.feed.snapshot().items, item => item.TmdbId), [20]);
    app.feed.dispose();
});
test('incremental failure preserves cards and retry requests the failed page, not the next one', async () => {
    const app = setup(); app.feed.seed(source(1, [1])); app.feed.load();
    app.calls[0].reject(new Error('private upstream info')); await flush();
    assert.equal(app.feed.snapshot().items.length, 1);
    assert.equal(app.status.children[1].hidden, false);
    assert.doesNotMatch(app.status.children[0].textContent, /private/);
    app.status.children[1].handlers.click[0](); assert.equal(app.calls[1].page, 2);
    app.calls[1].resolve(source(2, [2])); await flush();
    assert.equal(app.status.children[1].hidden, true); app.feed.dispose();
});
test('backend page cap, duplicate-only termination and stale disposal remain bounded', async () => {
    const app = setup(); app.feed.seed(source(1, [1], 10000));
    assert.equal(app.feed.snapshot().max, 100);
    for (let page = 2; page <= 4; page++) {
        app.feed.load(); app.calls.at(-1).resolve(source(page, [1], 10000)); await flush();
    }
    app.feed.load(); assert.equal(app.calls.length, 3, 'three duplicate-only pages stop automatic crawling');
    app.feed.reset(); app.feed.load(); const last = app.calls.at(-1);
    app.feed.dispose(); assert.equal(last.signal.aborted, true);
    last.resolve(source(1, [99])); await flush(); assert.equal(app.rail.children.length, 0);
});
test('result pagers are removed from markup, controllers and styles rather than hidden', () => {
    const html = fs.readFileSync(path.join(web, 'discovery.html'), 'utf8');
    const discover = fs.readFileSync(path.join(web, 'discovery.js'), 'utf8');
    const search = fs.readFileSync(path.join(web, 'global-search-addon.js'), 'utf8');
    const css = fs.readFileSync(path.join(web, 'discovery.css'), 'utf8') + fs.readFileSync(path.join(web, 'global-search-addon.css'), 'utf8');
    assert.doesNotMatch(html, /__pager|results page|Requests pages|Movies pages|TV pages/);
    assert.doesNotMatch(discover + search, /function pager|pageLabel|Page \$\{|search-prev|search-next|requests-prev|requests-next|load.more/i);
    assert.doesNotMatch(css, /__pager|threepic-fin-search nav/);
});
