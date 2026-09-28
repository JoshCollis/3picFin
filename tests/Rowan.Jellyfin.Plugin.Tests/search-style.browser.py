"""Search styles must load when ApiClient appears after the injected adapter."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={'width': 1280, 'height': 800})
    page.route('http://localhost:8765/**', lambda r: r.fulfill(status=200, content_type='text/html', body='''
        <html><head></head><body><main id="searchPage"><input id="searchTextInput" value="Alien">
        <div class="searchResults"><div>Native result</div></div></main></body></html>'''))
    page.route('**/global-search-addon.css', lambda r: r.fulfill(status=200, content_type='text/css',
        body=(web / 'global-search-addon.css').read_text()))
    page.route('https://image.tmdb.org/**', lambda r: r.abort())
    page.goto('http://localhost:8765/web/index.html#/search?query=Alien')
    page.add_script_tag(content=(web / 'global-search-addon.js').read_text())
    page.add_script_tag(content=(web / 'search-adapter.js').read_text())
    page.evaluate('''() => { window.ApiClient = {
        getCurrentUserId: () => 'alice', getUrl: p => '/' + p,
        getJSON: async url => url.includes('System/Info/Public') ? {Version:'12.1.0'} :
          {Items:[{TmdbId:9,MediaType:'movie',Title:'Seerr title',PosterPath:'/poster.jpg'},
                  {TmdbId:10,MediaType:'tv',Title:'No poster series'}],TotalPages:1}
    }; }''')
    page.wait_for_selector('.threepic-fin-search__cards article')
    assert page.locator('.searchResults').inner_text() == 'Native result'
    page.wait_for_function("getComputedStyle(document.querySelector('.threepic-fin-search__cards')).display === 'grid'", timeout=3500)
    page.wait_for_function("document.querySelectorAll('.threepic-fin-search__card--no-art').length === 2", timeout=3500)
    assert page.locator('.threepic-fin-search article img').count() == 0
    assert page.locator('.threepic-fin-search nav').is_hidden()
    boxes = [card.bounding_box() for card in page.locator('.threepic-fin-search article').all()]
    assert all(box and box['height'] < 100 and box['width'] <= 520 for box in boxes)
    assert page.locator('.threepic-fin-search article').nth(1).inner_text().startswith('No poster series')
    assert page.locator('.threepic-fin-search article button').count() == 2
    assert page.locator('link[href$="global-search-addon.css"]').count() == 1
    page.set_viewport_size({'width': 390, 'height': 800})
    mobile_boxes = [card.bounding_box() for card in page.locator('.threepic-fin-search article').all()]
    assert all(box and box['height'] < 100 and box['width'] <= 390 for box in mobile_boxes)
    assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    page.evaluate('''() => { window.oldSection = document.querySelector('.threepic-fin-search');
        document.querySelector('#searchPage').innerHTML = '<input id="searchTextInput" value="Alien"><div class="searchResults">Native rerendered</div>'; }''')
    page.wait_for_function("document.querySelector('.threepic-fin-search') && document.querySelector('.threepic-fin-search') !== oldSection", timeout=3500)
    assert page.locator('.searchResults').inner_text() == 'Native rerendered'
    assert page.locator('.threepic-fin-search article').count() == 2
    browser.close()
