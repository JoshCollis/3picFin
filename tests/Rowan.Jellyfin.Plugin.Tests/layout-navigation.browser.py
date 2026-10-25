"""Native-shaped shell regression. Hosted renderer acceptance is a separate gate.

Shapes checked against jellyfin-web c06cb17b (12.3), not copied upstream code.
No live services, accounts, or distribution fingerprints.
"""
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
tabs = '''<div class="skinHeader"><div class="headerTop"><div class="headerLeft"><button class="headerButton headerHomeButton" style="display:none">Home</button></div></div><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider"><button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div>'''
modern = '''<div class="MuiToolbar-root"><div class="MuiStack-root"><a class="MuiButton-root" href="#/">Server</a><a class="MuiButton-root" href="#/home?tab=1">Favorites</a><a href="#/movies">Movies</a></div></div><div class="MuiDrawer-root"><ul><li class="MuiListItem-root"><a href="#/">Home</a></li><li class="MuiListItem-root"><a href="#/home?tab=1">Favorites</a></li></ul></div>'''
legacy = '''<div class="mainDrawer"><div class="mainDrawer-scrollContainer focuscontainer-y"><a is="emby-linkbutton" class="navMenuOption lnkMediaFolder" href="#/home"><span class="navMenuOptionText">Home</span></a><a class="navMenuOption" href="#/movies">Movies</a></div></div>'''

with sync_playwright() as p:
    browser = p.chromium.launch()
    for mode in ('desktop', 'tv', 'desktop-legacy', 'mobile-legacy'):
        for width in (390, 1280):
            page = browser.new_page(viewport={'width': width, 'height': 850}, has_touch=True)
            page.set_default_timeout(3500)
            page.set_content('<html><body>' + (modern if mode == 'desktop' else legacy) + tabs + '<main id="indexPage"><div id="homeTab" data-index="0"><div class="sections">Native Home</div></div><div id="favoritesTab" data-index="1">Saved favorites</div></main><button id="favorite-item">Favorite item</button></body></html>')
            page.add_style_tag(content='a,button {display:inline-flex; padding:12px;} .headerTabs{display:flex;}')
            if mode == 'desktop':
                page.add_style_tag(content='.skinHeader {display:none}')
            else:
                page.add_style_tag(content='.mainDrawer {display:none}')
            page.add_style_tag(path=str(web / 'global-fin-nav.css'))
            page.evaluate('''mode => {
                document.documentElement.className = 'layout-' + mode.split('-')[0];
                window.user='synthetic'; location.hash='#/home';
                window.ApiClient={getCurrentUserId:()=>user,getUrl:p=>p,getJSON:async()=>({Version:'12.3.0',DiscoveryEnabled:true})};
            }''', mode)
            page.add_script_tag(path=str(web / 'global-fin-nav.js'))
            nav = page.locator('.threepic-fin-global-nav:visible').first
            expect(nav).to_be_visible()
            expect(page.locator('a[href="#/home?tab=1"]:visible')).to_have_count(0)
            expect(page.locator('.headerTabs .emby-tab-button[data-index="1"]:visible')).to_have_count(0)
            nav.focus()
            page.keyboard.press('Enter')
            page.wait_for_function('location.hash === "#/home?fin=1"')
            expect(nav).to_have_attribute('aria-current', 'page')
            if mode != 'desktop':
                # The native Home controller is already at index zero while Fin
                # is showing. Clicking its active tab emits no native tabchange.
                home = page.locator('.headerTabs > .threepic-fin-global-home:visible, .headerTabs .emby-tab-button[data-index="0"]:visible').first
                home.click()
                page.wait_for_function('location.hash === "#/home"')
                for _ in range(2):
                    nav.click()
                    expect(nav).to_have_attribute('aria-current', 'page')
                    expect(home).to_have_attribute('aria-current', 'false')
                    expect(page.locator('.headerTabs .emby-tab-button-active:visible')).to_have_count(0)
                    assert nav.evaluate('e=>getComputedStyle(e).backgroundColor') != home.evaluate('e=>getComputedStyle(e).backgroundColor')
                    if width == 390:
                        home.tap()
                    else:
                        home.focus(); page.keyboard.press('Enter')
                    page.wait_for_function('location.hash === "#/home"')
                    expect(home).to_have_attribute('aria-current', 'page')
                    expect(nav).to_have_attribute('aria-current', 'false')
                    assert home.evaluate('e=>getComputedStyle(e).backgroundColor') != nav.evaluate('e=>getComputedStyle(e).backgroundColor')
            assert page.locator('#favorite-item').is_visible()
            assert page.locator('#favoritesTab').inner_text() == 'Saved favorites'
            # Native tabs retain their indexes; the inserted action isn't a managed tab.
            assert page.locator('.emby-tabs-slider > .emby-tab-button').count() == 2
            for route in ('#/search?query=abc', '#/movies', '#/home'):
                if mode != 'desktop':
                    page.evaluate('''home => {
                        document.querySelector('.headerTabs').style.display=home ? '' : 'none';
                        document.querySelector('.headerHomeButton').style.display=home ? 'none' : '';
                    }''', route == '#/home')
                page.evaluate('r => location.hash=r', route)
                expect(nav).to_be_visible()
                nav.click()
                page.wait_for_function('location.hash === "#/home?fin=1"')
            shell = '.MuiToolbar-root' if mode == 'desktop' else '.skinHeader'
            if mode != 'desktop':
                page.locator('.headerTabs > [is="emby-tabs"]').evaluate('e=>e.classList.remove("threepic-fin-hidden-home")')
                expect(page.locator('.headerTabs .emby-tab-button-active:visible')).to_have_count(0)
                assert page.locator('.headerTabs .emby-tab-button-active[data-index="0"]').count() == 1
            page.locator(shell).evaluate('e=>e.outerHTML=e.outerHTML')
            expect(page.locator('.threepic-fin-global-nav:visible').first).to_be_visible()
            page.locator('.threepic-fin-global-nav:visible').first.click()
            if mode != 'desktop':
                page.locator('.threepic-fin-global-home:visible').first.evaluate('e=>e.remove()')
                expect(page.locator('.threepic-fin-global-home:visible').first).to_be_visible()
                page.locator('.threepic-fin-global-home:visible').first.click()
                page.wait_for_function('location.hash === "#/home"')
            page.evaluate('user=null')
            expect(page.locator('.threepic-fin-global-nav')).to_have_count(0)
            expect(page.locator('.threepic-fin-global-home')).to_have_count(0)
            page.evaluate('user="second-synthetic"')
            expect(page.locator('.threepic-fin-global-nav:visible').first).to_be_visible()
            if mode == 'desktop':
                page.evaluate('__threePicFinGlobalNav.dispose()')
            else:
                page.evaluate('''() => {
                    const header=document.querySelector('.skinHeader');
                    header.remove();
                    __threePicFinGlobalNav.dispose();
                    document.body.append(header);
                }''')
            expect(page.locator('.threepic-fin-global-nav')).to_have_count(0)
            expect(page.locator('.threepic-fin-global-home')).to_have_count(0)
            expect(page.locator('.headerTabs .emby-tab-button[data-index="1"]')).to_be_visible() if mode != 'desktop' else None
            page.close()
            print(mode, width, 'PASS', flush=True)
    browser.close()
