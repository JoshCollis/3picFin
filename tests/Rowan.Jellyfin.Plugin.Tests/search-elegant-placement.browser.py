"""Rendered Search placement with optional captured ElegantFin styles and multiple native rows.

Set ELEGANTFIN_CSS_DIR to a private capture directory to exercise the actual
custom theme without redistributing its CSS in this MIT repository.
"""
import base64
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
theme = Path(os.environ['ELEGANTFIN_CSS_DIR']) if os.environ.get('ELEGANTFIN_CSS_DIR') else None
if theme:
    assert all((theme / f'elegant-source-{i}.css').exists() for i in (0, 1))

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (3680, 1280, 390):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        native_rows = ''.join(f'<div class="verticalSection"><h2>{name}</h2><div class="itemsContainer"><div class="card"><div class="cardBox"><div class="cardScalable"><div class="cardPadder cardPadder-portrait"></div><div class="cardContent"><div class="cardImageContainer"></div></div></div><div class="cardText">Native {i}</div></div></div></div></div>' for i,name in enumerate(('Movies','Shows','Episodes','People','Studios')))
        html = f'''<html><head></head><body><main id="searchPage">
          <div class="searchField"><input id="searchTextInput" type="search" value="Alien"></div>
          <div class="searchResults">{native_rows}</div></main></body></html>'''
        page.route('http://localhost:8765/**', lambda route: route.fulfill(status=200, content_type='text/html', body=html))
        page.route('**/global-search-addon.css', lambda route: route.fulfill(status=200, content_type='text/css', body=(web / 'global-search-addon.css').read_text()))
        page.route('https://image.tmdb.org/**', lambda route: route.fulfill(status=200, content_type='image/png', body=base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lXcAAAAASUVORK5CYII=')))
        page.goto('http://localhost:8765/web/index.html#/search?query=Alien')
        page.add_style_tag(content='''#searchPage { padding: 1rem; } .searchField { margin-bottom: 1rem; }
          .searchResults .card, .threepic-fin-search__card { width: 11rem; } .searchResults .cardPadder { aspect-ratio: 2 / 3; }
          .searchResults .cardImageContainer { background: #39536b; } .cardPadder-overflowPortrait { padding-top: 150%; }''')
        if theme:
            for i in (0, 1):
                page.add_style_tag(content=(theme / f'elegant-source-{i}.css').read_text())
        page.add_script_tag(content=(web / 'global-search-addon.js').read_text())
        page.add_script_tag(content=(web / 'search-adapter.js').read_text())
        page.evaluate('''() => { window.ApiClient = {
          getCurrentUserId: () => 'alice', getUrl: p => '/' + p,
          getJSON: async url => url.includes('System/Info/Public') ? {Version:'12.1.0'} :
            {Items:[{TmdbId:9,MediaType:'movie',Title:'Seerr title',PosterPath:'/poster.jpg'},
                    {TmdbId:10,MediaType:'tv',Title:'No poster series'}],TotalPages:1}
        }; }''')
        page.wait_for_selector('.threepic-fin-search__card')
        page.wait_for_function("document.querySelector('.threepic-fin-search__card img')?.naturalWidth > 0")
        assert page.locator('.threepic-fin-search__card--has-art').count() == 1
        poster_box = page.locator('.threepic-fin-search__card--has-art').bounding_box()
        native_card_box = page.locator('.searchResults .card').first.bounding_box()
        assert poster_box and native_card_box
        assert poster_box['width'] > 70 and poster_box['width'] <= 290
        image_box = page.locator('.threepic-fin-search__card img').bounding_box()
        assert image_box and 1.35 < image_box['height'] / image_box['width'] < 1.65
        page.evaluate("document.querySelector('.threepic-fin-search__card img').dispatchEvent(new Event('error'))")
        page.wait_for_function("document.querySelectorAll('.threepic-fin-search__card--no-art').length === 2")
        input_box = page.locator('#searchTextInput').bounding_box()
        section_box = page.locator('.threepic-fin-search').bounding_box()
        native_box = page.locator('.searchResults .verticalSection').first.bounding_box()
        assert input_box and section_box and native_box
        assert section_box['y'] >= input_box['y'] + input_box['height'], (width, input_box, section_box)
        second_native_box = page.locator('.searchResults .verticalSection').nth(1).bounding_box()
        third_native_box = page.locator('.searchResults .verticalSection').nth(2).bounding_box()
        assert second_native_box and third_native_box
        assert native_box['y'] < second_native_box['y'] < section_box['y'] < third_native_box['y'], (width, section_box, native_box, second_native_box, third_native_box)
        assert page.locator('.searchResults .verticalSection').count() == 5
        assert page.locator('.threepic-fin-search__card button').count() == 2
        assert page.locator('.threepic-fin-search nav').is_hidden()
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        no_art = page.locator('.threepic-fin-search__card--no-art').first.bounding_box()
        assert no_art and no_art['height'] < 500 and no_art['width'] <= 290
        page.locator('.threepic-fin-search__card button').first.scroll_into_view_if_needed()
        assert page.evaluate('''() => { const e=document.querySelector('.threepic-fin-search__card button');
          const r=e.getBoundingClientRect(); return document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)===e; }''')
        page.evaluate('''() => { window.oldSection = document.querySelector('.threepic-fin-search');
          document.querySelector('#searchPage').innerHTML = `<div class="searchField"><input id="searchTextInput" value="Alien"></div><div class="searchResults">${document.querySelector('.searchResults').innerHTML}</div>`; }''')
        page.wait_for_function("document.querySelector('.threepic-fin-search') && document.querySelector('.threepic-fin-search') !== oldSection")
        assert page.locator('.searchResults .verticalSection').count() == 5
        assert page.locator('.threepic-fin-search__card button').count() == 2
        page.evaluate("window.__threePicFinSearchAdapter.dispose()")
        assert page.locator('.threepic-fin-search').count() == 0
        assert page.locator('.searchResults .verticalSection').count() == 5
        page.close()
    browser.close()
