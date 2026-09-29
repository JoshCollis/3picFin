"""Synthetic public-theme modal evidence; no hosted integration or external requests.
MODAL_BASELINE=1 loads the approved git baseline; MODAL_SCREENSHOT_DIR saves evidence.
ELEGANTFIN_CSS_DIR must contain the public references pinned in modal-refresh.md.
"""
import os, subprocess, hashlib
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
root = Path(__file__).resolve().parents[2]
web = root / 'src/Rowan.Jellyfin.Plugin/Web'
baseline = os.getenv('MODAL_BASELINE') == '1'
def asset(name):
    return subprocess.check_output(['git', 'show', 'e069ff14e601eb22f138df352ce96b5f6929b3d0:src/Rowan.Jellyfin.Plugin/Web/'+name], cwd=root).decode() if baseline else (web/name).read_text()
theme = Path(os.environ['ELEGANTFIN_CSS_DIR'])
hashes = {'elegant-source-0.css':'779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643',
          'elegant-source-1.css':'525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841'}
assert all(hashlib.sha256((theme/name).read_bytes()).hexdigest()==digest for name,digest in hashes.items()), 'Public theme reference changed'
print('Public theme SHA256:', hashes)
out = Path(os.environ.get('MODAL_SCREENSHOT_DIR', root / '.modal-evidence'))
out.mkdir(parents=True, exist_ok=True)
poster = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600"><rect width="400" height="600" fill="#233c4a"/><circle cx="265" cy="175" r="85" fill="#d7ba80"/><path d="M0 480L190 250 400 510V600H0" fill="#12252e"/><text x="30" y="550" fill="#f3e9cf" font-family="sans-serif" font-size="30">THE QUIET COAST</text></svg>'
with sync_playwright() as p:
    browser = p.chromium.launch()
    for width in (320,390,1280):
        for kind in ('movie','tv','missing','broken','long'):
            page=browser.new_page(viewport={'width':width,'height':900}, reduced_motion='reduce')
            page.set_default_timeout(5000)
            print('Rendering', 'before' if baseline else 'after', width, kind, flush=True)
            page.route('**/*', lambda r: r.fulfill(content_type='image/svg+xml',body=poster) if 'image.tmdb.org' in r.request.url and kind!='broken' else r.fulfill(content_type='text/css', body='') if r.request.resource_type=='stylesheet' else r.abort())
            page.set_content('<html class="layout-desktop"><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;font-family:Arial,sans-serif;background:#10151c">'+asset('discovery.html')+'</body></html>')
            for name in ('elegant-source-0.css','elegant-source-1.css'): page.add_style_tag(path=str(theme/name))
            page.add_style_tag(content=asset('discovery.css'))
            page.add_script_tag(content=asset('discovery.js'))
            page.evaluate('''kind => {
              const tv = kind === 'tv' || kind === 'long';
              window.item={Title:kind==='missing'?'Unknown title':kind==='long'?'The Extraordinary Chronicles of a Very Long Journey Beyond the Quiet Coast — '+ 'UnbrokenTitle'.repeat(8):'The Quiet Coast',MediaType:tv?'tv':'movie',TmdbId:9,PosterPath:kind==='missing'?null:'/fixture.jpg'};
              window.detail={...item,Date:kind==='missing'?null:'2026-09-27',Overview:kind==='missing'?null:'A cartographer returns to a remote coastal town to finish an atlas left behind by her father. As the tides reveal forgotten paths, she must choose between the life she built and the people who still call this place home. '+(kind==='long'?'The final chapter brings every journey together. '.repeat(5):''),Seasons:tv?Array.from({length:kind==='long'?40:3},(_,i)=>i+1):[],MediaStatus:1,CanRequest:true,CanRequest4k:true};
              window.calls=[]; window.posts=[]; window.requests=[];
              window.dispose=ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'),{
                getUrl:(r,p)=>'/'+r+(p?'?'+new URLSearchParams(p):''),
                getJSON:url=> {calls.push(url); if(url.includes('TitleDetails'))return Promise.resolve(detail); if(url.includes('RequestOptions'))return window.optionPending || Promise.resolve({CanRequest:true,CanRequest4k:true,MediaStatus:1,MediaStatus4k:1,Seasons:detail.Seasons}); if(url.includes('Requests'))return Promise.resolve({Items:requests}); return Promise.resolve({Movies:{Items:tv?[]:[item]},Tv:{Items:tv?[item]:[]},Requests:{Items:requests}});},
                ajax:args=> {posts.push(args); return new Promise(resolve=>window.resolvePost=resolve);}
              });
            }''',kind)
            trigger=page.locator('#threepic-fin-'+('tv' if kind in ('tv','long') else 'movies')+' .threepic-fin-discovery__title-button')
            trigger.click()
            modal=page.locator('#threepic-fin-details-dialog')
            expect(modal).to_contain_text('No overview available.' if kind=='missing' else 'A cartographer')
            if kind not in ('missing','broken'): page.locator('#threepic-fin-details-body img').evaluate('(img)=>img.decode()')
            if kind=='broken': expect(page.locator('#threepic-fin-details-body img')).to_have_count(0)
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            if not baseline: assert modal.evaluate('(e)=>e.scrollWidth <= e.clientWidth'), (width,kind)
            if width!=320: page.screenshot(path=str(out/f'{"before" if baseline else "after"}-{kind}-{width}.png'))
            if not baseline:
                assert modal.locator('#threepic-fin-details-overview').text_content() == page.evaluate('detail.Overview || "No overview available."')
                # Contrast against the brightest possible pixel under the 75% black backdrop.
                assert modal.evaluate(r'''e => {
                  const rgb=s=>(s.match(/[\d.]+/g)||[]).map(Number);
                  const bg=rgb(getComputedStyle(e).backgroundColor), alpha=bg[3]??1;
                  const surface=bg.slice(0,3).map(v=>v*alpha+64*(1-alpha));
                  const lum=c=>c.map(v=>v/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4).reduce((a,v,i)=>a+v*[.2126,.7152,.0722][i],0);
                  const ratio=(a,b)=>(Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);
                  return ['details-title','details-meta','details-overview','details-status'].every(id=>ratio(rgb(getComputedStyle(document.getElementById('threepic-fin-'+id)).color),surface)>=4.5);
                }'''), 'Text contrast below 4.5:1'
                expect(page.locator('#threepic-fin-details-close')).to_be_focused()
                page.keyboard.press('Shift+Tab')
                assert modal.evaluate('(e)=>e.contains(document.activeElement)')
                page.keyboard.press('Escape')
                expect(trigger).to_be_focused()
                trigger.click()
                page.locator('#threepic-fin-details-request').click()
                request=page.locator('#threepic-fin-request-dialog')
                expect(request).to_be_visible()
                expect(page.locator('#threepic-fin-request-submit')).to_be_enabled()
                expect(request).to_contain_text(page.evaluate('item.Title'))
                assert request.evaluate('(e)=>e.scrollWidth <= e.clientWidth')
                if kind=='tv':
                    page.locator('#threepic-fin-request-submit').click()
                    expect(request).to_contain_text('Choose at least one season.')
                    assert page.evaluate('posts.length')==0
                    page.locator('#threepic-fin-request-seasons input').first.check()
                    page.locator('#threepic-fin-request-4k').check()
                    assert page.locator('#threepic-fin-request-seasons input:checked').count()==1
                if width!=320 and kind in ('movie','tv'): page.screenshot(path=str(out/f'after-confirm-{kind}-{width}.png'))
                # Actual submission, in-flight dismissal lock, duplicate prevention and read-back.
                if kind in ('movie','tv'):
                    page.locator('#threepic-fin-request-submit').click()
                    expect(request).to_contain_text('Submitting request…')
                    page.evaluate("document.querySelector('#threepic-fin-request-form').requestSubmit()")
                    page.keyboard.press('Escape')
                    expect(request).to_be_visible()
                    page.locator('#threepic-fin-request-cancel').click()
                    expect(request).to_be_visible()
                    assert page.evaluate('posts.length')==1
                    payload=page.evaluate('JSON.parse(posts[0].data)')
                    assert payload==({'mediaType':'tv','mediaId':9,'is4k':True,'seasons':[1]} if kind=='tv' else {'mediaType':'movie','mediaId':9,'is4k':False}),payload
                    page.evaluate("requests=[{Id:42,TmdbId:9,MediaType:item.MediaType,Is4k:item.MediaType==='tv',Status:1,Title:item.Title}]; resolvePost({Id:42})")
                    expect(request).to_contain_text('Check My Requests for updates.')
                    expect(page.locator('#threepic-fin-request-submit')).to_be_disabled()
                page.locator('#threepic-fin-request-cancel').click()
                expect(trigger).to_be_focused()
                if kind=='long':
                    # All season controls remain reachable by keyboard in the scrolling fieldset.
                    trigger.click()
                    page.locator('#threepic-fin-details-request').click()
                    last=page.locator('#threepic-fin-request-seasons input').last
                    last.focus()
                    page.keyboard.press('Space')
                    expect(last).to_be_checked()
                    page.keyboard.press('Escape')
                    expect(trigger).to_be_focused()
                if kind=='movie':
                    page.evaluate('() => {window.optionPending=new Promise(resolve=>window.resolveOptions=resolve)}')
                    trigger.click()
                    page.locator('#threepic-fin-details-request').click()
                    expect(request).to_contain_text('Checking Seerr permissions')
                    page.keyboard.press('Escape')
                    page.evaluate('resolveOptions({CanRequest:true,CanRequest4k:true,Seasons:[]})')
                    expect(request).not_to_be_visible()
                    assert page.evaluate('posts.length')==1

            page.evaluate('dispose()')
            page.close()
    browser.close()
print('Modal theme geometry, contrast, full synopsis, focus, dismissal, seasons/4K, POST/read-back, duplicate protection and stale options: PASS' if not baseline else 'Baseline screenshots: captured')
