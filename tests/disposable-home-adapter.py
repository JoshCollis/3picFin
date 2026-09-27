"""Disposable Jellyfin smoke: optional dependency, coexistence, lab-only index injection.

No host config, credentials, or live services are used. Docker removes the container on exit.
"""

import json
import secrets
import os
import io
import hashlib
import sys
import shutil
import zipfile
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
SCRATCH = Path(os.environ.get('TMPDIR') or tempfile.gettempdir())
DEPENDENCIES = {
    'FileTransformation': ('jellyfin-plugin-file-transformation', '3.0.1.0'),
    'PluginPages': ('jellyfin-plugin-pages', '3.0.1.0'),
    'HomeSections': ('jellyfin-plugin-home-sections', '3.0.2.0'),
}
LAB_ORIGIN_INDEX = '32034be1d9d36874d2363c615beabca57377a2ad440fbf3adbdd8a2bb87fab55'
LAB_HOME_CHUNK = 'hometab.0ec3d9a22cad691c217e.chunk.js'
LAB_HOME_HASH = 'd4103e6d500c7e69b4952c4b3e819184ec0b7917c6b5680169c7d6e62509a986'


def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()


def request(url):
    try:
        with urllib.request.urlopen(url, timeout=5) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()

def post_json(url, value):
    req = urllib.request.Request(url, data=json.dumps(value).encode(),
        headers={'Content-Type': 'application/json'}, method='POST')
    return request_post(req)

def request_post(req):
    with urllib.request.urlopen(req, timeout=10) as response:
        return response.status, response.read()


def main():
    if not DLL.is_file():
        raise SystemExit('Build Release DLL first')
    with tempfile.TemporaryDirectory(prefix='rowan-home-disposable-', dir=SCRATCH) as tmp:
        plugins = Path(tmp) / 'config' / 'plugins' / 'Rowan'
        plugins.mkdir(parents=True)
        (Path(tmp) / 'cache').mkdir()
        (plugins / DLL.name).write_bytes(DLL.read_bytes())
        if '--test-index-pin' in sys.argv:
            assert '--with-dependencies' in sys.argv
            source = Path(tmp) / 'source'
            shutil.copytree(ROOT / 'src/Rowan.Jellyfin.Plugin', source,
                            ignore=shutil.ignore_patterns('bin', 'obj'))
            injector = source / 'Web/HomeAdapterRegistration.cs'
            code = injector.read_text()
            original = 'private static readonly string? VerifiedIndexSha256 = null;'
            assert code.count(original) == 1
            injector.write_text(code.replace(original,
                f'private static readonly string? VerifiedIndexSha256 = "{LAB_ORIGIN_INDEX}";'))
            if '--browser' in sys.argv:
                adapter_js = source / 'Web/home-adapter.js'
                code = adapter_js.read_text()
                assert code.count('VERIFIED_HOME_DISTRIBUTION = null') == 1
                adapter_js.write_text(code.replace('VERIFIED_HOME_DISTRIBUTION = null',
                    f"VERIFIED_HOME_DISTRIBUTION = {{ chunk: '{LAB_HOME_CHUNK}', sha256: '{LAB_HOME_HASH}' }}"))
                host_js = source / 'Web/home-tab-host.js'
                code = host_js.read_text()
                assert code.count('VERIFIED_WEB_BUNDLE_SHA256 = null') == 1
                host_js.write_text(code.replace('VERIFIED_WEB_BUNDLE_SHA256 = null',
                    f"VERIFIED_WEB_BUNDLE_SHA256 = '{LAB_HOME_HASH}'"))
            subprocess.run(['dotnet', 'build', str(source / 'Rowan.Jellyfin.Plugin.csproj'),
                            '-c', 'Release', '-v', 'quiet'], check=True)
            (plugins / DLL.name).write_bytes((source / 'bin/Release/net10.0' / DLL.name).read_bytes())
            config = plugins.parent / 'configurations'
            config.mkdir()
            (config / 'Rowan.Jellyfin.Plugin.xml').write_text(
                '<?xml version="1.0"?><PluginConfiguration><HomeEnabled>true</HomeEnabled>'
                '<DiscoveryPageEnabled>true</DiscoveryPageEnabled></PluginConfiguration>')
        if '--with-dependencies' in sys.argv:
            for label, (repository, version) in DEPENDENCIES.items():
                url = f'https://github.com/IAmParadox27/{repository}/releases/download/{version}/Release-12.1.0.zip'
                with urllib.request.urlopen(url, timeout=30) as response:
                    archive = zipfile.ZipFile(io.BytesIO(response.read()))
                target = plugins.parent / label
                target.mkdir()
                for file in archive.namelist():
                    if '/' not in file and file.endswith(('.dll', '.deps.json')):
                        (target / file).write_bytes(archive.read(file))
        name = f'rowan-home-disposable-{os.getpid()}'
        cid = docker('run', '-d', '--name', name, '--user', f'{os.getuid()}:{os.getgid()}', '-p', '127.0.0.1::8096',
                     '-v', f'{tmp}/config:/config', '-v', f'{tmp}/cache:/cache',
                     'jellyfin/jellyfin:12.1')
        try:
            port = docker('port', cid, '8096/tcp').rsplit(':', 1)[1]
            base = f'http://127.0.0.1:{port}'
            for _ in range(80):
                try:
                    status, _ = request(base + '/System/Info/Public')
                    if status == 200:
                        break
                except (OSError, TimeoutError):
                    pass
                time.sleep(1)
            else:
                raise AssertionError('Jellyfin did not become ready: ' + docker('logs', cid)[-4000:])
            body = b''
            status = 0
            for _ in range(45):
                try:
                    status, body = request(base + '/web/index.html')
                except (OSError, TimeoutError):
                    pass
                if status == 200:
                    break
                time.sleep(1)
            if '--test-index-pin' in sys.argv:
                for _ in range(30):
                    logs = docker('logs', cid)
                    if logs.count("Registering transformation for 'index.html'") >= 3:
                        status, body = request(base + '/web/index.html')
                        break
                    time.sleep(1)
                assert status == 200 and body.count(b'data-threepic-fin-adapter') == 1, (status, body[-500:], docker('logs', cid)[-1800:])
                assert b'PluginPages' in body or b'pluginPages' in body or b'plugin-pages' in body, ('Plugin Pages marker missing', body[-1500:])
                assert b'home-screen-sections' in body or b'HomeScreen' in body, ('HSS marker missing', body[-1500:])
                if '--browser' in sys.argv:
                    chunk_status, chunk_body = request(base + '/web/' + LAB_HOME_CHUNK)
                    assert chunk_status == 200 and hashlib.sha256(chunk_body).hexdigest() == LAB_HOME_HASH
                    from disposable_home_browser import run_browser
                    user = 'rowanlab'
                    password = secrets.token_urlsafe(18)
                    assert request(base + '/Startup/User')[0] == 200
                    post_json(base + '/Startup/User', {'Name': user, 'Password': password})
                    post_json(base + '/Startup/Complete', {})
                    run_browser(base, user, password)
            else:
                assert status == 200 and b'data-threepic-fin-adapter' not in body, (status, body[:400], docker('logs', cid)[-1200:])
            for _ in range(45):
                status, _ = request(base + '/3picFin/Web/home-adapter.js')
                if status != 503:
                    break
                time.sleep(1)
            assert status == (200 if '--test-index-pin' in sys.argv else 404), status
            logs = docker('logs', cid)
            assert 'Rowan.Jellyfin.Plugin' in logs, 'Rowan assembly was not loaded'
            if '--with-dependencies' in sys.argv:
                assert all(label in logs for label in ('File Transformation', 'Plugin Pages', 'Home Screen Sections')), logs[-2500:]
                chunk = '65126.1932a6d52e2f813f2205.chunk.js'
                chunk_status, chunk_body = request(base + '/web/' + chunk)
                assert chunk_status == 200 and b'loadSections' in chunk_body, chunk_status
                assert hashlib.sha256(chunk_body).hexdigest() != '60abc3759584f92b0db16e71a9c6df62ba44a7278d063c75d92896470793d0d2'
            served_hash = None
            if '--test-index-pin' in sys.argv:
                served_hash = hashlib.sha256(body).hexdigest()
                docker('restart', cid)
                port = docker('port', cid, '8096/tcp').rsplit(':', 1)[1]
                base = f'http://127.0.0.1:{port}'
                restarted_status, restarted_body = 0, b''
                for _ in range(60):
                    try:
                        restarted_status, restarted_body = request(base + '/web/index.html')
                        if (restarted_status == 200 and restarted_body.count(b'data-threepic-fin-adapter') == 1
                                and docker('logs', cid).count("Registering transformation for 'index.html'") >= 6):
                            break
                    except (OSError, TimeoutError):
                        pass
                    time.sleep(1)
                else:
                    raise AssertionError('Adapter absent after disposable restart: ' + str((restarted_status, restarted_body[-350:])))
                assert b'home-screen-sections' in restarted_body or b'HomeScreen' in restarted_body
                assert b'PluginPages' in restarted_body or b'pluginPages' in restarted_body or b'plugin-pages' in restarted_body
            assert 'Disposable probe requires' not in logs
            print(json.dumps({'status': 'pass', 'missing_file_transformation': '--with-dependencies' not in sys.argv,
                              'dependencies': '--with-dependencies' in sys.argv,
                              'asset_status': status,
                              'index_injected': '--test-index-pin' in sys.argv,
                              'served_index_sha256': served_hash if '--test-index-pin' in sys.argv else None}))
        except Exception:
            print(docker('logs', cid)[-6000:])
            raise
        finally:
            subprocess.run(['docker', 'rm', '-f', cid], capture_output=True, check=False)


if __name__ == '__main__':
    main()
