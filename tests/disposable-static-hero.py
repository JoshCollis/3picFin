"""Disposable Jellyfin 12.1 two-user hosted hero and confined-image HTTP proof.

Run with: python3 tests/disposable-static-hero.py. No live server is contacted.
"""
import base64
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path(os.environ.get('TMPDIR') or tempfile.gettempdir())
PLUGIN = 'bd36ab75-0f4a-49b6-92ef-3a93da040c7a'


def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()


def call(base, path, method='GET', body=None, token=None, content_type='application/json'):
    auth = 'MediaBrowser Client="RowanProbe", Device="Lab", DeviceId="rowan-hero-lab", Version="1.0"'
    if token:
        auth += f', Token="{token}"'
    headers = {'Authorization': auth}
    if body is not None:
        headers['Content-Type'] = content_type
    data = None if body is None else (json.dumps(body).encode() if content_type == 'application/json' else body)
    request = urllib.request.Request(base + path, method=method, headers=headers, data=data)
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read()
        kind = response.headers.get('Content-Type', '')
        return response.status, json.loads(raw) if raw and 'json' in kind else raw, dict(response.headers)


def ok(result):
    assert result[0] in (200, 201, 204), (result[0], result[1][:300] if isinstance(result[1], bytes) else result[1])
    return result[1]


def image(base, slide, token=None):
    return call(base, '/Rowan/Home/Hero/Image/' + slide['Id'] + '?tag=' + urllib.parse.quote(slide['ImageTag']), token=token)


def slides(base, token):
    status, rows, headers = call(base, '/Rowan/Home/Hero', token=token)
    assert status == 200 and isinstance(rows, list), (status, rows)
    assert headers.get('Cache-Control') == 'private, no-store', headers
    return rows


def main():
    subprocess.run(['dotnet', 'build', str(ROOT / 'src/Rowan.Jellyfin.Plugin/Rowan.Jellyfin.Plugin.csproj'), '-c', 'Release', '-v', 'quiet'], check=True)
    with tempfile.TemporaryDirectory(prefix='rowan-static-hero-', dir=SCRATCH) as temp:
        tmp = Path(temp)
        plugins = tmp / 'config/plugins/Rowan'
        plugins.mkdir(parents=True)
        (tmp / 'cache').mkdir()
        dll = ROOT / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
        (plugins / dll.name).write_bytes(dll.read_bytes())
        labels = ('alice-feature', 'alice-hidden', 'alice-hidden2', 'bob-feature')
        for label in labels:
            owner = label.split('-')[0]
            folder = tmp / 'libraries' / owner / ('Restricted' if label.startswith('alice-hidden') else label)
            folder.mkdir(parents=True, exist_ok=True)
            subprocess.run(['ffmpeg', '-nostdin', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=black:s=160x90:r=1', '-t', '1', '-c:v', 'mpeg4', str(folder / (label + '.mp4'))], check=True)
            (folder / (label + '.nfo')).write_text(f'<movie><title>{label}</title><year>2020</year></movie>')
        jpeg = tmp / 'backdrop.jpg'
        subprocess.run(['ffmpeg', '-nostdin', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=blue:s=320x180', '-frames:v', '1', '-update', '1', str(jpeg)], check=True)
        cid = docker('run', '-d', '--user', f'{os.getuid()}:{os.getgid()}', '-p', '127.0.0.1::8096',
                     '-v', f'{tmp}/config:/config', '-v', f'{tmp}/cache:/cache', '-v', f'{tmp}/libraries:/libraries:ro', 'jellyfin/jellyfin:12.1')
        try:
            base = 'http://127.0.0.1:' + docker('port', cid, '8096/tcp').rsplit(':', 1)[1]
            for _ in range(90):
                try:
                    if call(base, '/Startup/User')[0] == 200:
                        break
                except OSError:
                    pass
                time.sleep(1)
            else:
                raise AssertionError('startup timeout')
            alice_password, bob_password = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
            ok(call(base, '/Startup/User', 'POST', {'Name': 'alice', 'Password': alice_password}))
            ok(call(base, '/Startup/Complete', 'POST', {}))
            first = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'alice', 'Pw': alice_password}))
            admin, alice_id = first['AccessToken'], first['User']['Id']
            bob_id = ok(call(base, '/Users/New', 'POST', {'Name': 'bob', 'Password': bob_password}, admin))['Id']
            bob = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username': 'bob', 'Pw': bob_password}))['AccessToken']
            for owner in ('alice', 'bob'):
                ok(call(base, '/Library/VirtualFolders?name=' + owner + '&collectionType=movies&refreshLibrary=false', 'POST', {}, admin))
                ok(call(base, '/Library/VirtualFolders/Paths', 'POST', {'Name': owner, 'Path': '/libraries/' + owner}, admin))
            folders = ok(call(base, '/Library/VirtualFolders', token=admin))
            library_ids = {folder['Name']: folder['ItemId'] for folder in folders if folder['Name'] in ('alice', 'bob')}
            assert set(library_ids) == {'alice', 'bob'}, folders
            ok(call(base, '/Library/Refresh', 'POST', {}, admin))
            for _ in range(90):
                all_items = ok(call(base, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path&Limit=100', token=admin))['Items']
                item_ids = {Path(item['Path']).stem: item['Id'] for item in all_items if item.get('Path') and Path(item['Path']).stem in labels}
                if set(item_ids) == set(labels):
                    break
                time.sleep(1)
            assert set(item_ids) == set(labels), item_ids
            metadata = ok(call(base, '/System/Info', token=admin))['InternalMetadataPath']
            assert metadata.startswith('/config/'), metadata
            image_tags = {}
            image_paths = {}
            for label in labels:
                # Upload while the administrator can still see every library.
                encoded = base64.b64encode(jpeg.read_bytes())
                ok(call(base, '/Items/' + item_ids[label] + '/Images/Backdrop/0', 'POST', encoded, admin, 'image/jpeg'))
                image_info = ok(call(base, '/Items/' + item_ids[label] + '/Images', token=admin))
                info = next(entry for entry in image_info if entry['ImageType'] == 'Backdrop' and entry['ImageIndex'] == 0)
                assert info['Path'].startswith(metadata.rstrip('/') + '/library/'), info['Path']
                image_paths[label] = info['Path']
                image_tags[label] = info['ImageTag']
            for user_id, owner in ((alice_id, 'alice'), (bob_id, 'bob')):
                policy = ok(call(base, '/Users/' + user_id, token=admin))['Policy']
                policy['EnableAllFolders'] = False
                policy['EnabledFolders'] = [library_ids[owner]]
                if owner == 'alice':
                    policy['MaxParentalRating'] = 10
                ok(call(base, '/Users/' + user_id + '/Policy', 'POST', policy, admin))
                names = [view['Name'] for view in ok(call(base, '/Users/' + user_id + '/Views', token=admin))['Items']]
                assert owner in names and ('bob' if owner == 'alice' else 'alice') not in names, names
            hidden_parent = ok(call(base, '/Items/' + item_ids['alice-hidden'], token=admin))['ParentId']
            assert hidden_parent != library_ids['alice']
            assert ok(call(base, '/Items/' + hidden_parent, token=admin))['Path'] == '/libraries/alice/Restricted'
            parent = ok(call(base, '/Items/' + hidden_parent, token=admin))
            assert call(base, '/Items/' + item_ids['alice-hidden'], token=admin)[0] == 200
            parent['OfficialRating'] = 'R'
            ok(call(base, '/Items/' + hidden_parent, 'POST', parent, admin))
            assert call(base, '/Items/' + item_ids['alice-hidden'], token=admin)[0] == 404
            assert call(base, '/Items/' + item_ids['bob-feature'], token=admin)[0] == 404
            assert call(base, '/Items/' + item_ids['alice-feature'], token=bob)[0] == 404
            configuration = '/Plugins/' + PLUGIN + '/Configuration'
            config = ok(call(base, configuration, token=admin))
            config.update(HomeEnabled=True, HeroTrustedFilesystemEnabled=True, HeroLibraryIds=list(library_ids.values()))
            ok(call(base, configuration, 'POST', config, admin))
            assert ok(call(base, configuration, token=admin))['HeroLibraryIds'] == list(library_ids.values())
            results = {}
            for owner, token in (('alice', admin), ('bob', bob)):
                rows = slides(base, token)
                assert len(rows) == 1 and rows[0]['Id'] == item_ids[owner + '-feature'], (owner, rows)
                slide = rows[0]
                assert slide['ImageType'] == 'Backdrop' and slide['ImageIndex'] == 0 and slide['ImageTag']
                assert not any(key in slide for key in ('Path', 'Url', 'ImageUrl'))
                own = image(base, slide, token)
                assert own[0] == 200 and own[1].startswith(b'\xff\xd8\xff'), (owner, own[0], own[1][:30])
                assert own[2].get('Content-Type') == 'image/jpeg' and own[2].get('Cache-Control') == 'private, no-store'
                assert own[2].get('X-Content-Type-Options') == 'nosniff'
                other = bob if owner == 'alice' else admin
                assert image(base, slide, other)[0] == 404
                assert image(base, slide)[0] == 401
                results[owner] = {'slides': len(rows), 'own_image': own[0], 'cross_image': 404}
            assert item_ids['alice-hidden'] not in [row['Id'] for row in slides(base, admin)]
            hidden_slide = {'Id': item_ids['alice-hidden'], 'ImageTag': image_tags['alice-hidden']}
            assert image(base, hidden_slide, admin)[0] == 404
            # Replace only the actual index-0 metadata pathname; restore it even on failure.
            alice_slide = slides(base, admin)[0]
            stored = tmp / image_paths['alice-feature'].lstrip('/')
            assert stored.is_file() and stored.is_relative_to(tmp / 'config/metadata/library')
            backup = stored.with_name(stored.name + '.probe-backup')
            stored.rename(backup)
            try:
                stored.symlink_to(jpeg)  # outside the metadata root, with the same valid JPEG bytes
                assert image(base, alice_slide, admin)[0] == 404
            finally:
                stored.unlink(missing_ok=True)
                backup.rename(stored)
            assert image(base, alice_slide, admin)[0] == 200
            ok(call(base, '/Auth/Keys?app=RowanHeroProbe', 'POST', {}, admin))
            key = next(entry['AccessToken'] for entry in ok(call(base, '/Auth/Keys', token=admin))['Items'] if entry['AppName'] == 'RowanHeroProbe')
            assert call(base, '/Rowan/Home/Hero')[0] == 401
            assert call(base, '/Rowan/Home/Hero', token=key)[0] == 403
            assert image(base, slide, key)[0] == 403
            assert image(base, slide, admin)[0] == 404  # slide belongs to bob
            assert call(base, '/Rowan/Home/Hero/Image/not-a-guid?tag=abc', token=admin)[0] == 404
            assert call(base, '/Rowan/Home/Hero/Image/' + item_ids['alice-feature'] + '?tag=invalid', token=admin)[0] == 404
            config['HeroLibraryIds'] = [library_ids['alice']]
            ok(call(base, configuration, 'POST', config, admin))
            assert len(slides(base, admin)) == 1 and slides(base, bob) == []
            assert image(base, slide, bob)[0] == 404  # bob's formerly selected image
            config['HeroLibraryIds'] = []
            ok(call(base, configuration, 'POST', config, admin))
            assert slides(base, admin) == [] and slides(base, bob) == []
            assert image(base, slide, bob)[0] == 404
            config['HeroLibraryIds'] = [library_ids['alice'], library_ids['alice']]
            ok(call(base, configuration, 'POST', config, admin))
            assert slides(base, admin) == []
            config['HeroLibraryIds'] = [library_ids['alice']]
            config['HeroTrustedFilesystemEnabled'] = False
            ok(call(base, configuration, 'POST', config, admin))
            assert slides(base, admin) == []
            assert image(base, alice_slide, admin)[0] == 404
            print(json.dumps({'users': results, 'restricted_ancestor': 404, 'anonymous': 401,
                              'userless_key': 403, 'empty_selection': [], 'switch_selection': 'pass',
                              'duplicate_selection': [], 'metadata_backed_image': True,
                              'metadata_symlink': 404, 'restored_image': 200,
                              'child_lookup_before_parent_rating': 200,
                              'child_lookup_after_parent_rating': 404,
                              'disabled_feature_image': 404,
                              'image_cache': 'private, no-store', 'image_type': 'image/jpeg'}))
        except Exception:
            print(docker('logs', cid)[-5000:])
            raise
        finally:
            subprocess.run(['docker', 'rm', '-f', cid], capture_output=True, check=True)


if __name__ == '__main__':
    main()
