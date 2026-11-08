"""Focused signed-in guide/category proof on a throwaway 12.3 runtime; never production or streams."""
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
from urllib.parse import parse_qs, urlsplit
from urllib.request import urlopen
import zipfile
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
recipe = runpy.run_path(str(ROOT/'tests/disposable-version-compat.py'), run_name='category_recipe')
call, ok, docker, seed = (recipe[k] for k in ('call','ok','docker','seed'))
IMAGE = 'jellyfin/jellyfin@sha256:2038fe038ab3876a80f6e7b7e0aef6607b032c7dc0c9b3ad3296d0b2eba3bbb5'
FT_URL = 'https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/3.0.3.0/Release-12.3.0.zip'
FT_HASH = 'f872c2cf5cac1f645d74b38611d5c9533ca367747be66deadfe4835b02342e02'
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--expected-version', help='Verify the exact staged plugin assembly version')
args = parser.parse_args()
assert not args.output.resolve().is_relative_to(ROOT)
os.umask(0o077)
args.output.mkdir(parents=True,exist_ok=True)
receipt = {'source':'disposable signed-in 12.3; real auth/native APIs/FT; synthetic M3U/XMLTV; no playable streams','checks':[]}


def wait(check, label, seconds=120):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            value=check()
            if value: return value
        except OSError: pass
        time.sleep(.5)
    raise AssertionError('timeout: '+label)


with tempfile.TemporaryDirectory(prefix='fin-categories-',dir=os.environ['TMPDIR']) as scratch:
    lab=Path(scratch); plugins=lab/'config/plugins'; config=plugins/'configurations'
    config.mkdir(parents=True); (lab/'cache').mkdir(); fixtures=lab/'fixtures'; fixtures.mkdir()
    dll=ROOT/'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
    fin=plugins/'Fin'; fin.mkdir(); (fin/dll.name).write_bytes(dll.read_bytes())
    ft=plugins/'FileTransformation'; ft.mkdir()
    archive_path=args.output.parent/'file-transformation-3.0.3.0-12.3.zip'
    if not archive_path.exists(): archive_path.write_bytes(urlopen(FT_URL,timeout=60).read())
    archive=archive_path.read_bytes(); assert hashlib.sha256(archive).hexdigest()==FT_HASH
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        for name in z.namelist():
            if '/' not in name and name.endswith(('.dll','.deps.json')): (ft/name).write_bytes(z.read(name))
    (config/'Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><NativeGuidePagingEnabled>true</NativeGuidePagingEnabled></PluginConfiguration>')
    start=datetime.now(timezone.utc).replace(minute=0,second=0,microsecond=0)-timedelta(hours=1)
    m3u=['#EXTM3U']; xml=['<?xml version="1.0"?><tv>']
    for i in range(1,129):
        name='Lab station '+str(i)
        if i in (120,121): name='Lab France '+('2' if i==120 else '24')
        group='UK General' if i<100 or i==120 else 'UK News'
        m3u += [f'#EXTINF:-1 tvg-id="lab-{i}" tvg-chno="{i}" group-title="{group}",{name}',f'http://127.0.0.1:9/never-play/{i}']
        xml.append(f'<channel id="lab-{i}"><display-name>{name}</display-name></channel>')
        end=start+timedelta(days=2)
        xml.append(f'<programme channel="lab-{i}" start="{start:%Y%m%d%H%M%S} +0000" stop="{end:%Y%m%d%H%M%S} +0000"><title>Lab programme {i}</title><category>Unrelated genre</category></programme>')
    (fixtures/'lineup.m3u').write_text('\n'.join(m3u));(fixtures/'guide.xml').write_text('\n'.join(xml)+'</tv>')
    cid=docker('run','-d','--label','3pic-fin=disposable-guide-categories','--user',f'{os.getuid()}:{os.getgid()}',
        '-p','127.0.0.1::8096','-v',f'{lab}/config:/config','-v',f'{lab}/cache:/cache','-v',f'{fixtures}:/fixtures:ro',IMAGE)
    try:
        base='http://127.0.0.1:'+docker('port',cid,'8096/tcp').rsplit(':',1)[1]
        wait(lambda:call(base,'/Startup/User')[0]==200,'startup')
        password=secrets.token_urlsafe(24)
        ok(call(base,'/Startup/User','POST',{'Name':'lab','Password':password}));ok(call(base,'/Startup/Complete','POST',{}))
        login=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'lab','Pw':password})); token=login['AccessToken']
        info=ok(call(base,'/System/Info/Public')); assert info['Version']=='12.3.0'
        active=next(plugin for plugin in ok(call(base,'/Plugins',token=token)) if plugin['Id'].replace('-','')=='bd36ab750f4a49b692ef3a93da040c7a')
        assert active['Status']=='Active'
        if args.expected_version: assert active['Version']==args.expected_version
        receipt['pluginVersion']=active['Version']
        tuner=ok(call(base,'/LiveTv/TunerHosts','POST',{'Type':'m3u','Url':'/fixtures/lineup.m3u','EnableAllTuners':True},token))
        provider=ok(call(base,'/LiveTv/ListingProviders/Default',token=token));provider.update(Type='xmltv',Path='/fixtures/guide.xml',EnableAllTuners=True)
        ok(call(base,'/LiveTv/ListingProviders','POST',provider,token))
        task=next(t for t in ok(call(base,'/ScheduledTasks',token=token)) if t['Key']=='RefreshGuide')
        if ok(call(base,'/ScheduledTasks/'+task['Id'],token=token))['State']=='Idle': ok(call(base,'/ScheduledTasks/Running/'+task['Id'],'POST',None,token))
        def imported():
            data=ok(call(base,'/LiveTv/Channels?Limit=500&Fields=Tags&AddCurrentProgram=false',token=token))
            return data if data['TotalRecordCount']==128 and ok(call(base,'/ScheduledTasks/'+task['Id'],token=token))['State']=='Idle' else None
        channels=wait(imported,'synthetic import')
        endpoint='/3picFin/Guide/Categories'
        groups=ok(call(base,endpoint,token=token)); assert groups['Available']
        assert [(g['Name'],len(g['ChannelIds'])) for g in groups['Items']]==[('UK General',100),('UK News',28)]
        assert {i for g in groups['Items'] for i in g['ChannelIds']}=={c['Id'] for c in channels['Items']}
        assert not any(c.get('Tags') for c in channels['Items']), 'Unexpected native group-to-Tags import'
        cache=lab/'cache'/(tuner['Id']+'_channels'); raw=cache.read_bytes()
        cache.write_bytes(raw+b'x'*3057)  # disposable-only reproduction of native non-truncating OpenWrite
        assert ok(call(base,endpoint,token=token))==groups
        assert call(base,endpoint)[0]==401
        denied_password=secrets.token_urlsafe(24)
        other=ok(call(base,'/Users/New','POST',{'Name':'denied','Password':denied_password},token))
        policy=other['Policy'];policy.update(IsAdministrator=False,EnableLiveTvAccess=False)
        ok(call(base,'/Users/'+other['Id']+'/Policy','POST',policy,token))
        # A real userless key is authenticated but has no acceptable user claim.
        ok(call(base,'/Auth/Keys?app=CategoryLab','POST',None,token))
        key=next(k['AccessToken'] for k in ok(call(base,'/Auth/Keys',token=token))['Items'] if k['AppName']=='CategoryLab')
        assert call(base,endpoint,token=key)[0]==403
        denied=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'denied','Pw':denied_password}))
        assert call(base,endpoint,token=denied['AccessToken'])[0]==403
        receipt.update(dllSha256=hashlib.sha256(dll.read_bytes()).hexdigest(),nativeTags=[],groups=[{'name':g['Name'],'count':len(g['ChannelIds'])} for g in groups['Items']],trailingRemnantBytes=3057,anonymous=401,userless=403,liveTvDenied=403)
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True,executable_path=os.environ.get('FIN_GUIDE_CHROME'),env={**os.environ,'TMPDIR':os.environ['TMPDIR']})
            try:
                for label,width,layout in [('desktop',1280,'desktop'),('mobile',390,'desktop'),('tv',1280,'tv')]:
                    page=browser.new_page(viewport={'width':width,'height':850},service_workers='block');errors=[];programmes=[]
                    page.on('pageerror',lambda error:errors.append(str(error)))
                    def observe(request):
                        if urlsplit(request.url).path=='/LiveTv/Programs':
                            q=request.post_data_json if request.method=='POST' else {k:v[0] for k,v in parse_qs(urlsplit(request.url).query).items()}
                            q={k.lower():v for k,v in q.items()}
                            if 'minenddate' in q: programmes.append(str(q.get('channelids','')).split(','))
                    page.on('request',observe)
                    page.route('**/*',lambda r:r.abort() if any(s in r.request.url.lower() for s in ['/playbackinfo','/stream','never-play']) else r.continue_())
                    seed(page,base,info,login);page.add_init_script('localStorage.setItem("layout",'+json.dumps(layout)+')')
                    page.goto(base+'/web/index.html#/livetv?tab=1');page.wait_for_selector('.programCell:visible',timeout=45000)
                    page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    window=page.locator('.channelPrograms:visible').count();assert 0<window<=50
                    assert page.locator('.btnNextPage,.btnPreviousPage,.finGuidePage,.finGuideFirst,.finGuideLast').count()==0
                    select=page.locator('.finGuideCategory:visible'); assert select.input_value()==''
                    page.evaluate('''() => {const mark=document.createElement('div');mark.textContent='DISPOSABLE SIGNED-IN — real Jellyfin auth/APIs; synthetic lineup and EPG';
                        mark.style.cssText='position:fixed;bottom:0;left:0;z-index:100000;background:#111;color:white;font:12px sans-serif;padding:4px;pointer-events:none';document.body.appendChild(mark);}''')
                    geometry=page.locator('.finGuideToolbar:visible').bounding_box()
                    page.screenshot(path=str(args.output/(label+'-toolbar.png')))
                    seen=set()
                    for index in list(range(0,128,window))+[127]:
                        page.locator('.guideVerticalScroller:visible').evaluate('''(el,index)=>{const h=el.querySelector('.channelPrograms').getBoundingClientRect().height;
                            if(el.getScrollEventName()==='scrollanimate')el.scrollToPosition(index*h,true);else el.scrollTop=index*h;el.dispatchEvent(new Event('scroll'));}''',index)
                        snapshot=page.wait_for_function('''index=>{const el=document.querySelector('.tvguide'),start=Number(el?.dataset.finGuideStart),rows=Array.from(el?.querySelectorAll('.channelPrograms')||[]);return el?.getAttribute('aria-busy')==='false'&&index>=start&&index<start+rows.length?rows.map(r=>r.dataset.channelid):false;}''',arg=index).json_value()
                        seen.update(snapshot)
                    assert seen=={c['Id'] for c in channels['Items']},len(seen)
                    select.select_option(label='UK General');page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideStart==="0" && document.querySelector(".tvguide")?.dataset.finGuideTotal==="100" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    field=page.locator('.finGuideSearchInput:visible');field.fill('France2');field.press('Enter')
                    page.wait_for_function('document.querySelectorAll(".channelPrograms").length===1 && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    expected=next(c['Id'] for c in channels['Items'] if c['Name']=='Lab France 2')
                    assert page.locator('.channelPrograms').get_attribute('data-channelid')==expected
                    page.screenshot(path=str(args.output/(label+'-intersection.png')))
                    select.select_option('');page.wait_for_function('document.querySelectorAll(".channelPrograms").length===2 && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    page.locator('.finGuideClear:visible').click();page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="128" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    assert all(0<len(ids)<=50 and all(ids) for ids in programmes)
                    assert not errors,errors
                    receipt['checks'].append({'layout':label,'distinctChannels':len(seen),'pagerAbsent':True,'windowRows':window,'toolbar':geometry,'programmeScopes':list(map(len,programmes)),'errors':errors})
                    page.close()
            finally: browser.close()
        receipt['result']='PASS'
    finally:
        (args.output/'server.log').write_text(docker('logs',cid))
        subprocess.run(['docker','rm','-f',cid],check=True,capture_output=True)
        receipt['containerRemoved']=True
        (args.output/'receipt.json').write_text(json.dumps(receipt,indent=2))
print('PASS: disposable categories/auth + desktop/mobile/web-TV')
