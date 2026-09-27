// Run with: node --test tests/Rowan.Jellyfin.Plugin.Tests/configPage.smoke.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Configuration/configPage.html'), 'utf8');
const script = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)?.[1];
assert.ok(script, 'config page script exists');
const A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

class Element {
    constructor(tag = 'div') {
        this.tag = tag;
        this.children = [];
        this.handlers = {};
        this.checked = false;
        this.disabled = false;
        this.hidden = false;
        this.value = '';
        this.textContent = '';
        this.type = '';
    }
    appendChild(child) { this.children.push(child); return child; }
    replaceChildren(...children) { this.children = children; }
    addEventListener(name, handler) { this.handlers[name] = handler; }
    dispatch(name) { this.handlers[name]({ preventDefault() {} }); }
    descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
    querySelectorAll(selector) {
        const nodes = this.descendants();
        if (selector === 'input[type="checkbox"]') return nodes.filter(node => node.tag === 'input' && node.type === 'checkbox');
        if (selector === 'input[type="checkbox"]:checked') return nodes.filter(node => node.tag === 'input' && node.type === 'checkbox' && node.checked);
        throw Error(`Unimplemented selector: ${selector}`);
    }
}

function setup(config, folders, options = {}) {
    let persisted = structuredClone(config);
    const ids = ['RowanConfigPage', 'RowanConfigForm', 'RecentlyAddedLibraries', 'RowanSaveButton',
        'RowanConfigError', 'RowanConfigStatus', 'HomeEnabled', 'CombinedPlaybackRowEnabled', 'DiscoverRowEnabled', 'DiscoverMoviesRowEnabled', 'DiscoverTvRowEnabled', 'CombinedPlaybackHideWatched', 'MyRequestsRowEnabled', 'MyRequestsHideWatched', 'CollectionsRowEnabled', 'LiveTvRowEnabled', 'BecauseYouWatchedRowEnabled', 'BecauseYouWatchedHideWatched', 'HeroTrustedFilesystemEnabled', 'DiscoveryPageEnabled', 'SharedRequestsEnabled', 'DownloadsEnabled', 'CalendarEnabled',
        'UpcomingMoviesRowEnabled', 'UpcomingShowsRowEnabled', 'RadarrBaseUrl', 'RadarrApiKey', 'SonarrBaseUrl', 'SonarrApiKey', 'RecentlyAddedAll',
        'RecentlyAddedSelected', 'RecentlyAddedNone'];
    const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
    const page = elements.RowanConfigPage;
    const list = elements.RecentlyAddedLibraries;
    const form = elements.RowanConfigForm;
    const modes = ['RecentlyAddedAll', 'RecentlyAddedSelected', 'RecentlyAddedNone'];
    modes.forEach((id, index) => { elements[id].value = ['all', 'selected', 'none'][index]; });
    page.querySelector = selector => {
        if (selector === 'input[name="RecentlyAddedMode"]:checked') {
            return modes.map(id => elements[id]).find(mode => mode.checked) || null;
        }
        return elements[selector.slice(1)];
    };
    const writes = [];
    const calls = [];
    const api = {
        getUrl: endpoint => { calls.push(endpoint); return endpoint; },
        getJSON: async () => { if (options.loadFails) throw Error('library load failed'); return options.getFolders ? options.getFolders() : folders; },
        getPluginConfiguration: async () => options.getConfig ? options.getConfig() : structuredClone(persisted),
        updatePluginConfiguration: async (_id, updated) => {
            if (options.saveFails) throw Error('save failed');
            writes.push(updated);
            const result = options.update ? await options.update(updated) : {};
            persisted = structuredClone(updated);
            return result;
        }
    };
    vm.runInNewContext(script, {
        document: { querySelector: () => page, createElement: tag => new Element(tag) },
        ApiClient: api,
        Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} },
        Promise, Set, Array
    });
    const flush = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
    return { elements, list, page, form, writes, calls, flush,
        select(mode) {
            modes.forEach(id => { elements[id].checked = elements[id].value === mode; });
            form.dispatch('change');
        },
        async load() { page.dispatch('pageshow'); await flush(); },
        async save() { form.dispatch('submit'); await flush(); }
    };
}

const folders = [{ Name: '<img src=x onerror=alert(1)>', ItemId: A }, { Name: 'Movies', ItemId: B }];

test('personal My Requests flag and watched filter snapshot independently of household requests', async () => {
    const pending = deferred();
    const app = setup({ HomeEnabled: true, RecentlyAddedLibraryIds: null }, folders, { update: () => pending.promise });
    await app.load();
    assert.equal(app.elements.MyRequestsRowEnabled.checked, false);
    app.elements.MyRequestsRowEnabled.checked = true;
    app.elements.MyRequestsHideWatched.checked = true;
    app.form.dispatch('submit'); await app.flush();
    assert.equal(app.writes[0].MyRequestsRowEnabled, true);
    assert.equal(app.writes[0].MyRequestsHideWatched, true);
    assert.equal(app.writes[0].SharedRequestsEnabled, false);
    assert.equal(app.elements.MyRequestsHideWatched.disabled, true);
    pending.resolve({}); await app.flush();
    assert.equal(app.elements.RowanConfigStatus.textContent, 'Configuration saved.');
});

test('upcoming movie and show choices independently snapshot and disclose household visibility', async () => {
    assert.match(html, /<input id="UpcomingMoviesRowEnabled" type="checkbox"/);
    assert.match(html, /<input id="UpcomingShowsRowEnabled" type="checkbox"/);
    assert.match(html, /outside (?:their|each user's) Jellyfin libraries/);
    const pending = deferred();
    const app = setup({ HomeEnabled: true, CalendarEnabled: true, RecentlyAddedLibraryIds: null }, folders, { update: () => pending.promise });
    await app.load();
    assert.equal(app.elements.UpcomingMoviesRowEnabled.checked, false);
    assert.equal(app.elements.UpcomingShowsRowEnabled.checked, false);
    app.elements.UpcomingMoviesRowEnabled.checked = true;
    app.form.dispatch('submit'); await app.flush();
    assert.equal(app.writes[0].UpcomingMoviesRowEnabled, true);
    assert.equal(app.writes[0].UpcomingShowsRowEnabled, false);
    assert.equal(app.elements.UpcomingShowsRowEnabled.disabled, true);
    app.elements.UpcomingShowsRowEnabled.checked = true;
    pending.resolve({}); await app.flush();
    assert.match(app.elements.RowanConfigError.textContent, /changed during save/i);
});

test('three HSS Discover Home feeds are independent default-off admin choices with pending-save snapshots', async () => {
    const update = deferred();
    const app = setup({ HomeEnabled: true, RecentlyAddedLibraryIds: null }, folders, { update: () => update.promise });
    await app.load();
    for (const id of ['DiscoverRowEnabled', 'DiscoverMoviesRowEnabled', 'DiscoverTvRowEnabled']) {
        assert.match(html, new RegExp(`<input id="${id}" type="checkbox"`));
        assert.equal(app.elements[id].checked, false);
    }
    app.elements.DiscoverMoviesRowEnabled.checked = true;
    app.form.dispatch('submit'); await app.flush();
    assert.equal(app.writes[0].DiscoverRowEnabled, false);
    assert.equal(app.writes[0].DiscoverMoviesRowEnabled, true);
    assert.equal(app.writes[0].DiscoverTvRowEnabled, false);
    for (const id of ['DiscoverRowEnabled', 'DiscoverMoviesRowEnabled', 'DiscoverTvRowEnabled']) assert.equal(app.elements[id].disabled, true);
    app.elements.DiscoverTvRowEnabled.checked = true;
    update.resolve({}); await app.flush();
    assert.match(app.elements.RowanConfigError.textContent, /changed during save/i);
});

test('household requests require independent explicit admin opt-in and snapshot on save', async () => {
    assert.match(html, /<input id="SharedRequestsEnabled" type="checkbox"/);
    assert.match(html, /all signed-in Jellyfin users/i);
    const request = deferred();
    const app = setup({ RecentlyAddedLibraryIds: null, HomeEnabled: false, DiscoveryPageEnabled: false }, folders, { update: () => request.promise });
    await app.load();
    assert.equal(app.elements.SharedRequestsEnabled.checked, false);
    app.elements.SharedRequestsEnabled.checked = true;
    app.form.dispatch('submit'); await app.flush();
    assert.equal(app.elements.SharedRequestsEnabled.disabled, true);
    assert.equal(app.writes[0].SharedRequestsEnabled, true);
    assert.equal(app.writes[0].HomeEnabled, false);
    app.elements.SharedRequestsEnabled.checked = false;
    request.resolve({}); await app.flush();
    assert.match(app.elements.RowanConfigError.textContent, /changed during save/i);
});

test('combined and collections flags are default-off and save independently', async () => {
    const app = setup({ HomeEnabled: true, RecentlyAddedLibraryIds: null }, folders);
    await app.load();
    assert.equal(app.elements.CombinedPlaybackRowEnabled.checked, false);
    assert.equal(app.elements.CollectionsRowEnabled.checked, false);
    app.elements.CollectionsRowEnabled.checked = true;
    app.elements.CombinedPlaybackHideWatched.checked = true;
    await app.save();
    assert.equal(app.writes[0].CombinedPlaybackRowEnabled, false);
    assert.equal(app.writes[0].CollectionsRowEnabled, true);
    assert.equal(app.writes[0].CombinedPlaybackHideWatched, true);
    await app.load();
    assert.equal(app.elements.CollectionsRowEnabled.checked, true);
    assert.equal(app.elements.CombinedPlaybackHideWatched.checked, true);
});

test('Live TV row opt-in locks, snapshots and round-trips independently', async () => {
    assert.match(html, /<input id="LiveTvRowEnabled" type="checkbox"/);
    const request = deferred();
    const app = setup({ HomeEnabled: true, RecentlyAddedLibraryIds: null }, folders, { update: () => request.promise });
    await app.load();
    assert.equal(app.elements.LiveTvRowEnabled.checked, false);
    app.elements.LiveTvRowEnabled.checked = true;
    app.form.dispatch('submit'); await app.flush();
    assert.equal(app.elements.LiveTvRowEnabled.disabled, true);
    assert.equal(app.writes[0].LiveTvRowEnabled, true);
    assert.equal(app.writes[0].CollectionsRowEnabled, false);
    request.resolve({}); await app.flush();
    await app.load();
    assert.equal(app.elements.LiveTvRowEnabled.checked, true);
});

test('Because You Watched flags save independently and detect deferred edits', async () => {
    assert.match(html, /<input id="BecauseYouWatchedRowEnabled" type="checkbox"/);
    const update = deferred();
    const app = setup({ HomeEnabled: true, RecentlyAddedLibraryIds: null }, folders, { update: () => update.promise });
    await app.load();
    assert.equal(app.elements.BecauseYouWatchedRowEnabled.checked, false);
    app.elements.BecauseYouWatchedRowEnabled.checked = true;
    app.elements.BecauseYouWatchedHideWatched.checked = true;
    app.form.dispatch('submit'); await app.flush();
    assert.equal(app.writes[0].BecauseYouWatchedRowEnabled, true);
    assert.equal(app.writes[0].BecauseYouWatchedHideWatched, true);
    assert.equal(app.writes[0].CollectionsRowEnabled, false);
    assert.equal(app.elements.BecauseYouWatchedRowEnabled.disabled, true);
    app.elements.BecauseYouWatchedHideWatched.checked = false;
    update.resolve({}); await app.flush();
    assert.match(app.elements.RowanConfigError.textContent, /changed during save/i);
});

test('trusted-filesystem hero opt-in is unchecked by default and round-trips independently', async () => {
    assert.match(html, /<input id="HeroTrustedFilesystemEnabled" type="checkbox"/);
    assert.match(html, /confined to Jellyfin's internal metadata library root/);
    assert.match(html, /opened atomically within that root; symlinks and nested mounts are rejected/);
    assert.match(html, /malicious writers inside the metadata filesystem can still create hardlinks or change file contents in place/);
    assert.doesNotMatch(html, /path validation before opening is not atomic/);
    const app = setup({ HomeEnabled: true, RecentlyAddedLibraryIds: null }, folders);
    app.page.dispatch('pageshow');
    assert.equal(app.elements.HeroTrustedFilesystemEnabled.disabled, true);
    await app.flush();
    assert.equal(app.elements.HeroTrustedFilesystemEnabled.checked, false);
    app.elements.HeroTrustedFilesystemEnabled.checked = true;
    await app.save();
    assert.equal(app.writes[0].HeroTrustedFilesystemEnabled, true);
    assert.equal(app.writes[0].HomeEnabled, true);
    await app.load();
    assert.equal(app.elements.HeroTrustedFilesystemEnabled.checked, true);
    assert.equal(app.elements.HomeEnabled.checked, true);
    app.elements.HomeEnabled.checked = false;
    await app.save();
    await app.load();
    assert.equal(app.elements.HeroTrustedFilesystemEnabled.checked, true);
    assert.equal(app.elements.HomeEnabled.checked, false);
});

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}

test('pending load locks all choices and unlocks them when configuration and folders arrive', async () => {
    const configRequest = deferred();
    const foldersRequest = deferred();
    const app = setup({}, folders, { getConfig: () => configRequest.promise, getFolders: () => foldersRequest.promise });
    app.page.dispatch('pageshow');
    assert.equal(app.elements.HomeEnabled.disabled, true);
    for (const id of ['DiscoveryPageEnabled', 'RecentlyAddedAll', 'RecentlyAddedSelected', 'RecentlyAddedNone', 'RowanSaveButton'])
        assert.equal(app.elements[id].disabled, true, id);
    configRequest.resolve({ HomeEnabled: true, DiscoveryPageEnabled: true, RecentlyAddedLibraryIds: [A] });
    await app.flush();
    assert.equal(app.elements.HomeEnabled.disabled, true);
    assert.equal(app.elements.DiscoveryPageEnabled.disabled, true);
    assert.equal(app.elements.RowanSaveButton.disabled, true);
    foldersRequest.resolve(folders);
    await app.flush();
    assert.equal(app.elements.HomeEnabled.disabled, false);
    assert.equal(app.elements.RowanSaveButton.disabled, false);
    assert.equal(app.list.querySelectorAll('input[type="checkbox"]')[0].disabled, false);
    assert.equal(app.elements.DiscoveryPageEnabled.disabled, false);
    assert.equal(app.elements.DiscoveryPageEnabled.checked, true);
});

test('pending save snapshots home, mode, and IDs and refuses duplicate submits', async () => {
    const configRequest = deferred();
    const updateRequest = deferred();
    let configCalls = 0;
    const app = setup({ RecentlyAddedLibraryIds: [A], HomeEnabled: false, DiscoveryPageEnabled: false, Other: 'kept' }, folders, {
        getConfig: () => ++configCalls === 1 ? Promise.resolve({ RecentlyAddedLibraryIds: [A], HomeEnabled: false, Other: 'kept' }) : configRequest.promise,
        update: () => updateRequest.promise
    });
    await app.load();
    app.elements.HomeEnabled.checked = true;
    app.elements.DiscoveryPageEnabled.checked = true;
    app.list.querySelectorAll('input[type="checkbox"]')[1].checked = true;
    app.form.dispatch('submit');
    for (const id of ['HomeEnabled', 'DiscoveryPageEnabled', 'RecentlyAddedAll', 'RecentlyAddedSelected', 'RecentlyAddedNone', 'RowanSaveButton'])
        assert.equal(app.elements[id].disabled, true, id);
    assert.ok(app.list.querySelectorAll('input[type="checkbox"]').every(box => box.disabled));
    app.elements.HomeEnabled.checked = false; // simulate external mutation despite disabled controls
    app.elements.DiscoveryPageEnabled.checked = false;
    app.select('none');
    app.form.dispatch('submit');
    configRequest.resolve({ Other: 'kept' });
    await app.flush();
    assert.equal(configCalls, 2);
    assert.equal(app.writes.length, 1);
    assert.equal(app.writes[0].HomeEnabled, true);
    assert.equal(app.writes[0].DiscoveryPageEnabled, true);
    assert.deepEqual(Array.from(app.writes[0].RecentlyAddedLibraryIds), [A, B]);
    assert.equal(app.writes[0].Other, 'kept');
    assert.equal(app.elements.RowanSaveButton.disabled, true);
    assert.equal(app.elements.RowanConfigStatus.hidden, true);
    updateRequest.resolve({});
    await app.flush();
    assert.equal(app.elements.RowanSaveButton.disabled, false);
    assert.equal(app.elements.RowanConfigStatus.hidden, true);
    assert.match(app.elements.RowanConfigError.textContent, /changed during save/i);
});

test('discovery flag loads false by default and round-trips true without changing home', async () => {
    assert.match(html, /<input id="DiscoveryPageEnabled" type="checkbox"/);
    const app = setup({ HomeEnabled: false, DiscoveryPageEnabled: false, RecentlyAddedLibraryIds: null }, folders);
    await app.load();
    assert.equal(app.elements.DiscoveryPageEnabled.checked, false);
    app.elements.DiscoveryPageEnabled.checked = true;
    await app.save();
    assert.equal(app.writes[0].DiscoveryPageEnabled, true);
    assert.equal(app.writes[0].HomeEnabled, false);
});

test('external discovery-only mutation during save cannot be reported as saved', async () => {
    const request = deferred();
    const app = setup({ HomeEnabled: false, DiscoveryPageEnabled: false, RecentlyAddedLibraryIds: null },
        folders, { update: () => request.promise });
    await app.load();
    app.elements.DiscoveryPageEnabled.checked = true;
    app.form.dispatch('submit');
    await app.flush();
    assert.equal(app.writes[0].DiscoveryPageEnabled, true);
    assert.equal(app.elements.DiscoveryPageEnabled.disabled, true);
    app.elements.DiscoveryPageEnabled.checked = false;
    request.resolve({});
    await app.flush();
    assert.equal(app.elements.RowanConfigStatus.hidden, true);
    assert.match(app.elements.RowanConfigError.textContent, /changed during save/i);
});

test('failed pending save unlocks choices and shows error without success', async () => {
    const request = deferred();
    const app = setup({ RecentlyAddedLibraryIds: [A] }, folders, { update: () => request.promise });
    await app.load();
    app.form.dispatch('submit');
    await app.flush();
    request.reject(Error('save failed'));
    await app.flush();
    assert.match(app.elements.RowanConfigError.textContent, /Could not save/);
    assert.equal(app.elements.RowanConfigStatus.hidden, true);
    assert.equal(app.elements.HomeEnabled.disabled, false);
    assert.equal(app.elements.RowanSaveButton.disabled, false);
    assert.equal(app.list.querySelectorAll('input[type="checkbox"]')[0].disabled, false);
});

test('successful pending save reports success only after update settles', async () => {
    const request = deferred();
    const app = setup({ RecentlyAddedLibraryIds: [A] }, folders, { update: () => request.promise });
    await app.load();
    app.form.dispatch('submit');
    await app.flush();
    assert.equal(app.elements.RowanConfigStatus.hidden, true);
    assert.equal(app.elements.RowanSaveButton.disabled, true);
    request.resolve({});
    await app.flush();
    assert.match(app.elements.RowanConfigStatus.textContent, /Configuration saved/);
    assert.equal(app.elements.RowanSaveButton.disabled, false);
    assert.equal(app.elements.RowanConfigError.hidden, true);
});

test('failure fetching configuration before update unlocks choices without writing', async () => {
    const request = deferred();
    let calls = 0;
    const app = setup({ RecentlyAddedLibraryIds: [A] }, folders, {
        getConfig: () => ++calls === 1 ? Promise.resolve({ RecentlyAddedLibraryIds: [A] }) : request.promise
    });
    await app.load();
    app.form.dispatch('submit');
    assert.equal(app.elements.RowanSaveButton.disabled, true);
    request.reject(Error('fetch failed'));
    await app.flush();
    assert.equal(app.writes.length, 0);
    assert.equal(app.elements.RowanSaveButton.disabled, false);
    assert.equal(app.elements.HomeEnabled.disabled, false);
    assert.match(app.elements.RowanConfigError.textContent, /Could not save/);
    assert.equal(app.elements.RowanConfigStatus.hidden, true);
});

test('null selects all, names stay text, and all saves null while preserving other fields', async () => {
    const app = setup({ HomeEnabled: false, RecentlyAddedLibraryIds: null, Other: 'retained' }, folders);
    await app.load();
    assert.equal(app.elements.RecentlyAddedAll.checked, true);
    assert.equal(app.list.querySelectorAll('input[type="checkbox"]').length, 2);
    assert.equal(app.list.children[0].children[1].textContent, folders[0].Name);
    assert.equal(app.list.children[0].children[0].disabled, true);
    assert.deepEqual(app.calls, ['Library/VirtualFolders']);
    app.elements.HomeEnabled.checked = true;
    await app.save();
    assert.equal(app.writes[0].RecentlyAddedLibraryIds, null);
    assert.equal(app.writes[0].Other, 'retained');
    assert.equal(app.writes[0].HomeEnabled, true);
});

test('selected loads checked IDs and saves only valid available selections', async () => {
    const app = setup({ RecentlyAddedLibraryIds: [A.toUpperCase()], HomeEnabled: false },
        [...folders, { Name: 'Broken', ItemId: 'not-a-guid' }, { Name: 'Missing' }, { Name: 'Duplicate', ItemId: A }]);
    await app.load();
    assert.equal(app.elements.RecentlyAddedSelected.checked, true);
    const boxes = app.list.querySelectorAll('input[type="checkbox"]');
    assert.equal(boxes.length, 2);
    assert.equal(boxes[0].checked, true);
    assert.equal(boxes[0].disabled, false);
    boxes[0].value = 'not-a-guid';
    boxes[1].checked = true;
    await app.save();
    assert.deepEqual(Array.from(app.writes[0].RecentlyAddedLibraryIds), [B]);
});

test('selected mode without a checked valid library refuses to save', async () => {
    const app = setup({ RecentlyAddedLibraryIds: null }, folders);
    await app.load();
    app.select('selected');
    await app.save();
    assert.equal(app.writes.length, 0);
    assert.match(app.elements.RowanConfigError.textContent, /Select at least one/);
});

test('explicit none round-trips as empty array', async () => {
    const app = setup({ RecentlyAddedLibraryIds: [] }, folders);
    await app.load();
    assert.equal(app.elements.RecentlyAddedNone.checked, true);
    await app.save();
    assert.deepEqual(Array.from(app.writes[0].RecentlyAddedLibraryIds), []);
});

test('load failure disables saving and displays an error', async () => {
    const app = setup({ RecentlyAddedLibraryIds: null }, folders, { loadFails: true });
    await app.load();
    assert.equal(app.elements.RowanSaveButton.disabled, true);
    assert.equal(app.elements.HomeEnabled.disabled, true);
    assert.equal(app.elements.RecentlyAddedAll.disabled, true);
    assert.match(app.elements.RowanConfigError.textContent, /Could not load/);
    await app.save();
    assert.equal(app.writes.length, 0);
});

test('malformed library response blocks saves rather than implying none', async () => {
    const app = setup({ RecentlyAddedLibraryIds: null }, { Items: folders });
    await app.load();
    assert.equal(app.elements.RowanSaveButton.disabled, true);
    assert.match(app.elements.RowanConfigError.textContent, /Could not load/);
    assert.equal(app.writes.length, 0);
});

test('partial missing selected IDs remain visible, checked, and saved', async () => {
    const app = setup({ RecentlyAddedLibraryIds: [A, B.toUpperCase()] }, [folders[0]]);
    await app.load();
    assert.equal(app.elements.RecentlyAddedSelected.checked, true);
    const boxes = app.list.querySelectorAll('input[type="checkbox"]');
    assert.equal(boxes.length, 2);
    assert.equal(boxes[0].checked, true);
    assert.equal(boxes[1].checked, true);
    assert.equal(boxes[1].disabled, false);
    assert.equal(boxes[1].value, B.toUpperCase());
    assert.match(app.list.children[2].children[1].textContent, /unavailable/i);
    assert.match(app.list.children[2].children[1].textContent, new RegExp(B, 'i'));
    await app.save();
    assert.deepEqual(Array.from(app.writes[0].RecentlyAddedLibraryIds), [A, B.toUpperCase()]);
});

test('unchecking an unavailable selected ID explicitly removes it', async () => {
    const app = setup({ RecentlyAddedLibraryIds: [A, B] }, [folders[0]]);
    await app.load();
    app.list.querySelectorAll('input[type="checkbox"]')[1].checked = false;
    await app.save();
    assert.deepEqual(Array.from(app.writes[0].RecentlyAddedLibraryIds), [A]);
});

test('missing-only selection remains checked and can be explicitly removed', async () => {
    const app = setup({ RecentlyAddedLibraryIds: [A] }, [{ Name: 'Movies', ItemId: B }]);
    await app.load();
    assert.equal(app.elements.RecentlyAddedSelected.checked, true);
    const boxes = app.list.querySelectorAll('input[type="checkbox"]');
    assert.equal(boxes.length, 2);
    assert.equal(boxes[1].checked, true);
    await app.save();
    assert.deepEqual(Array.from(app.writes[0].RecentlyAddedLibraryIds), [A]);
    boxes[1].checked = false;
    await app.save();
    assert.equal(app.writes.length, 1);
    assert.match(app.elements.RowanConfigError.textContent, /Select at least one/);
});

test('unavailable selections are not carried into all or none modes', async () => {
    const app = setup({ RecentlyAddedLibraryIds: [A, B] }, [folders[0]]);
    await app.load();
    app.select('all');
    assert.equal(app.list.querySelectorAll('input[type="checkbox"]')[1].disabled, true);
    await app.save();
    assert.equal(app.writes[0].RecentlyAddedLibraryIds, null);
    app.select('none');
    await app.save();
    assert.deepEqual(Array.from(app.writes[1].RecentlyAddedLibraryIds), []);
});

test('save failure displays an error', async () => {
    const app = setup({ RecentlyAddedLibraryIds: null }, folders, { saveFails: true });
    await app.load();
    await app.save();
    assert.match(app.elements.RowanConfigError.textContent, /Could not save/);
});

test('household downloads default off, display disclosure, and snapshot independent Arr credentials', async () => {
    assert.match(html, /including titles outside their libraries or parental restrictions/);
    const app = setup({ HomeEnabled: false, RecentlyAddedLibraryIds: null }, folders);
    await app.load();
    assert.equal(app.elements.DownloadsEnabled.checked, false);
    app.elements.DownloadsEnabled.checked = true;
    app.elements.RadarrBaseUrl.value = 'https://arr.example/radarr/';
    app.elements.RadarrApiKey.value = 'secret';
    await app.save();
    assert.equal(app.writes[0].DownloadsEnabled, true);
    assert.equal(app.writes[0].RadarrApiKey, 'secret');
    assert.equal(app.writes[0].HomeEnabled, false);
    await app.load();
    assert.equal(app.elements.DownloadsEnabled.checked, true);
    assert.equal(app.elements.RadarrBaseUrl.value, 'https://arr.example/radarr/');
});

test('calendar is independently opt-in and discloses all-signed-in visibility', async () => {
    assert.match(html, /Every existing signed-in Jellyfin user can see upcoming Radarr movie releases and Sonarr episode titles\/dates/);
    const app = setup({ DownloadsEnabled: true, RecentlyAddedLibraryIds: null }, folders);
    await app.load();
    assert.equal(app.elements.CalendarEnabled.checked, false);
    assert.equal(app.elements.DownloadsEnabled.checked, true);
    app.elements.CalendarEnabled.checked = true;
    app.elements.DownloadsEnabled.checked = false;
    await app.save();
    assert.equal(app.writes[0].CalendarEnabled, true);
    assert.equal(app.writes[0].DownloadsEnabled, false);
    await app.load();
    assert.equal(app.elements.CalendarEnabled.checked, true);
    assert.equal(app.elements.DownloadsEnabled.checked, false);
});
