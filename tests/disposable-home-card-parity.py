"""Signed-in disposable Jellyfin 12.1 Home card/action parity under captured ElegantFin CSS."""
import json
import hashlib
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
THEME = Path(os.environ['FIN_THEME_CSS_DIR'])
SCRATCH = Path(os.environ['TMPDIR'])

def call(base, path, method='GET', body=None, token=None):
    auth = 'MediaBrowser Client="HomeCardProbe", Device="Disposable", DeviceId="home-card-probe", Version="1.0"'
    if token: auth += f', Token="{token}"'
    headers = {'Authorization': auth}
    if body is not None: headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(base + path, method=method, headers=headers,
        data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and 'json' in response.headers.get('Content-Type','') else None
    except urllib.error.HTTPError as error:
        return error.code, None

def ok(value):
    assert value[0] in (200, 201, 204), value[0]
    return value[1]

def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()

def main():
    assert hashlib.sha256((THEME/'0.css').read_bytes()).hexdigest() == '779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643'
    assert hashlib.sha256((THEME/'1.css').read_bytes()).hexdigest() == '525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841'
    with tempfile.TemporaryDirectory(prefix='home-card-parity-', dir=SCRATCH) as temp:
        tmp = Path(temp)
        screenshots = SCRATCH/'home-card-validation'
        screenshots.mkdir(exist_ok=True)
        (tmp/'config').mkdir(); (tmp/'cache').mkdir()
        media = tmp/'media'; media.mkdir()
        film = media/'Parity Film (2026)'; film.mkdir()
        source = film/'Parity Film.mp4'
        subprocess.run(['ffmpeg','-nostdin','-loglevel','error','-f','lavfi','-i','color=c=blue:s=160x90:r=1','-t','1','-c:v','mpeg4',str(source)], check=True)
        subprocess.run(['ffmpeg','-nostdin','-loglevel','error','-f','lavfi','-i','color=c=orange:s=200x300','-frames:v','1',str(film/'poster.jpg')], check=True)
        cid = docker('run','-d','--user',f'{os.getuid()}:{os.getgid()}','-p','127.0.0.1::8096',
            '-v',f'{tmp}/config:/config','-v',f'{tmp}/cache:/cache','-v',f'{media}:/media:ro','jellyfin/jellyfin:12.1')
        try:
            base = 'http://127.0.0.1:' + docker('port',cid,'8096/tcp').rsplit(':',1)[1]
            for _ in range(90):
                try:
                    if call(base,'/Startup/User')[0] == 200: break
                except OSError: pass
                time.sleep(1)
            else: raise AssertionError('startup timeout')
            password = secrets.token_urlsafe(18)
            ok(call(base,'/Startup/User','POST',{'Name':'probe','Password':password}))
            ok(call(base,'/Startup/Complete','POST',{}))
            login = ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'probe','Pw':password}))
            token = login['AccessToken']
            viewer_password = secrets.token_urlsafe(18)
            ok(call(base,'/Users/New','POST',{'Name':'viewer','Password':viewer_password},token))
            ok(call(base,'/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false','POST',{},token))
            ok(call(base,'/Library/VirtualFolders/Paths','POST',{'Name':'Movies','Path':'/media'},token))
            ok(call(base,'/Library/Refresh','POST',{},token))
            for _ in range(80):
                items = ok(call(base,'/Items?Recursive=true&IncludeItemTypes=Movie&Limit=20',token=token))['Items']
                if items: break
                time.sleep(1)
            else: raise AssertionError('movie scan timeout')
            item = ok(call(base,'/Items/'+items[0]['Id'],token=token))
            assert item.get('ImageTags',{}).get('Primary'), 'fixture poster not scanned'
            web = ROOT/'src/Rowan.Jellyfin.Plugin/Web'
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch(headless=True)
                try:
                    for width in (390,1280):
                        username, login_password = ('probe', password) if width == 390 else ('viewer', viewer_password)
                        page = browser.new_page(viewport={'width':width,'height':800})
                        page.goto(base+'/web/index.html',wait_until='domcontentloaded')
                        page.locator('#txtManualName').fill(username)
                        page.locator('#txtManualPassword').fill(login_password)
                        page.locator('#txtManualPassword').press('Enter')
                        page.locator('#homeTab .sections').wait_for(timeout=45000)
                        for name in ('0.css','1.css'):
                            page.add_style_tag(path=str(THEME/name))
                        native = page.locator('#homeTab .sections .card[data-type="Movie"]').first
                        native.wait_for(timeout=45000)
                        native_id = native.get_attribute('data-id')
                        native_dom = native.evaluate('''el => ({html:el.outerHTML.slice(0,4000), container:el.closest('[is="emby-itemscontainer"]')?.outerHTML.slice(0,350)})''')
                        print(json.dumps({'width':width,'native_card_classes':native.get_attribute('class'),'native_container':native_dom['container'][:130] if native_dom['container'] else None,'native_overlay': 'cardOverlayContainer' in native_dom['html']}))
                        page.add_style_tag(path=str(web/'native-home-rows.css'))
                        page.add_script_tag(path=str(web/'native-home-rows.js'))
                        page.evaluate('''item => {
                            window.cardObserver = null;
                            class Observer { constructor(callback) { this.callback=callback; cardObserver=this; } observe() {} disconnect() {} }
                            window.parityRows = RowanNativeHomeRows.createRows({document,IntersectionObserver:Observer,enabledRows:['LatestMovies'],
                                openItem:item => Emby.Page.showItem(item)});
                            const root=document.createElement('div'); root.className='rowan-native-rows';
                            document.querySelector('#homeTab').appendChild(root);
                            const api={getUrl:path=>ApiClient.getUrl(path),getJSON:async()=>({Kind:'LatestMovies',Items:[{
                                Id:item.Id, Type:'Movie',Name:'Parity Film',ImageTags:item.ImageTags}]}),
                                getCurrentUserId:()=>ApiClient.getCurrentUserId(),serverId:()=>ApiClient.serverId()};
                            parityRows.mount(root,api,ApiClient.getCurrentUserId());
                            cardObserver.callback([{target:root.querySelector('.rowan-native-row'),isIntersecting:true}]);
                        }''',item)
                        plugin = page.locator('.rowan-native-row__card').first
                        plugin.wait_for()
                        plugin.locator('img').evaluate('(img) => img.decode()')
                        measure = '''el => {const frame=el.querySelector('.cardImageContainer'),footer=el.querySelector('.cardFooter'),box=el.querySelector('.cardBox');return {card:el.getBoundingClientRect().width,frame:frame?.getBoundingClientRect().width,footer:footer?getComputedStyle(footer).backgroundColor:null,box:getComputedStyle(box).backgroundColor,classes:footer?.className,children:[...box.children].map(x=>x.className),gap:getComputedStyle(el).getPropertyValue('--itemColumnGap'),parentGap:getComputedStyle(el.parentElement).getPropertyValue('--itemColumnGap'),padding:getComputedStyle(el).padding}}'''
                        metrics = {'native':native.evaluate(measure),'plugin':plugin.evaluate(measure)}
                        print(json.dumps({'width':width,'metrics':metrics,'native_id_present':bool(native_id)}))
                        assert plugin.locator('.cardFooter, .visualCardBox').count()==0
                        assert plugin.locator('[data-action="resume"]').count()==1
                        assert plugin.locator('[data-action="menu"]').count()==1
                        assert abs(metrics['native']['frame']-metrics['plugin']['frame'])<3,metrics
                        assert metrics['native']['box']==metrics['plugin']['box'],metrics
                        page.screenshot(path=str(screenshots/f'home-card-{width}.png'),full_page=True)
                        plugin.hover();page.wait_for_timeout(500)
                        assert plugin.locator('[data-action="resume"]').is_visible()
                        page.screenshot(path=str(screenshots/f'home-card-hover-{width}.png'),full_page=True)
                        plugin.locator('[data-action="menu"]').click()
                        menu = page.locator('.dialog, [role="dialog"], .actionSheetMenu')
                        menu.first.wait_for(timeout=10000)
                        menu_text = menu.first.inner_text().lower()
                        assert 'play' in menu_text, menu_text
                        assert ('edit metadata' in menu_text) == (username == 'probe'), (username, menu_text)
                        print(json.dumps({'width':width,'user_role':'admin' if username=='probe' else 'viewer','menu_native':True,'edit_visible':'edit metadata' in menu_text}))
                        page.locator('.dialogBackdrop').last.click(position={'x':5,'y':5},force=True)
                        menu.first.wait_for(state='hidden',timeout=10000)
                        plugin.locator('[data-action="resume"]').click()
                        page.locator('video').first.wait_for(timeout=20000)
                        print(json.dumps({'width':width,'native_playback_video':True}))
                        page.close()
                finally: browser.close()
        finally:
            subprocess.run(['docker','rm','-f',cid],check=True,capture_output=True)

if __name__=='__main__': main()
