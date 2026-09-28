"""Persistent Fin entry across route changes; synthetic UI seam in Chromium."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (390, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 750})
        page.set_content('''<html><body><div class="skinHeader"><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider"><button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div><div class="MuiToolbar-root"><button aria-label="Open Menu">Menu</button><div class="MuiStack-root"><a href="#/">Home</a><a href="#/home?tab=1">Favorites</a><a href="#/movies">Movies</a></div></div><div class="MuiDrawer-paper"><ul><li class="MuiListItem-root"><a href="#/">Home</a></li><li class="MuiListItem-root"><a href="#/home?tab=1">Favorites</a></li><li class="MuiListItem-root"><a href="#/movies">Movies</a></li></ul></div><div id="indexPage"><div id="homeTab" data-index="0"><div class="sections">Home sections</div></div><div id="favoritesTab" data-index="1">Favorites</div></div></body></html>''')
        page.evaluate('''() => { window.ApiClient = {getCurrentUserId: () => window.user, getUrl: path => '/jellyfin/' + path, getJSON: async () => ({Version:'12.1.0', DiscoveryEnabled:true})}; window.user='alice'; location.hash='#/movies'; }''')
        page.add_style_tag(content=(web / 'global-fin-nav.css').read_text())
        page.add_script_tag(content=(web / 'global-fin-nav.js').read_text())
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2")
        assert page.locator('.MuiStack-root .threepic-fin-global-nav span').evaluate('(e) => getComputedStyle(e).textTransform') == 'none'
        for route in ('#/movies', '#/search?query=test', '#/home'):
            page.evaluate('(route) => location.hash = route', route)
            page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2")
            assert page.locator('.threepic-fin-global-nav').first.inner_text() == '3pic Fin'
            assert page.locator('.emby-tab-button').count() == 2
        page.locator('.MuiStack-root .threepic-fin-global-nav').click()
        assert page.evaluate('location.hash') == '#/home?fin=1'
        page.go_back()
        assert page.evaluate('location.hash') == '#/home'
        page.go_forward()
        assert page.evaluate('location.hash') == '#/home?fin=1'
        page.evaluate("location.hash='#/movies'")
        page.locator('.MuiStack-root').evaluate("e => e.outerHTML=e.outerHTML.replace('MuiStack-root','MuiStack-root')")
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2")
        page.locator('.MuiStack-root .threepic-fin-global-nav').click()
        assert page.evaluate('location.hash') == '#/home?fin=1', 'cloned shell must rebind navigation'
        page.evaluate("window.user=null")
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 0", timeout=3000)
        page.evaluate("window.user='bob'")
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2", timeout=3000)
        assert page.locator('#favoritesTab').inner_text() == 'Favorites'
        page.evaluate('window.__threePicFinGlobalNav.dispose()')
        assert page.locator('.threepic-fin-global-nav').count() == 0
        page.close()
    browser.close()
print('global Fin navigation mobile/desktop: pass')
