const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const base = path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web');
const fragment = fs.readFileSync(path.join(base, 'discovery.html'), 'utf8');
const css = fs.readFileSync(path.join(base, 'discovery.css'), 'utf8');
const script = () => fs.readFileSync(path.join(base, 'discovery.js'), 'utf8');
class Element {
    constructor(tag = 'div') { this.tagName = tag.toUpperCase(); this.children = []; this.attrs = {}; this.handlers = {}; this.textContent = ''; this.value = ''; this.disabled = false; this.hidden = false; this.checked = false; }
    setAttribute(key, value) { this.attrs[key] = String(value); }
    getAttribute(key) { return this.attrs[key] ?? null; }
    addEventListener(key, fn) { (this.handlers[key] ??= []).push(fn); }
    removeEventListener(key, fn) { this.handlers[key] = (this.handlers[key] || []).filter(x => x !== fn); }
    dispatch(key, props = {}) { for (const fn of this.handlers[key] || []) fn({ preventDefault() {}, ...props }); }
    focus() { this.focused = true; }
    showModal() { this.open = true; }
    close() { this.open = false; this.dispatch('close'); }
    appendChild(node) { this.children.push(node); return node; }
    replaceChildren(...nodes) { this.children = nodes; this.textContent = ''; }
    descendants() { return this.children.flatMap(n => [n, ...n.descendants()]); }
}
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 6; i++) await new Promise(resolve => setImmediate(resolve)); }
function setup(responses = [], posts = [], host = {}, details = []) {
    const ids = ['threepic-fin-details-dialog', 'threepic-fin-details-close', 'threepic-fin-details-body', 'threepic-fin-details-title', 'threepic-fin-details-meta', 'threepic-fin-details-overview', 'threepic-fin-details-status', 'threepic-fin-details-seasons', 'threepic-fin-details-open', 'threepic-fin-details-request', 'threepic-fin-calendar-tab', 'threepic-fin-calendar-panel', 'threepic-fin-calendar-prev', 'threepic-fin-calendar-next', 'threepic-fin-calendar-window', 'threepic-fin-calendar-radarr', 'threepic-fin-calendar-sonarr', 'threepic-fin-shared-requests-load', 'threepic-fin-shared-requests', 'threepic-fin-shared-requests-prev', 'threepic-fin-shared-requests-next', 'threepic-fin-shared-requests-page', 'threepic-fin-discover-tab', 'threepic-fin-downloads-tab', 'threepic-fin-downloads-panel', 'threepic-fin-downloads-radarr', 'threepic-fin-downloads-sonarr', 'threepic-fin-search-form', 'threepic-fin-search', 'threepic-fin-search-results', 'threepic-fin-movies', 'threepic-fin-tv', 'threepic-fin-requests', 'threepic-fin-recommendations', 'threepic-fin-search-prev', 'threepic-fin-search-next', 'threepic-fin-search-page', 'threepic-fin-discover-panel', 'threepic-fin-request-dialog', 'threepic-fin-request-form', 'threepic-fin-request-title', 'threepic-fin-request-status', 'threepic-fin-request-seasons', 'threepic-fin-request-4k-wrap', 'threepic-fin-request-4k', 'threepic-fin-request-submit', 'threepic-fin-request-cancel', ...['movies', 'tv', 'requests'].flatMap(name => [`threepic-fin-${name}-prev`, `threepic-fin-${name}-next`, `threepic-fin-${name}-page`])];
    for (const id of ids) assert.match(fragment, new RegExp(`id="${id}"`));
    const nodes = Object.fromEntries(ids.map(id => [id, new Element()]));
    const root = new Element(); root.querySelector = selector => nodes[selector.slice(1)] || null;
    const calls = [];
    const api = {
        getUrl: (route, params) => { calls.push(['url', route, params]); const u = new URL(route, 'https://example.test/jellyfin/'); for (const [k, v] of Object.entries(params || {})) u.searchParams.set(k, v); return u.href; },
        getJSON: (url, options) => { calls.push(['getJSON', url, options]); const isDetail = url.includes('TitleDetails'); const queue = isDetail && details.length ? details : responses; if (isDetail && queue === responses && !(responses[0] && (responses[0].MediaType || responses[0].mediaType))) return Promise.resolve(null); const reply = queue.shift(); return reply?.promise || (reply instanceof Error ? Promise.reject(reply) : Promise.resolve(reply)); },
        ajax: options => { calls.push(['ajax', options]); const reply = posts.shift(); return reply?.promise || (reply instanceof Error ? Promise.reject(reply) : Promise.resolve(reply)); }
    };
    const context = { document: { createElement: tag => new Element(tag) }, URL, AbortController };
    vm.runInNewContext(script(), context);
    const cleanup = context.ThreePicFinDiscovery.mount(root, api, host);
    const el = id => nodes[`threepic-fin-${id}`];
    const text = id => [el(id), ...el(id).descendants()].map(n => n.textContent).join(' ');
    return { nodes, root, calls, api, el, text, cleanup, flush };
}
const source = (Items = [], extra = {}) => ({ Items, Error: null, Page: 1, TotalPages: 1, ...extra });
const bundle = (Movies = source(), Tv = source(), Requests = source()) => ({ Movies, Tv, Requests });

test('hosted hidden Discovery waits for activation and coalesces rapid selections', async () => {
    const pending = deferred();
    const app = setup([pending], [], { deferInitialLoad: true });
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 0, 'hidden Home mount makes no Discovery request');
    app.cleanup.activate(); app.cleanup.activate();
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 1, 'rapid selections share first request');
    pending.resolve(bundle(source([{ Title: 'Ready', TmdbId: 7, MediaType: 'movie' }])));
    await app.flush();
    app.cleanup.activate();
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 1, 'return to Fin preserves loaded state');
    assert.match(app.text('movies'), /Ready/);
});

test('disposed hidden Discovery cannot fetch on a late activation', () => {
    const app = setup([], [], { deferInitialLoad: true });
    app.cleanup(); app.cleanup.activate();
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 0);
});

test('hyphenated backend library GUID offers Open and TV season request independently', async () => {
    const show = { TmdbId: 7, MediaType: 'tv', Title: 'Series' }, opened = [];
    const app = setup([bundle(source(), source([show])), { MediaType: 'tv', TmdbId: 7,
        LibraryItemId: '01234567-89ab-cdef-0123-456789abcdef', CanRequest: true, Seasons: [2] },
        { CanRequest: true, CanRequest4k: false, Seasons: [2] }], [], { openItem: (...args) => opened.push(args) });
    await app.flush();
    app.el('tv').children[0].descendants().find(n => n.textContent === 'Series').dispatch('click'); await app.flush();
    assert.equal(app.el('details-open').hidden, false);
    assert.equal(app.el('details-request').hidden, false);
    app.el('details-request').dispatch('click'); await app.flush();
    assert.equal(app.el('request-dialog').open, true);
    assert.match(app.text('request-seasons'), /Season 2/);
    assert.deepEqual(opened, []);
});

test('details without host callback never fall back to unverified navigation', async () => {
    const movie = { TmdbId: 9, MediaType: 'movie', Title: 'Film' };
    const app = setup([bundle(source([movie])), { MediaType: 'movie', TmdbId: 9,
        LibraryItemId: '01234567-89ab-cdef-0123-456789abcdef', CanRequest: false }]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.textContent === 'Film').dispatch('click'); await app.flush();
    assert.equal(app.el('details-open').hidden, true);
    assert.match(app.text('details-status'), /opening unavailable/);
});

test('household list is a deliberate separate read with source pagination, not request ownership', async () => {
    const pending = deferred(), second = deferred();
    const app = setup([bundle(), pending, second]); await app.flush();
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 1);
    app.el('shared-requests-load').dispatch('click');
    assert.match(app.text('shared-requests'), /Loading/);
    pending.resolve(source([{ Id: 4, Status: 2, Type: 'movie', TmdbId: 42, requestedBy: { name: 'secret' } }], { TotalPages: 2 })); await app.flush();
    assert.match(app.text('shared-requests'), /Title unavailable/);
    assert.doesNotMatch(app.text('shared-requests'), /secret/);
    assert.equal(app.el('shared-requests-next').disabled, false);
    app.el('shared-requests-next').dispatch('click');
    assert.match(app.calls.filter(c => c[0] === 'getJSON').at(-1)[1], /\/jellyfin\/3picFin\/SharedRequests\?page=2/);
    second.resolve(source([{ Id: 5, Status: 1, Type: 'tv', TmdbId: 44 }], { TotalPages: 2 })); await app.flush();
    assert.match(app.text('shared-requests'), /Title unavailable/);
    assert.match(app.text('requests'), /No Requests/);
});

test('fragment remains host-owned and Calendar is a keyboard accessible nested view', () => {
    assert.doesNotMatch(fragment, /<(script|link|main)\b/i);
    assert.match(fragment, /id="threepic-fin-calendar-tab"[^>]*aria-controls="threepic-fin-calendar-panel"/);
    assert.match(fragment, /id="threepic-fin-calendar-panel"[^>]*hidden/);
    assert.match(fragment, /<section aria-label="More to discover"><h3>More to discover<\/h3>/);
    assert.doesNotMatch(fragment, /<h3>Recommendations<\/h3>/);
    assert.match(css, /prefers-reduced-motion/);
    assert.match(css, /min-height:\s*44px/);
    assert.match(css, /:focus-visible/);
});
test('discovery renders movies, TV, recommendations and null-metadata personal requests safely', async () => {
    const app = setup([bundle(source([{ TmdbId: 12, MediaType: 'movie', Title: '<script>x</script>', PosterPath: '/ok.jpg' }]), source([{ TmdbId: 20, MediaType: 'tv', Title: 'A show' }]), source([{ Id: 4, Status: 2, Type: 'tv', TmdbId: 20, Title: null, PosterPath: null }]))]);
    await app.flush();
    assert.match(app.text('movies'), /<script>x<\/script>/);
    assert.match(app.text('tv'), /A show/);
    assert.match(app.text('recommendations'), /A show|<script>x<\/script>/);
    assert.match(app.text('requests'), /Title unavailable.*TV/);
    assert.doesNotMatch(app.text('requests'), /undefined|null/);
    assert.equal(app.el('discover-panel').hidden, false);
    assert.equal(app.el('movies').descendants().some(n => Object.hasOwn(n, 'innerHTML')), false);
    assert.deepEqual(app.calls.filter(c => c[0] === 'url').map(c => c[1]), ['3picFin/Discovery', '3picFin/TitleDetails']);
});
test('source-shaped Seerr request resolves metadata through authenticated detail without changing request ownership', async () => {
    // Seerr v3.4.1 (69f73a6f) server/routes/request.ts GET / joins
    // request.media; server/entity/Media.ts has tmdbId/mediaType/status but no
    // title/posterPath. Request-level title/poster are absent on the wire.
    // https://github.com/seerr-team/seerr/blob/v3.4.1/server/routes/request.ts#L125-L130
    const request = { Id: 4, Status: 2, Type: 'tv', TmdbId: 20, MediaType: 'tv', Title: null, PosterPath: null };
    const app = setup([bundle(source(), source(), source([request])),
        { MediaType: 'tv', TmdbId: 20, Title: 'Actual Series', PosterPath: '/actual.jpg' }]);
    await app.flush();
    assert.match(app.text('requests'), /Actual Series/);
    assert.doesNotMatch(app.text('requests'), /TMDb #20/);
    assert.equal(app.el('requests').descendants().find(n => n.tagName === 'IMG')?.src, 'https://image.tmdb.org/t/p/w342/actual.jpg');
    app.el('requests').descendants().find(n => n.tagName === 'IMG').dispatch('error');
    assert.match(app.text('requests'), /Poster unavailable/);
    assert.match(app.text('requests'), /Approved/);
    assert.equal(app.calls.filter(c => c[0] === 'url')[1][1], '3picFin/TitleDetails');
});
test('request enrichment refuses mismatched detail and never paints after disposal', async () => {
    const pending = deferred();
    const app = setup([bundle(source(), source(), source([{ Id: 8, Status: 1, Type: 'movie', MediaType: 'movie', TmdbId: 9 }]))], [], {}, [pending]);
    await app.flush();
    assert.match(app.text('requests'), /Loading title/);
    app.cleanup();
    pending.resolve({ MediaType: 'movie', TmdbId: 9, Title: 'Previous user', PosterPath: '/old.jpg' }); await app.flush();
    assert.doesNotMatch(app.text('requests'), /Previous user/);
    assert.equal(app.el('requests').children.length, 0);
    const mismatch = setup([bundle(source(), source(), source([{ Id: 8, Status: 1, Type: 'movie', MediaType: 'movie', TmdbId: 9 }])),
        { MediaType: 'movie', TmdbId: 10, Title: 'Wrong', PosterPath: '/wrong.jpg' }]);
    await mismatch.flush();
    assert.doesNotMatch(mismatch.text('requests'), /Wrong/);
    assert.match(mismatch.text('requests'), /Title unavailable/);
});
test('per-source error, loading, and empty states stay independent', async () => {
    const pending = deferred(); const app = setup([pending]);
    assert.match(app.text('movies'), /Loading/);
    assert.match(app.text('requests'), /Loading/);
    pending.resolve(bundle(source([], { Error: 'UpstreamUnavailable' }), source(), source([{ Id: 2, Status: 1, Type: 'movie', TmdbId: 3 }])));
    await app.flush();
    assert.match(app.text('movies'), /unavailable/i);
    assert.match(app.text('tv'), /No TV/);
    assert.match(app.text('requests'), /Title unavailable.*Movie/);
});
test('search uses ApiClient subpath URL, pagination and suppresses stale responses', async () => {
    const first = deferred(), second = deferred();
    const app = setup([bundle(), first, second]); await app.flush();
    app.el('search').value = 'old'; app.el('search-form').dispatch('submit');
    app.el('search').value = 'new'; app.el('search-form').dispatch('submit');
    second.resolve(source([{ TmdbId: 2, MediaType: 'movie', Title: 'New' }], { TotalPages: 2 })); await app.flush();
    first.resolve(source([{ TmdbId: 1, MediaType: 'movie', Title: 'Old' }])); await app.flush();
    assert.match(app.text('search-results'), /New/);
    assert.doesNotMatch(app.text('search-results'), /Old/);
    assert.equal(app.el('search-next').disabled, false);
    app.el('search-next').dispatch('click'); await app.flush();
    const urls = app.calls.filter(c => c[0] === 'getJSON').map(c => c[1]);
    assert.match(urls[2], /\/jellyfin\/3picFin\/Search\?query=new&page=1/);
    assert.match(urls[3], /query=new&page=2/);
    assert.equal(app.el('search-next').disabled, true);
});
test('search failures are distinct and empty search does not make a request', async () => {
    const app = setup([bundle(), new Error('private upstream detail')]); await app.flush();
    app.el('search').value = '  '; app.el('search-form').dispatch('submit'); await app.flush();
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 1);
    app.el('search').value = 'test'; app.el('search-form').dispatch('submit'); await app.flush();
    assert.match(app.text('search-results'), /unavailable/i);
    assert.doesNotMatch(app.text('search-results'), /private upstream detail/);
});
test('one inner Fin view contains requests, discovery and recommendations with nested Calendar', () => {
    assert.match(fragment, /id="threepic-fin-discover-panel"[\s\S]*id="threepic-fin-requests"/);
    assert.match(fragment, /id="threepic-fin-discover-panel"[\s\S]*id="threepic-fin-recommendations"/);
    assert.doesNotMatch(fragment, /id="threepic-fin-requests-panel"/);
    assert.doesNotMatch(fragment, /id="threepic-fin-my-requests"/);
    assert.match(fragment, /3pic Fin views[\s\S]*threepic-fin-calendar-tab/);
});
test('movie, TV and request pages advance independently without changing other source pages', async () => {
    const app = setup([bundle(source([{ Title: 'First movie', MediaType: 'movie', TmdbId: 1 }], { TotalPages: 3 }), source([{ Title: 'First TV', MediaType: 'tv', TmdbId: 2 }], { TotalPages: 1 }), source([{ Id: 3, Status: 2, Type: 'movie' }], { TotalPages: 2 })),
        bundle(source([{ Title: 'Second movie', MediaType: 'movie', TmdbId: 4 }], { Page: 2, TotalPages: 3 }), source([{ Title: 'First TV', MediaType: 'tv', TmdbId: 2 }], { TotalPages: 1 }), source([{ Id: 3, Status: 2, Type: 'movie' }], { TotalPages: 2 })),
        bundle(source([{ Title: 'Second movie', MediaType: 'movie', TmdbId: 4 }], { Page: 2, TotalPages: 3 }), source([{ Title: 'First TV', MediaType: 'tv', TmdbId: 2 }], { TotalPages: 1 }), source([{ Id: 5, Status: 1, Type: 'tv' }], { Page: 2, TotalPages: 2 }))]);
    await app.flush();
    assert.equal(app.el('tv-next').disabled, true);
    app.el('movies-next').dispatch('click'); await app.flush();
    assert.match(app.text('movies'), /Second movie/);
    assert.match(app.text('tv'), /First TV/);
    assert.match(app.calls.filter(c => c[0] === 'getJSON')[1][1], /moviePage=2&tvPage=1&requestsPage=1/);
    app.el('requests-next').dispatch('click'); await app.flush();
    assert.match(app.calls.filter(c => c[0] === 'getJSON')[2][1], /moviePage=2&tvPage=1&requestsPage=2/);
    assert.match(app.text('requests'), /Pending/);
    assert.equal(app.el('requests-next').disabled, true);
    app.el('movies-prev').dispatch('click'); await app.flush();
    assert.match(app.calls.filter(c => c[0] === 'getJSON')[3][1], /moviePage=1&tvPage=1&requestsPage=2/);
});
test('request status uses pinned Seerr labels and unknown values do not display raw numbers', async () => {
    const app = setup([bundle(source(), source(), source([1, 2, 3, 4, 5, 99].map((Status, Id) => ({ Status, Id, Type: 'movie' }))))]);
    await app.flush();
    const values = app.el('requests').children.map(card => card.descendants().map(n => n.textContent).join(' '));
    assert.deepEqual(values.map(value => value.match(/Request status: ([^ ]+)/)?.[1]), ['Pending', 'Approved', 'Declined', 'Failed', 'Completed', 'Unknown']);
});
test('camelCase DTOs render in the Fin panel', async () => {
    const app = setup([{ movies: { items: [{ tmdbId: 7, mediaType: 'movie', title: 'Lowercase' }], error: null }, tv: { items: [], error: null }, requests: { items: [], error: null } }]);
    await app.flush();
    assert.match(app.text('movies'), /Lowercase/);
    assert.equal(app.el('discover-panel').hidden, false);
});
test('movie request is confirmed once with default profile and verified in personal list', async () => {
    const pending = deferred();
    const movie = { TmdbId: 9, MediaType: 'movie', Title: 'Film' };
    const app = setup([bundle(source([movie])), { CanRequest: true, CanRequest4k: false, MediaStatus: 2, Seasons: [] },
        bundle(source([movie]), source(), source([{ Id: 91, Status: 1, Type: 'movie', TmdbId: 9, Is4k: false }]))], [pending]);
    await app.flush();
    app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    assert.equal(app.el('request-dialog').open, true);
    assert.equal(app.el('request-4k-wrap').hidden, true);
    app.el('request-form').dispatch('submit'); app.el('request-form').dispatch('submit');
    assert.equal(app.calls.filter(c => c[0] === 'ajax').length, 1);
    const sent = app.calls.find(c => c[0] === 'ajax')[1];
    assert.equal(sent.type, 'POST');
    assert.match(sent.url, /\/jellyfin\/3picFin\/Requests$/);
    assert.deepEqual(JSON.parse(sent.data), { mediaType: 'movie', mediaId: 9, is4k: false });
    pending.resolve({ Id: 91, Status: 1 }); await app.flush();
    assert.match(app.text('request-status'), /Pending/);
    assert.equal(app.el('request-submit').disabled, true);
});

test('TV requires explicit seasons, 4K permission, and never retries ambiguous POST', async () => {
    const show = { TmdbId: 7, MediaType: 'tv', Title: 'Series' };
    const app = setup([bundle(source(), source([show])), { CanRequest: true, CanRequest4k: true, Seasons: [1, 3] },
        bundle(source(), source([show]), source())], [new Error('timeout')]);
    await app.flush();
    app.el('tv').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    app.el('request-form').dispatch('submit'); assert.equal(app.calls.filter(c => c[0] === 'ajax').length, 0);
    const check = app.el('request-seasons').descendants().find(n => n.tagName === 'INPUT'); check.checked = true;
    app.el('request-4k').checked = true;
    app.el('request-form').dispatch('submit'); await app.flush();
    assert.deepEqual(JSON.parse(app.calls.find(c => c[0] === 'ajax')[1].data), { mediaType: 'tv', mediaId: 7, seasons: [1], is4k: true });
    assert.match(app.text('request-status'), /unknown|could not verify/i);
    assert.equal(app.el('request-submit').disabled, true);
});

test('a duplicate response reconciles matching personal request without claiming new creation', async () => {
    const movie = { TmdbId: 9, MediaType: 'movie', Title: 'Film' };
    const duplicate = Object.assign(new Error('Conflict'), { status: 409 });
    const app = setup([bundle(source([movie])), { CanRequest: true, CanRequest4k: false, Seasons: [] },
        bundle(source([movie]), source(), source([{ Id: 4, Status: 1, TmdbId: 9, Type: 'movie', Is4k: false }]))], [duplicate]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    app.el('request-form').dispatch('submit'); await app.flush();
    assert.match(app.text('request-status'), /matching request.*could not be verified/i);
    assert.equal(app.calls.filter(c => c[0] === 'ajax').length, 1);
    app.el('request-form').dispatch('submit');
    assert.equal(app.calls.filter(c => c[0] === 'ajax').length, 1);
});

test('4K-only permission selects its only permitted variant', async () => {
    const app = setup([bundle(source([{ TmdbId: 8, MediaType: 'movie', Title: '4K' }])), { CanRequest: false, CanRequest4k: true, Seasons: [] }]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    assert.equal(app.el('request-4k').checked, true);
    assert.equal(app.el('request-submit').disabled, false);
});

test('declined personal movie request does not suppress a new submission', async () => {
    const movie = { TmdbId: 9, MediaType: 'movie', Title: 'Film' };
    const declined = { Id: 3, Status: 3, TmdbId: 9, Type: 'movie', Is4k: false };
    const app = setup([bundle(source([movie]), source(), source([declined])),
        { CanRequest: true, CanRequest4k: false, Seasons: [] },
        bundle(source([movie]), source(), source([declined, { ...declined, Id: 4, Status: 1 }]))], [{ Id: 4, Status: 1 }]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    app.el('request-form').dispatch('submit'); await app.flush();
    assert.equal(app.calls.filter(c => c[0] === 'ajax').length, 1);
    assert.match(app.text('request-status'), /Pending/);
});

test('TV creation reconciles returned ID when Seerr trims already requested seasons', async () => {
    const show = { TmdbId: 7, MediaType: 'tv', Title: 'Series' };
    const app = setup([bundle(source(), source([show])), { CanRequest: true, CanRequest4k: true, Seasons: [1, 2] },
        bundle(source(), source([show]), source([{ Id: 71, Status: 2, Type: 'tv', TmdbId: 7, Is4k: true, Seasons: [2] }]))], [{ Id: 71, Status: 2 }]);
    await app.flush(); app.el('tv').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    for (const input of app.el('request-seasons').descendants().filter(n => n.tagName === 'INPUT')) input.checked = true;
    app.el('request-4k').checked = true;
    app.el('request-form').dispatch('submit'); await app.flush();
    assert.deepEqual(JSON.parse(app.calls.find(c => c[0] === 'ajax')[1].data).seasons, [1, 2]);
    assert.match(app.text('request-status'), /Approved/);
});

test('blocklisted status disables request regardless of permission and announces why', async () => {
    const movie = { TmdbId: 9, MediaType: 'movie', Title: 'Blocked' };
    const app = setup([bundle(source([movie])), { CanRequest: true, CanRequest4k: true, MediaStatus: 6, MediaStatus4k: 1, Seasons: [] }]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    assert.equal(app.el('request-submit').disabled, true);
    assert.match(app.text('request-status'), /blocklisted/i);
    app.el('request-4k').checked = true; app.el('request-form').dispatch('submit');
    assert.equal(app.calls.filter(c => c[0] === 'ajax').length, 0);
});

test('variant availability message uses selected standard or 4K status with camelCase DTOs', async () => {
    const movie = { tmdbId: 9, mediaType: 'movie', title: 'Film' };
    const app = setup([bundle(source([movie])), { canRequest: true, canRequest4k: true, mediaStatus: 1, mediaStatus4k: 5, seasons: [] }]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    assert.doesNotMatch(app.text('request-status'), /available; check/i);
    app.el('request-4k').checked = true; app.el('request-4k').dispatch('change');
    assert.match(app.text('request-status'), /available; check/i);
    assert.equal(app.el('request-submit').disabled, false);
});

test('successful POST with no returned ID on the first personal page stays unknown', async () => {
    const movie = { TmdbId: 9, MediaType: 'movie', Title: 'Film' };
    const app = setup([bundle(source([movie])), { CanRequest: true, CanRequest4k: false, Seasons: [] },
        bundle(source([movie]), source(), source([{ Id: 3, Status: 1, TmdbId: 9, Type: 'movie', Is4k: false }]))], [{ Id: 4, Status: 1 }]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    app.el('request-form').dispatch('submit'); await app.flush();
    assert.match(app.text('request-status'), /Outcome unknown/);
    assert.equal(app.el('request-submit').disabled, true);
});

test('failed POST with only a declined personal request stays unknown', async () => {
    const movie = { TmdbId: 9, MediaType: 'movie', Title: 'Film' };
    const declined = { Id: 3, Status: 3, TmdbId: 9, Type: 'movie', Is4k: false };
    const app = setup([bundle(source([movie])), { CanRequest: true, CanRequest4k: false, Seasons: [] },
        bundle(source([movie]), source(), source([declined]))], [new Error('timeout')]);
    await app.flush(); app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click'); await app.flush();
    app.el('request-form').dispatch('submit'); await app.flush();
    assert.match(app.text('request-status'), /Outcome unknown/);
    assert.equal(app.el('request-submit').disabled, true);
});

test('missing options fail closed and dialog closure ignores stale detail', async () => {
    const pending = deferred();
    const app = setup([bundle(source([{ TmdbId: 5, MediaType: 'movie', Title: 'Title' }])), pending]); await app.flush();
    app.el('movies').children[0].descendants().find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('Request')).dispatch('click');
    app.el('request-cancel').dispatch('click');
    pending.resolve({ CanRequest: true, CanRequest4k: true, Seasons: [] }); await app.flush();
    assert.equal(app.el('request-dialog').open, false);
    assert.equal(app.calls.filter(c => c[0] === 'ajax').length, 0);
});

test('cleanup prevents late rendering and detaches controls', async () => {
    const pending = deferred(); const app = setup([pending]);
    app.cleanup(); pending.resolve(bundle(source([{ TmdbId: 1, MediaType: 'movie', Title: 'Late' }]))); await app.flush();
    assert.doesNotMatch(app.text('movies'), /Late/);
    app.el('search').value = 'later'; app.el('search-form').dispatch('submit'); await app.flush();
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 1);
});

// Downloads behavior is exercised against the same host-owned fragment harness.
test('Downloads is a separate lazy shared view with title-wide status and safe disclosure', async () => {
    const app = setup([bundle(), { Radarr: source([{ Source: 'Radarr', MediaType: 'movie', TitleId: 7, Title: '<img src=x>', State: 'Downloading', Progress: .257 }]), Sonarr: source([{ Source: 'Sonarr', MediaType: 'tv', TitleId: 8, Title: 'Series', State: 'Queued', Progress: null }], { Partial: true }) }]);
    await app.flush();
    assert.deepEqual(app.calls.filter(c => c[0] === 'url').map(c => c[1]), ['3picFin/Discovery']);
    app.el('downloads-tab').dispatch('click'); await app.flush();
    assert.equal(app.el('discover-panel').hidden, true);
    assert.equal(app.el('downloads-panel').hidden, false);
    assert.match(app.calls.find(c => c[0] === 'getJSON' && c[1].includes('Downloads'))[1], /\/jellyfin\/3picFin\/Downloads$/);
    assert.match(app.text('downloads-radarr'), /<img src=x>.*Downloading.*26%/);
    assert.match(app.text('downloads-sonarr'), /Series.*Queued.*Partial/i);
    assert.match(fragment, /Shared Downloads/);
    assert.doesNotMatch(fragment, /Radarr<|Sonarr<|default server and profile|not personalized/i);
    assert.doesNotMatch(app.text('requests'), /Series|<img/);
    assert.equal(app.el('downloads-radarr').descendants().some(n => Object.hasOwn(n, 'innerHTML')), false);
    app.el('discover-tab').dispatch('click');
    assert.equal(app.el('discover-panel').hidden, false);
});

test('Downloads sources handle empty and errors independently without leaking upstream details', async () => {
    const pending = deferred(); const app = setup([bundle(), pending]); await app.flush();
    app.el('downloads-tab').dispatch('click');
    assert.match(app.text('downloads-radarr'), /Loading/);
    pending.resolve({ radarr: { items: [], error: null, partial: false }, sonarr: { items: [], error: 'private /path/file.mkv', partial: false } }); await app.flush();
    assert.match(app.text('downloads-radarr'), /No .*downloads/i);
    assert.match(app.text('downloads-sonarr'), /unavailable/i);
    assert.doesNotMatch(app.text('downloads-sonarr'), /private|path|mkv/);
});

test('404 disables Downloads without affecting Discover; other failures remain retryable', async () => {
    const disabled = Object.assign(new Error('disabled'), { status: 404 });
    const app = setup([bundle(), disabled]); await app.flush();
    app.el('downloads-tab').dispatch('click'); await app.flush();
    assert.equal(app.el('downloads-tab').hidden, true);
    assert.equal(app.el('discover-panel').hidden, false);
    assert.equal(app.el('downloads-panel').hidden, true);
    assert.match(app.text('requests'), /No Requests/);
    const retry = setup([bundle(), new Error('private'), { Radarr: source(), Sonarr: source() }]); await retry.flush();
    retry.el('downloads-tab').dispatch('click'); await retry.flush();
    assert.match(retry.text('downloads-radarr'), /unavailable/i);
    retry.el('discover-tab').dispatch('click'); retry.el('downloads-tab').dispatch('click'); await retry.flush();
    assert.match(retry.text('downloads-radarr'), /No .*downloads/i);
});

test('disabled module response removes Downloads and path-like titles use safe identity fallback', async () => {
    const disabled = setup([bundle(), { Radarr: { Items: [], Error: 'Disabled' }, Sonarr: { Items: [], Error: 'Disabled' } }]);
    await disabled.flush(); disabled.el('downloads-tab').dispatch('click'); await disabled.flush();
    assert.equal(disabled.el('downloads-tab').hidden, true);
    assert.equal(disabled.el('discover-panel').hidden, false);
    const app = setup([bundle(), { Radarr: source([{ Title: '/media/secret/file.mkv', TitleId: 7, State: 'Unknown' }, { Title: 'secret.mkv', TitleId: 8 }]), Sonarr: source() }]);
    await app.flush(); app.el('downloads-tab').dispatch('click'); await app.flush();
    assert.doesNotMatch(app.text('downloads-radarr'), /secret|file.mkv|media/);
    assert.match(app.text('downloads-radarr'), /TMDb #7.*TMDb #8/);
});

test('Downloads hides release filenames and release-style basenames, preserving ordinary dotted titles', async () => {
    const suspicious = [
        'Secret.2024.1080p.WEB-DL.mkv (1)',
        'Film.2024.REMUX',
        'Film.2024.HEVC',
        'Film.mkv!',
        'Film.2024',
        'Film.REMUX!copy',
        'Secret.2024.1080p.WEB-DL.mkv',
        'Show.S01E02.2160p.WEB-DL',
        'Show.S01E02.1080p.BluRay.x265',
        'Secret 2024 1080p WEB-DL',
        'Series S02E03 720p HDTV',
        'Secret.2024.1080p.WEB-DL.nzb.1',
        'Secret.2024.1080p.WEB-DL.mp4.part',
        'Secret.2024.1080p.WEB-DL.torrent [copy]',
        'Secret.2024.1080p.WEB-DL.mkv/other',
    ];
    const ordinary = ['Dr. Strangelove', 'Mr. Robot', 'S.W.A.T.', 'Spider-Man: Across the Spider-Verse'];
    const app = setup([bundle(), { Radarr: source([...suspicious, ...ordinary].map((Title, i) => ({ Title, TitleId: i + 1 }))), Sonarr: source([{ Title: 'Series.S02E03.1080p.WEB-DL.mkv (1)', TitleId: 50 }]) }]);
    await app.flush(); app.el('downloads-tab').dispatch('click'); await app.flush();
    const titles = app.el('downloads-radarr').children.map(row => row.descendants().find(n => n.tagName === 'H4')?.textContent);
    for (let i = 0; i < suspicious.length; i++) assert.equal(titles[i], `Movie · TMDb #${i + 1}`);
    assert.deepEqual(titles.slice(suspicious.length), ordinary);
    assert.equal(app.el('downloads-sonarr').children[0].descendants().find(n => n.tagName === 'H4')?.textContent, 'TV · TVDb #50');
    assert.doesNotMatch(app.text('downloads-radarr') + app.text('downloads-sonarr'), /Secret|Series\.S02|Show\.S01/);
});

test('leaving Downloads or unmounting aborts and ignores stale responses', async () => {
    const first = deferred(), second = deferred(); const app = setup([bundle(), first, second]); await app.flush();
    app.el('downloads-tab').dispatch('click');
    const signal = app.calls.filter(c => c[0] === 'getJSON')[1];
    app.el('discover-tab').dispatch('click');
    assert.equal(signal[2]?.signal?.aborted, true);
    app.el('downloads-tab').dispatch('click');
    first.resolve({ Radarr: source([{ Title: 'Stale' }]), Sonarr: source() }); await app.flush();
    assert.doesNotMatch(app.text('downloads-radarr'), /Stale/);
    app.cleanup();
    assert.equal(app.calls.filter(c => c[0] === 'getJSON')[2][2]?.signal?.aborted, true);
    second.resolve({ Radarr: source([{ Title: 'Late' }]), Sonarr: source() }); await app.flush();
    assert.doesNotMatch(app.text('downloads-radarr'), /Late/);
    app.el('downloads-tab').dispatch('click');
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 3);
});

test('Calendar loads lazily with a half-open 31-day UTC window and independent sources', async () => {
    const app = setup([bundle(), { Radarr: source([{ Title: 'Movie', TitleId: 7, EventType: 'Digital', Date: '2026-09-29T00:00:00Z' }]), Sonarr: source([{ Title: 'Show', TitleId: 8, EventType: 'Episode', SeasonNumber: 2, EpisodeNumber: 3, EpisodeTitle: 'Pilot', Date: '2026-09-30T21:00:00Z' }], { Partial: true }) }]);
    await app.flush();
    assert.deepEqual(app.calls.filter(c => c[0] === 'url').map(c => c[1]), ['3picFin/Discovery']);
    app.el('calendar-tab').dispatch('click'); await app.flush();
    const request = app.calls.find(c => c[0] === 'url' && c[1] === '3picFin/Calendar');
    assert.ok(request);
    assert.equal((Date.parse(request[2].end) - Date.parse(request[2].start)) / 86400000, 31);
    assert.match(request[2].start, /^\d{4}-\d\d-\d\d$/);
    assert.equal(app.el('discover-panel').hidden, true);
    assert.equal(app.el('calendar-panel').hidden, false);
    assert.equal(app.el('calendar-tab').getAttribute('aria-pressed'), 'true');
    assert.match(app.text('calendar-radarr'), /Movie.*Digital.*title-wide/i);
    assert.match(app.text('calendar-sonarr'), /Show.*S02E03.*Pilot.*Partial/i);
    assert.doesNotMatch(app.text('requests'), /Pilot/);
});

test('Calendar navigation advances bounded windows and suppresses stale results', async () => {
    const old = deferred(), fresh = deferred();
    const app = setup([bundle(), old, fresh]); await app.flush();
    app.el('calendar-tab').dispatch('click');
    old.resolve({ Radarr: source([{ Title: 'Old' }]), Sonarr: source() }); await app.flush();
    app.el('calendar-next').dispatch('click');
    const urls = app.calls.filter(c => c[0] === 'url' && c[1] === '3picFin/Calendar');
    assert.equal(Date.parse(urls[1][2].start), Date.parse(urls[0][2].end));
    assert.equal((Date.parse(urls[1][2].end) - Date.parse(urls[1][2].start)) / 86400000, 31);
    fresh.resolve({ Radarr: source([{ Title: 'Fresh', Date: '2026-10-01T00:00:00Z', EventType: 'Cinema' }]), Sonarr: source() }); await app.flush();
    assert.match(app.text('calendar-radarr'), /Fresh/);
    assert.doesNotMatch(app.text('calendar-radarr'), /Old/);
});

test('Calendar holds navigation during a pending read and releases it after failure', async () => {
    const pending = deferred();
    const app = setup([bundle(), pending, new Error('private'), { Radarr: source(), Sonarr: source() }]); await app.flush();
    app.el('calendar-tab').dispatch('click');
    assert.equal(app.el('calendar-next').disabled, true);
    for (let i = 0; i < 25; i++) app.el('calendar-next').dispatch('click');
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 2);
    pending.resolve({ Radarr: source(), Sonarr: source() }); await app.flush();
    assert.equal(app.el('calendar-next').disabled, false);
    app.el('calendar-next').dispatch('click'); await app.flush();
    assert.match(app.text('calendar-radarr'), /unavailable/i);
    assert.equal(app.el('calendar-next').disabled, false);
    app.el('calendar-next').dispatch('click'); await app.flush();
    assert.match(app.text('calendar-radarr'), /No Movie/i);
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 4);
});

test('Calendar abort on view exit releases navigation for reentry without stale rendering', async () => {
    const old = deferred(), fresh = deferred();
    const app = setup([bundle(), old, fresh]); await app.flush();
    app.el('calendar-tab').dispatch('click');
    app.el('discover-tab').dispatch('click');
    assert.equal(app.calls.filter(c => c[0] === 'getJSON')[1][2].signal.aborted, true);
    app.el('calendar-tab').dispatch('click');
    assert.equal(app.el('calendar-next').disabled, true);
    old.resolve({ Radarr: source([{ Title: 'Stale' }]), Sonarr: source() }); await app.flush();
    assert.equal(app.el('calendar-next').disabled, true);
    fresh.resolve({ Radarr: source([{ Title: 'Current' }]), Sonarr: source() }); await app.flush();
    assert.match(app.text('calendar-radarr'), /Current/);
    assert.doesNotMatch(app.text('calendar-radarr'), /Stale/);
    assert.equal(app.el('calendar-next').disabled, false);
});

test('Calendar 404 hides only its tab and returns to Discover', async () => {
    const disabled = Object.assign(new Error('disabled'), { status: 404 });
    const app = setup([bundle(), disabled]); await app.flush();
    app.el('calendar-tab').dispatch('click'); await app.flush();
    assert.equal(app.el('calendar-tab').hidden, true);
    assert.equal(app.el('calendar-panel').hidden, true);
    assert.equal(app.el('discover-panel').hidden, false);
    assert.equal(app.el('downloads-tab').hidden, false);
    app.el('calendar-tab').dispatch('click');
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 2);
});

test('Calendar renders camelCase source and event DTOs', async () => {
    const app = setup([bundle(), { radarr: { items: [{ title: 'Camel film', titleId: 51, eventType: 'Digital', date: '2026-09-28T00:00:00Z' }], error: null }, sonarr: { items: [{ title: 'Camel show', titleId: 52, eventType: 'Episode', seasonNumber: 1, episodeNumber: 2, episodeTitle: 'Pilot', date: '2026-09-29T00:00:00Z' }], partial: true } }]);
    await app.flush(); app.el('calendar-tab').dispatch('click'); await app.flush();
    assert.match(app.text('calendar-radarr'), /Camel film.*Digital/);
    assert.match(app.text('calendar-sonarr'), /Camel show.*S01E02.*Pilot.*Partial/i);
});

test('Calendar isolates disabled, errors, partial, empty and suspicious metadata', async () => {
    const app = setup([bundle(), { Radarr: source([
        { Title: '/secret/film.mkv', TitleId: 9, EventType: 'Cinema', Date: '2026-09-28T00:00:00Z' },
        { Title: 'Film.2024.REMUX', TitleId: 10, EventType: 'Digital', Date: '2026-09-29T00:00:00Z' },
        { Title: 'Dr. Strangelove', TitleId: 11, EventType: 'Physical', Date: '2026-09-30T00:00:00Z' }
    ], { Partial: true }), Sonarr: source([{ Title: 'Show', TitleId: 12, EventType: 'Episode', SeasonNumber: 1, EpisodeNumber: 2, EpisodeTitle: 'Episode.S01E02.WEB-DL.mkv', Date: '2026-09-30T00:00:00Z' }]) }]);
    await app.flush(); app.el('calendar-tab').dispatch('click'); await app.flush();
    assert.match(app.text('calendar-radarr'), /TMDb #9.*TMDb #10.*Dr. Strangelove.*Partial/i);
    assert.doesNotMatch(app.text('calendar-radarr') + app.text('calendar-sonarr'), /secret|REMUX|WEB-DL|mkv/i);
    assert.match(app.text('calendar-sonarr'), /episode title unavailable/i);
    assert.equal(app.el('calendar-radarr').descendants().some(n => Object.hasOwn(n, 'innerHTML')), false);
    const disabled = setup([bundle(), { Radarr: { Items: [], Error: 'Disabled' }, Sonarr: { Items: [], Error: 'Disabled' } }]);
    await disabled.flush(); disabled.el('calendar-tab').dispatch('click'); await disabled.flush();
    assert.equal(disabled.el('calendar-tab').hidden, true);
    assert.equal(disabled.el('discover-panel').hidden, false);
    assert.equal(disabled.el('downloads-tab').hidden, false);
    const partial = setup([bundle(), { Radarr: { Items: [], Error: 'private /secret', Partial: true }, Sonarr: source() }]);
    await partial.flush(); partial.el('calendar-tab').dispatch('click'); await partial.flush();
    assert.match(partial.text('calendar-radarr'), /unavailable/i);
    assert.match(partial.text('calendar-sonarr'), /No TV/i);
    assert.doesNotMatch(partial.text('calendar-radarr'), /secret/);
});

test('Calendar clears previously rendered household titles at user teardown', async () => {
    const app = setup([bundle(), { Radarr: source([{ Title: 'Old household', TitleId: 4 }]), Sonarr: source() }]);
    await app.flush(); app.el('calendar-tab').dispatch('click'); await app.flush();
    assert.match(app.text('calendar-radarr'), /Old household/);
    app.cleanup();
    assert.doesNotMatch(app.text('calendar-radarr'), /Old household/);
});

test('Calendar cancellation on tab switch and unmount prevents stale disclosure', async () => {
    const pending = deferred(); const app = setup([bundle(), pending]); await app.flush();
    app.el('calendar-tab').dispatch('click');
    app.el('downloads-tab').dispatch('click');
    assert.equal(app.calls.filter(c => c[0] === 'getJSON')[1][2].signal.aborted, true);
    app.cleanup();
    assert.equal(app.text('calendar-radarr').trim(), '');
    assert.equal(app.text('calendar-sonarr').trim(), '');
    pending.resolve({ Radarr: source([{ Title: 'Private old user' }]), Sonarr: source() }); await app.flush();
    assert.doesNotMatch(app.text('calendar-radarr'), /Private old user/);
    app.el('calendar-tab').dispatch('click');
    assert.equal(app.calls.filter(c => c[0] === 'getJSON').length, 3);
});
