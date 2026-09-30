"""Actual Discover/host scripts with synthetic API and Jellyfin navigation boundary."""
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
root=Path(__file__).resolve().parents[2]
web=root/'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser=p.chromium.launch()
    for width in (390,1280):
        page=browser.new_page(viewport={'width':width,'height':900})
        page.set_default_timeout(5000)
        page.route('**/*',lambda r:r.abort())
        page.set_content('''<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#10151c;color:white"><div class="skinHeader"><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider"><button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div><div class="MuiToolbar-root"><div class="MuiStack-root"><a href="#/">Logo</a><a href="#/home?tab=1">Favorites</a></div></div><div id="home"><div class="sections">Native rows</div></div><div id="favorites"></div></body></html>''')
        for name in ('discovery.css','home-tab-host.css'):page.add_style_tag(content=(web/name).read_text())
        for name in ('discovery.js','home-tab-host.js'):page.add_script_tag(content=(web/name).read_text())
        page.evaluate('''async html=>{
          location.hash='#/home';window.user='alice';window.shown=[];window.reads=0;
          window.freshId='01234567-89ab-cdef-0123-456789abcdef';
          window.Emby={Page:{showItem:item=>shown.push(item)}};
          const movie={Title:'Skywalker fixture',MediaType:'movie',TmdbId:181812};
          const tv={Title:'Series fixture',MediaType:'tv',TmdbId:9000};
          window.api={getCurrentUserId:()=>user,serverId:()=>'fixture-server',getUrl:(r,p)=>'/'+r+(p?'?'+new URLSearchParams(p):''),
            getJSON:(url,opts)=>{
              if(url.includes('Rowan/Home/Mode'))return Promise.resolve({DiscoveryEnabled:true,HeroEnabled:false});
              if(url.includes('TitleDetails')){
                reads++;window.lastSignal=opts?.signal;
                if(window.pending)return pending;
                return Promise.resolve({...url.includes('mediaType=tv')?tv:movie,LibraryItemId:freshId,LibraryStatus:freshId?'present':'unknown',CanRequest:false,MediaStatus:5});
              }
              return Promise.resolve({Movies:{Items:[movie]},Tv:{Items:[tv]},Requests:{Items:[]}});
            }};
          window.host=ThreePicFinHomeHost.createHost({document,loadFragment:async()=>html,loadScript:async()=>ThreePicFinDiscovery});
          if(!await host.mount({pane:document.querySelector('#home'),favorites:document.querySelector('#favorites'),apiClient:api,fingerprint:'12.1',userId:user,enabled:true}))throw Error('mount failed');
          document.querySelector('.threepic-fin-host__nav').click();
        }''',(web/'discovery.html').read_text())
        modal=page.locator('#threepic-fin-details-dialog')
        movie=page.locator('#threepic-fin-movies .threepic-fin-discovery__title-button')
        for rail,kind,media_id in [('movies','Movie',181812),('tv','Series',9000)]:
            button=page.locator('#threepic-fin-'+rail+' .threepic-fin-discovery__title-button')
            button.focus();page.keyboard.press('Enter');expect(modal).to_contain_text('In your library')
            page.get_by_role('button',name='View in library').focus();page.keyboard.press('Enter');expect(modal).not_to_be_visible()
            assert page.evaluate('shown.at(-1)')=={'Id':'01234567-89ab-cdef-0123-456789abcdef','Type':kind,'ServerId':'fixture-server'}
        assert page.evaluate('reads')==4
        # Revoked access between display and navigation.
        movie.click();page.evaluate('freshId=null');page.get_by_role('button',name='View in library').click()
        expect(modal).to_contain_text('Library item changed or unavailable');assert page.evaluate('shown.length')==2
        page.keyboard.press('Escape');page.evaluate('freshId="01234567-89ab-cdef-0123-456789abcdef"')
        # Host must reject a late response even if the API ignores AbortSignal.
        movie.click();page.evaluate('()=>{window.pending=new Promise(r=>window.finish=r)}')
        page.get_by_role('button',name='View in library').click();page.keyboard.press('Escape')
        assert page.evaluate('lastSignal.aborted')
        page.evaluate('finish({MediaType:"movie",TmdbId:181812,LibraryItemId:freshId});window.pending=null')
        assert page.evaluate('shown.length')==2
        # Current user and current route must still match at resolution time.
        for change in ('user="bob"', 'location.hash="#/favorites"'):
            movie.click();page.evaluate('()=>{window.pending=new Promise(r=>window.finish=r)}')
            page.get_by_role('button',name='View in library').click()
            page.evaluate(change+';finish({MediaType:"movie",TmdbId:181812,LibraryItemId:freshId});window.pending=null')
            expect(modal).to_contain_text('Library item changed or unavailable');assert page.evaluate('shown.length')==2
            page.evaluate('user="alice";location.hash="#/home"');page.keyboard.press('Escape')
        movie.click();expect(modal).to_contain_text('In your library')
        if os.getenv('DISCOVER_SCREENSHOT_DIR'):page.screenshot(path=str(Path(os.environ['DISCOVER_SCREENSHOT_DIR'])/f'host-library-{width}.png'))
        page.evaluate('host.dispose()');expect(modal).to_have_count(0)
        page.close();print(f'Actual host navigation, movie/series, keyboard, revocation/user/route/dismissal: {width}px PASS',flush=True)
    browser.close()
