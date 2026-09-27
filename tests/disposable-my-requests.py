"""Disposable Jellyfin 12.1 two-user personal My Requests HTTP proof."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path(os.environ.get('TMPDIR') or tempfile.gettempdir())

def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()

def call(base, path, method='GET', body=None, token=None):
    auth = 'MediaBrowser Client="RowanProbe", Device="Lab", DeviceId="rowan-my-requests-lab", Version="1.0"'
    if token: auth += f', Token="{token}"'
    headers = {'Authorization': auth}
    if body is not None: headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(base + path, method=method, headers=headers,
        data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and 'json' in response.headers.get('Content-Type', '') else None, dict(response.headers)
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw and 'json' in error.headers.get('Content-Type', '') else None, dict(error.headers)

def ok(response):
    assert response[0] in (200, 201, 204), response[0]
    return response[1]

def main():
    subprocess.run(['dotnet', 'build', str(ROOT / 'src/Rowan.Jellyfin.Plugin/Rowan.Jellyfin.Plugin.csproj'), '-c', 'Release', '-v', 'quiet'], check=True)
    with tempfile.TemporaryDirectory(prefix='rowan-my-requests-', dir=SCRATCH) as temp:
        tmp = Path(temp)
        plugins = tmp / 'config/plugins/Rowan'; plugins.mkdir(parents=True)
        settings = tmp / 'config/plugins/configurations'; settings.mkdir()
        (tmp / 'cache').mkdir()
        dll = ROOT / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
        (plugins / dll.name).write_bytes(dll.read_bytes())
        (settings / 'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><HomeEnabled>true</HomeEnabled><MyRequestsRowEnabled>true</MyRequestsRowEnabled><SeerrEnabled>true</SeerrEnabled><SeerrBaseUrl>http://127.0.0.1:19876/</SeerrBaseUrl><SeerrApiKey>disposable-only</SeerrApiKey></PluginConfiguration>')
        labels = ['alice-own', 'bob-own', 'restricted', 'unrequested4k']
        for label in labels:
            owner = 'bob' if label == 'bob-own' else 'alice'
            folder = tmp / 'libraries' / owner / label; folder.mkdir(parents=True)
            subprocess.run(['ffmpeg', '-nostdin', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=c=black:s=160x90:r=1', '-t', '1', '-c:v', 'mpeg4', str(folder / (label + '.mp4'))], check=True)
            if label == 'restricted': (folder / (label + '.nfo')).write_text('<movie><title>restricted</title><mpaa>Rated R</mpaa></movie>')
        cid = docker('run', '-d', '--user', f'{os.getuid()}:{os.getgid()}', '-p', '127.0.0.1::8096', '-v', f'{tmp}/config:/config', '-v', f'{tmp}/cache:/cache', '-v', f'{tmp}/libraries:/libraries:ro', 'jellyfin/jellyfin:12.1')
        stub = None
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
                all_items = []
                for user_id in (alice_id, bob_id):
                    all_items.extend(ok(call(base, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path&Limit=100&UserId=' + user_id, token=admin))['Items'])
                item_ids = {Path(item['Path']).stem:item['Id'] for item in all_items if item.get('Path') and Path(item['Path']).stem in labels}
                if set(item_ids) == set(labels): break
                time.sleep(1)
            assert set(item_ids) == set(labels), {'items': [(x['Name'], x['Type']) for x in all_items], 'folders': [(f['Name'],f.get('Locations')) for f in folders]}
            # Set rating via Jellyfin's metadata endpoint and verify persisted policy input.
            rated = ok(call(base, '/Items/' + item_ids['restricted'], token=admin))
            rated['OfficialRating'] = 'R'
            ok(call(base, '/Items/' + item_ids['restricted'], 'POST', rated, admin))
            rated = ok(call(base, '/Items/' + item_ids['restricted'], token=admin))
            assert rated.get('OfficialRating') == 'R', {k:rated.get(k) for k in ('Name','Type','OfficialRating')}
            for user_id, name in ((alice_id,'alice'), (bob_id,'bob')):
                policy = ok(call(base, '/Users/' + user_id, token=admin))['Policy']
                policy['EnableAllFolders'] = False; policy['EnabledFolders'] = [ids[name]]
                if name == 'alice': policy['MaxParentalRating'] = 10
                ok(call(base, '/Users/' + user_id + '/Policy', 'POST', policy, admin))
                views = ok(call(base, '/Users/' + user_id + '/Views', token=admin))['Items']
                assert name in [v['Name'] for v in views] and ('bob' if name == 'alice' else 'alice') not in [v['Name'] for v in views]
            assert call(base, '/Items/' + item_ids['alice-own'], token=admin)[0] == 200
            assert call(base, '/Items/' + item_ids['bob-own'], token=admin)[0] == 404
            assert call(base, '/Items/' + item_ids['restricted'], token=admin)[0] == 404
            assert call(base, '/Items/' + item_ids['bob-own'], token=bob)[0] == 200
            assert call(base, '/Items/' + item_ids['alice-own'], token=bob)[0] == 404
            fixture = {'items': item_ids, 'users': {label: {'label':label,'jellyfin':str(uuid.UUID(identity)),'seerr':n}
                for label,identity,n in (('alice',alice_id,71),('bob',bob_id,72))}}
            (tmp / 'fixture.json').write_text(json.dumps(fixture))
            stub = docker('run','-d','--network',f'container:{cid}','--user',f'{os.getuid()}:{os.getgid()}',
                '-v',f'{ROOT}/tests/disposable-my-requests-stub.py:/stub.py:ro','-v',f'{tmp}:/shared',
                'python:3.11-alpine','python','/stub.py')
            endpoint = '/Rowan/Home/Rows/MyRequests'
            results = {}
            for label, token in (('alice',admin),('bob',bob)):
                for _ in range(20):
                    response = call(base, endpoint, token=token)
                    if response[0] not in (404, 502): break
                    time.sleep(1)
                status, row, headers = response
                assert status == 200 and row['Kind'] == 'MyRequests', (label,status,row, (tmp/'calls').read_text() if (tmp/'calls').exists() else 'no calls', docker('logs',stub)[-1000:])
                assert headers.get('Cache-Control') == 'private, no-store', headers
                returned = [item['Id'] for item in row['Items']]
                assert returned == [item_ids[label+'-own']], (label, returned, item_ids)
                assert 'NEVER_EXPOSE_PATH' not in json.dumps(row)
                results[label] = returned
            assert call(base, endpoint)[0] == 401
            ok(call(base, '/Auth/Keys?app=RowanMyRequestsProbe', 'POST', {}, admin))
            keys = ok(call(base, '/Auth/Keys', token=admin))['Items']
            api_key = next(k['AccessToken'] for k in keys if k['AppName'] == 'RowanMyRequestsProbe')
            assert call(base, endpoint, token=api_key)[0] == 403
            calls = [json.loads(line) for line in (tmp / 'calls').read_text().splitlines()]
            request_calls = [c for c in calls if c['path'].startswith('/api/v1/request?')]
            assert {'71','72'} <= {c['user'] for c in request_calls}, request_calls
            (tmp / 'mismatch').touch()
            mismatch_status, mismatch_body, mismatch_headers = call(base, endpoint, token=admin)
            assert mismatch_status == 502 and mismatch_body == {'Error': 'UpstreamUnavailable'}, (mismatch_status, mismatch_body)
            assert mismatch_headers.get('Cache-Control') == 'private, no-store'
            print(json.dumps({'users': {k:len(v) for k,v in results.items()}, 'foreign_hidden_parental_and_4k_excluded':True,
                'anonymous':401,'userless_key':403,'requester_mismatch':mismatch_status,'cache':'private, no-store','mapped_request_reads':len(request_calls)}))
        except Exception:
            # No logs: application traces may contain generated session tokens.
            raise
        finally:
            subprocess.run(['docker','rm','-f'] + ([stub] if stub else []) + [cid], capture_output=True, check=True)

if __name__ == '__main__': main()
