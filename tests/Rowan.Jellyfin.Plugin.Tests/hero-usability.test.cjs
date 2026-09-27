const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/static-hero.js'), 'utf8');
class Element {
    constructor(tag) { this.tagName = tag; this.children = []; this.handlers = {}; this.textContent = ''; this.style = { values: {}, setProperty: (key, value) => { this.style.values[key] = value; } }; }
    setAttribute(key, value) { (this.attrs ??= {})[key] = value; }
    removeAttribute() {}
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener(name, fn) { this.handlers[name] = fn; }
    removeEventListener(name) { delete this.handlers[name]; }
    dispatch(name, props = {}) { this.handlers[name]?.({ preventDefault() {}, ...props }); }
    contains(target) { return target === this || this.children.some(child => child.contains(target)); }
}
const tick = () => new Promise(resolve => setImmediate(resolve));
function setup(names = ['One', 'Two', 'Three'], openItem) {
    let now = 0, hidden = false, token = 'token', pulse;
    const listeners = {};
    const document = { createElement: tag => new Element(tag), get hidden() { return hidden; },
        addEventListener: (name, fn) => { listeners[name] = fn; }, removeEventListener: name => { delete listeners[name]; } };
    const context = { document, performance: { now: () => now }, setInterval: (fn, ms) => { if (ms === 100) pulse = fn; return ms; }, clearInterval() {} };
    vm.runInNewContext(source, context);
    const root = new Element('div');
    const api = { getUrl: route => route, getJSON: async () => names.map((name, i) => ({ id: `11111111-1111-1111-1111-11111111111${i}`, name, imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1' })),
        getCurrentUserId: () => 'user', accessToken: () => token, fetch: async () => ({ ok: false }), openItem };
    const cleanup = context.RowanStaticHero.mount(root, api);
    return { root, cleanup, listeners, advance: ms => { now += ms; pulse(); }, hide: value => { hidden = value; listeners.visibilitychange?.(); }, session: value => { token = value; } };
}
test('pagination retains focused button identity on click and keyboard navigation', async () => {
    const app = setup(); await tick();
    const panel = app.root.children[0], dots = panel.children.find(child => child.className === 'rowan-hero-pagination').children;
    dots[1].dispatch('click');
    assert.equal(dots[1], panel.children.find(child => child.className === 'rowan-hero-pagination').children[1]);
    assert.equal(dots[1].attrs['aria-current'], 'true');
    panel.dispatch('keydown', { key: 'ArrowRight', target: dots[1] });
    assert.equal(dots[1], panel.children.find(child => child.className === 'rowan-hero-pagination').children[1]);
    assert.equal(dots[2].attrs['aria-current'], 'true');
    app.cleanup();
});
test('selecting the active dot does not refetch its image', async () => {
    const root = new Element('div'); let fetched = 0;
    const context = { document: { createElement: tag => new Element(tag) }, setInterval: () => 1, clearInterval() {} };
    vm.runInNewContext(source, context);
    const cleanup = context.RowanStaticHero.mount(root, {
        getUrl: route => route, getCurrentUserId: () => 'user', accessToken: () => 'token',
        getJSON: async () => ['One', 'Two'].map((name, i) => ({ id: `11111111-1111-1111-1111-11111111111${i}`, name, imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1' })),
        fetch: async () => { fetched++; return { ok: false }; }
    });
    await tick();
    root.children[0].children.find(child => child.className === 'rowan-hero-pagination').children[0].dispatch('click');
    assert.equal(fetched, 1);
    cleanup();
});
test('wall time accrues only while visible, unfocused and unhovered', async () => {
    const app = setup(); await tick(); const panel = app.root.children[0];
    app.advance(6000);
    assert.equal(panel.children.find(child => child.className === 'rowan-hero-progress').style.values['--rowan-progress'], '50%');
    panel.dispatch('mouseenter'); app.advance(14000);
    panel.dispatch('focusin'); panel.dispatch('mouseleave'); app.advance(6000);
    assert.equal(panel.children[1].textContent, 'One');
    panel.dispatch('focusout', { relatedTarget: null });
    app.hide(true); app.advance(6000);
    assert.equal(panel.children[1].textContent, 'One');
    app.hide(false); app.advance(5000);
    assert.equal(panel.children[1].textContent, 'One');
    app.advance(1000);
    assert.equal(panel.children[1].textContent, 'Two');
    app.cleanup(); assert.equal(app.listeners.visibilitychange, undefined);
});
test('focus transitions within the panel do not resume autoplay', async () => {
    const app = setup(); await tick(); const panel = app.root.children[0];
    panel.dispatch('focusin');
    panel.dispatch('focusout', { relatedTarget: panel.children[3] });
    app.advance(12000);
    assert.equal(panel.children[1].textContent, 'One');
    app.cleanup();
});
test('Open delegates only the validated item ID while the original session is active', async () => {
    const opened = [], app = setup(['One'], value => opened.push(value)); await tick();
    const open = app.root.children[0].children.find(child => child.textContent === 'Open');
    assert.ok(open);
    open.dispatch('click'); assert.deepEqual(opened, ['11111111-1111-1111-1111-111111111110']);
    app.session('other'); open.dispatch('click'); assert.equal(opened.length, 1);
    app.cleanup();
});
