const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRows } = require('../../src/Rowan.Jellyfin.Plugin/Web/native-home-rows.js');
const id = '0123456789abcdef0123456789abcdef';
class Node {
    constructor(tag = 'div') { this.tagName = tag.toUpperCase(); this.children = []; this.textContent = ''; this.parent = null; this.className = ''; this.classList = { add: name => this.className += ` ${name}` }; this.handlers = {}; }
    appendChild(n) { this.children.push(n); n.parent = this; return n; }
    append(...nodes) { nodes.forEach(n => this.appendChild(n)); }
    remove() { if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; }
    replaceChildren(...nodes) { this.children.forEach(n => n.parent = null); this.children = []; this.append(...nodes); }
    addEventListener(name, fn) { this.handlers[name] = fn; }
    click() { this.handlers.click?.(); }
}
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture(enabledRows = ['LatestMovies']) {
    const root = new Node(), calls = [], observers = [];
    let user = 'alice', resolve;
    const document = { createElement: tag => new Node(tag) };
    const api = { getUrl: (p) => `/jellyfin/${p}`, getCurrentUserId: () => user,
        getJSON: url => { calls.push(url); return new Promise(r => resolve = r); } };
    class Observer { constructor(callback) { this.callback = callback; observers.push(this); } observe() {} disconnect() { this.closed = true; }
        fire(target) { this.callback([{ target, isIntersecting: true }]); } }
    const opened = [], rows = createRows({ document, IntersectionObserver: Observer, enabledRows,
        openItem: item => opened.push(item) });
    return { root, calls, observers, api, rows, opened, reply: row => resolve(row), switchUser: id => user = id };
}
test('only explicitly selected rows mount; no default requests', () => {
    const f = fixture([]); assert.equal(f.rows.mount(f.root, f.api, 'alice'), false);
    assert.equal(f.root.children.length, 0);
    const g = fixture(['LatestMovies', 'LatestMovies', 'UpcomingMovies']);
    assert.equal(g.rows.mount(g.root, g.api, 'alice'), true);
    assert.equal(g.root.children.length, 1);
    assert.deepEqual(g.calls, []);
});
test('seed headings alone name the Because You Watched section without changing lazy section indexing', async () => {
    const f = fixture(['BecauseYouWatched', 'LatestMovies']);
    f.rows.mount(f.root, f.api, 'alice');
    const seedSection = f.root.children[0], movieSection = f.root.children[1];
    assert.equal(seedSection.children.length, 1);
    f.observers[0].fire(seedSection); await tick();
    assert.deepEqual(f.calls, ['/jellyfin/Rowan/Home/BecauseYouWatched']);
    f.reply([{ Heading: 'Because You Watched One', Items: [{ Id: id, Type: 'Movie', Name: 'Film' }] }]);
    await tick();
    assert.equal(seedSection.children[0].children[0].children[0].textContent, 'Because You Watched One');
    f.observers[0].fire(movieSection); await tick();
    assert.equal(f.calls[1], '/jellyfin/Rowan/Home/Rows/LatestMovies');
});
test('bounded populated row renders tagged art and native Open identity lazily', async () => {
    const f = fixture(); f.rows.mount(f.root, f.api, 'alice');
    f.observers[0].fire(f.root.children[0]); await tick();
    assert.deepEqual(f.calls, ['/jellyfin/Rowan/Home/Rows/LatestMovies']);
    f.reply({ Kind: 'LatestMovies', Items: [{ Id: id, Type: 'Movie', Name: 'Own film', ImageTags: { Primary: 'ab' } }] });
    await tick();
    const card = f.root.children[0].children[1].children[0];
    assert.equal(card.tagName, 'BUTTON');
    assert.equal(card.children[0].src, `/jellyfin/Items/${id}/Images/Primary`);
    assert.equal(card.children[1].textContent, 'Own film');
    card.click(); assert.deepEqual(f.opened, [{ Id: id, Type: 'Movie' }]);
    f.observers[0].fire(f.root.children[0]); await tick(); assert.equal(f.calls.length, 1);
});
test('late previous-user response and detached Open cannot disclose old content', async () => {
    const f = fixture(); f.rows.mount(f.root, f.api, 'alice');
    const old = f.root.children[0]; f.observers[0].fire(old); await tick();
    f.switchUser('bob'); f.rows.dispose();
    f.reply({ Kind: 'LatestMovies', Items: [{ Id: id, Type: 'Movie', Name: 'Alice secret' }] }); await tick();
    assert.equal(f.root.children.length, 0);
    assert.equal(f.observers[0].closed, true);
    f.rows.mount(f.root, f.api, 'bob');
    assert.notEqual(f.root.children[0], old);
});
test('failed visible row retries once on a later intersection without a remount', async () => {
    const f = fixture(); f.rows.mount(f.root, f.api, 'alice');
    const section = f.root.children[0];
    f.observers[0].fire(section); await tick();
    f.reply({ Kind: 'LatestMovies', Items: null }); await tick();
    assert.equal(section.children[1].textContent, 'Row unavailable');
    f.observers[0].fire(section); await tick();
    assert.equal(f.calls.length, 2);
    f.reply({ Kind: 'LatestMovies', Items: [{ Id: id, Type: 'Movie', Name: 'Recovered' }] }); await tick();
    assert.equal(section.children[1].children[0].children[0].textContent, 'Recovered');
    f.observers[0].fire(section); await tick(); assert.equal(f.calls.length, 2);
});

test('empty personal rows disappear instead of leaving blank Home sections', async () => {
    const f = fixture(); f.rows.mount(f.root, f.api, 'alice');
    const section = f.root.children[0]; f.observers[0].fire(section); await tick();
    f.reply({ Kind: 'LatestMovies', Items: [] }); await tick();
    assert.equal(section.hidden, true);
    assert.equal(section.children[1].textContent, '');
});

test('malformed rows fail without rendering item data', async () => {
    const f = fixture(); f.rows.mount(f.root, f.api, 'alice');
    f.observers[0].fire(f.root.children[0]); await tick();
    f.reply({ Kind: 'LatestMovies', Items: Array.from({ length: 65 }, () => ({ Id: id, Name: 'Overflow' })) });
    await tick(); assert.equal(f.root.children[0].children[1].textContent, 'Row unavailable');
});

test('playback artwork prefers own thumb then series or parent art before primary', async () => {
    const f = fixture(['ContinueWatching']); f.rows.mount(f.root, f.api, 'alice');
    f.observers[0].fire(f.root.children[0]); await tick();
    const series = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', parent = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
    f.reply({ Kind: 'ContinueWatching', Items: [
        { Id: id, Type: 'Episode', Name: 'Own', ImageTags: { Thumb: '01', Primary: '02' }, SeriesId: series, SeriesThumbImageTag: '03' },
        { Id: id, Type: 'Episode', Name: 'Series thumb', SeriesId: series, SeriesThumbImageTag: '04', ImageTags: { Primary: '05' } },
        { Id: id, Type: 'Episode', Name: 'Parent thumb', ParentThumbItemId: parent, ParentThumbImageTag: '06' },
        { Id: id, Type: 'Episode', Name: 'Series backdrop', SeriesId: series, SeriesBackdropImageTag: '07' },
        { Id: id, Type: 'Episode', Name: 'Own backdrop', BackdropImageTags: ['08'] },
        { Id: id, Type: 'Episode', Name: 'Primary', ImageTags: { Primary: '09' } },
        { Id: id, Type: 'Episode', Name: 'No art' }
    ] }); await tick();
    const cards = f.root.children[0].children[1].children;
    assert.deepEqual(cards.map(card => card.children[0].src || null), [
        `/jellyfin/Items/${id}/Images/Thumb`, `/jellyfin/Items/${series}/Images/Thumb`,
        `/jellyfin/Items/${parent}/Images/Thumb`, `/jellyfin/Items/${series}/Images/Backdrop/0`,
        `/jellyfin/Items/${id}/Images/Backdrop/0`, `/jellyfin/Items/${id}/Images/Primary`, null
    ]);
});

test('untrusted inherited IDs never become image URLs; broken art tries safe fallback', async () => {
    const f = fixture(['ContinueWatching']); f.rows.mount(f.root, f.api, 'alice');
    f.observers[0].fire(f.root.children[0]); await tick();
    f.reply({ Kind: 'ContinueWatching', Items: [{ Id: id, Type: 'Episode', Name: 'Safe',
        SeriesId: '../private', SeriesThumbImageTag: 'ab', ParentThumbItemId: 'evil',
        ParentThumbImageTag: 'cd', ImageTags: { Primary: 'ef' } }] }); await tick();
    const card = f.root.children[0].children[1].children[0];
    assert.equal(card.children[0].src, `/jellyfin/Items/${id}/Images/Primary`);
    card.children[0].handlers.error();
    assert.equal(card.children.some(child => child.tagName === 'IMG'), false);
});

test('a failed thumb advances to the next valid image without crossing users', async () => {
    const f = fixture(['ContinueWatching']); f.rows.mount(f.root, f.api, 'alice');
    f.observers[0].fire(f.root.children[0]); await tick();
    f.reply({ Kind: 'ContinueWatching', Items: [{ Id: id, Type: 'Episode', Name: 'Two arts',
        ImageTags: { Thumb: 'ab', Primary: 'cd' } }] }); await tick();
    const card = f.root.children[0].children[1].children[0];
    const image = card.children[0];
    assert.equal(image.src, `/jellyfin/Items/${id}/Images/Thumb`);
    image.handlers.error();
    assert.equal(image.src, `/jellyfin/Items/${id}/Images/Primary`);
    f.switchUser('bob'); image.handlers.error();
    assert.equal(card.children.some(child => child.tagName === 'IMG'), false);
});

test('carousel buttons and keyboard scroll a bounded page and disable at edges', async () => {
    const f = fixture(['ContinueWatching']); f.rows.mount(f.root, f.api, 'alice');
    f.observers[0].fire(f.root.children[0]); await tick();
    f.reply({ Kind: 'ContinueWatching', Items: [{ Id: id, Type: 'Episode', Name: 'One' }] }); await tick();
    const section = f.root.children[0], track = section.children[1], controls = section.children[2];
    assert.equal(track.tabIndex, 0);
    assert.equal(controls.children.length, 2);
    track.clientWidth = 300; track.scrollWidth = 900; track.scrollLeft = 0;
    track.handlers.scroll();
    assert.equal(controls.children[0].disabled, true);
    assert.equal(controls.children[1].disabled, false);
    track.scrollBy = ({ left }) => { track.scrollLeft += left; track.handlers.scroll(); };
    controls.children[1].click(); assert.equal(track.scrollLeft, 300);
    let prevented = false;
    track.handlers.keydown({ key: 'ArrowLeft', preventDefault: () => prevented = true });
    assert.equal(prevented, true); assert.equal(track.scrollLeft, 0);
    track.scrollLeft = 600; track.handlers.scroll();
    assert.equal(controls.children[1].disabled, true);
});
