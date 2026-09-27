"""Disposable v12.1 two-user Seerr shared-read HTTP probe."""
import json, os, secrets, subprocess, tempfile, time, urllib.request, urllib.error
from pathlib import Path

root = Path(__file__).resolve().parents[1]
scratch = Path(os.environ.get('TMPDIR') or tempfile.gettempdir())


def call(url, method='GET', body=None, token=None):
    headers = {'Authorization': 'MediaBrowser Client="RowanProbe", Device="Lab", DeviceId="rowan-shared-lab", Version="1.0"'}
    if token: headers['Authorization'] += f', Token="{token}"'
    if body is not None: headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=20) as r: return r.status, json.loads(r.read() or b'null') if 'json' in r.headers.get('Content-Type', '') else None
    except urllib.error.HTTPError as e: return e.code, None

def docker(*args): return subprocess.check_output(['docker', *args], text=True).strip()
with tempfile.TemporaryDirectory(prefix='rowan-shared-', dir=scratch) as tmp:
    conf = Path(tmp)/'config'; cache = Path(tmp)/'cache'; plugins = conf/'plugins'/'Rowan'; plugins.mkdir(parents=True); cache.mkdir()
    dll = root/'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
    (plugins/dll.name).write_bytes(dll.read_bytes())
    settings = conf/'plugins'/'configurations'; settings.mkdir()
    (settings/'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><SeerrEnabled>true</SeerrEnabled><SharedRequestsEnabled>true</SharedRequestsEnabled><SeerrBaseUrl>http://127.0.0.1:19876/</SeerrBaseUrl><SeerrApiKey>test-only</SeerrApiKey></PluginConfiguration>')
    cid = docker('run','-d','--user',f'{os.getuid()}:{os.getgid()}','-p','127.0.0.1::8096','-v',f'{conf}:/config','-v',f'{cache}:/cache','jellyfin/jellyfin:12.1')
    sidecar = None
    try:
        sidecar = docker('run','-d','--network',f'container:{cid}','--user',f'{os.getuid()}:{os.getgid()}', '-v',f'{root}/tests/disposable-seerr-stub.py:/stub.py:ro','-v',f'{tmp}:/shared','python:3.11-alpine','python','/stub.py')
        base = 'http://127.0.0.1:' + docker('port',cid,'8096/tcp').rsplit(':',1)[1]
        for _ in range(90):
            try:
                status,_ = call(base+'/Startup/User')
                if status == 200: break
            except Exception: pass
            time.sleep(1)
        else: raise RuntimeError('startup timeout')
        name1, name2 = 'alice', 'bob'
        password1, password2 = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
        s,_ = call(base+'/Startup/User','POST',{'Name':name1,'Password':password1}); assert s in (200,204), ('startup user',s)
        s,_ = call(base+'/Startup/Complete','POST',{}); assert s in (200,204), ('startup complete',s)
        status, login = call(base+'/Users/AuthenticateByName','POST',{'Username':name1,'Pw':password1}); assert status == 200, status
        admin = login['AccessToken']
        status, second = call(base+'/Users/New','POST',{'Name':name2,'Password':password2},admin); assert status in (200,201), status
        status, login2 = call(base+'/Users/AuthenticateByName','POST',{'Username':name2,'Pw':password2}); assert status == 200, status
        users = [admin, login2['AccessToken']]
        results = []
        for token in users:
            for _ in range(20):
                status, data = call(base+'/3picFin/SharedRequests?page=1',token=token)
                if status != 503: break
                time.sleep(1)
            results.append((status,data))
        anon = call(base+'/3picFin/SharedRequests?page=1')[0]
        key_status, key_data = call(base+'/Auth/Keys?app=RowanProbe','POST',{},admin)
        key_result = None
        if key_status in (200,201,204):
            _, keys = call(base+'/Auth/Keys',token=admin)
            entries = keys.get('Items',[]) if isinstance(keys,dict) else []
            matching = [key for key in entries if key.get('AppName') == 'RowanProbe']
            if matching:
                api_key = matching[0].get('AccessToken')
                if api_key: key_result = call(base+'/3picFin/SharedRequests?page=1',token=api_key)[0]
        assert [r[0] for r in results] == [200,200], [r[0] for r in results]
        assert results[0][1] == results[1][1], 'different lists'
        wire = json.dumps(results[0][1]); assert 'PRIVATE_OWNER' not in wire and 'PRIVATE_PATH' not in wire, wire
        assert anon in (401,403), anon
        assert key_result == 403, key_result
        calls = [json.loads(line) for line in (Path(tmp)/'calls').read_text().splitlines()]
        assert len(calls) == 2 and all(u is None for _,u in calls), calls
        print(json.dumps({'users':[r[0] for r in results], 'anonymous':anon, 'userless_key':key_result, 'key_creation':key_status, 'shared_upstream_reads':len(calls), 'same_allowlisted_dto':True}))
    except Exception:
        if sidecar: print('STUB', docker('logs',sidecar)[-1000:])
        print(docker('logs',cid)[-5000:])
        raise
    finally:
        subprocess.run(['docker','rm','-f'] + ([sidecar] if sidecar else []) + [cid], capture_output=True)
