"""Rendered app-shell navigation contract against captured ElegantFin CSS."""
import hashlib
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
# The captured stylesheet is private lab input, never packaged with the plugin.
assert os.environ.get('ELEGANTFIN_CSS'), 'Set ELEGANTFIN_CSS to the captured theme CSS'
theme = Path(os.environ['ELEGANTFIN_CSS'])
assert theme.is_file(), f'Missing captured ElegantFin stylesheet: {theme}'
assert hashlib.sha256(theme.read_bytes()).hexdigest() == '525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841', 'Theme capture changed'

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (390, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 750})
        page.set_content('''<html class="layout-desktop"><head><style>
:root { --ef12-navPillBackground: rgb(32, 32, 38); --ef12-navRadius: 20px;
--ef12-navPaddingInline: 16px; --ef12-navGap: 8px; --textColor: white;
--btnPlayColor: rgb(20, 130, 170); --zeroContrastColor: white;
--hoverTabBackgroundColor: rgb(65, 72, 85); --smallRadius: 8px;
--activeBackgroundColor: rgb(40, 90, 110); --activeTextColor: white; }
.MuiButton-root { display:inline-flex; align-items:center; border:0; cursor:pointer; text-decoration:none; min-height:40px; font-family:Arial; }
.MuiListItemButton-root { display:flex; align-items:center; padding:10px 16px; text-decoration:none; }
.MuiListItemIcon-root { display:inline-flex; width:32px; }
</style></head><body><div class="MuiAppBar-root"><div class="MuiToolbar-root"><button aria-label="Open Menu">Menu</button><div class="MuiStack-root"><a class="MuiButtonBase-root MuiButton-root MuiButton-text MuiButton-textPrimary" href="#/">Home</a><a class="MuiButtonBase-root MuiButton-root MuiButton-text MuiButton-textInherit" href="#/home?tab=1"><span class="MuiButton-icon MuiButton-startIcon"><svg viewBox="0 0 24 24"></svg></span><span class="MuiTypography-root">Favorites</span></a><a class="MuiButtonBase-root MuiButton-root MuiButton-text MuiButton-textInherit" href="#/movies">Movies</a></div></div></div><div class="MuiDrawer-root"><div class="MuiDrawer-paper"><ul><li class="MuiListItem-root"><a class="MuiButtonBase-root MuiListItemButton-root" href="#/"><span class="MuiListItemIcon-root">⌂</span><span class="MuiListItemText-root">Home</span></a></li><li class="MuiListItem-root"><a class="MuiButtonBase-root MuiListItemButton-root" href="#/home?tab=1"><span class="MuiListItemIcon-root">☆</span><span class="MuiListItemText-root">Favorites</span></a></li><li class="MuiListItem-root"><a class="MuiButtonBase-root MuiListItemButton-root" href="#/movies">Movies</a></li></ul></div></div><div class="skinHeader"><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider"><button class="emby-tab-button" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div></div><div id="indexPage"><div id="homeTab" data-index="0"><div class="sections">Home sections</div></div><div id="favoritesTab" data-index="1">Favorites</div></div></div></body></html>''')
        page.evaluate('(mobile) => { document.documentElement.className=mobile ? "layout-mobile" : "layout-desktop"; window.ApiClient={getCurrentUserId:()=>window.user,getUrl:path=>"/jellyfin/"+path,getJSON:async()=>({Version:"12.1.0",DiscoveryEnabled:true})}; window.user="alice"; location.hash="#/movies"; }', width < 600)
        page.add_style_tag(content=theme.read_text())
        page.add_style_tag(content=(web / 'global-fin-nav.css').read_text())
        page.on('pageerror', lambda e: print('PAGE ERROR:', e))
        page.add_script_tag(content=(web / 'global-fin-nav.js').read_text())
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2")
        compare = '''() => {
          const stack=document.querySelector('.MuiStack-root'), drawer=document.querySelector('.MuiDrawer-paper');
          const a=stack.querySelector('a[href="#/home?tab=1"]'), b=stack.querySelector('.threepic-fin-global-nav');
          const d=drawer.querySelector('a[href="#/home?tab=1"]'), f=drawer.querySelector('.threepic-fin-global-nav');
          const style=e=>{const s=getComputedStyle(e);return {background:s.backgroundColor,radius:s.borderRadius,padding:s.padding,font:s.fontFamily,color:s.color,display:s.display}};
          return {toolbar:[style(a),style(b)],drawer:[style(d),style(f)],
            toolbarStructure:[a.querySelector('.MuiButton-startIcon')?.tagName,b.querySelector('.MuiButton-startIcon')?.tagName],
            drawerStructure:[d.querySelector('.MuiListItemIcon-root')?.tagName,f.querySelector('.MuiListItemIcon-root')?.tagName],
            label:b.textContent.trim(),transform:getComputedStyle(b).textTransform,
            selected:b.classList.contains('MuiButton-textPrimary'), aria:b.getAttribute('aria-current')};
        }'''
        for route in ('#/movies', '#/search?query=test', '#/home'):
            page.evaluate('(route) => location.hash=route', route)
            page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2")
            result=page.evaluate(compare)
            assert result['toolbar'][0] == result['toolbar'][1], result
            assert result['drawer'][0] == result['drawer'][1], result
            assert result['toolbarStructure'] == ['SPAN','SPAN'], result
            assert result['drawerStructure'] == ['SPAN','SPAN'], result
            assert result['label'] == '3pic Fin' and result['transform'] == 'none', result
            assert not result['selected'] and result['aria'] == 'false', result
            assert page.locator('.emby-tab-button').count() == 2
        if width == 1280:
            page.locator('.MuiStack-root .threepic-fin-global-nav').hover()
            hover = page.evaluate('''() => {
              const e=document.querySelector('.MuiStack-root .threepic-fin-global-nav');
              const s=getComputedStyle(e); return [s.backgroundColor,s.color];
            }''')
            page.locator('.MuiStack-root a[href="#/home?tab=1"]').hover()
            native_hover = page.evaluate('''() => {
              const e=document.querySelector('.MuiStack-root a[href="#/home?tab=1"]');
              const s=getComputedStyle(e); return [s.backgroundColor,s.color];
            }''')
            assert hover == native_hover, (hover,native_hover)
        page.locator('.MuiStack-root .threepic-fin-global-nav').focus()
        assert page.evaluate('document.activeElement.getAttribute("aria-label")') == '3pic Fin'
        page.locator('.MuiStack-root .threepic-fin-global-nav').click()
        page.wait_for_function("document.querySelector('.MuiStack-root .threepic-fin-global-nav')?.getAttribute('aria-current') === 'page'")
        assert page.evaluate('location.hash') == '#/home?fin=1'
        assert page.locator('.MuiStack-root .threepic-fin-global-nav').evaluate('(e)=>e.classList.contains("MuiButton-textPrimary")')
        assert page.locator('.MuiStack-root .threepic-fin-global-nav').evaluate('(e)=>getComputedStyle(e).backgroundColor') == page.locator('.MuiStack-root a[href="#/"]').evaluate('(e)=>getComputedStyle(e).backgroundColor')
        assert page.locator('.MuiDrawer-paper .threepic-fin-global-nav').evaluate('(e)=>e.classList.contains("Mui-selected")')
        assert page.locator('.MuiDrawer-paper .threepic-fin-global-nav').evaluate('(e)=>getComputedStyle(e).backgroundColor') == page.locator('.MuiDrawer-paper .Mui-selected').evaluate('(e)=>getComputedStyle(e).backgroundColor')
        page.evaluate('location.hash="#/movies"')
        page.locator('.MuiDrawer-paper .threepic-fin-global-nav').click()
        assert page.evaluate('location.hash') == '#/home?fin=1'
        page.go_back(); assert page.evaluate('location.hash') == '#/movies'
        page.go_forward(); assert page.evaluate('location.hash') == '#/home?fin=1'
        page.locator('.MuiStack-root').evaluate('e => e.outerHTML=e.outerHTML')
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2")
        page.locator('.MuiStack-root .threepic-fin-global-nav').click()
        assert page.evaluate('location.hash') == '#/home?fin=1'
        page.evaluate('window.user=null')
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 0", timeout=3000)
        page.evaluate('window.user="bob"')
        page.wait_for_function("document.querySelectorAll('.threepic-fin-global-nav').length === 2", timeout=3000)
        assert page.locator('#favoritesTab').inner_text() == 'Favorites'
        page.evaluate('window.__threePicFinGlobalNav.dispose()')
        assert page.locator('.threepic-fin-global-nav').count() == 0
        page.close()
    browser.close()
print('global Fin navigation ElegantFin mobile/desktop: pass')
