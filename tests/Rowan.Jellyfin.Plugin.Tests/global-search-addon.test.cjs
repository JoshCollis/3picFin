const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/global-search-addon.js'), 'utf8');
class Node {
    constructor(tag) { this.tagName = tag; this.children = []; this._text = ''; this.handlers = {}; this.attributes = {}; this.disabled = false; this.parent = null; }
    get textContent() { return this._text || this.children.map(n => n.textContent).join(''); }
    set textContent(v) { this._text = v; this.children = []; }
    appendChild(n) { this.children.push(n); n.parent = this; return n; }
    replaceChildren(...ns) { this._text = ''; this.children = ns; ns.forEach(n => n.parent = this); }
    remove() { if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this),1); this.parent = null; }
    setAttribute(k,v) { this.attributes[k] = v; }
    addEventListener(k,f) { (this.handlers[k] ??= []).push(f); }
    removeEventListener(k,f) { this.handlers[k] = (this.handlers[k] || []).filter(x => x !== f); }
    click() { for (const f of this.handlers.click || []) f(); }
}
const tick = () => new Promise(r => setImmediate(r));
function fixture(pinned = true) {
    const context = { document: { createElement: tag => new Node(tag) }, module: { exports: {} } };
    vm.runInNewContext(pinned ? source.replace('const VERIFIED_WEB_BUNDLE_SHA256 = null;', "const VERIFIED_WEB_BUNDLE_SHA256 = 'test-bundle';") : source, context);
    const root = new Node('div'), native = new Node('native'); root.appendChild(native);
    const pending = [], actions = [], api = { getUrl: (p, q) => `/jellyfin/${p}?query=${encodeURIComponent(q.query)}&page=${q.page}`, getJSON: url => new Promise((resolve,reject) => pending.push({url, resolve, reject})) };
    let active = 'alice';
    const addon = context.module.exports.createSearchAddon();
    const mount = (extra = {}) => addon.mount({ root, apiClient: api, userId: 'alice', sessionUserId: () => active,
        query: 'Alien', parentId: null, collectionType: null, enabled: true, fingerprint: 'test-bundle', nativeTmdbKeys: ['movie:1'],
        requestAction: (item, button) => actions.push([item,button]), ...extra });
    return { root, native, pending, actions, addon, mount, setUser: x => active = x };
}
const result = (items, page=1, totalPages=2) => ({ Items: items, Page: page, TotalPages: totalPages });
const movie = (id, title='Alien') => ({ TmdbId: id, MediaType: 'movie', Title: title });
test('production build is unpinned and leaves native search untouched', () => {
    const f = fixture(false); assert.equal(f.mount(), false); assert.deepEqual(f.root.children, [f.native]); assert.equal(f.pending.length, 0);
});
test('requires host contract and leaves native DOM untouched on mismatch', () => {
    const f = fixture(); assert.equal(f.mount({fingerprint: 'wrong'}), false); assert.equal(f.mount({requestAction: null}), false);
    assert.deepEqual(f.root.children, [f.native]); assert.equal(f.pending.length, 0);
});
test('dedupes native and repeated catalog identities without changing native results', async () => {
    const f = fixture(); assert.equal(f.mount(), true);
    f.pending[0].resolve(result([movie(1), movie(2,'Second'), movie(2,'Duplicate'), {TmdbId:0,MediaType:'tv',Title:'Bad'}])); await tick();
    const section = f.root.children[1], cards = section.children[1];
    assert.equal(f.root.children[0], f.native); assert.equal(cards.children.length, 1);
    assert.equal(cards.children[0].children[0].children[0].textContent, 'Second');
    cards.children[0].children[0].children[1].click(); assert.equal(f.actions.length, 1);
    assert.equal(f.actions[0][0].TmdbId, 2);
});
test('query race, pagination, empty and upstream errors do not change native results', async () => {
    const f = fixture(); f.mount(); f.addon.update({query:'New', nativeTmdbKeys: ['movie:1']});
    f.pending[0].resolve(result([movie(5)])); await tick();
    f.pending[1].resolve(result([movie(6)],1,3)); await tick();
    const section = f.root.children[1]; assert.equal(section.children[1].children[0].children[0].children[0].textContent, 'Alien');
    const pager = section.children[2]; pager.children[1].click(); assert.match(f.pending[2].url,/page=2/);
    f.pending[2].resolve(result([],2,3)); await tick(); assert.match(section.children[1].textContent,/No Seerr/);
    pager.children[0].click(); f.pending[3].reject(Error('offline')); await tick(); assert.match(section.children[1].textContent,/unavailable/);
    assert.equal(f.root.children[0], f.native);
});
test('user switch and teardown suppress late results and actions', async () => {
    const f = fixture(); f.mount(); const section = f.root.children[1]; f.setUser('bob');
    f.pending[0].resolve(result([movie(8)])); await tick(); assert.deepEqual(f.root.children,[f.native]);
    assert.equal(f.mount(), false); assert.equal(f.pending.length,1);
    f.setUser('alice'); f.mount(); f.pending[1].resolve(result([movie(9)])); await tick();
    const button = f.root.children[1].children[1].children[0].children[0].children[1]; f.addon.dispose();
    assert.equal((button.handlers.click || []).length, 0);
    button.click(); assert.equal(f.actions.length,0); assert.deepEqual(f.root.children,[f.native]);
});

test('detached result buttons cannot request after query change or page change', async () => {
    const f = fixture(); f.mount(); f.pending[0].resolve(result([movie(2, 'Old')])); await tick();
    const section = f.root.children[1], old = section.children[1].children[0].children[0].children[1];
    f.addon.update({query: 'New', nativeTmdbKeys: ['movie:1']});
    assert.equal((old.handlers.click || []).length, 0);
    old.click(); assert.equal(f.actions.length, 0);
    f.pending[1].resolve(result([movie(3, 'New')], 1, 2)); await tick();
    const newer = section.children[1].children[0].children[0].children[1];
    section.children[2].children[1].click();
    assert.equal((newer.handlers.click || []).length, 0);
    newer.click(); assert.equal(f.actions.length, 0);
    f.pending[2].resolve(result([movie(4)], 2, 2)); await tick();
    section.children[1].children[0].children[0].children[1].click();
    assert.equal(f.actions.length, 1); assert.equal(f.actions[0][0].TmdbId, 4);
});
test('query transition without resolved native identities tears down old buttons and ignores late work', async () => {
    const f = fixture(); f.mount(); f.pending[0].resolve(result([movie(2)])); await tick();
    const old = f.root.children[1].children[1].children[0].children[0].children[1];
    f.addon.update({query: 'New'});
    assert.deepEqual(f.root.children, [f.native]);
    old.click(); assert.equal(f.actions.length, 0);
    assert.equal(f.pending.length, 1);
    assert.equal(f.mount({query: 'New', nativeTmdbKeys: ['movie:3']}), true);
    f.pending[1].resolve(result([movie(3), movie(4)])); await tick();
    const cards = f.root.children[1].children[1].children[0];
    assert.equal(cards.children.length, 1);
    assert.equal(cards.children[0].children[0].textContent, 'Alien');
    assert.equal(f.actions.length, 0);
});
test('unresolved native identity update fails closed, including detached action', async () => {
    const f = fixture(); f.mount(); f.pending[0].resolve(result([movie(2)])); await tick();
    const old = f.root.children[1].children[1].children[0].children[0].children[1];
    f.addon.update({nativeTmdbKeys: null});
    old.click(); assert.equal(f.actions.length, 0);
    assert.deepEqual(f.root.children, [f.native]);
});
test('malformed query updates tear down old actions and permit a clean remount', async () => {
    for (const query of [null, undefined, 42, {}, ['New']]) {
        const f = fixture(); f.mount(); f.pending[0].resolve(result([movie(2)])); await tick();
        const section = f.root.children[1], old = section.children[1].children[0].children[0].children[1];
        assert.doesNotThrow(() => f.addon.update({query, nativeTmdbKeys: ['movie:1']}));
        assert.deepEqual(f.root.children, [f.native]);
        assert.equal((old.handlers.click || []).length, 0);
        old.click(); section.children[2].children[1].click();
        assert.equal(f.actions.length, 0); assert.equal(f.pending.length, 1);
        assert.equal(f.mount({query: 'New', nativeTmdbKeys: ['movie:3']}), true);
        f.pending[1].resolve(result([movie(3), movie(4)])); await tick();
        assert.equal(f.root.children[1].children[1].children[0].children.length, 1);
    }
});
test('scope must be explicitly unscoped; scope transitions tear down pending and rendered actions', async () => {
    const f = fixture();
    assert.equal(f.mount({parentId: undefined}), false);
    assert.equal(f.mount({collectionType: undefined}), false);
    assert.equal(f.mount({parentId: 'library-id'}), false);
    assert.equal(f.mount({collectionType: 'movies'}), false);
    assert.equal(f.pending.length, 0);
    assert.equal(f.mount(), true);
    f.pending[0].resolve(result([movie(2)])); await tick();
    const button = f.root.children[1].children[1].children[0].children[0].children[1];
    f.addon.update({parentId: 'library-id'});
    button.click(); assert.equal(f.actions.length, 0);
    assert.deepEqual(f.root.children, [f.native]);
    f.mount(); f.addon.update({collectionType: 'movies'});
    f.pending[1].resolve(result([movie(3)])); await tick();
    assert.deepEqual(f.root.children, [f.native]); assert.equal(f.actions.length, 0);
    f.mount(); f.addon.update({parentId: undefined});
    assert.deepEqual(f.root.children, [f.native]);
});
