"""Unchanged Release DLL acceptance on loopback-only disposable Jellyfin.

Requires Docker, ffmpeg, Playwright/Chromium. No production services/credentials.
Use --image jellyfin/jellyfin:12.1 to recheck the minimum supported runtime.
Search results are a browser-local Seerr DTO fixture, not an upstream integration test.
"""
import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from typing import Any
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
GUID = 'bd36ab75-0f4a-49b6-92ef-3a93da040c7a'
DLL = ROOT / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'


def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()


def call(base, path, method='GET', body=None, token=None, content_type=None) -> tuple[int, Any]:
    headers = {'Authorization': 'MediaBrowser Client="CompatibilityProbe", Device="Disposable", DeviceId="fin-compat", Version="1.0"'}
    if token:
        headers['Authorization'] += f', Token="{token}"'
    if body is not None:
        headers['Content-Type'] = content_type or 'application/json'
    data = body if isinstance(body, bytes) else json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(base + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and 'json' in response.headers.get('Content-Type', '') else raw
    except urllib.error.HTTPError as error:
        return error.code, None


def ok(result) -> Any:
    assert result[0] in (200, 201, 204), ('HTTP failure', result[0])
    return result[1]


def wait(check, label, seconds=90):
    for _ in range(seconds):
        try:
            value = check()
            if value:
                return value
        except OSError:
            pass
        time.sleep(1)
    raise AssertionError('timeout: ' + label)


def seed(page, base, info, login):
    page.add_init_script('localStorage.setItem("jellyfin_credentials", ' + json.dumps(json.dumps({'Servers': [{
        'Id': info['Id'], 'ManualAddress': base, 'LocalAddress': base,
        'UserId': login['User']['Id'], 'AccessToken': login['AccessToken']}] })) + ');')


def browser_probe(base, info, password, output):
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True)
        try:
            for width in (390, 1280):
                login = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'probe', 'Pw': password}))
                page = browser.new_page(viewport={'width': width, 'height': 850})
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                seed(page, base, info, login)
                # Only the candidate feed is synthetic; assets, native data and auth are real.
                page.route('**/3picFin/Search?*', lambda route: route.fulfill(status=200, content_type='application/json', body=json.dumps({
                    'Items': [{'TmdbId': 9, 'MediaType': 'movie', 'Title': 'Fixture candidate', 'PosterPath': None}], 'TotalPages': 1})))
                page.goto(base + '/web/index.html#/home', wait_until='domcontentloaded')
                page.wait_for_selector('#homeTab > .sections', timeout=45000)
                page.wait_for_function('!!window.ApiClient?.getCurrentUserId()')
                assert page.evaluate('ApiClient.getCurrentUserId()') == login['User']['Id']
                page.wait_for_selector('.rowan-static-hero img', timeout=30000)
                page.wait_for_function('!!document.querySelector(".rowan-static-hero img")?.naturalWidth')
                page.locator('.rowan-static-hero img').first.evaluate('(img) => img.decode()')
                page.wait_for_selector('#homeTab .threepic-fin-host__panel', state='attached')
                assert page.locator('#homeTab > .sections').is_visible()
                hero = page.locator('.rowan-static-hero').first
                assert hero.is_visible()
                assert page.evaluate('document.querySelector("#homeTab > .sections").getBoundingClientRect().top >= document.querySelector(".rowan-static-hero").getBoundingClientRect().bottom - 1')
                if width == 390:
                    page.get_by_role('button', name='Open Menu', exact=True).click()
                nav = page.locator('.threepic-fin-global-nav:visible').first
                nav.wait_for()
                nav.click()
                if width == 390:
                    page.keyboard.press('Escape')
                page.wait_for_function('location.hash === "#/home?fin=1"')
                page.wait_for_selector('#homeTab .threepic-fin-host__panel', state='visible')
                assert not page.locator('#homeTab > .sections').is_visible()
                page.evaluate('location.hash = "#/home?tab=1"')
                page.wait_for_selector('#favoritesTab', state='visible')
                page.wait_for_selector('#homeTab .threepic-fin-host__panel', state='detached')
                page.evaluate('location.hash = "#/search?query=Compatibility"')
                page.wait_for_selector('#searchTextInput', timeout=30000)
                page.wait_for_selector('.threepic-fin-search .cardText', timeout=30000)
                assert page.locator('.threepic-fin-search .cardText').inner_text() == 'Fixture candidate'
                page.wait_for_selector('#searchPage .card[data-type="Movie"]', timeout=30000)
                assert page.locator('#searchPage .card[data-type="Movie"]').count() > 0
                if width == 390:
                    page.get_by_role('button', name='Open Menu', exact=True).click()
                page.locator('.threepic-fin-global-nav:visible').first.click()
                if width == 390:
                    page.keyboard.press('Escape')
                page.wait_for_selector('#homeTab .threepic-fin-host__panel', state='visible')
                page.wait_for_selector('.threepic-fin-search', state='detached')
                page.evaluate('location.hash = "#/home"')
                page.wait_for_selector('.rowan-static-hero img')
                page.wait_for_function('!!document.querySelector(".rowan-static-hero img")?.naturalWidth')
                page.locator('.rowan-static-hero img').first.evaluate('(img) => img.decode()')
                page.screenshot(path=str(output / f'home-{width}.png'))
                page.locator('.rowan-static-hero button').filter(has_text='Open').click()
                page.wait_for_function('!location.hash.startsWith("#/home")')
                page.evaluate('location.hash = "#/configurationpage?name=3pic%20Fin"')
                page.wait_for_selector('#RowanConfigPage', timeout=30000)
                page.wait_for_function('!document.querySelector("#RowanSaveButton").disabled && document.querySelector("#HomeEnabled").checked')
                toggle = page.locator('#CombinedPlaybackHideWatched')
                toggle.check()
                page.locator('#RowanSaveButton').click()
                page.wait_for_function('document.querySelector("#RowanConfigStatus").textContent.trim().length > 0')
                assert 'Configuration saved.' in page.locator('#RowanConfigStatus').inner_text(), page.locator('#RowanConfigStatus').inner_text()
                assert page.evaluate('(id) => ApiClient.getPluginConfiguration(id).then(c => c.CombinedPlaybackHideWatched)', GUID)
                toggle.uncheck()
                page.locator('#RowanSaveButton').click()
                page.wait_for_function('!document.querySelector("#RowanSaveButton").disabled')
                assert not page.evaluate('(id) => ApiClient.getPluginConfiguration(id).then(c => c.CombinedPlaybackHideWatched)', GUID)
                page.screenshot(path=str(output / f'admin-{width}.png'))
                page.evaluate('location.hash = "#/home"')
                page.wait_for_selector('.rowan-static-hero img')
                page.evaluate('ApiClient.logout()')
                page.wait_for_selector('.rowan-static-hero', state='detached')
                page.wait_for_selector('.threepic-fin-global-nav', state='detached')
                assert not errors, errors
                page.close()
                print(json.dumps({'width': width, 'home': 'pass', 'hero_decode_open': 'pass', 'fin_favorites_global_nav': 'pass', 'native_search_and_fixture_candidate': 'pass', 'admin_toggle_save_readback': 'pass', 'logout': 'pass', 'pageerrors': errors}), flush=True)
        finally:
            browser.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', default='jellyfin/jellyfin:12.3@sha256:2038fe038ab3876a80f6e7b7e0aef6607b032c7dc0c9b3ad3296d0b2eba3bbb5')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    dll_hash = hashlib.sha256(DLL.read_bytes()).hexdigest()
    with tempfile.TemporaryDirectory(prefix='fin-version-compat-', dir=os.environ['TMPDIR']) as temp:
        root = Path(temp)
        plugins = root / 'config/plugins/Fin'; plugins.mkdir(parents=True)
        (plugins / DLL.name).write_bytes(DLL.read_bytes())
        (root / 'cache').mkdir()
        config = plugins.parent / 'configurations'; config.mkdir()
        (config / 'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><HomeEnabled>true</HomeEnabled><DiscoveryPageEnabled>true</DiscoveryPageEnabled><GlobalSearchEnabled>true</GlobalSearchEnabled></PluginConfiguration>')
        # Dependency is intentionally the same minimum-ABI build used on 12.1.
        archive = urllib.request.urlopen('https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/3.0.1.0/Release-12.1.0.zip', timeout=60).read()
        dependency = plugins.parent / 'FileTransformation'; dependency.mkdir()
        with zipfile.ZipFile(io.BytesIO(archive)) as z:
            for name in z.namelist():
                if '/' not in name and name.endswith(('.dll', '.deps.json')):
                    (dependency / name).write_bytes(z.read(name))
        media = root / 'media/Compatibility Film (2020)'; media.mkdir(parents=True)
        (media / 'movie.nfo').write_text('<movie><title>Compatibility Film</title><premiered>2020-01-01</premiered><year>2020</year></movie>')
        subprocess.run(['ffmpeg', '-nostdin', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=blue:s=160x90:r=1', '-t', '1', '-c:v', 'mpeg4', str(media / 'Compatibility Film.mp4')], check=True)
        subprocess.run(['ffmpeg', '-nostdin', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=orange:s=640x360', '-frames:v', '1', str(root / 'image.jpg')], check=True)
        cid = docker('run', '-d', '--label', '3pic-fin=disposable-version-compat', '--user', f'{os.getuid()}:{os.getgid()}', '-p', '127.0.0.1::8096', '-v', f'{root}/config:/config', '-v', f'{root}/cache:/cache', '-v', f'{root}/media:/media:ro', args.image)
        try:
            base = 'http://127.0.0.1:' + docker('port', cid, '8096/tcp').rsplit(':', 1)[1]
            wait(lambda: call(base, '/Startup/User')[0] == 200, 'startup')
            password = secrets.token_urlsafe(24)
            ok(call(base, '/Startup/User', 'POST', {'Name': 'probe', 'Password': password}))
            ok(call(base, '/Startup/Complete', 'POST', {}))
            login = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'probe', 'Pw': password}))
            token = login['AccessToken']
            info = ok(call(base, '/System/Info/Public'))
            active = ok(call(base, '/Plugins', token=token))
            assert any(p['Id'].replace('-', '') == GUID.replace('-', '') and p['Status'] == 'Active' for p in active), [(p['Name'], p['Status']) for p in active]
            ok(call(base, '/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false', 'POST', {}, token))
            ok(call(base, '/Library/VirtualFolders/Paths', 'POST', {'Name': 'Movies', 'Path': '/media'}, token))
            folders = ok(call(base, '/Library/VirtualFolders', token=token))
            library = next(f['ItemId'] for f in folders if f['Name'] == 'Movies')
            ok(call(base, '/Library/Refresh', 'POST', {}, token))
            items = wait(lambda: ok(call(base, '/Items?Recursive=true&IncludeItemTypes=Movie&Limit=10', token=token))['Items'], 'media scan')
            item = items[0]['Id']
            ok(call(base, f'/Items/{item}/Images/Backdrop/0', 'POST', base64.b64encode((root / 'image.jpg').read_bytes()), token, 'image/jpeg'))
            configuration = ok(call(base, '/Plugins/' + GUID + '/Configuration', token=token))
            configuration.update(HomeEnabled=True, DiscoveryPageEnabled=True, GlobalSearchEnabled=True, HeroTrustedFilesystemEnabled=True, HeroLibraryIds=[library])
            ok(call(base, '/Plugins/' + GUID + '/Configuration', 'POST', configuration, token))
            saved = ok(call(base, '/Plugins/' + GUID + '/Configuration', token=token))
            assert saved['HeroLibraryIds'] == [library]
            wait(lambda: call(base, '/web/index.html')[1].count(b'data-threepic-fin-adapter') == 1, 'automatic injection')
            ok(call(base, '/Auth/Keys?app=CompatibilityProbe', 'POST', None, token))
            keys = ok(call(base, '/Auth/Keys', token=token))['Items']
            key = next(k['AccessToken'] for k in keys if k['AppName'] == 'CompatibilityProbe')
            for path in ['/Rowan/Home/Mode', '/Rowan/Home/Hero', f'/Rowan/Home/Hero/Image/{item}', '/Rowan/Home/RecentlyAdded', '/3picFin/Search?query=Compatibility']:
                assert call(base, path)[0] == 401, path
                assert call(base, path, token=key)[0] == 403, path
            mode = ok(call(base, '/Rowan/Home/Mode', token=token))
            assert mode['HeroEnabled'] and mode['DiscoveryEnabled'], mode
            slides = ok(call(base, '/Rowan/Home/Hero', token=token))
            assert slides
            tag = slides[0]['ImageTag']
            image_status = call(base, f'/Rowan/Home/Hero/Image/{item}?tag={tag}', token=token)[0]
            assert image_status == 200, image_status
            viewer_password = secrets.token_urlsafe(24)
            viewer = ok(call(base, '/Users/New', 'POST', {'Name': 'viewer', 'Password': viewer_password}, token))
            policy = viewer['Policy']
            policy.update(EnableAllFolders=False, EnabledFolders=[])
            ok(call(base, '/Users/' + viewer['Id'] + '/Policy', 'POST', policy, token))
            viewer_login = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'viewer', 'Pw': viewer_password}))
            viewer_token = viewer_login['AccessToken']
            assert ok(call(base, '/Rowan/Home/Hero', token=viewer_token)) == []
            assert call(base, f'/Rowan/Home/Hero/Image/{item}?tag={tag}', token=viewer_token)[0] == 404
            assert call(base, '/Plugins/' + GUID + '/Configuration', token=viewer_token)[0] == 403
            print(json.dumps({'runtime': info['Version'], 'dll_sha256': dll_hash, 'plugin_active': True, 'anonymous': 401, 'userless_key': 403, 'metadata_hero_image': 200}), flush=True)
            browser_probe(base, info, password, args.output)
            token = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'probe', 'Pw': password}))['AccessToken']
            configuration = ok(call(base, '/Plugins/' + GUID + '/Configuration', token=token))
            configuration['DiscoveryPageEnabled'] = False
            ok(call(base, '/Plugins/' + GUID + '/Configuration', 'POST', configuration, token))
            assert not ok(call(base, '/Plugins/' + GUID + '/Configuration', token=token))['DiscoveryPageEnabled']
            assert ok(call(base, '/Rowan/Home/Mode', token=token))['HeroEnabled']
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch(headless=True)
                try:
                    page = browser.new_page(viewport={'width': 390, 'height': 850})
                    login = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'probe', 'Pw': password}))
                    seed(page, base, info, login)
                    page.goto(base + '/web/index.html#/home', wait_until='domcontentloaded')
                    page.wait_for_function('!!document.querySelector(".rowan-static-hero img")?.naturalWidth', timeout=45000)
                    page.locator('.rowan-static-hero img').evaluate('(img) => img.decode()')
                    assert page.locator('.threepic-fin-global-nav').count() == 0
                    assert page.locator('.threepic-fin-host__panel').count() == 0
                    assert page.locator('#homeTab > .sections').is_visible()
                    print(json.dumps({'hero_only_mobile': 'pass', 'restricted_user_slides': [], 'restricted_user_image': 404, 'nonadmin_configuration': 403}), flush=True)
                finally:
                    browser.close()
        finally:
            (args.output / 'server.log').write_text(docker('logs', cid))
            subprocess.run(['docker', 'rm', '-f', cid], check=True, capture_output=True)
            assert cid not in docker('ps', '-aq')
            assert hashlib.sha256(DLL.read_bytes()).hexdigest() == dll_hash
            print('Disposable container removed; Release DLL unchanged', flush=True)


if __name__ == '__main__':
    main()
