"""Focused DOM/controller safety, not hosted/themed renderer or server auth proof."""
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

web=Path(__file__).resolve().parents[2]/'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser=p.chromium.launch(headless=True,executable_path=os.environ.get('FIN_GUIDE_CHROME'))
    try:
        page=browser.new_page()
        page.route('https://fixture.invalid/**',lambda r:r.fulfill(content_type='text/html',body='''<main id="searchPage"><div class="searchResults">
            <div class="verticalSection" id="movie"><div class="card" data-type="Movie">Native movie</div></div>
            <div class="verticalSection" id="series"><div class="card" data-type="Series">Native series</div></div>
            <div class="verticalSection" id="channel"><div class="card" data-type="TvChannel">Duplicate channel poster</div></div>
            <div class="verticalSection" id="mixed"><div class="card" data-type="TvChannel">Channel</div><div class="card" data-type="Movie">Movie</div></div>
            </div></main>'''))
        page.goto('https://fixture.invalid/#/search?query=France2')
        page.add_script_tag(path=str(web/'channel-search.js'))
        page.add_style_tag(path=str(web/'global-search-addon.css'))
        page.evaluate('''() => {
            window.user='alice';window.reads=0;
            window.api={getCurrentUserId:()=>user,serverId:()=> 'server',getCurrentUser:async()=>({Policy:{EnableLiveTvAccess:true}}),
                getLiveTvChannels:async()=>{reads++;return {Items:[{Id:'a'.repeat(32),Name:'France 2',ChannelNumber:'1895',Type:'TvChannel'},
                    {Id:'b'.repeat(32),Name:'France 24',ChannelNumber:'1896',Type:'TvChannel'}],TotalRecordCount:2};}};
            window.section=ThreePicFinChannelSearch.createSection();
            window.mount=query=>section.mount({root:document.querySelector('main'),apiClient:api,userId:user,sessionUserId:()=>user,isCurrent:()=>true,query});
            mount('France2');
        }''')
        page.wait_for_selector('.finChannelResult')
        assert not page.locator('#channel').is_visible()
        assert all(page.locator('#'+name).is_visible() for name in ['movie','series','mixed'])
        page.evaluate('''() => {window.stale=document.querySelector('.finChannelResult');section.update({query:'BBC'});stale.click();}''')
        assert '#/search?' in page.url, 'Detached channel action navigated after its query became stale'
        page.wait_for_function('document.querySelector(".threepic-fin-channels")?.getAttribute("aria-busy")!=="true"')
        assert page.evaluate('reads')==1
        page.evaluate('section.suspend();mount("1895")')
        page.wait_for_selector('.finChannelResult')
        assert page.locator('.finChannelResult').count()==1
        assert page.evaluate('reads')==1
        page.evaluate('document.querySelector(".threepic-fin-channels").remove();section.update({query:"1895"})')
        assert page.locator('.finChannelResult:visible').count()==1
        assert page.evaluate('reads')==1
        page.evaluate('''() => {const replacement=document.createElement('main');replacement.id='searchPage';document.querySelector('main').replaceWith(replacement);section.suspend();mount('1895');}''')
        page.wait_for_selector('.finChannelResult')
        assert page.evaluate('reads')==1, 'Query rerender rebuilt the channel index'
        page.evaluate('section.update({query:"x".repeat(101)})')
        assert '100 characters' in page.locator('.threepic-fin-channels').inner_text()
        assert page.evaluate('reads')==1
        page.evaluate('user="bob";section.checkIdentity(api,user)')
        assert page.locator('.finChannelResult').count()==0
        page.evaluate('mount("France")')
        page.wait_for_selector('.finChannelResult')
        assert page.evaluate('reads')==2
        page.evaluate('section.dispose();api.getCurrentUser=async()=>({Policy:{EnableLiveTvAccess:false}});mount("France")')
        page.wait_for_timeout(500)
        assert page.locator('.finChannelResult:visible').count()==0
        assert page.evaluate('reads')==2
        print('channel section stale action, query reuse, identity invalidation and grant guard PASS')
    finally: browser.close()
