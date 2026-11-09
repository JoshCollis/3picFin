const { test } = require('node:test');
const assert = require('node:assert/strict');
const { guid, sameGuid, poster, normalize, hiddenRoute, chooseStarters, pageItem } = require('../../src/Rowan.Jellyfin.Plugin/Web/assistant.js');

test('assistant accepts only real GUID-shaped item identities and safe TMDb artwork', () => {
    assert.ok(sameGuid('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'));
    for (const value of ['javascript:alert(1)', '00000000000000000000000000000000', 'x', null]) assert.equal(guid(value), false);
    assert.ok(poster('/safe-title_1.jpg'));
    for (const value of ['//evil.test/a.jpg', '/../../a.jpg', '/x.jpg?key=secret', '/x.svg', 'https://evil.test/x.jpg']) assert.equal(poster(value), false);
});

test('starter ideas rotate without repeats and match actual capabilities', () => {
    const context = { personalization: true, seeds: [{ title: 'Arrival' }], sources: { catalogConfigured: true, liveTvAvailable: true } };
    let previous = [];
    for (let n = 0; n < 8; n++) {
        const ideas = chooseStarters(context, previous, () => 0);
        assert.equal(ideas.length, 3);
        assert.equal(new Set(ideas.map(p => p.id)).size, 3);
        assert.ok(ideas.every(p => !previous.includes(p.id)));
        assert.match(ideas[0].text, /Arrival/);
        assert.ok(['on-now', 'tonight', 'sports', 'channels', 'next-tv'].includes(ideas[2].id));
        previous = ideas.map(p => p.id);
    }
    const privateOff = chooseStarters({ ...context, personalization: false, sources: {} }, [], () => 0);
    assert.ok(privateOff.every(p => !p.text.includes('Arrival') && !p.text.includes('live TV')));
    assert.equal(new Set(privateOff.map(p => p.id)).size, 3);
});

test('starters handle missing and malformed context, quoted titles and local time', () => {
    assert.equal(chooseStarters().length, 3);
    const title = '<img src=x onerror=alert(1)> "A title"';
    const ideas = chooseStarters({ personalization: true, seeds: [{ title }, { title: 'bad\ntext' }, null] }, [], () => 0);
    assert.ok(ideas[0].text.endsWith(JSON.stringify(title)));
    const late = chooseStarters({}, [], () => .999, new Date(2026, 10, 9, 23));
    assert.equal(late.length, 3);
    assert.equal(chooseStarters({ personalization: true, seeds: 'bad' }).length, 3);
});
test('assistant stays off playback, login and dashboard/configuration screens', () => {
    for (const route of ['#/video', '#/videoplayer?id=x', '#/login', '#/dashboard', '#/dashboardgeneral', '#/webconfigurationpage?name=3pic', '#/plugins']) assert.ok(hiddenRoute(route), route);
    for (const route of ['#/home', '#/home?fin=1', '#/search?query=video', '#/details?id=abc', '#/movies']) assert.equal(hiddenRoute(route), false, route);
});
test('chat DTOs tolerate native PascalCase and camelCase recursively without interpreting text', () => {
    assert.deepEqual(normalize({ Threads: [{ Id: 'abc', Messages: [{ Text: '<img src=x onerror=alert(1)>' }] }] }),
        { threads: [{ id: 'abc', messages: [{ text: '<img src=x onerror=alert(1)>' }] }] });
});
test('page context accepts only native detail GUIDs, not arbitrary pages or URLs', () => {
    const id = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    assert.equal(pageItem('#/details?id=' + id + '&serverId=fixture'), id);
    for (const route of ['#/home?id=' + id, '#/details?id=https://evil.test', '#/details?id=00000000000000000000000000000000', '#/details', '#/search?query=' + id]) assert.equal(pageItem(route), null);
});
