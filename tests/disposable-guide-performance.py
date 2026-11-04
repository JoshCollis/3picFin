"""Genuine 12.3 + FT activation on a loopback-only, disposable server. No production credentials/streams."""
import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import runpy
import secrets
import subprocess
import tempfile
import time
from urllib.parse import urlencode, urlsplit, parse_qs
from urllib.request import Request, urlopen
import zipfile
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
GUID = 'bd36ab75-0f4a-49b6-92ef-3a93da040c7a'
ASSET = '14553.6ddb0da23f2698557d33.chunk.js'
IMAGE = 'jellyfin/jellyfin:12.3@sha256:2038fe038ab3876a80f6e7b7e0aef6607b032c7dc0c9b3ad3296d0b2eba3bbb5'
FT_URL = 'https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/3.0.3.0/Release-12.3.0.zip'
FT_HASH = 'f872c2cf5cac1f645d74b38611d5c9533ca367747be66deadfe4835b02342e02'
NATIVE_HASH = 'a613661f2a2bd541c694184c769ab168d7678ee677efcdae5e0eabf8d3acfe0d'
recipe = runpy.run_path(str(ROOT/'tests/disposable-version-compat.py'), run_name='disposable_recipe')
call, ok, docker, seed = (recipe[k] for k in ('call', 'ok', 'docker', 'seed'))
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--channel-search', action='store_true')
parser.add_argument('--theme', type=Path)
parser.add_argument('--expected-version')
parser.add_argument('--amendments', action='store_true')
args = parser.parse_args()
os.umask(0o077)
args.output.mkdir(parents=True, exist_ok=True, mode=0o700)
receipts = {'scope': 'genuine disposable Jellyfin/FT/users; synthetic M3U/XMLTV, no playable streams', 'checks': []}


def wait(check, label, seconds=180):
    deadline = time.monotonic()+seconds
    while time.monotonic() < deadline:
        try:
            result = check()
            if result:
                return result
        except (OSError, ValueError):
            pass
        time.sleep(.5)
    raise AssertionError('Timeout: '+label)


def record(name, **data):
    receipts['checks'].append({'name': name, **data})
    (args.output/'hosted-receipt.json').write_text(json.dumps(receipts, indent=2))
    print(name, flush=True)


def asset(base):
    with urlopen(base+'/web/'+ASSET+'?lab='+secrets.token_hex(4), timeout=30) as response:
        return response.read()


with tempfile.TemporaryDirectory(prefix='fin-guide-hosted-', dir=os.environ['TMPDIR']) as scratch:
    lab = Path(scratch)
    plugins = lab/'config/plugins'; config = plugins/'configurations'
    config.mkdir(parents=True)
    (lab/'cache').mkdir()
    fixtures = lab/'fixtures'; fixtures.mkdir()
    if args.channel_search:
        movie = fixtures/'movies/Lab France Movie (2020)'; movie.mkdir(parents=True)
        (movie/'movie.nfo').write_text('<movie><title>Lab France Movie</title><year>2020</year></movie>')
        subprocess.run(['ffmpeg','-nostdin','-loglevel','error','-f','lavfi','-i','color=c=blue:s=160x90:r=1','-t','1','-c:v','mpeg4',str(movie/'Lab Movie.mp4')],check=True)
        series = fixtures/'tv/Lab France Series'; series.mkdir(parents=True)
        (series/'tvshow.nfo').write_text('<tvshow><title>Lab France Series</title></tvshow>')
        (series/'Lab Series S01E01.mp4').write_bytes((movie/'Lab Movie.mp4').read_bytes())
    dll = ROOT/'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
    plugin = plugins/'Fin'; plugin.mkdir(); (plugin/dll.name).write_bytes(dll.read_bytes())
    cached_ft=args.output.parent/'file-transformation-3.0.3.0-12.3.zip'
    if cached_ft.exists(): archive=cached_ft.read_bytes()
    else:
        with urlopen(Request(FT_URL, headers={'User-Agent':'FinDisposableLab/1.0'}), timeout=60) as response:
            archive = response.read()
    assert hashlib.sha256(archive).hexdigest() == FT_HASH
    if not cached_ft.exists(): cached_ft.write_bytes(archive)
    ft = plugins/'FileTransformation'; ft.mkdir()
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        for name in z.namelist():
            if '/' not in name and name.endswith(('.dll','.deps.json')):
                (ft/name).write_bytes(z.read(name))
    (config/'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><HomeEnabled>true</HomeEnabled><DiscoveryPageEnabled>true</DiscoveryPageEnabled><GlobalSearchEnabled>true</GlobalSearchEnabled></PluginConfiguration>')
    # Public code from the exact image, bind-mounted only to enable a controlled drift test.
    original = subprocess.check_output(['docker','run','--rm','--entrypoint','cat',IMAGE,'/jellyfin/jellyfin-web/'+ASSET])
    assert hashlib.sha256(original).hexdigest() == NATIVE_HASH
    guide_file = lab/'native-guide.js'; guide_file.write_bytes(original)
    start = datetime.now(timezone.utc).replace(minute=0,second=0,microsecond=0)-timedelta(hours=1)
    m3u=['#EXTM3U']; xml=['<?xml version="1.0"?><tv>']
    for i in range(1,1880):
        name = ('Lab French-tail ' if i>1827 else 'Lab station ')+str(i)
        if args.channel_search and i in (1852,1853): name = 'Lab France '+('2' if i==1852 else '24')
        m3u += [f'#EXTINF:-1 tvg-id="fin-lab-{i}" tvg-chno="{i}",{name}',f'http://127.0.0.1:9/never-play/{i}']
        xml.append(f'<channel id="fin-lab-{i}"><display-name>{name}</display-name></channel>')
        # Activation/auth is not a volume benchmark. Jellyfin parses this XMLTV
        # file per station; two full days keep the 1879-station gate finite.
        for day in range(2):
            a=start+timedelta(days=day); b=a+timedelta(days=1)
            programme_name=('Fixture' if args.channel_search else 'Lab')+f' {i} day {day}'
            xml.append(f'<programme channel="fin-lab-{i}" start="{a:%Y%m%d%H%M%S} +0000" stop="{b:%Y%m%d%H%M%S} +0000"><title>{programme_name}</title><category>{"Sports" if i%3==0 else "Series"}</category></programme>')
    (fixtures/'lineup.m3u').write_text('\n'.join(m3u)); (fixtures/'guide.xml').write_text('\n'.join(xml)+ '</tv>')
    cid = docker('run','-d','--label','3pic-fin=disposable-guide-performance','--user',f'{os.getuid()}:{os.getgid()}',
        '-p','127.0.0.1::8096','-v',f'{lab}/config:/config','-v',f'{lab}/cache:/cache',
        '-v',f'{fixtures}:/fixtures:ro','-v',f'{guide_file}:/jellyfin/jellyfin-web/{ASSET}:ro', IMAGE)
    try:
        base = 'http://127.0.0.1:'+docker('port',cid,'8096/tcp').rsplit(':',1)[1]
        wait(lambda: call(base,'/Startup/User')[0]==200,'startup')
        password=secrets.token_urlsafe(24)
        ok(call(base,'/Startup/User','POST',{'Name':'lab-admin','Password':password}))
        ok(call(base,'/Startup/Complete','POST',{}))
        login=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'lab-admin','Pw':password})); token=login['AccessToken']
        info=ok(call(base,'/System/Info/Public')); assert info['Version']=='12.3.0'
        active=ok(call(base,'/Plugins',token=token))
        assert any(p['Id'].replace('-','')==GUID.replace('-','') and p['Status']=='Active' for p in active)
        if args.expected_version:
            assert next(p['Version'] for p in active if p['Id'].replace('-','')==GUID.replace('-',''))==args.expected_version
        assert any(p['Name']=='File Transformation' and p['Status']=='Active' for p in active)
        assert any(p['Name']=='File Transformation' and p['Version']=='3.0.3.0' for p in active)
        receipts.update(serverVersion=info['Version'],image=IMAGE,dllSha256=hashlib.sha256(dll.read_bytes()).hexdigest(),fileTransformationArchiveSha256=FT_HASH)
        cfg=ok(call(base,f'/Plugins/{GUID}/Configuration',token=token))
        assert cfg['NativeGuidePagingEnabled'] is False
        assert hashlib.sha256(asset(base)).hexdigest()==NATIVE_HASH
        index=wait(lambda: (body if (body:=ok(call(base,'/web/index.html'))).count(b'data-threepic-fin-adapter')==1 else None),'existing automatic injection',40)
        assert index.count(b'data-threepic-fin-search src=')==1
        if args.amendments: assert b'data-threepic-fin-guide-module' not in index
        record('default-off + both plugins active + existing Home/Search injection retained',flag='NativeGuidePagingEnabled',savedValue=False,deliveredSha256=NATIVE_HASH)
        ok(call(base,'/LiveTv/TunerHosts','POST',{'Type':'m3u','Url':'/fixtures/lineup.m3u','FriendlyName':'Disposable non-playable fixture','EnableAllTuners':True},token))
        provider=ok(call(base,'/LiveTv/ListingProviders/Default',token=token))
        provider.update(Type='xmltv',Path='/fixtures/guide.xml',EnableAllTuners=True)
        ok(call(base,'/LiveTv/ListingProviders','POST',provider,token))
        tasks=ok(call(base,'/ScheduledTasks',token=token)); task=next(t for t in tasks if t['Key']=='RefreshGuide')
        def import_done():
            state=ok(call(base,'/ScheduledTasks/'+task['Id'],token=token))
            data=ok(call(base,'/LiveTv/Channels?'+urlencode({'UserId':login['User']['Id'],'Limit':3000,'AddCurrentProgram':'false'}),token=token))
            return data if state['State']=='Idle' and data['TotalRecordCount']==1879 else None
        # Only this isolated runtime's guide task; never production.
        if ok(call(base,'/ScheduledTasks/'+task['Id'],token=token))['State']=='Idle':
            ok(call(base,'/ScheduledTasks/Running/'+task['Id'],'POST',None,token))
        channels=wait(import_done,'1879 lab channels and guide import',300)
        record('lab metadata-only guide import',channels=channels['TotalRecordCount'])
        if args.channel_search:
            for name,kind,path in [('Movies','movies','/fixtures/movies'),('TV','tvshows','/fixtures/tv')]:
                ok(call(base,'/Library/VirtualFolders?'+urlencode({'name':name,'collectionType':kind,'refreshLibrary':'false'}),'POST',{},token))
                ok(call(base,'/Library/VirtualFolders/Paths','POST',{'Name':name,'Path':path},token))
            ok(call(base,'/Library/Refresh','POST',{},token))
            def media_ready():
                items=ok(call(base,'/Items?Recursive=true&IncludeItemTypes=Movie,Series&SearchTerm=Lab&Limit=10',token=token))['Items']
                return items if {'Movie','Series'}.issubset({i['Type'] for i in items}) else None
            media=wait(media_ready,'native Movie/Series metadata',90)
            favorite=next(i['Id'] for i in media if i['Type']=='Movie')
            ok(call(base,f'/UserFavoriteItems/{favorite}?UserId='+login['User']['Id'],'POST',None,token))
            record('native Movie/Series libraries + favourite available',kinds=sorted({i['Type'] for i in media}))
        other_password=secrets.token_urlsafe(24)
        other=ok(call(base,'/Users/New','POST',{'Name':'lab-restricted','Password':other_password},token))
        policy=other['Policy']; policy.update(IsAdministrator=False,EnableAllChannels=False,EnabledChannels=[c['Id'] for c in channels['Items'][:17]],BlockedChannels=[c['Id'] for c in channels['Items'][17:]],EnableLiveTvAccess=True)
        ok(call(base,'/Users/'+other['Id']+'/Policy','POST',policy,token))
        viewer=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'lab-restricted','Pw':other_password})); vt=viewer['AccessToken']
        own=ok(call(base,'/LiveTv/Channels?'+urlencode({'UserId':other['Id'],'Limit':100,'AddCurrentProgram':'false'}),token=vt))
        restricted_count=own['TotalRecordCount']
        record('native per-channel policy capability',requestedAllowedChannels=17,actualVisibleChannels=restricted_count)
        cross=call(base,'/LiveTv/Channels?UserId='+login['User']['Id'],token=vt)[0]; assert cross==403
        assert call(base,f'/Plugins/{GUID}/Configuration',token=vt)[0]==403
        assert call(base,'/LiveTv/Channels')[0]==401
        assert ok(call(base,'/Users/Me',token=vt))['Id']==other['Id']
        assert call(base,f'/Plugins/{GUID}/Configuration','POST',cfg,vt)[0]==403
        query={'UserId':other['Id'],'ChannelIds':','.join(c['Id'] for c in own['Items'][:17]), 'EnableImages':'false','EnableUserData':'false','EnableTotalRecordCount':'false'}
        programmes=ok(call(base,'/LiveTv/Programs?'+urlencode(query),token=vt))
        assert programmes['Items'] and {p['ChannelId'] for p in programmes['Items']}.issubset({c['Id'] for c in own['Items']})
        assert call(base,'/LiveTv/Programs?UserId='+login['User']['Id'],token=vt)[0]==403
        assert call(base,'/Rowan/Home/Mode')[0]==401
        assert call(base,'/Rowan/Home/Mode',token=vt)[0]==200
        ok(call(base,'/Auth/Keys?app=DisposableGuideProbe','POST',None,token))
        keys=ok(call(base,'/Auth/Keys',token=token))['Items']; key=next(k['AccessToken'] for k in keys if k['AppName']=='DisposableGuideProbe')
        assert call(base,'/Rowan/Home/Mode',token=key)[0]==403
        if restricted_count!=17:
            # These legacy channel-policy fields may apply to channel plugins,
            # not Live TV. Do not fabricate a 17-station server-side restriction.
            policy['EnableLiveTvAccess']=False
            ok(call(base,'/Users/'+other['Id']+'/Policy','POST',policy,token))
            viewer=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'lab-restricted','Pw':other_password}));vt=viewer['AccessToken']
            assert call(base,'/LiveTv/Channels',token=vt)[0]==403
            assert call(base,'/LiveTv/Programs',token=vt)[0]==403
            restricted_count=0
        record('actual two-user visibility + anonymous/cross-user/admin boundary',adminChannels=1879,restrictedChannels=restricted_count,anonymous=401,crossUser=cross,nonadminConfiguration=403,ownProgrammesBoundToRequestedChannels=True,existingModeAnonymous=401,existingModeUserlessKey=403)
        cfg['NativeGuidePagingEnabled']=True
        ok(call(base,f'/Plugins/{GUID}/Configuration','POST',cfg,token))
        assert ok(call(base,f'/Plugins/{GUID}/Configuration',token=token))['NativeGuidePagingEnabled'] is True
        transformed=wait(lambda: (body if (body:=asset(base))!=original else None),'FT guide callback activation',40)
        assert b'finGuideLast' in transformed and b'finCreateGuideController' in transformed
        (args.output/'hosted-guide-enabled.js').write_bytes(transformed)
        record('saved enable + actual transformed HTTP asset',flag='NativeGuidePagingEnabled',savedValue=True,sha256=hashlib.sha256(transformed).hexdigest(),bytes=len(transformed))
        if args.channel_search:
            assert b'finGuideSearchInput' in transformed
            assert ok(call(base,'/web/index.html')).count(b'data-threepic-fin-channel-search')==1
            assert call(base,'/3picFin/Web/channel-search.js')[0]==200
        if args.amendments:
            index=ok(call(base,'/web/index.html'))
            assert index.count(b'data-threepic-fin-guide-module')==1
            revision=hashlib.sha256(transformed).hexdigest()
            assert (ASSET+'?fin-guide='+revision).encode() in index
            assert ok(call(base,'/web/'+ASSET+'?fin-guide='+revision))==transformed
            hashes={}
            for name in ('channel-search.js','search-adapter.js','global-search-addon.css','home-adapter.js','home-tab-host.js'):
                delivered=ok(call(base,'/3picFin/Web/'+name))
                assert delivered==(ROOT/'src/Rowan.Jellyfin.Plugin/Web'/name).read_bytes()
                hashes[name]=hashlib.sha256(delivered).hexdigest()
            record('real web-root discovery injects exactly one content-versioned native guide module',revision=revision)
            record('current HTTP helper bodies match final source resources',sha256=hashes)
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True,executable_path=os.environ.get('FIN_GUIDE_CHROME'))
            try:
                if args.amendments:
                    cache_probe=runpy.run_path(str(ROOT/'tests/guide-cache-regression.py'))['run']
                    cache_probe(browser,base,info,login,seed,original,transformed,ASSET,args.output)
                    record('cached original executes RED; versioned native module executes GREEN without cache clearing',proof='http-cache-regression.json')
                    fin_probe=runpy.run_path(str(ROOT/'tests/fin-initial-load.browser.py'))['run']
                    fin_probe(browser,base,info,login,seed,args.output)
                    record('signed-in cold/warm and off-Home Fin clicks populate; delayed transient fragment retains intent',proof='fin-initial-load.json')
                for label,session,expected in [('admin',login,1879),('restricted',viewer,restricted_count)]:
                    page=browser.new_page(viewport={'width':1280,'height':850},service_workers='block')
                    # No API/asset interception. Only forbid accidental stream tests.
                    page.route('**/*',lambda route: route.abort() if any(part in route.request.url.lower() for part in ['/playbackinfo','/stream','never-play']) else route.continue_())
                    seed(page,base,info,session)
                    page.goto(base+'/web/index.html#/livetv?tab=1')
                    if expected:
                        page.wait_for_selector('.programCell:visible',timeout=60000)
                    else:
                        page.wait_for_function('!!window.ApiClient?.getCurrentUserId()',timeout=60000)
                        page.wait_for_timeout(1500)
                    assert page.evaluate('ApiClient.getCurrentUserId()')==session['User']['Id']
                    assert page.evaluate('ApiClient.getCurrentUser().then(u=>u.Id)')==session['User']['Id']
                    assert page.locator('.channelPrograms:visible').count()==min(50,expected)
                    if label=='admin':
                        page.locator('.finGuideLast').click()
                        page.wait_for_function('document.querySelector(".finGuidePage")?.value==="38" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                        assert page.locator('.channelPrograms:visible').count()==29
                        assert '1879' in page.locator('.finGuideRange:visible').inner_text()
                    else:
                        if args.channel_search:
                            assert page.locator('.programGrid').evaluate('(grid)=>grid.inert')
                        else:
                            assert page.locator('.guideOptions:visible').count()==0
                    page.screenshot(path=str(args.output/f'hosted-{label}.png'))
                    record('genuine signed-in native guide '+label,channels=expected,visibleRows=page.locator('.channelPrograms:visible').count())
                    page.close()
                if args.channel_search:
                    for theme_name,theme in [('stock',None),('captured-theme',args.theme)]:
                        if theme_name=='captured-theme' and not theme: continue
                        if theme:
                            ok(call(base,'/System/Configuration/Branding','POST',{'CustomCss':theme.read_text()},token))
                        for layout,width in [('desktop',1280),('desktop',390),('tv',1280)]:
                            page=browser.new_page(viewport={'width':width,'height':850},service_workers='block')
                            seed(page,base,info,login);page.add_init_script('localStorage.setItem("layout",'+json.dumps(layout)+')')
                            # Only Seerr/Home upstream DTOs are local fixtures. Native
                            # media, channels, assets and authorization remain genuine.
                            page.route('**/*',lambda r:r.abort() if any(x in r.request.url.lower() for x in ['/playbackinfo','/stream','never-play']) else r.continue_())
                            page.route('**/3picFin/Search?*',lambda r:r.fulfill(content_type='application/json',body=json.dumps({'Items':[{'TmdbId':9,'MediaType':'movie','Title':'Lab Seerr fixture'}],'TotalPages':1})))
                            page.route('**/3picFin/Discovery?*',lambda r:r.fulfill(content_type='application/json',body=json.dumps({'Movies':{'Items':[{'TmdbId':9,'MediaType':'movie','Title':'Lab Fin fixture'}]},'Tv':{'Items':[]},'Requests':{'Items':[]}})))
                            native_reads=[]
                            def observe(request):
                                if '/LiveTv/Channels?' in request.url:
                                    query={k.lower():v for k,v in parse_qs(urlsplit(request.url).query).items()}
                                    native_reads.append({'limit':int(query.get('limit',['0'])[0]),'start':int(query.get('startindex',['0'])[0])})
                            page.on('request',observe)
                            try:
                                page.goto(base+'/web/index.html#/search?query=Lab')
                                page.wait_for_selector('.finChannelResult:visible',timeout=45000)
                                assert page.locator('.finChannelResult:visible').count()==8
                                assert '1879' in page.locator('.threepic-fin-channels').inner_text()
                                # V2's existing native unified-search cap can fill
                                # with station/programme matches for a broad term.
                                # A bounded mixed query proves media coexistence.
                                page.locator('#searchTextInput').fill('Lab France');page.locator('#searchTextInput').press('Enter')
                                page.wait_for_function('document.querySelectorAll(".finChannelResult").length===2 && document.querySelector(".threepic-fin-channels")?.getAttribute("aria-busy")!=="true"')
                                page.wait_for_selector('#searchPage .card[data-type="Movie"]',timeout=30000)
                                page.wait_for_selector('#searchPage .card[data-type="Series"]',timeout=30000)
                                page.wait_for_selector('.threepic-fin-search .cardText',timeout=30000)
                                assert page.locator('.finChannelResult:visible').count()==2
                                assert page.locator('.finChannelResult').first.get_attribute('href').startswith('#/details?id=')
                                assert 'Channel details' in page.locator('.finChannelResult').first.inner_text()
                                page.screenshot(path=str(args.output/f'search-{theme_name}-{layout}-{width}.png'),full_page=True)
                                count=sum(r['limit']==500 for r in native_reads)
                                field=page.locator('#searchTextInput');field.fill('France2');field.press('Enter')
                                page.wait_for_function('document.querySelectorAll(".finChannelResult").length===2 && document.querySelector(".threepic-fin-channels")?.getAttribute("aria-busy")!=="true"')
                                assert sum(r['limit']==500 for r in native_reads)==count, 'Typing rebuilt the full channel index'
                                assert {'Lab France 2','Lab France 24'}==set(page.locator('.finChannelResult strong').all_text_contents())
                                chosen=page.locator('.finChannelResult strong').first.inner_text()
                                page.locator('.finChannelResult').first.click()
                                page.wait_for_function('location.hash.startsWith("#/details?")')
                                page.wait_for_selector('h1:visible',timeout=30000)
                                assert chosen in ' '.join(page.locator('h1:visible').all_text_contents())
                                page.screenshot(path=str(args.output/f'details-{theme_name}-{layout}-{width}.png'))
                                page.go_back();page.wait_for_selector('.finChannelGuide:visible',timeout=30000)
                                page.locator('.finChannelGuide').click()
                                page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false" && document.querySelectorAll(".channelPrograms").length===2')
                                assert page.locator('.finGuideSearchInput:visible').input_value()=='France2'
                                page.screenshot(path=str(args.output/f'guide-{theme_name}-{layout}-{width}.png'))
                                # Home/Fin/Favourites are actual native routes, with
                                # only the populated Fin upstream DTO fixture above.
                                page.evaluate('location.hash="#/home"')
                                page.wait_for_selector('#homeTab:visible > .sections',timeout=30000)
                                page.evaluate('location.hash="#/home?fin=1"')
                                page.wait_for_selector('.threepic-fin-host__panel:visible',timeout=30000)
                                page.wait_for_selector('.threepic-fin-host__panel .threepic-fin-discovery__title-button')
                                page.evaluate('location.hash="#/home?tab=1"')
                                page.wait_for_selector('#favoritesTab:visible',timeout=30000)
                                record('hosted search/guide + native media/Home/Fin/Favourites '+f'{theme_name}-{layout}-{width}',channelIndexReusedWhileTyping=True,metadataReadsBeforeTyping=count,seerrScope='local upstream DTO fixture only')
                            except Exception:
                                page.screenshot(path=str(args.output/f'failure-{theme_name}-{layout}-{width}.png'),full_page=True)
                                (args.output/'browser-failure.json').write_text(json.dumps({'url':page.url,'nativeChannelReads':native_reads,'body':page.locator('body').inner_text()[:4000]},indent=2))
                                raise
                            finally: page.close()
                    denied=browser.new_page(service_workers='block');seed(denied,base,info,viewer)
                    denied.goto(base+'/web/index.html#/search?query=Lab');denied.wait_for_selector('#searchTextInput')
                    denied.wait_for_timeout(1200);assert denied.locator('.finChannelResult:visible').count()==0;denied.close()
                    record('Live-TV-denied real session has no main Search channel results')
            finally: browser.close()
        cfg['NativeGuidePagingEnabled']=False;ok(call(base,f'/Plugins/{GUID}/Configuration','POST',cfg,token))
        assert ok(call(base,f'/Plugins/{GUID}/Configuration',token=token))['NativeGuidePagingEnabled'] is False
        assert hashlib.sha256(asset(base)).hexdigest()==NATIVE_HASH
        if args.amendments: assert b'data-threepic-fin-guide-module' not in ok(call(base,'/web/index.html'))
        record('disable readback + original delivered asset restored',flag='NativeGuidePagingEnabled',savedValue=False,deliveredSha256=NATIVE_HASH)
        cfg['NativeGuidePagingEnabled']=True;ok(call(base,f'/Plugins/{GUID}/Configuration','POST',cfg,token))
        guide_file.write_bytes(original.replace(b'var h=500;H=h',b'var h=501;H=h'))
        drift=asset(base); assert b'finCreateGuideController' not in drift and b'var h=501' in drift
        if args.amendments: assert b'data-threepic-fin-guide-module' not in ok(call(base,'/web/index.html'))
        guide_file.write_bytes(original)
        assert hashlib.sha256(asset(base)).hexdigest()==hashlib.sha256(transformed).hexdigest()
        record('drift fails closed + exact native restoration reapplies atomically')
        if args.channel_search:
            cfg['GlobalSearchEnabled']=False;ok(call(base,f'/Plugins/{GUID}/Configuration','POST',cfg,token))
            assert ok(call(base,f'/Plugins/{GUID}/Configuration',token=token))['GlobalSearchEnabled'] is False
            assert call(base,'/3picFin/Web/channel-search.js')[0]==404
            index=ok(call(base,'/web/index.html'));assert b'data-threepic-fin-channel-search' not in index and b'data-threepic-fin-adapter' in index
            record('existing independent GlobalSearch flag/readback gates channel asset; Home retained')
        receipts['result']='PASS'
    finally:
        (args.output/'server.log').write_text(docker('logs',cid))
        subprocess.run(['docker','rm','-f',cid],check=True,capture_output=True)
        receipts['containerRemoved']=True
        (args.output/'hosted-receipt.json').write_text(json.dumps(receipts,indent=2))
