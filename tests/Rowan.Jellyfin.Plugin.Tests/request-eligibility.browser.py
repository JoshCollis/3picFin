"""Synthetic eligibility browser regression. Uses only workspace-local, hash-pinned public CSS.
ELIGIBILITY_BASELINE=1 renders current-main baseline affordances; no live APIs are contacted.
"""
import os, subprocess, hashlib
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
root = Path(__file__).resolve().parents[2]
base = os.getenv('ELIGIBILITY_BASELINE') == '1'
theme = Path(os.environ['ELEGANTFIN_CSS_DIR'])
out = Path(os.environ['ELIGIBILITY_SCREENSHOT_DIR']); out.mkdir(parents=True, exist_ok=True)
for name, digest in {'elegant-source-0.css':'779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643', 'elegant-source-1.css':'525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841'}.items():
    assert hashlib.sha256((theme/name).read_bytes()).hexdigest() == digest

def asset(name):
    path = 'src/Rowan.Jellyfin.Plugin/Web/' + name
    return subprocess.check_output(['git','show','f1dd9e7:'+path],cwd=root).decode() if base else (root/path).read_text()

with sync_playwright() as p:
    browser = p.chromium.launch()
    for width in (320, 390, 1280):
        for scenario in ('pending', 'processing', 'default-off', 'partial-tv', '4k-upgrade'):
            page = browser.new_page(viewport={'width':width,'height':900}, reduced_motion='reduce')
            page.set_default_timeout(5000)
            page.route('**/*',lambda r:r.fulfill(content_type='text/css',body='') if r.request.resource_type=='stylesheet' else r.abort())
            page.set_content('<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#10151c;font-family:Arial,sans-serif">'+asset('discovery.html')+'</body></html>')
            for name in ('elegant-source-0.css','elegant-source-1.css'): page.add_style_tag(path=str(theme/name))
            page.add_style_tag(content=asset('discovery.css')); page.add_script_tag(content=asset('discovery.js'))
            page.evaluate('''({scenario, base}) => {
              const tv = scenario === 'partial-tv';
              const upgrade = scenario === '4k-upgrade';
              const state = scenario==='pending'?2:scenario==='processing'?3:tv?4:upgrade?5:1;
              const can = state!==5 && (base || ![2,3].includes(state));
              window.item={Title:tv?'Synthetic series — additional seasons':'Avengers: Doomsday',MediaType:tv?'tv':'movie',TmdbId:9};
              window.detail={...item,Overview:'Synthetic fixture. Existing requests from another user reserve the same version. Additional missing seasons and opted-in missing 4K versions remain eligible.',LibraryStatus:'absent',MediaStatus:state,CanRequest:can,CanRequest4k:base || upgrade,Seasons:tv?[1,2,3,4]:[]};
              window.options={CanRequest:can,CanRequest4k:base || upgrade,MediaStatus:state,MediaStatus4k:1,Seasons:tv?(base?[1,2,3,4]:[4]):[],Seasons4k:tv?[2,4]:[]};
              window.posts=[];window.current='alice';window.readback=[];
              ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'),{
                getCurrentUserId:()=>current,
                getUrl:(r,p)=>'/'+r+(p?'?'+new URLSearchParams(p):''),
                getJSON:r=>r.includes('TitleDetails')?Promise.resolve(detail):r.includes('RequestOptions')?(window.pendingOptions || Promise.resolve(options)):Promise.resolve({Movies:{Items:tv?[]:[item]},Tv:{Items:tv?[item]:[]},Requests:{Items:readback}}),
                ajax:args=>{posts.push(JSON.parse(args.data));return new Promise(resolve=>window.finish=resolve);}
              },{userId:'alice',isCurrent:()=>current==='alice'});
            }''', {'scenario':scenario,'base':base})
            trigger = page.locator('#threepic-fin-'+('tv' if scenario=='partial-tv' else 'movies')+' .threepic-fin-discovery__title-button')
            trigger.click(); expect(page.locator('#threepic-fin-details-status')).to_contain_text('Not in your accessible library')
            action = page.locator('#threepic-fin-details-request')
            if not base and scenario in ('pending','processing'): expect(action).to_be_disabled()
            if width != 320: page.screenshot(path=str(out/f'{"before" if base else "after"}-{scenario}-details-{width}.png'))
            assert page.evaluate('document.documentElement.scrollWidth<=innerWidth')
            if scenario in ('default-off','partial-tv','4k-upgrade'):
                action.click()
                request = page.locator('#threepic-fin-request-dialog'); expect(request).to_be_visible()
                expect(page.locator('#threepic-fin-request-submit')).to_be_enabled()
                if not base:
                    if scenario=='default-off': expect(page.locator('#threepic-fin-request-4k-wrap')).to_be_hidden()
                    elif scenario=='partial-tv':
                        expect(page.locator('#threepic-fin-request-seasons input')).to_have_count(1)
                        expect(page.locator('#threepic-fin-request-seasons input')).to_have_value('4')
                        page.locator('#threepic-fin-request-seasons input').check()
                    else:
                        expect(page.locator('#threepic-fin-request-4k')).to_be_checked()
                if width != 320: page.screenshot(path=str(out/f'{"before" if base else "after"}-{scenario}-form-{width}.png'))
                assert request.evaluate('(e)=>e.scrollWidth<=e.clientWidth')
                if not base:
                    page.locator('#threepic-fin-request-submit').click()
                    page.evaluate("document.querySelector('#threepic-fin-request-form').requestSubmit()")
                    assert page.evaluate('posts.length')==1
                    sent=page.evaluate('posts[0]')
                    assert sent['is4k']==(scenario=='4k-upgrade')
                    if scenario=='partial-tv': assert sent['seasons']==[4]
                    if scenario=='default-off':
                        # A switched user must never render old-user request data after a POST.
                        page.evaluate("current='bob';readback=[{Id:91,TmdbId:9,MediaType:'movie',Is4k:false,Status:1,Title:'Private Alice request'}];finish({Id:91})")
                        page.wait_for_timeout(80)
                        assert 'Private Alice request' not in page.locator('body').inner_text()
                    else:
                        page.evaluate("readback=[{Id:91,TmdbId:9,MediaType:item.MediaType,Is4k:posts[0].is4k,Status:1}];finish({Id:91})")
                        expect(request).to_contain_text('Check My Requests for updates.')
            print(('before' if base else 'after'),width,scenario,'PASS',flush=True)
            page.close()
    browser.close()
