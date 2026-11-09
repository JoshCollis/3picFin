"""Real 12.3 auth/FT/HDHR/controller/admin/browser proof. ONLY task-owned disposable containers.

Creates one synthetic tuner/EPG on a throwaway server, freezes its inventory, then
proves metadata reads never change it. Never contacts Gill or real providers.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
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
from urllib.request import Request, urlopen
import zipfile
from playwright.sync_api import sync_playwright

ROOT=Path(__file__).resolve().parents[1]
recipe=runpy.run_path(str(ROOT/'tests/disposable-version-compat.py'),run_name='source_recipe')
call,ok,docker,seed=(recipe[k] for k in ('call','ok','docker','seed'))
parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);parser.add_argument('--expected-version',default='1.0.2.7');args=parser.parse_args()
assert not args.output.resolve().is_relative_to(ROOT)
os.umask(0o077);args.output.mkdir(parents=True,exist_ok=True)
(args.output/'driver.pid').write_text(str(os.getpid())+'\n')
receipt={'scope':__doc__.splitlines()[0],'driverPid':os.getpid(),'checks':[]}
def wait(check,label,seconds=120):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            value=check()
            if value:return value
        except OSError:pass
        time.sleep(.25)
    raise AssertionError('timeout: '+label)
def require(condition,label):
    assert condition,label
    receipt['checks'].append(label);print('PASS:',label,flush=True)

with tempfile.TemporaryDirectory(prefix='fin-dispatcharr-',dir=os.environ['TMPDIR']) as directory:
    lab=Path(directory);config=lab/'config';plugins=config/'plugins';plugin=plugins/'Fin';plugin.mkdir(parents=True)
    (lab/'cache').mkdir();(plugins/'configurations').mkdir()
    dll=ROOT/'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll';(plugin/dll.name).write_bytes(dll.read_bytes())
    (plugins/'configurations/Rowan.Jellyfin.Plugin.xml').write_text('<PluginConfiguration><NativeGuidePagingEnabled>true</NativeGuidePagingEnabled></PluginConfiguration>')
    ft=plugins/'FileTransformation';ft.mkdir()
    archive=args.output.parent/'file-transformation-3.0.3.0-12.3.zip'
    if not archive.exists(): archive.write_bytes(urlopen('https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/3.0.3.0/Release-12.3.0.zip',timeout=60).read())
    require(hashlib.sha256(archive.read_bytes()).hexdigest()=='f872c2cf5cac1f645d74b38611d5c9533ca367747be66deadfe4835b02342e02','pinned File Transformation 3.0.3.0')
    with zipfile.ZipFile(io.BytesIO(archive.read_bytes())) as z:
        for name in z.namelist():
            if '/' not in name and name.endswith(('.dll','.deps.json')):(ft/name).write_bytes(z.read(name))
    network='fin-category-'+secrets.token_hex(5);docker('network','create',network);cid=None;stub=None
    try:
        stub=docker('run','-d','--network',network,'--network-alias','fixtures','-p','127.0.0.1::8080',
            '-v',str(ROOT/'tests/Fixtures/dispatcharr-metadata-stub.py')+':/stub.py:ro','python:3.11-alpine','python','/stub.py')
        fixture='http://127.0.0.1:'+docker('port',stub,'8080/tcp').rsplit(':',1)[1]
        wait(lambda:urlopen(fixture+'/test/state',timeout=2).status==200,'metadata stub')
        cid=docker('run','-d','--network',network,'--user',f'{os.getuid()}:{os.getgid()}','-p','127.0.0.1::8096',
            '-v',str(config)+':/config','-v',str(lab/'cache')+':/cache','jellyfin/jellyfin@sha256:2038fe038ab3876a80f6e7b7e0aef6607b032c7dc0c9b3ad3296d0b2eba3bbb5')
        base='http://127.0.0.1:'+docker('port',cid,'8096/tcp').rsplit(':',1)[1]
        wait(lambda:call(base,'/Startup/User')[0]==200,'startup')
        password=secrets.token_urlsafe(24);ok(call(base,'/Startup/User','POST',{'Name':'lab','Password':password}));ok(call(base,'/Startup/Complete','POST',{}))
        login=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'lab','Pw':password}));token=login['AccessToken']
        info=ok(call(base,'/System/Info/Public'));require(info['Version']=='12.3.0','Jellyfin 12.3.0')
        active=next(p for p in ok(call(base,'/Plugins',token=token)) if p['Id'].replace('-','')=='bd36ab750f4a49b692ef3a93da040c7a')
        require(active['Status']=='Active' and active['Version']==args.expected_version,'exact active candidate version')
        tuner=ok(call(base,'/LiveTv/TunerHosts','POST',{'Type':'hdhomerun','Url':'http://fixtures:8080','EnableAllTuners':True},token))
        provider=ok(call(base,'/LiveTv/ListingProviders/Default',token=token));provider.update(Type='xmltv',Path='http://fixtures:8080/guide.xml',EnableAllTuners=True)
        ok(call(base,'/LiveTv/ListingProviders','POST',provider,token))
        task=next(t for t in ok(call(base,'/ScheduledTasks',token=token)) if t['Key']=='RefreshGuide')
        if ok(call(base,'/ScheduledTasks/'+task['Id'],token=token))['State']=='Idle':ok(call(base,'/ScheduledTasks/Running/'+task['Id'],'POST',None,token))
        def imported():
            data=ok(call(base,'/LiveTv/Channels?Limit=10000&AddCurrentProgram=false',token=token))
            return data if data['TotalRecordCount']==594 and ok(call(base,'/ScheduledTasks/'+task['Id'],token=token))['State']=='Idle' else None
        data=wait(imported,'synthetic 594 HDHR import')
        def inventory():
            rows=ok(call(base,'/LiveTv/Channels?Limit=10000&AddCurrentProgram=false',token=token))['Items']
            return sorted((r['Id'],r['Name'],r.get('ChannelNumber')) for r in rows)
        baseline=inventory();require(len(baseline)==len({r[0] for r in baseline})==594,'freeze 594 unique native items')
        source='/3picFin/Guide/Dispatcharr';endpoint='/3picFin/Guide/Categories'
        view=ok(call(base,source,token=token));require(not view['Enabled'] and not view['HasApiKey'],'source independently default off')
        def save(enabled=True,key='',clear=False):
            current=ok(call(base,source,token=token))
            result=ok(call(base,source,'POST',{'Enabled':enabled,'BaseUrl':'http://fixtures:8080/','ApiKey':key,'ClearApiKey':clear,'Revision':current['Revision']},token))
            require('ApiKey' not in result,'secret never returned by source settings')
            return result
        save(key='disposable-metadata-key')
        require('disposable-metadata-key' not in json.dumps(ok(call(base,'/Plugins/bd36ab750f4a49b692ef3a93da040c7a/Configuration',token=token))),'ordinary configuration contains no source secret')
        groups=ok(call(base,endpoint,token=token));require(groups['Available'] and sorted(len(g['ChannelIds']) for g in groups['Items'])==[293,300],'HDHR UUID groups available through real authenticated controller')
        members=[i for g in groups['Items'] for i in g['ChannelIds']]
        require(len(members)==len(set(members))==593 and set(members)<{r[0] for r in baseline},'unique membership only existing IDs; ungrouped remains under All')
        require(ok(call(base,source+'/Test',token=token))['Ready'],'admin authenticated metadata test')
        save();require(ok(call(base,source,token=token))['HasApiKey'],'blank key preserves secret')
        # Genuine anonymous/userless/denied/non-admin protocols; never borrowed live sessions.
        require(call(base,endpoint)[0]==401 and call(base,source)[0]==401,'anonymous denied')
        ok(call(base,'/Auth/Keys?app=DisposableCategorySource','POST',None,token));key=next(k['AccessToken'] for k in ok(call(base,'/Auth/Keys',token=token))['Items'] if k['AppName']=='DisposableCategorySource')
        require(call(base,endpoint,token=key)[0]==403 and call(base,source,token=key)[0]==403,'userless key denied')
        other=ok(call(base,'/Users/New','POST',{'Name':'denied','Password':password},token));policy=other['Policy'];policy.update(IsAdministrator=False,EnableLiveTvAccess=False)
        ok(call(base,'/Users/'+other['Id']+'/Policy','POST',policy,token));denied=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'denied','Pw':password}))
        require(call(base,endpoint,token=denied['AccessToken'])[0]==403 and call(base,source,token=denied['AccessToken'])[0]==403,'LiveTV-denied and non-admin source settings denied')
        for name,group_name in [('alice','Synthetic A'),('bob','Synthetic B')]:
            allowed=next(g['ChannelIds'][0] for g in groups['Items'] if g['Name']==group_name)
            viewer=ok(call(base,'/Users/New','POST',{'Name':name,'Password':password},token));policy=viewer['Policy']
            policy.update(IsAdministrator=False,EnableLiveTvAccess=True,EnableAllChannels=False,EnabledChannels=[allowed])
            ok(call(base,'/Users/'+viewer['Id']+'/Policy','POST',policy,token))
            auth=ok(call(base,'/Users/AuthenticateByName','POST',{'Username':name,'Pw':password}))
            personal=ok(call(base,endpoint,token=auth['AccessToken']))
            native=ok(call(base,'/LiveTv/Channels?UserId='+viewer['Id']+'&Limit=10000&AddCurrentProgram=false',token=auth['AccessToken']))
            require(set(i for g in personal['Items'] for i in g['ChannelIds']) <= {r['Id'] for r in native['Items']},name+' hosted membership intersects native authorized catalogue')
            require(call(base,source,token=auth['AccessToken'])[0]==403,name+' non-admin management denied')
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True,executable_path='/usr/bin/google-chrome',env={**os.environ,'TMPDIR':os.environ['TMPDIR']})
            webkit=p.webkit.launch(headless=True,executable_path=os.environ['FIN_GUIDE_WEBKIT']) if os.environ.get('FIN_GUIDE_WEBKIT') else None
            try:
                layouts=[('desktop',1280,False,browser),('phone-touch',390,True,browser)]
                if webkit:layouts.append(('webkit-phone-touch',390,True,webkit))
                for label,width,touch,engine in layouts:
                    page=engine.new_page(viewport={'width':width,'height':850},has_touch=touch,service_workers='block');errors=[]
                    page.on('pageerror',lambda error:errors.append(str(error)))
                    page.route('**/*',lambda r:r.abort() if any(s in r.request.url.lower() for s in ['/playbackinfo','/proxy/ts/stream/','/videos/']) else r.continue_())
                    seed(page,base,info,login);page.goto(base+'/web/index.html#/livetv?tab=1');page.wait_for_selector('.programCell:visible',timeout=60000)
                    page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    select=page.locator('.finGuideCategory:visible');require(not select.is_disabled(),label+' generated native select enabled')
                    select.select_option(label='Synthetic A');page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="300" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    wanted=set(next(g['ChannelIds'] for g in groups['Items'] if g['Name']=='Synthetic A'))
                    visible=page.locator('.channelPrograms:visible').evaluate_all('(rows)=>rows.map(r=>r.dataset.channelid)')
                    require(0<len(visible)<=50 and len(visible)==len(set(visible)) and set(visible)<=wanted,label+' native results bounded unique and assigned')
                    field=page.locator('.finGuideSearchInput:visible');field.fill('Synthetic station 31');field.press('Enter')
                    page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="1" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    select.select_option(label='Synthetic B');page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="10" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    page.locator('.finGuideClear:visible').click();page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="293" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    select.select_option('');page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="594" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    ok(call(fixture,'/test/mode?value=unauthorized','POST',{}));save()
                    page.evaluate('Emby.Page.showSearch()');page.wait_for_selector('.programCell:visible',state='hidden')
                    page.evaluate('Emby.Page.showGuide()')
                    page.wait_for_function('document.querySelector(".finGuideCategoryState")?.textContent.includes("unavailable") && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    require(page.locator('.finGuideCategory:visible').is_disabled() and page.locator('.finGuideCategoryRetry:visible').count()==1,label+' visible unavailable feedback and retry; All remains available')
                    ok(call(fixture,'/test/mode?value=normal','POST',{}));save()
                    page.locator('.finGuideCategoryRetry:visible').click()
                    page.wait_for_function('!document.querySelector(".finGuideCategory").disabled && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    page.locator('.finGuideCategory:visible').select_option(label='Synthetic B')
                    page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="293" && document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
                    require(True,label+' real Retry categories restores functional membership')
                    page.screenshot(path=str(args.output/(label+'-native.png')))
                    require(not errors,label+' no browser errors');page.close()
                page=browser.new_page();seed(page,base,info,login)
                page.goto(base+'/web/index.html#/configurationpage?name=3pic%20Fin')
                page.get_by_text('Native Live TV guide',exact=True).click(timeout=45000)
                page.wait_for_selector('#DispatcharrSave:enabled',timeout=45000)
                require(page.locator('#DispatcharrApiKey').input_value()=='','real admin UI never renders saved key')
                page.locator('#DispatcharrSave').click();page.wait_for_function('document.querySelector("#DispatcharrStatus")?.textContent.includes("source saved")')
                require(page.locator('#DispatcharrApiKey').input_value()=='','real admin blank-key save/readback')
                page.close()
            finally:
                browser.close()
                if webkit:webkit.close()
        ok(call(fixture,'/test/mode?value=slow','POST',{}));save()
        with ThreadPoolExecutor(max_workers=1) as pool:
            pending=pool.submit(call,base,endpoint,token=token);time.sleep(.3);save(enabled=False)
            changed=ok(pending.result(timeout=15));require(not changed['Available'] and changed.get('Status')=='settings-changed','in-flight source setting change fails closed')
        for mode in ['duplicate','group-duplicate','missing','pagination','unauthorized','oversized','redirect','dead','empty']:
            ok(call(fixture,'/test/mode?value='+mode,'POST',{}));save()  # Revision invalidates only our plugin's metadata cache.
            result=ok(call(base,endpoint,token=token))
            require((result['Available'] and not result['Items']) if mode=='empty' else not result['Available'],'source '+mode+' fails closed without duplicate membership')
            require(inventory()==baseline,'inventory invariant after '+mode)
        save(enabled=False,clear=True);require(not ok(call(base,source,token=token))['HasApiKey'],'explicit clear after disabling')
        require(inventory()==baseline and len(ok(call(base,'/System/Configuration/livetv',token=token))['TunerHosts'])==1,'final exact inventory and single-tuner invariant')
        state=ok(call(fixture,'/test/state'));require(not any(r['path'] in ('STREAM-REQUEST-FORBIDDEN','REDIRECT-CREDENTIAL-TRAP') for r in state['reads']),'no stream or redirected credential requests')
        receipt.update(result='PASS',dllSha256=hashlib.sha256(dll.read_bytes()).hexdigest(),version=active['Version'],uniqueNativeIds=594,
            inventorySha256=hashlib.sha256(json.dumps(baseline).encode()).hexdigest(),metadataRequests=state['reads'])
    finally:
        if cid:
            # Private disposable log only; never live logs. Secrets are fixture-only but redact anyway.
            log=docker('logs',cid).replace('disposable-metadata-key','[REDACTED]')
            (args.output/'server.log').write_text(log);subprocess.run(['docker','rm','-f',cid],check=True,capture_output=True)
        if stub:subprocess.run(['docker','rm','-f',stub],check=True,capture_output=True)
        subprocess.run(['docker','network','rm',network],check=True,capture_output=True)
        receipt['disposableResourcesRemoved']=True;(args.output/'receipt.json').write_text(json.dumps(receipt,indent=2))
print('PASS: hosted Dispatcharr category source; no native inventory changes')
