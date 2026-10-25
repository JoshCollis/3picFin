const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync(require.resolve('../../src/Rowan.Jellyfin.Plugin/Web/global-fin-nav.js'), 'utf8');
for (const [version, supported] of [
    ['12.1.0', true], ['12.1.1', true], ['12.3.0', true], ['12.3.1', true], ['12.3.0.0', true],
    ['12.2.0', false], ['12.4.0', false], ['13.0.0', false],
    ['12.3.0-rc.1', false], ['12.3.0junk', false], [null, false]
]) test(`global shell navigation checks Mode only for supported stable version ${version}`, async () => {
    let modeReads = 0;
    const timers = new Map(); let nextTimer = 0;
    const context = {
        document: { documentElement: {}, querySelectorAll: () => [] },
        ApiClient: { getCurrentUserId: () => 'viewer', getUrl: path => path,
            getJSON: async path => {
                if (path === 'System/Info/Public') return { Version: version };
                assert.equal(path, 'Rowan/Home/Mode'); modeReads++;
                return { DiscoveryEnabled: true };
            } },
        location: { hash: '#/movies' }, queueMicrotask,
        MutationObserver: class { observe() {} disconnect() {} },
        addEventListener() {}, removeEventListener() {}, setInterval() {}, clearInterval() {},
        setTimeout(callback) { timers.set(++nextTimer, callback); return nextTimer; },
        clearTimeout(id) { timers.delete(id); }
    };
    vm.runInNewContext(source, context);
    while (timers.size) {
        const [id, callback] = timers.entries().next().value;
        timers.delete(id); callback();
        await new Promise(resolve => setImmediate(resolve));
    }
    assert.equal(modeReads, supported ? 1 : 0);
    context.__threePicFinGlobalNav.dispose();
});
