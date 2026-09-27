"""Disposable Jellyfin 12.1 + Seerr-shaped Discover HTTP proof."""
import json, os, secrets, subprocess, tempfile, time, urllib.error, urllib.request, uuid
from pathlib import Path

root = Path(__file__).resolve().parents[1]
scratch = Path(os.environ.get('TMPDIR') or tempfile.gettempdir())
plugin_id = 'bd36ab75-0f4a-49b6-92ef-3a93da040c7a'

def call(base, route, method='GET', body=None, token=None):
    auth = 'MediaBrowser Client="RowanDiscoverLab", Device="Lab", DeviceId="rowan-discover-lab", Version="1.0"'
    if token: auth += f', Token="{token}"'
    headers = {'Authorization':auth}
    if body is not None: headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(base+route, data=json.dumps(body).encode() if body is not None else None, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=25) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and 'json' in response.headers.get('Content-Type','') else None, dict(response.headers)
    except urllib.error.HTTPError as e: return e.code, None, dict(e.headers)

def ok(result):
    assert result[0] in (200,201,204), result[0]
    return result[1]

def docker(*args): return subprocess.check_output(['docker', *args], text=True).strip()

def rows(base, route, token, code=200):
    result = call(base, '/3picFin/HomeDiscover/'+route, token=token)
    assert result[0] == code, (route, result[0], code)
    if code == 200: assert result[2].get('Cache-Control') == 'private, no-store', result[2]
    return result[1]

with tempfile.TemporaryDirectory(prefix='rowan-discover-', dir=scratch) as directory:
    tmp = Path(directory)
    conf = tmp/'config'; cache = tmp/'cache'; plugins = conf/'plugins'/'Rowan'
    plugins.mkdir(parents=True); cache.mkdir()
    dll = root/'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
    (plugins/dll.name).write_bytes(dll.read_bytes())
    settings = conf/'plugins'/'configurations'; settings.mkdir()
    (settings/'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><SeerrEnabled>true</SeerrEnabled><SeerrBaseUrl>http://127.0.0.1:19877/</SeerrBaseUrl><SeerrApiKey>disposable-only</SeerrApiKey></PluginConfiguration>')
    (tmp/'libraries'/'alice').mkdir(parents=True); (tmp/'libraries'/'bob').mkdir(parents=True)
    cid = docker('run','-d','--user',f'{os.getuid()}:{os.getgid()}','-p','127.0.0.1::8096','-v',f'{conf}:/config','-v',f'{cache}:/cache','-v',f'{tmp}/libraries:/libraries:ro','jellyfin/jellyfin:12.1')
    sidecar = None
    try:
        sidecar = docker('run','-d','--network',f'container:{cid}','--user',f'{os.getuid()}:{os.getgid()}', '-v',f'{root}/tests/disposable-discover-stub.py:/stub.py:ro','-v',f'{tmp}:/shared','python:3.11-alpine','python','/stub.py')
        base = 'http://127.0.0.1:' + docker('port',cid,'8096/tcp').rsplit(':',1)[1]
        for _ in range(90):
            try:
                if call(base,'/Startup/User')[0] == 200: break
            except Exception: pass
            time.sleep(1)
        else: raise RuntimeError('startup timeout')
        password1, password2 = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
        ok(call(base,'/Startup/User','POST',{'Name':'alice','Password':password1}))
        ok(call(base,'/Startup/Complete','POST',{}))
        login1 = ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'alice','Pw':password1})); admin = login1['AccessToken']; alice = login1['User']['Id']
        bob = ok(call(base,'/Users/New','POST',{'Name':'bob','Password':password2},admin))['Id']
        login2 = ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'bob','Pw':password2})); bob_token = login2['AccessToken']
        (tmp/'users.json').write_text(json.dumps({str(uuid.UUID(alice)):41,str(uuid.UUID(bob)):42}))
        for name in ('alice','bob'):
            ok(call(base,'/Library/VirtualFolders?name='+name+'&collectionType=movies&refreshLibrary=false','POST',{},admin))
            ok(call(base,'/Library/VirtualFolders/Paths','POST',{'Name':name,'Path':'/libraries/'+name},admin))
        folders = ok(call(base,'/Library/VirtualFolders',token=admin)); ids = {x['Name']:x['ItemId'] for x in folders if x['Name'] in ('alice','bob')}
        assert set(ids) == {'alice','bob'}, folders
        for uid, own in ((alice,'alice'),(bob,'bob')):
            policy = ok(call(base,'/Users/'+uid,token=admin))['Policy']
            policy['EnableAllFolders'] = False; policy['EnabledFolders'] = [ids[own]]
            ok(call(base,'/Users/'+uid+'/Policy','POST',policy,admin))
            names = [x['Name'] for x in ok(call(base,'/Users/'+uid+'/Views',token=admin))['Items']]
            assert own in names and ('bob' if own == 'alice' else 'alice') not in names, names
        for route in ('Discover','DiscoverMovies','DiscoverTV'):
            rows(base,route+'?page=1',admin,404)
        config = ok(call(base,'/Plugins/'+plugin_id+'/Configuration',token=admin))
        assert not any(config.get(k) for k in ('HomeEnabled','DiscoverRowEnabled','DiscoverMoviesRowEnabled','DiscoverTvRowEnabled'))
        config.update(HomeEnabled=True,DiscoverRowEnabled=True)
        ok(call(base,'/Plugins/'+plugin_id+'/Configuration','POST',config,admin))
        rows(base,'DiscoverMovies?page=1',admin,404); rows(base,'DiscoverTV?page=1',admin,404)
        for flags, active in (({'DiscoverRowEnabled':False,'DiscoverMoviesRowEnabled':True},'DiscoverMovies'),({'DiscoverMoviesRowEnabled':False,'DiscoverTvRowEnabled':True},'DiscoverTV'),({'DiscoverRowEnabled':True,'DiscoverMoviesRowEnabled':True},'Discover')):
            config.update(flags)
            ok(call(base,'/Plugins/'+plugin_id+'/Configuration','POST',config,admin))
            for route, enabled in (("Discover",config['DiscoverRowEnabled']),("DiscoverMovies",config['DiscoverMoviesRowEnabled']),("DiscoverTV",config['DiscoverTvRowEnabled'])):
                rows(base,route+'?page=1',admin,200 if enabled else 404)
        assert active == 'Discover'
        expected = {'Discover':[('movie',101),('tv',201)],'DiscoverMovies':[('movie',101)],'DiscoverTV':[('tv',201)]}
        for token in (admin,bob_token):
            for route, wanted in expected.items():
                result = rows(base,route+'?page=1',token)
                assert result.get('Error') is None and [(x['MediaType'],x['TmdbId']) for x in result['Items']] == wanted, (route,result)
                wire = json.dumps(result)
                assert all(secret not in wire for secret in ('SECRET_PATH','disposable-only','privatePath','Adult Film','Teen Series','Unrated Series','Blocked Series')),wire
            before_invalid = (tmp/'calls').read_text()
            for route in expected:
                for page in ('0','101','not-a-number'):
                    assert call(base,'/3picFin/HomeDiscover/'+route+'?page='+page,token=token)[0] == 400
            assert (tmp/'calls').read_text() == before_invalid, 'invalid page reached upstream'
        before_denied = (tmp/'calls').read_text()
        for route in expected:
            rows(base,route+'?page=1',None,401)
        key_status = call(base,'/Auth/Keys?app=RowanDiscoverLab','POST',{},admin)[0]
        assert key_status in (200,201,204), key_status
        entries = ok(call(base,'/Auth/Keys',token=admin))
        keys = entries.get('Items',[]) if isinstance(entries,dict) else entries
        api_key = next(k['AccessToken'] for k in keys if k.get('AppName') == 'RowanDiscoverLab')
        for route in expected: rows(base,route+'?page=1',api_key,403)
        assert (tmp/'calls').read_text() == before_denied, 'anonymous or userless key reached upstream'
        # A failed detail page forces bounded, non-admitting enrichment across three catalog pages.
        for token in (admin,bob_token):
            result = rows(base,'DiscoverTV?page=2',token)
            assert result['Items'] == [], result
        calls = [json.loads(x) for x in (tmp/'calls').read_text().splitlines()]
        mapped = [x for x in calls if '/user/jellyfin/' in x['path']]
        catalog = [x for x in calls if '/discover/' in x['path']]
        details = [x for x in calls if '/api/v1/tv/' in x['path']]
        assert mapped and catalog and details and all(x['key_present'] for x in calls)
        assert all(x['acting'] is None for x in mapped)
        assert all(x['acting'] in ('41','42') for x in catalog+details)
        assert {x['acting'] for x in catalog} == {'41','42'}
        assert sum(x['path'].startswith('/api/v1/tv/3') for x in details) <= 40, len(details)
        print(json.dumps({'users':2,'disjoint_views':True,'flags':'independent/default-off','candidate_ids':expected,'anonymous':401,'userless_key':403,'mapped_users':sorted({x['acting'] for x in catalog}),'mapping_reads':len(mapped),'catalog_reads':len(catalog),'detail_reads':len(details),'failed_detail_reads':sum(x['path'].startswith('/api/v1/tv/3') for x in details),'bounded':True}))
    except Exception:
        if sidecar: print('STUB LOG',docker('logs',sidecar)[-1200:])
        print('JELLYFIN LOG',docker('logs',cid)[-4500:])
        raise
    finally:
        subprocess.run(['docker','rm','-f']+([sidecar] if sidecar else [])+[cid],capture_output=True)
