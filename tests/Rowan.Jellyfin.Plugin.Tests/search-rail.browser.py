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
    for width in (2560, 1280, 390):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        rows = ''.join(f'<div class="verticalSection"><h2>{name}</h2><div class="itemsContainer"><div class="card">Native {i}</div></div></div>' for i,name in enumerate(('Movies','Shows','Episodes','People','Studios')))
        html = f'<html><head></head><body><main id="searchPage"><div class="searchField"><input id="searchTextInput" value="Alien"></div><div class="searchResults">{rows}</div></main></body></html>'
        page.route('http://localhost:8765/**', lambda r: r.fulfill(status=200, content_type='text/html', body=html))
        page.route('**/global-search-addon.css', lambda r: r.fulfill(status=200, content_type='text/css', body=(web / 'global-search-addon.css').read_text()))
        page.route('https://image.tmdb.org/**', lambda r: r.fulfill(status=200, content_type='image/png', body=png))
        page.goto('http://localhost:8765/web/index.html#/search?query=Alien')
        page.add_style_tag(content='#searchPage {padding: 1rem} .searchField {margin-bottom:1rem} .searchResults .card {width:11rem;height:16rem;background:#456} .cardPadder-overflowPortrait {padding-top:150%} .threepic-fin-search__card {width:11rem}')
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
          return c.querySelector('.threepic-fin-search__poster-label')?.textContent === c.querySelector('.cardText')?.textContent}''')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        box = page.locator('.threepic-fin-search').bounding_box()
        input_box = page.locator('#searchTextInput').bounding_box()
        assert box and input_box
        first_group = page.locator('.searchResults .verticalSection').first.bounding_box()
        second_group = page.locator('.searchResults .verticalSection').nth(1).bounding_box()
        third_group = page.locator('.searchResults .verticalSection').nth(2).bounding_box()
        assert first_group and second_group and third_group
        assert first_group['y'] < second_group['y'] < box['y'] < third_group['y']
        native_card = page.locator('.searchResults .card').first.bounding_box()
        seerr_card = page.locator('.threepic-fin-search__card').first.bounding_box()
        assert native_card and seerr_card
        assert abs(native_card['x'] - seerr_card['x']) < 20, (width, native_card, seerr_card)
        assert page.evaluate('''() => {let b=document.querySelector('.threepic-fin-search__card button').getBoundingClientRect();
          let p=document.querySelector('.threepic-fin-search__poster').getBoundingClientRect();
          return b.width < p.width * .55 && b.height < p.height * .27}''')
        # Rail navigation exposes all cards without creating a second row.
        page.locator('.threepic-fin-search nav button').last.click()
        page.wait_for_function("document.querySelector('.threepic-fin-search__cards').scrollLeft > 0")
        page.evaluate("document.querySelector('.threepic-fin-search__cards').scrollLeft = 100000")
        page.wait_for_function("document.querySelector('.threepic-fin-search nav button:last-of-type').disabled === false")
        page.locator('.threepic-fin-search nav button').last.click()
        page.wait_for_function("document.querySelector('.threepic-fin-search nav span').textContent === 'Page 2 of 6'")
        assert page.locator('.threepic-fin-search__card button').count() == 20
        assert page.evaluate("document.querySelector('.threepic-fin-search__cards').scrollLeft === 0")
        assert page.evaluate('''() => {const rows=[...document.querySelectorAll('.searchResults .verticalSection')];
          return rows.length === new Set(rows).size && rows.every(row=>row.parentElement.classList.contains('searchResults'));}''')
        if os.environ.get('SEARCH_RAIL_SCREENSHOT_DIR'):
            Path(os.environ['SEARCH_RAIL_SCREENSHOT_DIR']).mkdir(parents=True, exist_ok=True)
            page.screenshot(path=str(Path(os.environ['SEARCH_RAIL_SCREENSHOT_DIR']) / f'{width}-rail.png'))
        page.locator('.searchResults').evaluate('(results) => results.replaceChildren()')
        assert page.evaluate('''() => {const i=document.querySelector('#searchTextInput').getBoundingClientRect();
          const s=document.querySelector('.threepic-fin-search').getBoundingClientRect(); return s.y >= i.bottom && s.y-i.bottom < 120;}''')
        page.evaluate("window.__threePicFinSearchAdapter.dispose()")
        assert page.locator('.threepic-fin-search').count() == 0
        assert page.locator('.searchResults .verticalSection').count() == 0
        page.close()
    browser.close()
