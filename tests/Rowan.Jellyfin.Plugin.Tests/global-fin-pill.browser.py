"""Fin pill state and single-line geometry across app-shell routes."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
html = '''<html><head><style>
:root { --ef12-navPillBackground: rgba(17,24,39,.85); --textColor: #d1d5db; --hoverTabBackgroundColor: hsla(0,0%,100%,.9); }
.MuiToolbar-root > .MuiStack-root { display:flex; align-items:center; gap:8px; width:350px; }
.MuiButton-root { display:flex; align-items:center; padding:12px 16px; min-height:40px; background:var(--ef12-navPillBackground); color:var(--textColor); transition:background-color .25s; text-decoration:none; }
.MuiButton-root:hover { background:var(--hoverTabBackgroundColor); color:black; }
.MuiButton-textPrimary { background:var(--textColor); color:black; }
.MuiButton-startIcon { display:flex; width:20px; flex:none; margin-right:8px; }
.MuiButton-startIcon svg { width:20px; height:20px; }
.MuiListItemButton-root { display:flex; padding:12px; color:var(--textColor); }
.MuiListItemButton-root.Mui-selected { background:#5d55e7; color:white; }
</style></head><body><div class="MuiToolbar-root"><button aria-label="Open Menu">Menu</button><div class="MuiStack-root"><a href="#/">Home</a><a class="MuiButton-root MuiButton-textInherit" href="#/home?tab=1"><span class="MuiButton-startIcon"><svg></svg></span>Favorites</a><a class="MuiButton-root" href="#/movies">Movies</a></div></div><ul class="MuiDrawer-paper"><li class="MuiListItem-root"><a class="MuiListItemButton-root" href="#/">Home</a></li><li class="MuiListItem-root"><a class="MuiListItemButton-root" href="#/home?tab=1"><span class="MuiListItemIcon-root">☆</span><span class="MuiListItemText-root">Favorites</span></a></li></ul></body></html>'''

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True)
    for width in (1280, 390):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_content(html)
        page.evaluate('''() => { location.hash='#/home'; window.ApiClient={getCurrentUserId:()=> 'alice',getUrl:p=>p,getJSON:async()=>({Version:'12.1.0',DiscoveryEnabled:true})}; }''')
        page.add_style_tag(content=(web / 'global-fin-nav.css').read_text())
        page.add_script_tag(content=(web / 'global-fin-nav.js').read_text())
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2")
        nav = page.locator('.MuiStack-root .threepic-fin-global-nav')
        drawer = page.locator('.MuiDrawer-paper .threepic-fin-global-nav')
        favorite = page.locator('.MuiStack-root a[href="#/home?tab=1"]')
        # Our label should be a single line like its native sibling, not a tall pill.
        geometry = nav.evaluate('(e)=>({label:e.lastElementChild.getBoundingClientRect().height, icon:e.firstElementChild.getBoundingClientRect().height})')
        assert geometry['label'] <= 30, (width, geometry)
        for route in ('#/home', '#/search?query=Avatar', '#/movies'):
            page.evaluate('(route)=>location.hash=route', route)
            page.wait_for_function('(route)=>location.hash===route', arg=route)
            assert nav.get_attribute('aria-current') == 'false'
            assert drawer.get_attribute('aria-current') == 'false'
            assert not drawer.evaluate('(e)=>e.classList.contains("Mui-selected")')
        nav.click()
        page.wait_for_function('location.hash==="#/home?fin=1"')
        assert nav.get_attribute('aria-current') == 'page'
        assert drawer.get_attribute('aria-current') == 'page'
        assert drawer.evaluate('(e)=>e.classList.contains("Mui-selected")')
        page.evaluate('location.hash="#/search?query=Avatar"')
        page.wait_for_function("document.querySelector('.MuiStack-root .threepic-fin-global-nav').getAttribute('aria-current')==='false'")
        # Pointer remains over the old selected control after route change: hover must
        # not masquerade as the selected white pill on another page.
        page.wait_for_timeout(350)
        state = nav.evaluate('''(e)=>({bg:getComputedStyle(e).backgroundColor, fg:getComputedStyle(e).color, selected:e.classList.contains('MuiButton-textPrimary'),hover:e.matches(':hover')})''')
        assert not state['selected'] and state['hover'], (width, state)
        assert state['bg'] != 'rgba(255, 255, 255, 0.9)', (width, state)
        assert state['fg'] != 'rgb(0, 0, 0)', (width, state)
        assert drawer.get_attribute('aria-current') == 'false'
        assert not drawer.evaluate('(e)=>e.classList.contains("Mui-selected")')
        page.mouse.move(0, 300)
        page.wait_for_timeout(350)
        assert nav.evaluate('(e)=>getComputedStyle(e).backgroundColor') == favorite.evaluate('(e)=>getComputedStyle(e).backgroundColor')
        page.close()
    browser.close()
print('Fin pill single-line, state and mobile drawer: pass')
