"""Read-only diagnostic of the *served* 12.3 web guide with a frozen synthetic catalogue.

No personal credentials. All APIs and WebSockets are intercepted locally; only public
web assets are downloaded. This is renderer scaling evidence, NOT a real-user login
or live EPG integration test. Output/cache must be outside Git, under private storage.
"""
import argparse
import hashlib
import json
import os
import time
from pathlib import Path
from urllib.parse import urlsplit, parse_qs, unquote
from urllib.request import Request, urlopen
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--asset', type=Path, help='Locally transformed guide chunk; omit for native baseline')
parser.add_argument('--dataset', type=Path, help='Private, allowlisted, frozen read-only API capture')
parser.add_argument('--channels', type=Path, help='Frozen real channel metadata with explicitly synthetic programmes')
parser.add_argument('--assert-bounded', action='store_true')
parser.add_argument('--traversal', action='store_true')
parser.add_argument('--functional', action='store_true', help='Exercise native date/settings/focus/route controls with synthetic data')
parser.add_argument('--layout', choices=('desktop', 'tv'), default='desktop')
parser.add_argument('--empty-guide', action='store_true', help='Regression: no programme RPC for an empty channel page')
parser.add_argument('--cpu', type=int, default=4)
parser.add_argument('--width', type=int, default=1280)
parser.add_argument('--jump', action='store_true')
parser.add_argument('--rapid', action='store_true')
parser.add_argument('--search', action='store_true')
parser.add_argument('--categories', action='store_true', help='Focused assigned-group intersection and toolbar regression')
parser.add_argument('--toolbar-only', action='store_true', help='Capture toolbar geometry without the scaling benchmark')
parser.add_argument('--continuous', action='store_true', help='Verify bounded native row windowing, not legacy result pages')
parser.add_argument('--refinement', action='store_true', help='Mixed programme states and scoped live-screenshot geometry gates')
parser.add_argument('--now-offset-minutes', type=int, default=0, help='Exercise the current-time marker inside programme bodies, not just their boundary')
parser.add_argument('--theme', type=Path, help='Captured public/custom CSS; not a claim about every deployed plugin')
parser.add_argument('--route', default='/web/index.html#/livetv?tab=1')
parser.add_argument('--user-agent')
args = parser.parse_args()
if args.output.resolve().is_relative_to(Path(__file__).resolve().parents[1]):
    parser.error('Keep diagnostic output outside the repository')
os.umask(0o077)
args.output.mkdir(parents=True, exist_ok=True, mode=0o700)
cache = args.output.parent / 'public-web-cache'
cache.mkdir(exist_ok=True, mode=0o700)
ORIGIN = 'https://guide.fixture.invalid'
GUIDE = '14553.6ddb0da23f2698557d33.chunk.js'
USER = '11111111111111111111111111111111'
SECOND_USER = '44444444444444444444444444444444'
SERVER = '22222222222222222222222222222222'
START = '2026-11-02T18:00:00.000Z'
channels = [{'Id': f'{i:032x}', 'Name': f'Station {i:04d}', 'ChannelNumber': str(i),
             'Type': 'TvChannel', 'ServerId': SERVER, 'ImageTags': {'Primary': 'fixture'},
             'IsFolder': False, 'UserData': {'IsFavorite': i % 17 == 0}}
            for i in range(1, 1880)]
for channel in channels[-52:]:
    channel['Name'] = 'French-tail fixture ' + channel['ChannelNumber']
if args.continuous:
    channels[16]['Name'] = 'A long synthetic channel name that must remain readable beside its logo'
    for channel in channels[::11]: channel['ImageTags'] = {}
for offset,name in enumerate(['France 2','France 24','beIN SPORTS 1 FR','DR 1','Télé Sport','Sky Sports','Sky Sports']):
    channels[1851+offset]['Name']=name
dataset = json.loads(args.dataset.read_text()) if args.dataset else None
if dataset:
    channels = dataset['channels']
    START = dataset['frozenNow']
    for channel in channels:
        channel['ServerId'] = SERVER
        channel.setdefault('ImageTags', {})
if args.channels:
    assert not dataset
    channels=json.loads(args.channels.read_text())['channels']
    for channel in channels:
        channel['ServerId']=SERVER
        channel.setdefault('ImageTags',{})
if args.empty_guide:
    channels = []
requests = []
non_gets = []
errors = []
delay_channels = False


def phase(name):
    (args.output/'phase.txt').write_text(name)
    print('fixture: ' + name, flush=True)


def public_asset(path):
    path = unquote(path)
    if path == '/web/'+GUIDE and os.environ.get('FIN_GUIDE_NATIVE_ASSET'):
        return Path(os.environ['FIN_GUIDE_NATIVE_ASSET']).read_bytes()
    web_root = os.environ.get('FIN_GUIDE_WEB_ROOT')
    if web_root and path.startswith('/web/'):
        local = (Path(web_root) / path.removeprefix('/web/')).resolve()
        assert local.is_relative_to(Path(web_root).resolve())
        return local.read_bytes()
    filename = cache / hashlib.sha256(path.encode()).hexdigest()
    if not filename.exists():
        with urlopen(Request(os.environ['FIN_GUIDE_PUBLIC_ORIGIN'] + path, headers={'User-Agent': 'GuideFixture/1.0'}), timeout=40) as response:
            filename.write_bytes(response.read())
    return filename.read_bytes()


def fixture_programs(query):
    import datetime
    ids = query.get('channelids', [''])[0].split(',')
    if ids == ['']:
        # Native API semantics: an empty channel-ID restriction means ALL, not none.
        ids = [channel['Id'] for channel in channels]
    if dataset:
        selected = set(ids)
        return [{**program, 'ServerId': SERVER} for program in dataset['programmes'] if program['ChannelId'] in selected]
    start = datetime.datetime.fromisoformat(query['minenddate'][0].replace('Z', '+00:00'))
    result = []
    for channel in ids:
        if not channel:
            continue
        if args.refinement:
            # Deliberately irregular native EPG shapes: clipped ongoing entries,
            # missing EPG, different text heights, category classes and overlaps.
            if int(channel,16)==34: continue
            segments=[(-35,15), (15,120), (120,170), (170,240)]
            if int(channel,16)%4==0: segments=[(-90,60),(60,120),(120,240)]
            if int(channel,16)%5==0: segments=[(-15,45),(30,95),(95,240)]
            for index,(begin,end) in enumerate(segments):
                a=start+datetime.timedelta(minutes=begin);b=start+datetime.timedelta(minutes=end)
                result.append({'Id':f'p-{channel}-mixed-{index}','ChannelId':channel,'Name':f'Fixture programme {index+1}',
                    'Type':'Program','ServerId':SERVER,'StartDate':a.isoformat(),'EndDate':b.isoformat(),
                    'IsSports':int(channel,16)%3==0,'IsMovie':index==1,'IsKids':index==2,'IsNews':index==3,
                    'IsSeries':index==0,'IsLive':index==1,'IsPremiere':index==2,'IsHD':index==0,
                    'EpisodeTitle':'A synthetic episode' if int(channel,16)%4==0 else None,
                    'Overview':'A representative synopsis with enough text to exercise native sticky content.' if index%2 else None,
                    'TimerId':'fixture-timer' if index==2 else None})
            continue
        for hour in range(24):
            a = start + datetime.timedelta(hours=hour)
            b = a + datetime.timedelta(hours=1)
            result.append({'Id': f'p-{channel}-{hour}', 'ChannelId': channel, 'Name': f'Programme {hour:02d}',
                           'Type': 'Program', 'ServerId': SERVER, 'StartDate': a.isoformat(), 'EndDate': b.isoformat(),
                           'IsSeries': int(channel, 16) % 3 != 0, 'IsSports': int(channel, 16) % 3 == 0,
                            'EpisodeTitle': 'Frozen representative hourly programme',
                            'Overview': 'A synthetic synopsis describing this programme for the native guide fixture.',
                           'TimerId': 'fixture-timer' if hour == 4 else None})
    return result


def intercept(route):
    request = route.request
    url = urlsplit(request.url)
    path = unquote(url.path)
    with (args.output/'fixture-requests.log').open('a') as log:
        log.write(request.method + ' ' + path + '\n')
    if url.netloc != urlsplit(ORIGIN).netloc or path.startswith('/cdn-cgi/'):
        return route.fulfill(status=200, content_type='text/javascript', body='')
    query = {k.lower(): v for k, v in parse_qs(url.query).items()}
    if path.startswith('/web/'):
        if path.endswith('/' + GUIDE) and args.asset:
            body = args.asset.read_bytes()
        else:
            body = public_asset(url.path)
        # Remove other plugin injections in this disposable fixture, equally in A/B.
        if path.endswith('/index.html') or path == '/web/':
            import re
            text = body.decode()
            text = re.sub(r'<script\b[^>]*src="(?![^"<>]*\.bundle\.js|[^"<>]*serviceworker\.js)[^"<>]*"[^>]*>\s*</script>', '', text)
            text = re.sub(r'<link\b[^>]*href="\.\./[^"<>]*"[^>]*>', '', text)
            body = text.encode()
        content_type = 'text/javascript' if path.endswith('.js') else 'text/css' if path.endswith('.css') else 'text/html' if path.endswith('.html') or path == '/web/' else 'application/octet-stream'
        return route.fulfill(status=200, content_type=content_type, body=body)
    if '/Images/' in path:
        return route.fulfill(content_type='image/svg+xml', body=(Path(__file__).parent/'Fixtures/guide-channel-logo.svg').read_bytes())
    if path == '/LiveTv/Programs':
        if request.method == 'POST':
            query = {k.lower(): [str(v)] for k, v in request.post_data_json.items()}
        if 'minenddate' not in query:
            # The default Live TV route may first show bounded native programme
            # suggestions. They are not guide-window requests or perf samples.
            limit=min(int(query.get('limit',['12'])[0]),25)
            q={**query,'minenddate':[START],'channelids':[','.join(c['Id'] for c in channels[:limit])]}
            items=fixture_programs(q)[:limit]
            return route.fulfill(content_type='application/json',body=json.dumps({'Items':items,'TotalRecordCount':len(items)}))
        items = fixture_programs(query)
        data = {'Items': items, 'TotalRecordCount': len(items)}
        requests.append({'kind': 'programmes', 'channels': len(query.get('channelids', [''])[0].split(',')),
                         'items': len(items), 'bytes': len(json.dumps(data).encode()), 'method': request.method,
                         'minEnd': query['minenddate'][0], 'maxStart': query['maxstartdate'][0]})
        if not query.get('channelids', [''])[0]:
            requests[-1]['channels'] = len(channels)
            requests[-1]['restriction'] = 'unrestricted'
    elif request.method not in ('GET', 'HEAD'):
        non_gets.append(path)  # Locally acknowledge background capabilities/activity; NEVER forward writes.
        return route.fulfill(status=204)
    elif path == '/System/Info/Public':
        data = {'Id': SERVER, 'Version': '12.3.0', 'ServerName': 'Guide fixture', 'StartupWizardCompleted': True,
                'LocalAddress': ORIGIN, 'ProductName': 'Jellyfin Server'}
    elif path in ('/Users/' + USER, '/Users/' + SECOND_USER, '/Users/Me'):
        identity = SECOND_USER if path.endswith(SECOND_USER) else USER
        data = {'Id': identity, 'ServerId': SERVER, 'Name': 'Guide fixture', 'HasPassword': True,
                'Policy': {'IsAdministrator': False, 'EnableLiveTvAccess': True, 'EnableLiveTvManagement': True,
                           'EnableMediaPlayback': True, 'EnableContentDownloading': True, 'EnableAllFolders': True},
                'Configuration': {'EnableNextEpisodeAutoPlay': True, 'OrderedViews': [], 'MyMediaExcludes': [],
                                  'LatestItemsExcludes': [], 'GroupedFolders': [], 'SubtitleMode': 'Default'}}
    elif path.endswith('/Views') or path == '/UserViews':
        data = {'Items': [{'Id': '55555555555555555555555555555555', 'Name': 'Fixture movies',
                           'Type': 'CollectionFolder', 'CollectionType': 'movies', 'ServerId': SERVER},
                          {'Id': '33333333333333333333333333333333', 'Name': 'Live TV',
                           'Type': 'CollectionFolder', 'CollectionType': 'livetv', 'ServerId': SERVER}], 'TotalRecordCount': 2}
        if not args.functional:
            items = [] if path == '/UserViews' else data['Items'][1:]
            data = {'Items': items, 'TotalRecordCount': len(items)}
    elif path == '/LiveTv/GuideInfo':
        requests.append({'kind':'guideinfo'})
        data = dataset['guideInfo'] if dataset else {'StartDate': START, 'EndDate': '2026-11-09T23:59:00.000Z'}
    elif path == '/3picFin/Guide/Categories':
        groups = [('Fixture General', [c['Id'] for i,c in enumerate(channels) if i < 1000 or i == 1851]),
                  ('Fixture News', [c['Id'] for i,c in enumerate(channels) if i >= 1000 and i != 1851])]
        data = {'Available': True, 'Source': 'jellyfin-m3u-group-title-cache', 'Items': [
            {'Id': 'group-'+hashlib.sha256(name.encode()).hexdigest(), 'Name': name, 'ChannelIds': ids} for name,ids in groups]}
    elif path == '/LiveTv/Info':
        data = {'IsEnabled': True, 'EnabledUsers': [USER], 'Services': [], 'DvrOptions': []}
    elif path == '/Users/Public':
        data = []
    elif path == '/LiveTv/Channels':
        if delay_channels:
            time.sleep(.25)  # Local diagnostic response delay, never a live server request.
        start = int(query.get('startindex', ['0'])[0])
        limit = int(query.get('limit', ['500'])[0])
        visible = channels
        if query.get('userid') == [SECOND_USER]:
            visible = visible[:17]
        if query.get('issports', ['false'])[0].lower() == 'true':
            visible = [channel for channel in visible if int(channel['Id'], 16) % 3 == 0]
        if query.get('enablefavoritesorting', ['false'])[0].lower() == 'true':
            visible = sorted(visible, key=lambda channel: not channel.get('UserData', {}).get('IsFavorite', False))
        items = visible[start:start + limit]
        data = {'Items': items, 'TotalRecordCount': len(visible), 'StartIndex': start}
        requests.append({'kind': 'channels', 'start': start, 'limit': limit, 'items': len(items),
                         'favoriteSorting': query.get('enablefavoritesorting'), 'sports': query.get('issports')})
    elif path == '/Branding/Configuration':
        data = {}
    elif path.startswith('/DisplayPreferences/'):
        data = {'Id': 'fixture', 'CustomPrefs': {}, 'ScrollDirection': 'Horizontal'}
    elif path == '/Localization/Cultures':
        data = [{'Name': 'English', 'DisplayName': 'English', 'TwoLetterISOLanguageName': 'en', 'ThreeLetterISOLanguageName': 'eng'}]
    else:
        data = {'Items': [], 'TotalRecordCount': 0}
    route.fulfill(content_type='application/json', body=json.dumps(data))


with sync_playwright() as p:
    native_hash = hashlib.sha256(public_asset('/web/' + GUIDE)).hexdigest()
    assert native_hash == 'a613661f2a2bd541c694184c769ab168d7678ee677efcdae5e0eabf8d3acfe0d', 'Served web guide changed; review the new build before testing'
    phase('launch')
    browser = p.chromium.launch(headless=True, executable_path=os.environ.get('FIN_GUIDE_CHROME'), env={**os.environ, 'TMPDIR':os.environ['TMPDIR']})
    context = browser.new_context(viewport={'width': args.width, 'height': 850}, timezone_id='UTC', service_workers='block', **({'user_agent':args.user_agent} if args.user_agent else {}))
    context.route('**/*', intercept)
    # A locally open mock socket (never connect_to_server). Closing it would make
    # the native client's reconnect loop spin and corrupt the performance fixture.
    context.route_web_socket('**/*', lambda ws: None)
    context.add_init_script('localStorage.setItem("jellyfin_credentials", ' + json.dumps(json.dumps({'Servers': [{
        'Id': SERVER, 'ManualAddress': ORIGIN, 'LocalAddress': ORIGIN, 'LastConnectionMode': 2,
        'UserId': USER, 'AccessToken': 'synthetic-local-only-not-a-credential'}]})) + '); localStorage.setItem("layout", ' + json.dumps(args.layout) + ');')
    page = context.new_page()
    page.on('pageerror', lambda error: errors.append(str(error)))
    # Anchor wall time to the same UTC guide slot, but KEEP IT ADVANCING: native
    # animations/debounces use Date.now(). performance, RAF and timers stay native.
    page.add_init_script('''{ const NativeDate=Date, anchor=NativeDate.parse(''' + json.dumps(START) + ''')+''' + str(args.now_offset_minutes * 60000) + ''', realStart=NativeDate.now();
        const now=()=>anchor+NativeDate.now()-realStart;
        window.Date=new Proxy(NativeDate, {construct:(target,args,newTarget)=>Reflect.construct(target,args.length?args:[now()],newTarget),
            apply:()=>new NativeDate(now()).toString(), get:(target,key)=>key==='now'?now:Reflect.get(target,key)}); }''')
    cdp = context.new_cdp_session(page)
    cdp.send('Emulation.setCPUThrottlingRate', {'rate': args.cpu})
    cdp.send('Performance.enable')
    cdp.send('Debugger.enable')
    scripts=[]
    cdp.on('Debugger.scriptParsed',lambda event:scripts.append({'id':event['scriptId'],'url':event.get('url','')}))
    cdp.send('Profiler.enable')
    cdp.send('Profiler.start')
    page.add_init_script('''performance.setResourceTimingBufferSize(10000); window.guideResources=[];
        new PerformanceObserver(list=>{for(const e of list.getEntries()) { const path=new URL(e.name).pathname;
            if(path==='/LiveTv/Channels'||path==='/LiveTv/Programs') guideResources.push({path,startTime:e.startTime,responseEnd:e.responseEnd});
        }}).observe({type:'resource',buffered:true});
        window.guideLongTasks=[]; new PerformanceObserver(list=>guideLongTasks.push(...list.getEntries().map(e=>({start:e.startTime,duration:e.duration})))).observe({type:'longtask',buffered:true});''')
    try:
        phase('navigation')
        page.goto(ORIGIN + args.route, wait_until='domcontentloaded')
        if 'collectionType=livetv' in args.route:
            # Native remembers its view separately; retain the exact route query.
            page.get_by_text('Programs',exact=True).first.click(timeout=30000)
            page.get_by_text('Guide',exact=True).last.click(timeout=10000)
        if args.empty_guide:
            page.wait_for_selector('.guide-date-tab-button')
            page.wait_for_timeout(1500)
            assert any(r['kind'] == 'channels' for r in requests)
            assert not any(r['kind'] == 'programmes' for r in requests), 'Empty channel page issued a programme query'
            (args.output/'empty-guide.json').write_text(json.dumps({'requests': requests, 'errors': errors}, indent=2))
            assert not errors, errors
            raise SystemExit(0)
        phase('await-programmes')
        page.wait_for_selector('.programCell', timeout=120000)
        if 'collectionType=livetv' in args.route:
            # Also exercise a visible Guide at the report's query-only URL. The
            # native menu adds tab=1; this silent route replacement does not drive
            # a second app navigation or pretend that Programs is Guide.
            selected_route=page.url
            page.evaluate('(route)=>history.replaceState(null,"",route)',args.route)
            assert 'tab=' not in page.url
            assert page.locator('.finGuidePage:visible').count()==1
            assert page.locator('.channelPrograms:visible').first.get_attribute('data-channelid')==next(c['Id'] for c in channels if c.get('UserData',{}).get('IsFavorite'))
            page.screenshot(path=str(args.output/'query-only-guide.png'))
            # Subsequent settings close uses native navigation and therefore its
            # selected tab URL, not an artificial replacement of router state.
            page.evaluate('(route)=>history.replaceState(null,"",route)',selected_route)
        executed=[]
        for script in scripts:
            if GUIDE in script['url']:
                text=cdp.send('Debugger.getScriptSource',{'scriptId':script['id']})['scriptSource']
                executed.append({'url':script['url'],'sha256':hashlib.sha256(text.encode()).hexdigest(),'bytes':len(text.encode()),'hasControls':'finCreateGuideController' in text})
        (args.output/'executed-guide.json').write_text(json.dumps({'userAgent':page.evaluate('navigator.userAgent'),'entryRoute':args.route,'route':page.url,'nativeGuideSelectionThenSilentQueryOnlyReplacement':'collectionType=livetv' in args.route,'scripts':executed},indent=2))
        if args.theme:
            page.add_style_tag(content=args.theme.read_text())
        phase('await-paint')
        page.evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))')
        phase('measure')
        first = page.evaluate('''() => {
            const channel=guideResources.find(e=>e.path==='/LiveTv/Channels');
            const programme=guideResources.find(e=>e.path==='/LiveTv/Programs');
            const now=performance.now();
            return {nodes:document.querySelectorAll('*').length, rows:document.querySelectorAll('.channelPrograms').length,
                cells:document.querySelectorAll('.programCell').length, usableMs:now,
                channelRequestToUsableMs:now-channel.startTime, programmeResponseToUsableMs:now-programme.responseEnd,
                fixtureApiMs:programme.responseEnd-channel.startTime,
                longTasks:guideLongTasks.filter(e=>e.start>=channel.startTime)};
        }''')
        (args.output/'first.json').write_text(json.dumps(first, indent=2))
        (args.output/'first-usable.cpuprofile').write_text(json.dumps(cdp.send('Profiler.stop')['profile']))
        if args.continuous:
            from guide_continuous_checks import check
            check(page, args, channels, requests, errors, SECOND_USER)
            raise SystemExit(0)
        if args.toolbar_only or args.categories:
            page.evaluate('''() => {const mark=document.createElement('div');mark.textContent='FIXTURE — synthetic identity, catalogue and programmes; native web renderer';
                mark.style.cssText='position:fixed;bottom:0;left:0;z-index:100000;background:#111;color:white;font:12px sans-serif;padding:4px;pointer-events:none';document.body.appendChild(mark);}''')
            geometry=page.evaluate('''() => {const rect=el=>{const r=el.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height}};
                return {toolbar:rect(document.querySelector('.finGuideToolbar')),viewport:[innerWidth,innerHeight],
                    controls:[...document.querySelectorAll('.finGuideToolbar input,.finGuideToolbar button,.finGuideToolbar select')].map(el=>({class:el.className,...rect(el)})),
                    overflow:document.documentElement.scrollWidth>innerWidth+1};}''')
            geometry['source']='fixture: synthetic identity/catalogue/programmes + pinned native web renderer'
            (args.output/'toolbar-geometry.json').write_text(json.dumps(geometry,indent=2))
            page.screenshot(path=str(args.output/'toolbar.png'))
            if args.categories:
                assert first['rows']==50
                select=page.locator('.finGuideCategory:visible'); assert select.input_value()==''
                assert select.locator('option').all_text_contents()==['All categories','Fixture General','Fixture News']
                page.locator('.finGuideLast:visible').click()
                page.wait_for_function('document.querySelector(".finGuidePage")?.value==="38" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                select.select_option(label='Fixture General')
                page.wait_for_function('document.querySelector(".finGuidePage")?.value==="1" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                assert '1001' in page.locator('.finGuideRange:visible').inner_text()
                field=page.locator('.finGuideSearchInput:visible'); field.fill('France2'); field.press('Enter')
                page.wait_for_function('document.querySelectorAll(".channelPrograms").length===1 && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                assert page.locator('.channelPrograms:visible').get_attribute('data-channelid')==channels[1851]['Id']
                if args.layout=='tv':
                    field.focus()
                    for _ in range(10):
                        page.keyboard.press('ArrowDown')
                        if page.evaluate('document.activeElement?.classList.contains("programCell")'): break
                    assert page.evaluate('document.activeElement?.classList.contains("programCell")'), 'Remote cannot reach filtered programme'
                page.screenshot(path=str(args.output/'category-search.png'))
                select.select_option('')
                page.wait_for_function('document.querySelectorAll(".channelPrograms").length===2 && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                assert field.input_value()=='France2'
                field.fill('no-such-channel');field.press('Enter')
                page.wait_for_function('document.querySelectorAll(".channelPrograms").length===0 && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                count=sum(r['kind']=='programmes' for r in requests)
                select.select_option(label='Fixture News')
                page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                assert sum(r['kind']=='programmes' for r in requests)==count
                select.select_option('');page.locator('.finGuideClear:visible').click()
                page.wait_for_function('document.querySelectorAll(".channelPrograms").length===50 && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                assert all(r['channels']<=50 and r.get('restriction')!='unrestricted' for r in requests if r['kind']=='programmes')
                assert not geometry['overflow']
                assert all(c['height']>=44 and c['width']>=44 and c['x']>=0 and c['x']+c['width']<=args.width+1 for c in geometry['controls'])
                if args.width>=1280: assert geometry['toolbar']['height']<=70
                (args.output/'categories.json').write_text(json.dumps({'source':geometry['source'],'checks':'50 rows; assigned groups; full-catalogue intersection; page reset; All; no-match skips programmes; remote result focus if TV', 'requests':requests,'errors':errors},indent=2))
                assert not errors,errors
            if args.toolbar_only: raise SystemExit(0)
        before = {m['name']: m['value'] for m in cdp.send('Performance.getMetrics')['metrics']}
        cdp.send('Profiler.start')
        phase('scroll')
        scroll = page.evaluate('''async () => {
            const vertical=document.querySelector('.guideVerticalScroller'), grid=document.querySelector('.programGrid');
            const frames=[]; const start=performance.now(); let last=start, elapsed=0; guideLongTasks.length=0;
            const rowHeight=document.querySelector('.channelPrograms').getBoundingClientRect().height;
            const verticalDistance=Math.min(10*rowHeight,vertical.scrollHeight-vertical.clientHeight);
            do { await new Promise(resolve=>requestAnimationFrame(resolve)); const now=performance.now(); frames.push(now-last); last=now;
                elapsed=now-start; const fraction=Math.min(elapsed/4000,1);
                vertical.scrollTop=fraction*verticalDistance;
                grid.scrollLeft=fraction*(grid.scrollWidth-grid.clientWidth);
            } while(elapsed<4000);
            await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
            return {frames, longTasks:[...guideLongTasks], maxVertical:vertical.scrollHeight-vertical.clientHeight,
                maxHorizontal:grid.scrollWidth-grid.clientWidth, verticalDistance, elapsedMs:performance.now()-start}; }''')
        after = {m['name']: m['value'] for m in cdp.send('Performance.getMetrics')['metrics']}
        (args.output/'scroll.cpuprofile').write_text(json.dumps(cdp.send('Profiler.stop')['profile']))
        phase('assertions')
        if args.assert_bounded:
            assert first['rows'] <= 50, f"native page renders {first['rows']} rows; budget is 50"
            assert max(x['channels'] for x in requests if x['kind'] == 'programmes') <= 50
        functional = []
        if args.search:
            phase('channel-search')
            last_page=(len(channels)+49)//50
            field=page.locator('.finGuideSearchInput:visible')
            field.wait_for(timeout=2000)
            page.locator('.finGuideLast:visible').click()
            page.wait_for_function('(last)=>document.querySelector(".finGuidePage")?.value===String(last) && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"',arg=last_page)
            before_search=len(requests)
            field.fill('France2'); field.press('Enter')
            page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false" && document.querySelectorAll(".channelPrograms").length===2')
            expected_ids={c['Id'] for c in channels if 'france2' in c['Name'].replace(' ','').lower()}
            assert set(page.locator('.channelPrograms:visible').evaluate_all('(rows)=>rows.map(r=>r.dataset.channelid)'))==expected_ids
            assert page.locator('.finGuidePage:visible').input_value()=='1'
            metadata=[r for r in requests[before_search:] if r['kind']=='channels']
            assert len(metadata)==4 and all(r['limit']==500 for r in metadata)
            page.screenshot(path=str(args.output/'search-france.png'))
            if args.layout=='tv':
                field.focus()
                for _ in range(8):
                    page.keyboard.press('ArrowDown')
                    if page.evaluate('document.activeElement?.classList.contains("programCell")'): break
                assert page.evaluate('document.activeElement?.classList.contains("programCell")'), 'D-pad cannot reach a search result row'
            for restore in (False,True):
                page.locator('.btnGuideViewSettings:visible').click();page.wait_for_selector('.chkCategory')
                for kind in ('movies','kids','news'):
                    box=page.locator(f'.chkCategory[data-type={kind}]');box.locator('xpath=..').click();assert box.is_checked()==restore
                page.locator('.formDialog .btnCancel').click()
                page.wait_for_selector('.formDialog',state='hidden')
                wanted_rows=2 if restore else sum(int(value,16)%3==0 for value in expected_ids)
                page.wait_for_function('(count)=>document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false" && document.querySelectorAll(".channelPrograms").length===count',arg=wanted_rows)
                assert page.locator('.finGuidePage:visible').input_value()=='1'
                assert page.locator('.finGuideSearchInput:visible').input_value()=='France2'
            number=next(c['ChannelNumber'] for c in channels if c['Name']=='France 2')
            field.fill(number);field.press('Enter')
            page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false" && document.querySelectorAll(".channelPrograms").length===1')
            assert page.locator('.channelPrograms:visible').get_attribute('data-channelid')==next(c['Id'] for c in channels if c['Name']=='France 2')
            before_missing=len([r for r in requests if r['kind']=='programmes'])
            field.fill('no-such-station-9fc75c3'); field.press('Enter')
            page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false" && document.querySelectorAll(".channelPrograms").length===0')
            assert len([r for r in requests if r['kind']=='programmes'])==before_missing
            page.screenshot(path=str(args.output/'search-empty.png'))
            before_typing=len(requests)
            page.evaluate('''() => { window.typingFrames=[];window.typingActive=true;let last=performance.now();
                const tick=now=>{typingFrames.push(now-last);last=now;if(typingActive)requestAnimationFrame(tick);};requestAnimationFrame(tick); }''')
            field.fill(''); field.press_sequentially('Eurosport',delay=20)
            page.wait_for_timeout(500)
            page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
            assert not [r for r in requests[before_typing:] if r['kind']=='channels']
            assert len([r for r in requests[before_typing:] if r['kind']=='programmes'])<=1
            frames=page.evaluate('() => {typingActive=false;return typingFrames;}')
            (args.output/'search-metrics.json').write_text(json.dumps({'initialMetadataReads':len(metadata),'metadataRows':sum(r['items'] for r in metadata),
                'metadataLimit':500,'typingMetadataReads':len([r for r in requests[before_typing:] if r['kind']=='channels']),
                'typingProgrammeReads':len([r for r in requests[before_typing:] if r['kind']=='programmes']),
                'typingFrameCount':len(frames),'typingMaxFrameGapMs':max(frames),'noMatchProgrammeReads':0},indent=2))
            assert max(frames)<500, 'Typing stalled a frame beyond the bounded lab budget'
            page.locator('.finGuideClear:visible').click()
            page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false" && document.querySelectorAll(".channelPrograms").length===50')
            assert page.locator('.finGuidePage:visible').input_value()=='1'
            for selector in ['.finGuideSearchInput','.finGuideClear','.finGuidePage','.finGuideGo','.finGuideFirst','.finGuideLast','.btnPreviousPage','.btnNextPage']:
                box=page.locator(selector+':visible').bounding_box();assert box and box['x']>=0 and box['x']+box['width']<=args.width+1 and box['height']>=42,selector
            assert page.evaluate('document.documentElement.scrollWidth<=innerWidth+1'), 'Guide toolbar caused horizontal page overflow'
            functional.append('full-catalogue normalized France2 search from tail resets page; no-match skips programme RPC; rapid typing reuses bounded metadata; Clear restores full guide')
        if args.jump or args.rapid:
            phase('jump')
            footer=page.locator('.guideOptions:visible')
            last=(len(channels)+49)//50
            page.locator('.finGuidePage:visible').wait_for()
            if args.layout=='tv':
                footer.locator('.finGuideLast').focus(); page.keyboard.press('Enter')
            else:
                footer.locator('.finGuideLast').click()
            page.wait_for_function('(last)=>document.querySelector(".finGuidePage")?.value===String(last) && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"',arg=last)
            assert page.locator('.channelPrograms:visible').last.get_attribute('data-channelid')==channels[-1]['Id']
            if args.layout=='tv':
                assert page.evaluate('document.activeElement?.classList.contains("programCell")')
                for _ in range(6):
                    page.keyboard.press('ArrowUp')
                    if page.evaluate('!!document.activeElement?.closest(".finGuideToolbar")'):
                        break
                assert page.evaluate('!!document.activeElement?.closest(".finGuideToolbar")'), 'D-pad cannot reach guide toolbar'
            for selector in ['.finGuidePage','.finGuideGo','.finGuideFirst','.finGuideLast']:
                box=footer.locator(selector).bounding_box(); assert box and box['x']>=0 and box['x']+box['width']<=args.width+1
            page.screenshot(path=str(args.output/'jump-tail.png'))
            footer.locator('.finGuidePage').fill(str(last-1)); footer.locator('.finGuidePage').press('Enter')
            page.wait_for_function('(target)=>document.querySelector(".finGuidePage")?.value===String(target) && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"',arg=last-1)
            page.screenshot(path=str(args.output/'jump-near-tail.png'))
            footer.locator('.finGuidePage').fill('2'); footer.locator('.finGuidePage').press('Enter')
            page.wait_for_function('document.querySelector(".finGuidePage")?.value==="2" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
            footer.locator('.finGuideFirst').click()
            page.wait_for_function('document.querySelector(".finGuidePage")?.value==="1" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
            functional.append('renderer-generated First/Last + keyboard page jump reaches final catalogue range; controls fit viewport')
        if args.rapid:
            phase('rapid-intents')
            count=len(requests)
            assert page.evaluate('''() => {const input=document.querySelector('.finGuidePage'),form=document.querySelector('.finGuideJump');
                for(let page=2;page<=12;page++){input.value=String(page);form.requestSubmit();}
                return document.querySelector('.programGrid').inert; }''')
            page.wait_for_function('document.querySelector(".finGuidePage")?.value==="12" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
            burst=requests[count:]
            assert len([r for r in burst if r['kind']=='channels'])<=2, burst
            assert next(r for r in reversed(requests) if r['kind']=='channels')['start']==550
            assert page.evaluate('''() => {const rows=[...document.querySelectorAll('.channelPrograms')].map(r=>r.dataset.channelid);
                return [...document.querySelectorAll('.programCell')].every(c=>rows.includes(c.dataset.channelid) && (!c.dataset.id.startsWith('p-') || c.dataset.id.startsWith('p-'+c.dataset.channelid+'-'))); }''')
            page.evaluate('''() => {const tabs=document.querySelectorAll('.guide-date-tab-button');for(const i of [1,0,2])tabs[i].click();}''')
            page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
            expected=page.locator('.guide-date-tab-button').nth(2).get_attribute('data-date')
            wanted=page.evaluate('(date)=>new Date(Number(date)+1000).toISOString()',expected)
            page.wait_for_function('(date)=>new Date(document.querySelector(".programCell")?.dataset.startdate).toISOString()===date && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"',arg=wanted)
            assert next(r for r in reversed(requests) if r['kind']=='programmes')['minEnd']==wanted
            functional.append('rapid jump/date intents coalesce; latest row/programme bindings correct; pending actions inert')
            # Real native settings UI, starting at a tail page. Close/reopen the
            # dialog without waiting for the first refresh to finish, then check
            # that the newest filter/sort intent wins and its cursor is reset.
            page.locator('.finGuideLast:visible').click()
            page.wait_for_function('document.querySelector(".finGuidePage")?.value==="38" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
            settings_start=len(requests)
            for restore in (False,True):
                page.locator('.btnGuideViewSettings:visible').click()
                page.wait_for_selector('.chkCategory')
                for kind in ('movies','kids','news'):
                    box=page.locator(f'.chkCategory[data-type={kind}]')
                    box.locator('xpath=..').click()
                    assert box.is_checked()==restore
                favorite=page.locator('.chkFavoriteChannelsAtTop')
                favorite.locator('xpath=..').click()
                assert favorite.is_checked()==restore
                page.locator('.formDialog .btnCancel').click()
                page.wait_for_selector('.dialogContainer',state='hidden')
            page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false" && document.querySelector(".finGuidePage")?.value==="1" && Array.from(document.querySelectorAll(".channelPrograms")).some(r=>parseInt(r.dataset.channelid,16)%3!==0)')
            latest=next(r for r in reversed(requests) if r['kind']=='channels')
            assert latest['start']==0 and latest['favoriteSorting']==['true'] and latest['sports'] is None
            assert all(r.get('start',0)==0 for r in requests[settings_start:] if r['kind']=='channels')
            assert page.locator('.channelPrograms:visible').first.get_attribute('data-channelid')==channels[16]['Id']
            functional.append('rapid native filter/favourite toggles from tail reset to page 1 and restore All/favourite ordering')
        if args.functional:
            assert not dataset, 'Functional cases use explicit synthetic metadata rather than inventing live EPG coverage'
            phase('functional')
            assert page.evaluate('ApiClient.getCurrentUserId()') == USER
            assert page.locator('.guide-date-tab-button').count() == 8
            old_start = page.locator('.programCell').first.get_attribute('data-startdate')
            page.locator('.guide-date-tab-button').nth(1).click()
            page.wait_for_function('(old)=>document.querySelector(".programCell")?.dataset.startdate !== old', arg=old_start)
            assert requests[-1]['maxStart'] != '2026-11-03T17:59:59.000Z'
            page.locator('.guide-date-tab-button').first.click()
            page.wait_for_function('(old)=>document.querySelector(".programCell")?.dataset.startdate === old', arg=old_start)
            functional.append('native date tabs keep the full day/time navigation')
            page.locator('.btnGuideViewSettings').click()
            page.wait_for_selector('.chkCategory')
            for kind in ('movies', 'kids', 'news'):
                box = page.locator(f'.chkCategory[data-type={kind}]')
                box.locator('xpath=..').click()
                assert not box.is_checked()
            page.locator('.formDialog .btnCancel').click()
            page.wait_for_function('Array.from(document.querySelectorAll(".channelPrograms")).every(row=>parseInt(row.dataset.channelid,16)%3===0)')
            assert next(x for x in reversed(requests) if x['kind'] == 'channels')['sports'] == ['true']
            page.locator('.btnGuideViewSettings').click()
            page.wait_for_selector('.chkCategory')
            for kind in ('movies', 'kids', 'news'):
                box = page.locator(f'.chkCategory[data-type={kind}]')
                box.locator('xpath=..').click()
                assert box.is_checked()
            page.locator('.formDialog .btnCancel').click()
            page.wait_for_function('Array.from(document.querySelectorAll(".channelPrograms")).some(row=>parseInt(row.dataset.channelid,16)%3!==0)')
            functional.append('native programme-type filtering and restoring All')
            page.locator('.guideVerticalScroller').evaluate('(el)=>el.scrollTop=0')
            page.locator('.programGrid').evaluate('(el)=>el.scrollLeft=0')
            cell = page.locator('.programCell').first
            cell.focus()
            if args.layout == 'tv':
                channel = cell.get_attribute('data-channelid')
                page.keyboard.press('ArrowDown')
                page.wait_for_function('(id)=>document.activeElement?.dataset.channelid && document.activeElement.dataset.channelid !== id', arg=channel)
                page.keyboard.press('ArrowUp')
                page.wait_for_function('(id)=>document.activeElement?.dataset.channelid === id', arg=channel)
                functional.append('native TV D-pad row focus down/up')
            else:
                page.keyboard.press('Tab')
                assert page.evaluate('document.activeElement.classList.contains("programCell")')
                functional.append('native keyboard programme focus')
        if args.traversal:
            phase('catalogue-traversal')
            seen = set(page.locator('.channelPrograms').evaluate_all('(rows)=>rows.map(r=>r.dataset.channelid)'))
            for _ in range(100):
                if page.locator('.btnNextPage').is_disabled():
                    break
                old = page.locator('.channelPrograms').first.get_attribute('data-channelid')
                page.locator('.btnNextPage').click()
                page.wait_for_function('(id)=>document.querySelector(".channelPrograms")?.dataset.channelid !== id', arg=old)
                seen.update(page.locator('.channelPrograms').evaluate_all('(rows)=>rows.map(r=>r.dataset.channelid)'))
            assert len(seen) == len(channels), (len(seen), len(channels))
            assert page.locator('.channelPrograms').last.get_attribute('data-channelid') == channels[-1]['Id']
            assert page.locator('.btnNextPage').is_disabled()
            old = page.locator('.channelPrograms').first.get_attribute('data-channelid')
            page.locator('.btnPreviousPage').click()
            page.wait_for_function('(id)=>document.querySelector(".channelPrograms")?.dataset.channelid !== id', arg=old)
            if args.functional:
                page.locator('.btnGuideViewSettings').click()
                page.wait_for_selector('.chkCategory')
                for kind in ('movies', 'kids', 'news'):
                    page.locator(f'.chkCategory[data-type={kind}]').locator('xpath=..').click()
                with page.expect_response(lambda response: urlsplit(response.url).path == '/LiveTv/Channels') as response:
                    page.locator('.formDialog .btnCancel').click()
                assert response.value.json()['StartIndex'] == 0, 'Late-page filter retained an out-of-range native page offset'
                page.wait_for_selector('.programCell:visible')
                page.locator('.btnGuideViewSettings').click()
                page.wait_for_selector('.chkCategory')
                for kind in ('movies', 'kids', 'news'):
                    page.locator(f'.chkCategory[data-type={kind}]').locator('xpath=..').click()
                page.locator('.formDialog .btnCancel').click()
                page.wait_for_function('Array.from(document.querySelectorAll(".channelPrograms")).some(row=>parseInt(row.dataset.channelid,16)%3!==0)')
                functional.append('late-page filter resets cursor and All restores complete access')
        if args.rapid:
            page.evaluate('''() => {document.querySelector('.finGuidePage').value='2';document.querySelector('.finGuideJump').requestSubmit();Emby.Page.showSearch();}''')
            page.wait_for_selector('.programCell:visible',state='hidden')
            page.evaluate('Emby.Page.showGuide()')
            page.wait_for_selector('.programCell:visible')
            page.wait_for_function('Array.from(document.querySelectorAll(".tvguide")).filter(e=>e.getClientRects().length && getComputedStyle(e).visibility!=="hidden").every(e=>e.getAttribute("aria-busy")==="false")')
            assert page.locator('.finGuidePage:visible').input_value()=='1', 'Guide route reentry did not reset to first page'
            assert next(r for r in reversed(requests) if r['kind']=='channels')['start']==0
            functional.append('leave/reenter while a page is pending drops stale work and resets to page 1')
        result = {'scope': 'served-native-web + synthetic identity/API responses; not live-user/Wolf/AndroidTV proof',
                  'dataset': 'frozen allowlisted real API capture (not personal visibility)' if dataset else 'frozen real channel metadata + synthetic hourly programmes' if args.channels else 'synthetic hourly catalogue',
                  'datasetSha256': hashlib.sha256(args.dataset.read_bytes()).hexdigest() if args.dataset else None,
                  'catalogue': len(channels), 'programmesPerDay': len(dataset['programmes']) if dataset else len(channels)*24, 'viewport': [args.width,850], 'cpuRate': args.cpu,
                  'frozenNow': START, 'first': first, 'scroll': scroll,
                  'scrollCpuSeconds': {k: after[k]-before[k] for k in ('ScriptDuration','LayoutDuration','RecalcStyleDuration','TaskDuration')},
                  'requests': requests, 'locallyAcknowledgedNonGets': non_gets, 'errors': errors}
        result['functional'] = functional
        result['layout'] = args.layout
        result['browserVersion'] = browser.version
        result['nativeSourceSha256'] = native_hash
        result['executedGuideSha256'] = hashlib.sha256(args.asset.read_bytes()).hexdigest() if args.asset else native_hash
        if args.traversal:
            result['traversal'] = {'distinctChannels': len(seen), 'finalChannelReached': True, 'previousPageWorks': True}
        if args.functional:
            phase('native-lifecycle')
            (args.output/'controls.json').write_text(json.dumps({'functional': functional, 'traversal': result.get('traversal')}, indent=2))
            # These are synthetic identity transitions in an isolated context,
            # not validation of real-server authentication or channel policy.
            if not page.locator('.btnNextPage').is_disabled():
                prior_programmes = len([r for r in requests if r['kind'] == 'programmes'])
                delay_channels = True
                page.evaluate('''() => { setTimeout(()=>Emby.Page.showSearch(),25); document.querySelector('.btnNextPage').click(); }''')
                page.wait_for_timeout(2000)
                delay_channels = False
                continued = len([r for r in requests if r['kind'] == 'programmes']) > prior_programmes
                result['navigateAwayCancellationProbe'] = {
                    'followUpProgrammeQueryObservedDuringNavigation': continued,
                    'status': 'observational only; controller drops stale callbacks/pending work, but does not abort native transport'}
            page.evaluate('Emby.Page.showSearch()')
            page.wait_for_selector('.programCell:visible', state='hidden')
            if args.layout == 'tv':
                page.keyboard.press('Escape')
            else:
                page.evaluate('Emby.Page.showGuide()')
            page.wait_for_selector('.programCell:visible')
            functional.append('native navigate-away and guide return')
            if args.layout == 'tv':
                functional.append('native web-TV remote Back/Escape returns to guide')
            if args.layout == 'desktop':
                page.locator('button[aria-controls="app-user-menu"]').click()
                page.get_by_role('menuitem', name='Sign Out', exact=True).click()
                page.wait_for_function('!window.ApiClient?.getCurrentUserId()')
                page.wait_for_selector('.programCell:visible', state='hidden')
                functional.append('native logout hides guide')
                page.evaluate('(id)=>{ ApiClient.setAuthenticationInfo("synthetic-local-only-second-identity",id); Emby.Page.showGuide(); }', SECOND_USER)
                page.wait_for_selector('.programCell:visible')
                assert page.evaluate('ApiClient.getCurrentUserId()') == SECOND_USER
                page.wait_for_function('Array.from(document.querySelectorAll(".channelPrograms")).filter(el=>el.getClientRects().length && getComputedStyle(el).visibility!=="hidden").length===17')
                functional.append('synthetic account switch reloads second identity channel set (17)')
            else:
                result['identityLifecycle'] = 'Desktop synthetic account flow tested separately; TV account-menu flow not exercised'
        if args.assert_bounded:
            assert all(r['channels'] <= 50 and r.get('restriction') != 'unrestricted' for r in requests if r['kind'] == 'programmes')
        (args.output/'benchmark.json').write_text(json.dumps(result, indent=2))
        page.screenshot(path=str(args.output/'guide.png'))
        assert not errors, errors
        print(json.dumps({'rows': first['rows'], 'cells':first['cells'], 'nodes':first['nodes'], 'usableMs':first['usableMs'], 'errors':len(errors)}))
    except Exception:
        (args.output/'failure.json').write_text(json.dumps({'errors':errors, 'requests':requests,
            'location':page.url, 'body':page.locator('body').inner_text()[:2000]},indent=2))
        page.screenshot(path=str(args.output/'failure.png'))
        raise
    finally:
        context.close()
        browser.close()
