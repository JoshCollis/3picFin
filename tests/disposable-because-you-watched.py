"""Disposable Jellyfin 12.1 Because You Watched HTTP proof."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path(os.environ.get('TMPDIR') or tempfile.gettempdir())

def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()

def call(base, path, method='GET', body=None, token=None):
    auth = 'MediaBrowser Client="RowanProbe", Device="Lab", DeviceId="rowan-byw-lab", Version="1.0"'
    if token: auth += f', Token="{token}"'
    headers = {'Authorization': auth}
    if body is not None: headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(base + path, method=method, headers=headers,
        data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and 'json' in response.headers.get('Content-Type', '') else None, dict(response.headers)
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw and 'json' in error.headers.get('Content-Type', '') else None, dict(error.headers)

def ok(response):
    assert response[0] in (200, 201, 204), (response[0], response[1])
    return response[1]

def main():
    subprocess.run(['dotnet', 'build', str(ROOT / 'src/Rowan.Jellyfin.Plugin/Rowan.Jellyfin.Plugin.csproj'), '-c', 'Release', '-v', 'quiet'], check=True)
    with tempfile.TemporaryDirectory(prefix='rowan-byw-', dir=SCRATCH) as temp:
        tmp = Path(temp)
        plugins = tmp / 'config/plugins/Rowan'; plugins.mkdir(parents=True)
        settings = tmp / 'config/plugins/configurations'; settings.mkdir()
        (tmp / 'cache').mkdir()
        dll = ROOT / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
        (plugins / dll.name).write_bytes(dll.read_bytes())
        (settings / 'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><HomeEnabled>true</HomeEnabled><BecauseYouWatchedRowEnabled>true</BecauseYouWatchedRowEnabled><BecauseYouWatchedHideWatched>true</BecauseYouWatchedHideWatched></PluginConfiguration>')
        labels = ['alice-seed', 'alice-similar', 'alice-hidden', 'alice-hidden2', 'bob-seed', 'bob-similar']
        for label in labels:
            owner = label.split('-')[0]
            folder = tmp / 'libraries' / owner / ('Restricted' if label.startswith('alice-hidden') else label)
            folder.mkdir(parents=True, exist_ok=True)
            subprocess.run(['ffmpeg', '-nostdin', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=black:s=160x90:r=1', '-t', '1', '-c:v', 'mpeg4', str(folder / (label + '.mp4'))], check=True)
            (folder / (label + '.nfo')).write_text(f'<movie><title>{label}</title><year>2020</year><genre>Action</genre><studio>Probe Studio</studio><tag>probe</tag></movie>')
        cid = docker('run', '-d', '--user', f'{os.getuid()}:{os.getgid()}', '-p', '127.0.0.1::8096', '-v', f'{tmp}/config:/config', '-v', f'{tmp}/cache:/cache', '-v', f'{tmp}/libraries:/libraries:ro', 'jellyfin/jellyfin:12.1')
        try:
            base = 'http://127.0.0.1:' + docker('port', cid, '8096/tcp').rsplit(':', 1)[1]
            for _ in range(90):
                try:
                    if call(base, '/Startup/User')[0] == 200: break
                except OSError: pass
                time.sleep(1)
            else: raise AssertionError('startup timeout')
            passwords = [secrets.token_urlsafe(18), secrets.token_urlsafe(18)]
            ok(call(base, '/Startup/User', 'POST', {'Name':'alice','Password':passwords[0]}))
            ok(call(base, '/Startup/Complete', 'POST', {}))
            first = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username':'alice','Pw':passwords[0]}))
            admin = first['AccessToken']; alice_id = first['User']['Id']
            bob_id = ok(call(base, '/Users/New', 'POST', {'Name':'bob','Password':passwords[1]}, admin))['Id']
            bob = ok(call(base, '/Users/AuthenticateByName', 'POST', {'Username':'bob','Pw':passwords[1]}))['AccessToken']
            for owner in ('alice', 'bob'):
                ok(call(base, '/Library/VirtualFolders?name=' + owner + '&collectionType=movies&refreshLibrary=false', 'POST', {}, admin))
                ok(call(base, '/Library/VirtualFolders/Paths', 'POST', {'Name':owner,'Path':'/libraries/'+owner}, admin))
            folders = ok(call(base, '/Library/VirtualFolders', token=admin))
            ids = {f['Name']:f['ItemId'] for f in folders if f['Name'] in ('alice','bob')}
            assert set(ids) == {'alice','bob'}, folders
            ok(call(base, '/Library/Refresh', 'POST', {}, admin))
            item_ids = {}
            for _ in range(75):
                all_items = ok(call(base, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path&Limit=100', token=admin))['Items']
                item_ids = {Path(item['Path']).stem:item['Id'] for item in all_items if item.get('Path') and Path(item['Path']).stem in labels}
                if set(item_ids) == set(labels): break
                time.sleep(1)
            assert set(item_ids) == set(labels), [(x['Name'],x['Type']) for x in all_items]
            for user_id, name in ((alice_id,'alice'), (bob_id,'bob')):
                policy = ok(call(base, '/Users/' + user_id, token=admin))['Policy']
                policy['EnableAllFolders'] = False; policy['EnabledFolders'] = [ids[name]]
                if name == 'alice': policy['MaxParentalRating'] = 10
                ok(call(base, '/Users/' + user_id + '/Policy', 'POST', policy, admin))
                views = ok(call(base, '/Users/' + user_id + '/Views', token=admin))['Items']
                assert name in [v['Name'] for v in views] and ('bob' if name == 'alice' else 'alice') not in [v['Name'] for v in views]
            # Parent restriction: rate containing folder R while child movie stays unrated.
            hidden = ok(call(base, '/Items/' + item_ids['alice-hidden'], token=admin))
            parent_id = hidden['ParentId']
            parent = ok(call(base, '/Items/' + parent_id, token=admin))
            assert parent_id != ids['alice'], ('restriction parent is library', parent['Type'])
            parent['OfficialRating'] = 'R'
            ok(call(base, '/Items/' + parent_id, 'POST', parent, admin))
            assert call(base, '/Items/' + parent_id, token=admin)[0] == 404
            assert call(base, '/Items/' + item_ids['alice-hidden'], token=admin)[0] == 404
            assert call(base, '/Items/' + item_ids['bob-seed'], token=admin)[0] == 404
            assert call(base, '/Items/' + item_ids['alice-seed'], token=bob)[0] == 404
            for label, token in (('alice',admin),('bob',bob)):
                seed = label + '-seed'
                user_id = alice_id if label == 'alice' else bob_id
                assert call(base, '/Items/' + item_ids[seed], token=token)[0] == 200, (label, 'seed not visible')
                played_response = call(base, '/UserPlayedItems/' + item_ids[seed] + '?userId=' + user_id, 'POST', {}, token)
                assert played_response[0] == 200, (label, played_response[0], played_response[1])
            endpoint = '/Rowan/Home/BecauseYouWatched'
            rows_by_user = {}
            for label, token in (('alice',admin),('bob',bob)):
                seed = label + '-seed'
                for _ in range(20):
                    response = call(base, endpoint, token=token)
                    if response[0] != 404: break
                    time.sleep(1)
                status, rows, headers = response
                assert status == 200 and isinstance(rows, list), (label,status,rows)
                assert headers.get('Cache-Control') == 'private, no-store', headers
                ids_in_wire = [id for row in rows for id in ([row['SeedId']] + [x['Id'] for x in row['Items']])]
                assert item_ids[seed] in ids_in_wire, (label, [r['SeedId'] for r in rows])
                seed_row = next(row for row in rows if row['SeedId'] == item_ids[seed])
                assert item_ids[label+'-similar'] in [card['Id'] for card in seed_row['Items']], (label, [card['Id'] for card in seed_row['Items']])
                assert not (set(ids_in_wire) & {item_ids[n] for n in labels if n.startswith(('bob-' if label == 'alice' else 'alice-'))}), (label,rows)
                assert item_ids['alice-hidden'] not in ids_in_wire, (label,rows)
                assert all(row['Heading'] == 'Because You Watched ' + row['Seed']['Name'] for row in rows), rows
                rows_by_user[label] = [{'seed': row['SeedId'], 'results': [x['Id'] for x in row['Items']]} for row in rows]
            for label, token in (('alice',admin),('bob',bob)):
                user_id = alice_id if label == 'alice' else bob_id
                similar_id = item_ids[label+'-similar']
                assert call(base, '/UserPlayedItems/' + similar_id + '?userId=' + user_id, 'POST', {}, token)[0] == 200
                watched_rows = ok(call(base, endpoint, token=token))
                original = next((row for row in (watched_rows or []) if row['SeedId'] == item_ids[label+'-seed']), None)
                assert original is None or similar_id not in [card['Id'] for card in original['Items']], (label, 'watched result remained')
            assert call(base, endpoint)[0] == 401
            ok(call(base, '/Auth/Keys?app=RowanBywProbe', 'POST', {}, admin))
            keys = ok(call(base, '/Auth/Keys', token=admin))['Items']
            api_key = next(k['AccessToken'] for k in keys if k['AppName'] == 'RowanBywProbe')
            assert call(base, endpoint, token=api_key)[0] == 403
            print(json.dumps({'rows':rows_by_user,'item_ids':item_ids,'parent_rating':'R','anonymous':401,'userless_key':403,'cache':'private, no-store'}))
        finally:
            subprocess.run(['docker','rm','-f',cid], capture_output=True, check=True)

if __name__ == '__main__': main()
