"""Isolated Jellyfin 12.1 signed-in household UI probe."""
import asyncio
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import urllib.error
import zipfile
import io
from playwright.async_api import async_playwright
from playwright.async_api import Error as PlaywrightError

ROOT = Path(__file__).resolve().parents[1]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('home_lab', ROOT / 'tests/disposable-home-adapter.py')
assert spec and spec.loader
home = importlib.util.module_from_spec(spec)
spec.loader.exec_module(home)
# Do not import disposable-shared-requests.py: it starts containers at import time.

def call(base, path, method='GET', body=None, token=None):
    headers = {'Authorization': 'MediaBrowser Client="RowanProbe", Device="Lab", DeviceId="rowan-shared-browser", Version="1.0"'}
    if token: headers['Authorization'] += f', Token="{token}"'
    if body is not None: headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            data = response.read()
            return response.status, json.loads(data) if data and 'json' in response.headers.get('Content-Type', '') else None
    except urllib.error.HTTPError as error:
        return error.code, None

def assert_ok(result):
    assert result[0] in (200, 201, 204), result
    return result[1]

async def browser_probe(base, alice, alice_password, bob, bob_password, alice_id, bob_id):
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch(headless=True)
        try:
            page = await browser.new_page()
            async def login(name, password):
                await page.goto(base + '/web/index.html', wait_until='domcontentloaded')
                await page.locator('#txtManualName').wait_for(timeout=30000)
                await page.locator('#txtManualName').fill(name)
                await page.locator('#txtManualPassword').fill(password)
                await page.locator('#txtManualPassword').press('Enter')
                await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(timeout=45000)
                await page.locator('.threepic-fin-host__tab').filter(has_text='3pic Fin').click()
                await page.locator('#threepic-fin-shared-requests-load').wait_for()
            await login(alice, alice_password)
            assert await page.evaluate('ApiClient.getCurrentUserId()') == alice_id
            await page.locator('#threepic-fin-shared-requests-load').click()
            shared = page.locator('#threepic-fin-shared-requests')
            await shared.get_by_text('TMDb #17').wait_for(timeout=15000)
            alice_shared = await shared.inner_text()
            assert 'PRIVATE_OWNER' not in alice_shared
            assert await page.locator('#threepic-fin-requests').count() == 1
            alice_home = await page.locator('#homeTab > .sections').text_content() or ''
            assert 'alice' in alice_home and 'bob' not in alice_home
            # Route a second read into an unresolved browser-network request before logout.
            pending = asyncio.Event()
            intercepted = asyncio.Event()
            async def hold(route):
                intercepted.set()
                await pending.wait()
                try:
                    await route.fulfill(status=200, content_type='application/json', body=json.dumps({'Items':[{'Id':93,'Type':'movie','TmdbId':999,'Status':2}],'Page':1,'TotalPages':1}))
                except PlaywrightError:
                    pass  # Jellyfin may abort the pending network request during logout.
            await page.route('**/3picFin/SharedRequests?page=1', hold)
            await page.locator('#threepic-fin-shared-requests-load').click()
            await asyncio.wait_for(intercepted.wait(), timeout=15)
            assert 'Loading household requests' in await shared.inner_text()
            # The adapter must synchronously tear down the pane on logout, before late data settles.
            await page.evaluate('ApiClient.logout()')
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(state='detached', timeout=10000)
            pending.set()
            await page.wait_for_timeout(1500)
            assert await page.locator('text=TMDb #999').count() == 0
            await page.unroute('**/3picFin/SharedRequests?page=1', hold)
            await login(bob, bob_password)
            assert await page.evaluate('ApiClient.getCurrentUserId()') == bob_id
            assert await shared.get_by_text('TMDb #17').count() == 0, 'retained previous user list'
            await page.locator('#threepic-fin-shared-requests-load').click()
            await shared.get_by_text('TMDb #17').wait_for(timeout=15000)
            assert await shared.inner_text() == alice_shared, 'different signed-in global lists'
            bob_home = await page.locator('#homeTab > .sections').text_content() or ''
            assert 'bob' in bob_home and 'alice' not in bob_home
            assert await page.locator('text=TMDb #999').count() == 0, 'old response crossed identity'
            # Deliver a late result into the existing page (not an aborted network route).
            await page.evaluate("""() => {
                window.sharedProbeOriginalGet = ApiClient.getJSON;
                ApiClient.getJSON = function(url) {
                    if (String(url).includes('/SharedRequests'))
                        return new Promise(resolve => { window.sharedProbeResolve = resolve; });
                    return window.sharedProbeOriginalGet.apply(this, arguments);
                };
                window.sharedProbeOriginalUser = ApiClient.getCurrentUserId;
            }""")
            await page.locator('#threepic-fin-shared-requests-load').click()
            assert 'Loading household requests' in await shared.inner_text()
            await page.evaluate('ApiClient.getCurrentUserId = () => null')
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(state='detached', timeout=10000)
            await page.evaluate("""() => {
                window.sharedProbeResolve({Items:[{Id:94,Type:'movie',TmdbId:999,Status:2}],Page:1,TotalPages:1});
                ApiClient.getJSON = window.sharedProbeOriginalGet;
                ApiClient.getCurrentUserId = window.sharedProbeOriginalUser;
            }""")
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(timeout=15000)
            assert await page.locator('text=TMDb #999').count() == 0, 'delivered stale promise painted after teardown'
            await page.locator('.threepic-fin-host__tab').filter(has_text='Home').click()
            assert await page.locator('#homeTab > .sections').is_visible()
            assert not await page.locator('#homeTab .threepic-fin-host__panel').is_visible()
            await page.evaluate("location.hash = '#/home?tab=1'")
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(state='detached', timeout=10000)
            assert await page.locator('#favoritesTab').count() == 1
            await page.evaluate("location.hash = '#/home'")
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(timeout=15000)
            assert await page.locator('text=TMDb #999').count() == 0
            await page.evaluate("location.hash = '#/search'")
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(state='detached', timeout=10000)
            await page.evaluate("location.hash = '#/home'")
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(timeout=15000)
            print(json.dumps({'browser': 'pass', 'alice_id': alice_id, 'bob_id': bob_id, 'same_shared_title': 17, 'late_old_title_visible': False, 'home_favorites_navigation': 'pass'}))
        finally:
            await browser.close()

def main():
    with tempfile.TemporaryDirectory(prefix='rowan-shared-browser-', dir=home.SCRATCH) as temp:
        tmp = Path(temp)
        plugins = tmp / 'config/plugins/Rowan'; plugins.mkdir(parents=True)
        (tmp / 'cache').mkdir()
        (tmp / 'libraries' / 'alice').mkdir(parents=True)
        (tmp / 'libraries' / 'bob').mkdir(parents=True)
        # Test the public Release artifact unchanged, not a lab-pinned source copy.
        subprocess.run(['dotnet', 'build', str(ROOT / 'src/Rowan.Jellyfin.Plugin/Rowan.Jellyfin.Plugin.csproj'),
                        '-c', 'Release', '-v', 'quiet'], check=True)
        (plugins / 'Rowan.Jellyfin.Plugin.dll').write_bytes(
            (ROOT / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll').read_bytes())
        config = plugins.parent / 'configurations'; config.mkdir()
        (config / 'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><HomeEnabled>true</HomeEnabled><DiscoveryPageEnabled>true</DiscoveryPageEnabled><SeerrEnabled>true</SeerrEnabled><SharedRequestsEnabled>true</SharedRequestsEnabled><SeerrBaseUrl>http://127.0.0.1:19876/</SeerrBaseUrl><SeerrApiKey>test-only</SeerrApiKey></PluginConfiguration>')
        for label, (repo, version) in home.DEPENDENCIES.items():
            url = f'https://github.com/IAmParadox27/{repo}/releases/download/{version}/Release-12.1.0.zip'
            with urllib.request.urlopen(url, timeout=60) as response: archive = zipfile.ZipFile(io.BytesIO(response.read()))
            target = plugins.parent / label; target.mkdir()
            for name in archive.namelist():
                if '/' not in name and name.endswith(('.dll', '.deps.json')): (target / name).write_bytes(archive.read(name))
        cid = home.docker('run', '-d', '--user', f'{os.getuid()}:{os.getgid()}', '-p', '127.0.0.1::8096', '-v', f'{tmp}/config:/config', '-v', f'{tmp}/cache:/cache', '-v', f'{tmp}/libraries:/libraries', 'jellyfin/jellyfin:12.1')
        sidecar = None
        try:
            sidecar = home.docker('run', '-d', '--network', f'container:{cid}', '--user', f'{os.getuid()}:{os.getgid()}', '-v', f'{ROOT}/tests/disposable-seerr-stub.py:/stub.py:ro', '-v', f'{tmp}:/shared', 'python:3.11-alpine', 'python', '/stub.py')
            base = 'http://127.0.0.1:' + home.docker('port', cid, '8096/tcp').rsplit(':', 1)[1]
            for _ in range(100):
                try:
                    if call(base, '/Startup/User')[0] == 200: break
                except OSError: pass
                time.sleep(1)
            else: raise AssertionError('startup timeout')
            alice_password, bob_password = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
            assert_ok(call(base, '/Startup/User', 'POST', {'Name':'alice','Password':alice_password}))
            assert_ok(call(base, '/Startup/Complete', 'POST', {}))
            alice = assert_ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username':'alice','Pw':alice_password}))
            token = alice['AccessToken']; alice_id = alice['User']['Id']
            bob = assert_ok(call(base, '/Users/New', 'POST', {'Name':'bob','Password':bob_password}, token)); bob_id = bob['Id']
            assert_ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username':'bob','Pw':bob_password}))
            for name in ('alice', 'bob'):
                path = tmp / 'libraries' / name; path.mkdir(parents=True, exist_ok=True)
                (path / f'{name}-only.mkv').write_bytes(b'')
                assert_ok(call(base, '/Library/VirtualFolders?name=' + name + '&collectionType=movies&refreshLibrary=false', 'POST', {}, token))
                assert_ok(call(base, '/Library/VirtualFolders/Paths', 'POST', {'Name':name,'Path':'/libraries/'+name}, token))
            folders = assert_ok(call(base, '/Library/VirtualFolders', token=token))
            ids = {folder['Name']:folder['ItemId'] for folder in folders if folder['Name'] in ('alice','bob')}
            assert set(ids) == {'alice','bob'}, folders
            for user_id, allowed in [(alice_id, 'alice'), (bob_id, 'bob')]:
                policy = assert_ok(call(base, '/Users/' + user_id, token=token))['Policy']
                policy['EnableAllFolders'] = False
                policy['EnabledFolders'] = [ids[allowed]]
                assert_ok(call(base, '/Users/' + user_id + '/Policy', 'POST', policy, token))
            for user_id, expected in [(alice_id,'alice'),(bob_id,'bob')]:
                views = assert_ok(call(base, '/Users/' + user_id + '/Views', token=token))
                names = [item['Name'] for item in views['Items']]
                assert expected in names and ('bob' if expected == 'alice' else 'alice') not in names, names
            for _ in range(45):
                status, html = home.request(base + '/web/index.html')
                if status == 200 and html.count(b'data-threepic-fin-adapter') == 1: break
                time.sleep(1)
            else: raise AssertionError('lab adapter unavailable')
            assert hashlib.sha256(home.request(base + '/web/' + home.LAB_HOME_CHUNK)[1]).hexdigest() == home.LAB_HOME_HASH
            asyncio.run(browser_probe(base, 'alice', alice_password, 'bob', bob_password, alice_id, bob_id))
        except Exception:
            print(home.docker('logs', cid)[-4000:])
            if sidecar: print('STUB:', home.docker('logs', sidecar)[-1000:])
            raise
        finally:
            subprocess.run(['docker', 'rm', '-f'] + ([sidecar] if sidecar else []) + [cid], capture_output=True)

if __name__ == '__main__': main()
