const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('documented tab host CSS URL resolves under a Jellyfin subpath', () => {
    const docs = fs.readFileSync(path.join(__dirname, '../../docs/home-adapter-integration.md'), 'utf8');
    const example = docs.match(/<!-- discovery-host-css-contract -->\s*```js\s*([\s\S]*?)```/)?.[1];
    assert.ok(example, 'tab host contract includes an executable JavaScript example');
    const getUrl = endpoint => new URL(endpoint, 'https://example.test/jellyfin/').href;
    const url = vm.runInNewContext(`${example}\ndiscoveryStylesheetUrl({ getUrl })`, { getUrl, URL });
    assert.equal(url, 'https://example.test/jellyfin/3picFin/Web/discovery.css');
});