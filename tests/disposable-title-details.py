"""Two-user disposable Jellyfin 12.1 title-details HTTP and optional signed-in browser probe."""
import asyncio
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('home_lab', ROOT/'tests/disposable-home-adapter.py')
assert spec and spec.loader
home = importlib.util.module_from_spec(spec); spec.loader.exec_module(home)
from playwright.async_api import async_playwright


def call(base, path, method='GET', body=None, token=None):
    auth = 'MediaBrowser Client="TitleLab", Device="Lab", DeviceId="title-lab", Version="1.0"'
    if token: auth += f', Token="{token}"'
    headers = {'Authorization': auth}
    if body is not None: headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(base+path, data=json.dumps(body).encode() if body is not None else None, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=25) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and 'json' in response.headers.get('Content-Type','') else None, dict(response.headers)
    except urllib.error.HTTPError as e:
        raw = e.read()
        return e.code, json.loads(raw) if raw and 'json' in e.headers.get('Content-Type','') else None, dict(e.headers)


def ok(result):
    assert result[0] in (200, 201, 204), result[0]
    return result[1]


def video(path):
    subprocess.run(['ffmpeg','-nostdin','-loglevel','error','-f','lavfi','-i','color=c=black:s=160x90:r=1','-t','1','-c:v','mpeg4',str(path)],check=True)


def main():
    browser_mode = '--browser' in sys.argv
    with tempfile.TemporaryDirectory(prefix='rowan-title-details-', dir=home.SCRATCH) as temp:
        tmp = Path(temp)
        source = ROOT/'src/Rowan.Jellyfin.Plugin'
        subprocess.run(['dotnet','build',str(source/'Rowan.Jellyfin.Plugin.csproj'),'-c','Release','-v','quiet'],check=True)
        plugins = tmp/'config/plugins/Rowan'; plugins.mkdir(parents=True)
        (tmp/'cache').mkdir()
        dll = source/'bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
        (plugins/dll.name).write_bytes(dll.read_bytes())
        settings = plugins.parent/'configurations'; settings.mkdir()
        (settings/'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><HomeEnabled>true</HomeEnabled><DiscoveryPageEnabled>true</DiscoveryPageEnabled><SeerrEnabled>true</SeerrEnabled><SeerrBaseUrl>http://127.0.0.1:19878/</SeerrBaseUrl><SeerrApiKey>disposable-only</SeerrApiKey></PluginConfiguration>')
        if browser_mode:
            for label,(repo,version) in home.DEPENDENCIES.items():
                url = f'https://github.com/IAmParadox27/{repo}/releases/download/{version}/Release-12.1.0.zip'
                with urllib.request.urlopen(url,timeout=60) as response: archive = zipfile.ZipFile(io.BytesIO(response.read()))
                target = plugins.parent/label; target.mkdir()
                for name in archive.namelist():
                    if '/' not in name and name.endswith(('.dll','.deps.json')): (target/name).write_bytes(archive.read(name))
        # Each user sees one movie and one TV library; parent restriction lives below the library root.
        entries = [('alice','movies','own',101),('bob','movies','foreign',102),('alice','movies','mismatch',103),
                   ('alice','movies','hidden/child',104),('alice','tv','series',201),('bob','tv','other-series',202)]
        for owner,kind,label,tmdb in entries:
            folder = tmp/'libraries'/owner/kind/label; folder.mkdir(parents=True)
            if kind == 'movies':
                video(folder/(label.split('/')[-1]+'.mp4'))
                (folder/(label.split('/')[-1]+'.nfo')).write_text(f'<movie><title>{label}</title><uniqueid type="tmdb" default="true">{tmdb}</uniqueid></movie>')
            else:
                (folder/'tvshow.nfo').write_text(f'<tvshow><title>{label}</title><uniqueid type="tmdb" default="true">{tmdb}</uniqueid></tvshow>')
                season = folder/'Season 01'; season.mkdir()
                video(season/'S01E01.mp4')
        cid = home.docker('run','-d','--user',f'{os.getuid()}:{os.getgid()}','-p','127.0.0.1::8096','-v',f'{tmp}/config:/config','-v',f'{tmp}/cache:/cache','-v',f'{tmp}/libraries:/libraries:ro','jellyfin/jellyfin:12.1')
        sidecar = None
        try:
            base = 'http://127.0.0.1:' + home.docker('port',cid,'8096/tcp').rsplit(':',1)[1]
            for _ in range(100):
                try:
                    if call(base,'/Startup/User')[0] == 200: break
                except OSError: pass
                time.sleep(1)
            else: raise AssertionError('startup timeout')
            pw1,pw2 = secrets.token_urlsafe(18),secrets.token_urlsafe(18)
            ok(call(base,'/Startup/User','POST',{'Name':'alice','Password':pw1}))
            ok(call(base,'/Startup/Complete','POST',{}))
            login = ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'alice','Pw':pw1})); alice_token = login['AccessToken']; alice = login['User']['Id']
            bob = ok(call(base,'/Users/New','POST',{'Name':'bob','Password':pw2},alice_token))['Id']
            bob_token = ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'bob','Pw':pw2}))['AccessToken']
            (tmp/'users.json').write_text(json.dumps({str(uuid.UUID(alice)):71,str(uuid.UUID(bob)):72}))
            for owner in ('alice','bob'):
                for kind in ('movies','tv'):
                    name = owner+'-'+kind
                    ok(call(base,f'/Library/VirtualFolders?name={name}&collectionType={"movies" if kind == "movies" else "tvshows"}&refreshLibrary=false','POST',{},alice_token))
                    ok(call(base,'/Library/VirtualFolders/Paths','POST',{'Name':name,'Path':f'/libraries/{owner}/{kind}'},alice_token))
            folders = ok(call(base,'/Library/VirtualFolders',token=alice_token))
            ids = {f['Name']:f['ItemId'] for f in folders if f['Name'] in [o+'-'+k for o in ('alice','bob') for k in ('movies','tv')]}
            assert len(ids) == 4, ids
            ok(call(base,'/Library/Refresh','POST',{},alice_token))
            items = {}
            for _ in range(100):
                found = ok(call(base,'/Items?Recursive=true&IncludeItemTypes=Movie,Series&Fields=ProviderIds,Path&Limit=100&UserId='+alice,token=alice_token))['Items']
                found += ok(call(base,'/Items?Recursive=true&IncludeItemTypes=Movie,Series&Fields=ProviderIds,Path&Limit=100&UserId='+bob,token=alice_token))['Items']
                items = {int(x.get('ProviderIds',{}).get('Tmdb')):x for x in found if str(x.get('ProviderIds',{}).get('Tmdb','')).isdigit()}
                if all(i in items for i in (101,102,103,104,201,202)): break
                time.sleep(1)
            assert all(i in items for i in (101,102,103,104,201,202)), [(x.get('Name'),x.get('ProviderIds')) for x in found]
            for ident in (101,103,201):
                visible = ok(call(base,'/Items/'+items[ident]['Id'],token=alice_token))
                visible['OfficialRating'] = 'G'
                ok(call(base,'/Items/'+items[ident]['Id'],'POST',visible,alice_token))
            parent = ok(call(base,'/Items/'+items[104]['Id'],token=alice_token))['ParentId']; assert parent != ids['alice-movies'], (parent,ids['alice-movies'])
            parent_item = ok(call(base,'/Items/'+parent,token=alice_token)); parent_item['OfficialRating'] = 'R'
            ok(call(base,'/Items/'+parent,'POST',parent_item,alice_token))
            for uid,owner in ((alice,'alice'),(bob,'bob')):
                policy = ok(call(base,'/Users/'+uid,token=alice_token))['Policy']
                policy['EnableAllFolders'] = False; policy['EnabledFolders'] = [ids[owner+'-'+kind] for kind in ('movies','tv')]
                if owner == 'alice': policy['MaxParentalRating'] = 10
                ok(call(base,'/Users/'+uid+'/Policy','POST',policy,alice_token))
                views = [x['Name'] for x in ok(call(base,'/Users/'+uid+'/Views',token=alice_token))['Items']]
                assert all(owner+'-'+kind in views for kind in ('movies','tv')) and not any(('bob' if owner=='alice' else 'alice')+'-'+kind in views for kind in ('movies','tv')), views
            # Mismatched Seerr hint must not be accepted even when a local TMDb item exists.
            (tmp/'hints.json').write_text(json.dumps({'103':items[101]['Id']}))
            assert call(base,'/Items/'+items[104]['Id'],token=alice_token)[0] == 404
            assert call(base,'/Items/'+items[102]['Id'],token=alice_token)[0] == 404
            assert call(base,'/Items/'+items[101]['Id'],token=bob_token)[0] == 404
            sidecar = home.docker('run','-d','--network',f'container:{cid}','--user',f'{os.getuid()}:{os.getgid()}','-v',f'{ROOT}/tests/disposable-title-details-stub.py:/stub.py:ro','-v',f'{tmp}:/shared','python:3.11-alpine','python','/stub.py')
            def detail(kind, ident, token, expected=200):
                result = call(base,f'/3picFin/TitleDetails?mediaType={kind}&mediaId={ident}',token=token)
                assert result[0] == expected, (kind,ident,result[0],result[1])
                if expected == 200:
                    assert result[2].get('Cache-Control') == 'private, no-store'
                    assert set(result[1]) == {'Title','Overview','PosterPath','MediaType','TmdbId','MediaStatus','CanRequest','CanRequest4k','Seasons','Date'} | ({'LibraryItemId'} if 'LibraryItemId' in result[1] else set()), result[1]
                    assert 'NEVER_EXPOSE' not in json.dumps(result[1])
                return result[1]
            for _ in range(25):
                status = call(base,'/3picFin/TitleDetails?mediaType=movie&mediaId=101',token=alice_token)[0]
                if status not in (404,502): break
                time.sleep(1)
            own = detail('movie',101,alice_token)
            compact = items[101]['Id'].replace('-','').lower()
            assert own['LibraryItemId'].replace('-','').lower() == compact and re.fullmatch(r'[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}',own['LibraryItemId'],re.I)
            assert own['MediaStatus'] == 5 and own['CanRequest'] is True
            assert detail('movie',101,bob_token).get('LibraryItemId') is None
            assert detail('movie',102,alice_token).get('LibraryItemId') is None
            assert detail('movie',102,bob_token)['LibraryItemId'] is not None
            assert detail('movie',104,alice_token).get('LibraryItemId') is None
            assert detail('movie',103,alice_token).get('LibraryItemId') is None
            series = detail('tv',201,alice_token)
            assert series['LibraryItemId'] is not None and series['Seasons'] == [1,2] and series['CanRequest'] is True
            assert detail('tv',201,bob_token).get('LibraryItemId') is None
            detail('movie',101,None,401)
            ok(call(base,'/Auth/Keys?app=RowanTitleLab','POST',{},alice_token))
            keys = ok(call(base,'/Auth/Keys',token=alice_token))['Items']
            api_key = next(x['AccessToken'] for x in keys if x['AppName']=='RowanTitleLab')
            detail('movie',101,api_key,403)
            calls = [json.loads(x) for x in (tmp/'calls').read_text().splitlines()]
            assert all(x['key_present'] for x in calls)
            assert all(x['acting'] is None for x in calls if '/user/jellyfin/' in x['path'])
            assert set(x['acting'] for x in calls if '/user/jellyfin/' not in x['path']) == {'71','72'}
            assert len(calls) == 2*sum(1 for x in calls if '/user/jellyfin/' in x['path'])
            print(json.dumps({'http':'pass','users':2,'disjoint_movie_tv':True,'own_id_wire':own['LibraryItemId'],'wire_keys':list(own),'hidden_parent':None,'mismatched_hint':None,'tv_seasons':series['Seasons'],'anonymous':401,'userless_key':403,'upstream_calls':len(calls)}))
            if browser_mode:
                for _ in range(45):
                    status,html = home.request(base+'/web/index.html')
                    if status == 200 and html.count(b'data-threepic-fin-adapter') == 1: break
                    time.sleep(1)
                else: raise AssertionError('portable adapter unavailable')
                asyncio.run(browser_probe(base,pw1,items[101]['Id'],items[201]['Id']))
        finally:
            subprocess.run(['docker','rm','-f']+([sidecar] if sidecar else [])+[cid],capture_output=True,check=True)


async def browser_probe(base,password,movie_id,series_id):
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True)
        try:
            page = await browser.new_page()
            await page.goto(base+'/web/index.html',wait_until='domcontentloaded')
            await page.locator('#txtManualName').fill('alice')
            await page.locator('#txtManualPassword').fill(password)
            await page.locator('#txtManualPassword').press('Enter')
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(timeout=45000)
            await page.locator('.threepic-fin-host__tab').filter(has_text='3pic Fin').click()
            card = page.get_by_role('button', name='Details for Probe movie')
            await card.first.wait_for(timeout=20000)
            await card.first.click()
            dialog = page.locator('#threepic-fin-details-dialog')
            await dialog.wait_for(state='visible')
            await page.get_by_text('Available in your library', exact=True).wait_for(timeout=15000)
            assert await page.locator('#threepic-fin-details-open').is_visible()
            assert not await page.locator('#threepic-fin-details-request').is_hidden()  # permission independent of local match
            await page.locator('#threepic-fin-details-open').click()
            await page.wait_for_url(re.compile(r'.*details\?id=.*'), timeout=15000)
            await page.goto(base+'/web/index.html#/home', wait_until='domcontentloaded')
            await page.locator('#homeTab .threepic-fin-host__tabs').wait_for(timeout=45000)
            await page.locator('.threepic-fin-host__tab').filter(has_text='3pic Fin').click()
            await page.get_by_role('button',name='Details for Hidden child').first.click()
            await page.get_by_text('Reported available · not in your library').wait_for(timeout=15000)
            assert await page.locator('#threepic-fin-details-open').is_hidden()
            await page.locator('#threepic-fin-details-close').click()
            await page.get_by_role('button',name='Details for Probe series').first.click()
            await page.get_by_text('Available in your library',exact=True).wait_for(timeout=15000)
            assert await page.locator('#threepic-fin-details-open').is_visible()
            assert await page.locator('#threepic-fin-details-request').is_visible()
            await page.locator('#threepic-fin-details-request').click()
            await page.locator('#threepic-fin-request-dialog').wait_for(state='visible')
            await page.locator('#threepic-fin-request-seasons label').first.wait_for(timeout=15000)
            assert await page.locator('#threepic-fin-request-seasons label').all_text_contents() == ['Season 1','Season 2']
            assert await page.locator('#threepic-fin-request-submit').is_enabled()
            print(json.dumps({'browser':'pass','movie_open_native':True,'hidden_no_open':True,'local_series_request_seasons':[1,2]}))
        finally: await browser.close()

if __name__ == '__main__': main()
