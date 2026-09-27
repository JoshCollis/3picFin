"""Disposable v12.1 two-user empty-guide Live TV route/auth probe."""
import json
import os
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

root = Path(__file__).resolve().parents[1]
scratch = Path(os.environ.get('TMPDIR') or tempfile.gettempdir())


def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()


def call(base, path, method='GET', body=None, token=None):
    auth = 'MediaBrowser Client="RowanProbe", Device="Lab", DeviceId="rowan-live-lab", Version="1.0"'
    if token:
        auth += f', Token="{token}"'
    headers = {'Authorization': auth}
    if body is not None:
        headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(base + path, method=method, headers=headers,
        data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and 'json' in response.headers.get('Content-Type', '') else None, dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, None, dict(error.headers)


with tempfile.TemporaryDirectory(prefix='rowan-live-tv-', dir=scratch) as tmp:
    config = Path(tmp) / 'config'
    cache = Path(tmp) / 'cache'
    plugin = config / 'plugins' / 'Rowan'
    settings = config / 'plugins' / 'configurations'
    plugin.mkdir(parents=True)
    settings.mkdir()
    cache.mkdir()
    dll = root / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
    (plugin / dll.name).write_bytes(dll.read_bytes())
    (settings / 'Rowan.Jellyfin.Plugin.xml').write_text(
        '<PluginConfiguration><HomeEnabled>true</HomeEnabled><LiveTvRowEnabled>true</LiveTvRowEnabled></PluginConfiguration>')
    container = docker('run', '-d', '--user', f'{os.getuid()}:{os.getgid()}', '-p', '127.0.0.1::8096',
        '-v', f'{config}:/config', '-v', f'{cache}:/cache', 'jellyfin/jellyfin:12.1')
    try:
        base = 'http://127.0.0.1:' + docker('port', container, '8096/tcp').rsplit(':', 1)[1]
        for _ in range(90):
            try:
                status, _, _ = call(base, '/Startup/User')
                if status == 200:
                    break
            except Exception:
                pass
            time.sleep(1)
        else:
            raise RuntimeError('startup timed out')
        passwords = [secrets.token_urlsafe(18), secrets.token_urlsafe(18)]
        status, _, _ = call(base, '/Startup/User', 'POST', {'Name': 'alice', 'Password': passwords[0]})
        assert status in (200, 204), status
        status, _, _ = call(base, '/Startup/Complete', 'POST', {})
        assert status in (200, 204), status
        status, alice, _ = call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'alice', 'Pw': passwords[0]})
        assert status == 200, status
        assert alice is not None
        admin = alice['AccessToken']
        status, _, _ = call(base, '/Users/New', 'POST', {'Name': 'bob', 'Password': passwords[1]}, admin)
        assert status in (200, 201), status
        status, bob, _ = call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'bob', 'Pw': passwords[1]})
        assert status == 200, status
        assert bob is not None
        results = []
        for token in [admin, bob['AccessToken']]:
            row = None
            headers = {}
            for _ in range(20):
                status, row, headers = call(base, '/Rowan/Home/Rows/LiveTV', token=token)
                if status != 404:
                    break
                time.sleep(1)
            assert status == 200, status
            assert row == {'Kind': 'LiveTV', 'Items': []}, row
            assert headers.get('Cache-Control') == 'private, no-store', headers
            results.append(status)
        anonymous, _, _ = call(base, '/Rowan/Home/Rows/LiveTV')
        assert anonymous == 401, anonymous
        status, _, _ = call(base, '/Auth/Keys?app=RowanLiveProbe', 'POST', {}, admin)
        assert status in (200, 201, 204), status
        _, keys, _ = call(base, '/Auth/Keys', token=admin)
        assert keys is not None
        api_key = next(key['AccessToken'] for key in keys['Items'] if key['AppName'] == 'RowanLiveProbe')
        userless, _, _ = call(base, '/Rowan/Home/Rows/LiveTV', token=api_key)
        assert userless == 403, userless
        print(json.dumps({'users': results, 'anonymous': anonymous, 'userless_key': userless,
            'guide': 'empty: no tuner configured', 'cache': 'private, no-store'}))
    except Exception:
        print(docker('logs', container)[-5000:])
        raise
    finally:
        subprocess.run(['docker', 'rm', '-f', container], capture_output=True, check=True)
