const { test } = require('node:test');
const assert = require('node:assert/strict');
const { guid, sameGuid, poster, normalize, hiddenRoute } = require('../../src/Rowan.Jellyfin.Plugin/Web/assistant.js');

test('assistant accepts only real GUID-shaped item identities and safe TMDb artwork', () => {
    assert.ok(sameGuid('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'));
    for (const value of ['javascript:alert(1)', '00000000000000000000000000000000', 'x', null]) assert.equal(guid(value), false);
    assert.ok(poster('/safe-title_1.jpg'));
    for (const value of ['//evil.test/a.jpg', '/../../a.jpg', '/x.jpg?key=secret', '/x.svg', 'https://evil.test/x.jpg']) assert.equal(poster(value), false);
});
test('assistant stays off playback, login and dashboard/configuration screens', () => {
    for (const route of ['#/video', '#/videoplayer?id=x', '#/login', '#/dashboard', '#/dashboardgeneral', '#/webconfigurationpage?name=3pic', '#/plugins']) assert.ok(hiddenRoute(route), route);
    for (const route of ['#/home', '#/home?fin=1', '#/search?query=video', '#/details?id=abc', '#/movies']) assert.equal(hiddenRoute(route), false, route);
});
test('chat DTOs tolerate native PascalCase and camelCase recursively without interpreting text', () => {
    assert.deepEqual(normalize({ Threads: [{ Id: 'abc', Messages: [{ Text: '<img src=x onerror=alert(1)>' }] }] }),
        { threads: [{ id: 'abc', messages: [{ text: '<img src=x onerror=alert(1)>' }] }] });
});
