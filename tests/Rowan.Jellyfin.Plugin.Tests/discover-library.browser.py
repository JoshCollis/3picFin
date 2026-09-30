"""Synthetic Discover lifecycle evidence. No network or hosted/private library data.
DISCOVER_BASELINE=1 renders base 641bff4; requires local pinned ELEGANTFIN_CSS_DIR.
"""
import hashlib, os, subprocess
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
root = Path(__file__).resolve().parents[2]
web = root / 'src/Rowan.Jellyfin.Plugin/Web'
baseline = os.getenv('DISCOVER_BASELINE') == '1'
out = Path(os.environ['DISCOVER_SCREENSHOT_DIR']); out.mkdir(parents=True, exist_ok=True)
theme = Path(os.environ['ELEGANTFIN_CSS_DIR'])
hashes = {'elegant-source-0.css':'779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643', 'elegant-source-1.css':'525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841'}
for name, digest in hashes.items(): assert hashlib.sha256((theme/name).read_bytes()).hexdigest() == digest
print('Pinned theme SHA256:', hashes, flush=True)
def asset(name):
    return subprocess.check_output(['git','show','641bff4:src/Rowan.Jellyfin.Plugin/Web/'+name],cwd=root).decode() if baseline else (web/name).read_text()
with sync_playwright() as p:
    browser=p.chromium.launch()
    for width in (320,390,1280):
        page=browser.new_page(viewport={'width':width,'height':900},reduced_motion='reduce')
        page.set_default_timeout(5000)
        page.route('**/*',lambda r:r.fulfill(content_type='text/css',body='') if r.request.resource_type=='stylesheet' else r.abort())
        page.set_content('<html class="layout-desktop"><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;font-family:Arial,sans-serif;background:#10151c">'+asset('discovery.html')+'</body></html>')
        for name in hashes: page.add_style_tag(path=str(theme/name))
        page.add_style_tag(content=asset('discovery.css'))
        page.add_script_tag(content=asset('discovery.js'))
        page.evaluate('''() => {
            window.calls=[]; window.navigated=[]; window.user='alice'; window.mode='present';
            window.movie={Title:'Star Wars: The Rise of Skywalker',MediaType:'movie',TmdbId:181812};
            window.tv={Title:'Synthetic Series',MediaType:'tv',TmdbId:9000};
            window.api={getCurrentUserId:()=>user,getUrl:(r,p)=>'/'+r+(p?'?'+new URLSearchParams(p):''),
              getJSON:(url,options)=> {
                calls.push({url,options});
                if(url.includes('TitleDetails')) {
                  if(window.detailPending)return detailPending;
                  const item=url.includes('mediaType=tv')?tv:movie;
                  return Promise.resolve({...item,Title:mode==='long'?'A Very Long Synthetic Series — '+'UnbrokenTitle'.repeat(15):item.Title,
                    Overview:mode==='missing'?null:'Full synthetic synopsis. '.repeat(20),Date:'2019-12-18',
                    Seasons:item.MediaType==='tv'?Array.from({length:40},(_,i)=>i+1):[],
                    MediaStatus:5,CanRequest:false,CanRequest4k:false,
                    LibraryStatus:mode==='missing'?'unknown':mode,
                    LibraryItemId:['present','long'].includes(mode)?'01234567-89ab-cdef-0123-456789abcdef':null});
                }
                if(url.includes('/Search')) {
                  if(window.searchPending)return searchPending;
                  const q=new URL(url,'https://fixture.test').searchParams.get('query');
                  if(q==='failure')return Promise.reject(Error('fixture'));
                  return Promise.resolve({Items:q==='empty'?[]:[movie,tv],Page:1,TotalPages:2});
                }
                if(url.includes('Discovery/'))return Promise.resolve({Items:[movie,tv]});
                return Promise.resolve({Movies:{Items:[movie]},Tv:{Items:[tv]},Requests:{Items:[{...movie,Id:7,Status:2,RequesterDisplayName:'Fixture'},{...tv,Id:8,Status:2,RequesterDisplayName:'Fixture'}]}});
              }};
            window.dispose=ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'),api,
              {userId:'alice',openItem:async (item,opts)=>{if(window.navigationPending)await navigationPending;if(opts?.signal.aborted)return false;navigated.push(item);return true;}});
        }''')
        heading=page.get_by_role('heading',name='Search results',exact=True)
        movie=page.locator('#threepic-fin-movies .threepic-fin-discovery__title-button')
        tv=page.locator('#threepic-fin-tv .threepic-fin-discovery__title-button')
        modal=page.locator('#threepic-fin-details-dialog')
        expect(movie).to_be_visible()
        if not baseline:
            expect(page.get_by_role('heading',name='3pic Fin',exact=True)).to_have_count(0)
            expect(page.get_by_role('region',name='3pic Fin',exact=True)).to_have_count(1)
            expect(heading).to_have_count(0)
        if width!=320: page.screenshot(path=str(out/f'{"before" if baseline else "after"}-discover-{width}.png'))
        for kind,trigger in [('movie',movie),('tv',tv),('missing',movie),('long',tv)]:
            page.evaluate('(kind)=>mode=kind==="movie"||kind==="tv"?"present":kind',kind)
            trigger.focus();page.keyboard.press('Enter');expect(modal).to_be_visible()
            expect(page.locator('#threepic-fin-details-status')).not_to_have_text('Loading details…')
            assert page.evaluate('document.documentElement.scrollWidth<=innerWidth')
            assert modal.evaluate('e=>e.scrollWidth<=e.clientWidth')
            if not baseline:
                if kind!='missing':
                    expect(modal).to_contain_text('In your library')
                    expect(page.get_by_role('button',name='View in library')).to_be_visible()
                else:
                    expect(modal).to_contain_text('Library status unknown')
                    expect(modal).not_to_contain_text('not in your library')
                if kind=='long':expect(modal).to_contain_text('40 seasons')
                expect(page.locator('#threepic-fin-details-overview')).to_have_text('No overview available.' if kind=='missing' else 'Full synthetic synopsis. '*20)
                expect(page.locator('#threepic-fin-details-close')).to_be_focused()
                page.keyboard.press('Shift+Tab');assert modal.evaluate('e=>e.contains(document.activeElement)')
            if width!=320:page.screenshot(path=str(out/f'{"before" if baseline else "after"}-{kind}-{width}.png'))
            page.keyboard.press('Escape');expect(modal).not_to_be_visible();expect(trigger).to_be_focused()
        if baseline:page.close();continue
        page.evaluate('mode="present"')
        search=page.locator('#threepic-fin-search')
        def submit(query):
            search.fill(query);search.press('Enter')
        submit('skywalker');expect(heading).to_be_visible()
        expect(page.locator('#threepic-fin-search-results article')).to_have_count(2)
        page.locator('#threepic-fin-search-next').click();expect(heading).to_be_visible()
        assert any('page=2' in c['url'] for c in page.evaluate('calls'))
        # Search and request entry points both expose keyboard navigation.
        for selector,index in [(s,i) for s in ('#threepic-fin-search-results','#threepic-fin-requests') for i in (0,1)]:
            trigger=page.locator(selector+' .threepic-fin-discovery__title-button').nth(index)
            trigger.focus();page.keyboard.press('Enter');expect(modal).to_contain_text('In your library')
            action=page.get_by_role('button',name='View in library');action.focus();page.keyboard.press('Enter')
            expect(modal).not_to_be_visible()
        assert len(page.evaluate('navigated'))==4
        submit('empty');expect(heading).to_have_count(0);expect(page.locator('#threepic-fin-search-results')).to_contain_text('No Search results found.')
        submit('failure');expect(heading).to_have_count(0);expect(page.locator('#threepic-fin-search-results')).to_contain_text('unavailable')
        page.evaluate('() => { window.searchPending=new Promise(r=>window.finishSearch=r); }')
        submit('old');expect(heading).to_have_count(0);expect(page.locator('#threepic-fin-search-results')).to_contain_text('Loading')
        search.fill('');page.evaluate('finishSearch({Items:[movie]});window.searchPending=null')
        expect(page.locator('#threepic-fin-search-results article')).to_have_count(0);expect(heading).to_have_count(0)
        page.evaluate('() => { window.searchPending=new Promise(r=>window.finishSearch=r); }');submit('old')
        page.evaluate('window.searchPending=null');submit('skywalker');expect(heading).to_be_visible()
        page.evaluate('finishSearch({Items:[]})');expect(heading).to_be_visible()
        # Unknown, failed and verified absence are distinct; Seerr status alone never enables navigation.
        for mode,label in [('unknown','Library status unknown'),('unavailable','Library lookup unavailable'),('absent','Not in your accessible library')]:
            page.evaluate('(m)=>window.mode=m',mode);movie.click();expect(modal).to_contain_text(label)
            expect(page.get_by_role('button',name='View in library')).to_have_count(0)
            page.keyboard.press('Escape')
        # Closing during navigation aborts the host callback and permits a fresh open.
        page.evaluate('() => { mode="present";window.navigationPending=new Promise(r=>window.finishNavigation=r); }')
        movie.click();page.get_by_role('button',name='View in library').click();page.keyboard.press('Escape')
        page.evaluate('finishNavigation();window.navigationPending=null');assert len(page.evaluate('navigated'))==4
        movie.click();expect(page.get_by_role('button',name='View in library')).to_be_enabled();page.keyboard.press('Escape')
        page.evaluate('() => { window.detailPending=new Promise(r=>window.finishDetail=r); }')
        movie.click();page.keyboard.press('Escape')
        page.evaluate('finishDetail({...movie,Title:"STALE PRIVATE TITLE"});window.detailPending=null')
        expect(page.locator('body')).not_to_contain_text('STALE PRIVATE TITLE')
        page.evaluate('() => { window.detailPending=new Promise(r=>window.finishDetail=r); }')
        movie.click();page.evaluate('user="bob";finishDetail({...movie,Title:"OTHER USER TITLE"})')
        expect(page.locator('body')).not_to_contain_text('OTHER USER TITLE')
        page.evaluate('dispose()');page.close()
        print(f'Discover lifecycle/keyboard/stale responses passed: {width}px',flush=True)
    browser.close()
