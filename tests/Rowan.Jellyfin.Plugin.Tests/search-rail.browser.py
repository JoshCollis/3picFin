"""Rail layout and interaction under optionally captured ElegantFin CSS.

ELEGANTFIN_CSS_DIR points at local elegant-source-0.css / -1.css captures;
no third-party stylesheet is redistributed.
"""
import base64
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
theme = Path(os.environ['ELEGANTFIN_CSS_DIR']) if os.environ.get('ELEGANTFIN_CSS_DIR') else None
png = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lXcAAAAASUVORK5CYII=')
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (3680, 1280, 390):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        rows = ''.join(f'<div class="verticalSection"><h2>Native row {i}</h2><div class="itemsContainer"><div class="card">Native {i}</div></div></div>' for i in range(5))
        html = f'<html><head></head><body><main id="searchPage"><div class="searchField"><input id="searchTextInput" value="Alien"></div><div class="searchResults">{rows}</div></main></body></html>'
        page.route('http://localhost:8765/**', lambda r: r.fulfill(status=200, content_type='text/html', body=html))
        page.route('**/global-search-addon.css', lambda r: r.fulfill(status=200, content_type='text/css', body=(web / 'global-search-addon.css').read_text()))
        page.route('https://image.tmdb.org/**', lambda r: r.fulfill(status=200, content_type='image/png', body=png))
        page.goto('http://localhost:8765/web/index.html#/search?query=Alien')
        page.add_style_tag(content='#searchPage {padding: 1rem} .searchField {margin-bottom:1rem} .searchResults .card {width:11rem;height:16rem;background:#456}')
        if theme:
            for i in (0, 1):
                page.add_style_tag(content=(theme / f'elegant-source-{i}.css').read_text())
        page.add_script_tag(content=(web / 'global-search-addon.js').read_text())
        page.add_script_tag(content=(web / 'search-adapter.js').read_text())
        page.evaluate('''() => { window.ApiClient={getCurrentUserId:()=> 'alice',getUrl:p=>'/'+p,
          getJSON:async url=>url.includes('System/Info/Public')?{Version:'12.1.0'}:
          {Items:Array.from({length:20},(_,i)=>({TmdbId:i+1,MediaType:'movie',Title:'Title '+i,
            PosterPath:i===1?null:'/poster.jpg'})),TotalPages:6}} }''')
        page.wait_for_selector('.threepic-fin-search__card:nth-child(20)')
        page.wait_for_function("document.querySelector('.threepic-fin-search__card img')?.naturalWidth > 0")
        assert page.locator('.threepic-fin-search h2').inner_text() == 'Available to request'
        assert page.locator('.searchResults .verticalSection').count() == 5
        assert page.locator('.threepic-fin-search__card').count() == 20
        assert page.locator('.threepic-fin-search__card button').count() == 20
        assert page.evaluate('''() => {const e=document.querySelector('.threepic-fin-search__cards');
          return getComputedStyle(e).overflowX === 'auto' && e.scrollWidth > e.clientWidth &&
          [...e.children].every(c=>Math.abs(c.offsetTop-e.children[0].offsetTop)<2)}'''), width
        assert page.evaluate('''() => {const c=document.querySelector('.threepic-fin-search__card--has-art');
          const b=c.querySelector('button').getBoundingClientRect(), i=c.querySelector('img').getBoundingClientRect();
          return b.left>=i.left && b.right<=i.right && b.top>=i.top && b.bottom<=i.bottom}''')
        assert page.evaluate('''() => {const a=document.querySelector('.threepic-fin-search__card--has-art').getBoundingClientRect(),
          b=document.querySelector('.threepic-fin-search__card--no-art').getBoundingClientRect();
          return Math.abs(a.width-b.width)<2 && Math.abs(a.height-b.height)<15}''')
        assert page.evaluate('''() => {const c=document.querySelector('.threepic-fin-search__card--no-art');
          return c.querySelector('.threepic-fin-search__poster-label')?.textContent === c.querySelector('h3')?.textContent}''')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        box = page.locator('.threepic-fin-search').bounding_box()
        input_box = page.locator('#searchTextInput').bounding_box()
        native_box = page.locator('.searchResults').bounding_box()
        assert box and input_box and native_box
        assert input_box['y']+input_box['height'] <= box['y'] < native_box['y']
        assert box['y']-(input_box['y']+input_box['height']) < 120
        # Rail navigation exposes all cards without creating a second row.
        page.locator('.threepic-fin-search nav button').last.click()
        page.wait_for_function("document.querySelector('.threepic-fin-search__cards').scrollLeft > 0")
        page.evaluate("document.querySelector('.threepic-fin-search__cards').scrollLeft = 100000")
        page.wait_for_function("document.querySelector('.threepic-fin-search nav button:last-of-type').disabled === false")
        page.locator('.threepic-fin-search nav button').last.click()
        page.wait_for_function("document.querySelector('.threepic-fin-search nav span').textContent === 'Page 2 of 6'")
        assert page.locator('.threepic-fin-search__card button').count() == 20
        assert page.evaluate("document.querySelector('.threepic-fin-search__cards').scrollLeft === 0")
        if os.environ.get('SEARCH_RAIL_SCREENSHOT_DIR'):
            Path(os.environ['SEARCH_RAIL_SCREENSHOT_DIR']).mkdir(parents=True, exist_ok=True)
            page.screenshot(path=str(Path(os.environ['SEARCH_RAIL_SCREENSHOT_DIR']) / f'{width}-rail.png'))
        page.evaluate("window.__threePicFinSearchAdapter.dispose()")
        assert page.locator('.threepic-fin-search').count() == 0
        assert page.locator('.searchResults .verticalSection').count() == 5
        page.close()
    browser.close()
