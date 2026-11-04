"""Bounded signed-in cold/warm click probes, only on the disposable server."""
import json
import hashlib
import time
from urllib.parse import urlsplit


def run(browser, base, info, login, seed, output):
    receipts=[]
    for label, initial, transient in [('cold-home', 'home', False), ('cold-off-home-delayed', 'search?query=Lab', True)]:
        context=browser.new_context(service_workers='block',viewport={'width':1440,'height':950})
        try:
            page=context.new_page();seed(page,base,info,login)
            errors=[];console=[];responses=[];counts={'mode':0,'fragment':0}
            page.on('pageerror',lambda error:errors.append(str(error)))
            # Retain console classifications/digests, never API tokens or raw
            # credential-bearing diagnostics from the native client.
            page.on('console',lambda message:console.append({'type':message.type,
                'category':'resource-load-failure' if 'Failed to load resource' in message.text else 'native-console',
                'messageSha256':hashlib.sha256(message.text.encode()).hexdigest()}) if message.type in ('warning','error') else None)
            page.on('response',lambda response:responses.append({'path':urlsplit(response.url).path,'status':response.status}) if any(x in response.url for x in ('/Rowan/Home/Mode','/3picFin/Web/','/3picFin/Discovery')) else None)
            def intercept(route):
                path=urlsplit(route.request.url).path
                if any(x in path.lower() for x in ('/playbackinfo','/stream','never-play')):return route.abort()
                if transient and ('/hometab.' in path or path.endswith('/global-fin-nav.js')):time.sleep(.25)
                if path.endswith('/Rowan/Home/Mode'):
                    counts['mode']+=1
                    if transient:time.sleep(.15)
                if path.endswith('/3picFin/Web/discovery.html'):
                    counts['fragment']+=1
                    if transient and counts['fragment']==1:
                        return route.fulfill(status=503,body='Isolated transient lab failure')
                if path.endswith('/3picFin/Discovery'):
                    return route.fulfill(content_type='application/json',body=json.dumps({'Movies':{'Items':[{'TmdbId':9,'MediaType':'movie','Title':'Populated Fin click fixture'}]},'Tv':{'Items':[]},'Requests':{'Items':[]}}))
                route.continue_()
            context.route('**/*',intercept)
            try:
                page.goto(base+'/web/index.html#/'+initial)
                page.wait_for_selector('.threepic-fin-global-nav:visible',timeout=30000)
                page.locator('.threepic-fin-global-nav:visible').first.click()
                page.wait_for_function('location.hash==="#/home?fin=1"')
                page.wait_for_selector('.threepic-fin-host__panel:visible .threepic-fin-discovery__title-button',timeout=30000)
                assert 'Populated Fin click fixture' in page.locator('.threepic-fin-host__panel:visible').inner_text()
                assert page.evaluate('location.hash')=='#/home?fin=1'
                # Warm document and native off-Home route replacement, both using
                # genuine clickable Fin navigation rather than setting the hash.
                page.reload();page.wait_for_selector('.threepic-fin-host__panel:visible .threepic-fin-discovery__title-button',timeout=30000)
                page.evaluate('location.hash="#/search?query=Lab"')
                page.wait_for_selector('#searchPage:visible',timeout=30000)
                page.locator('.threepic-fin-global-nav:visible').first.click()
                page.wait_for_selector('.threepic-fin-host__panel:visible .threepic-fin-discovery__title-button',timeout=30000)
                assert page.evaluate('location.hash')=='#/home?fin=1'
                page.screenshot(path=str(output/('fin-click-'+label+'.png')))
                assert not errors,errors
                if transient:assert counts['fragment']>=2
                receipts.append({'case':label,'populated':True,'finIntentPreserved':True,'warmReloadAndOffHomeClick':True,'counts':counts,'responses':responses,'console':console,'errors':errors})
            except Exception:
                page.screenshot(path=str(output/('fin-failure-'+label+'.png')))
                (output/'fin-failure.json').write_text(json.dumps({'case':label,'hash':page.evaluate('location.hash'),'counts':counts,'responses':responses,'errors':errors,'body':page.locator('body').inner_text()[:3000]},indent=2))
                raise
        finally:context.close()
    (output/'fin-initial-load.json').write_text(json.dumps({'scope':'real signed-in native app/clicks; only populated upstream DTO and explicitly delayed/transient responses are fixtures; not owner root-cause proof','cases':receipts},indent=2))
    return receipts
