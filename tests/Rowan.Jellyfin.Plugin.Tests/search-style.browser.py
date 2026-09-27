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
          {Items:[{TmdbId:9,MediaType:'movie',Title:'Seerr title',PosterPath:'/poster.jpg'}],TotalPages:1}
    }; }''')
    page.wait_for_selector('.threepic-fin-search__cards article')
    assert page.locator('.searchResults').inner_text() == 'Native result'
    page.wait_for_function("getComputedStyle(document.querySelector('.threepic-fin-search__cards')).display === 'grid'", timeout=3500)
    page.wait_for_function("document.querySelector('.threepic-fin-search article')?.classList.contains('threepic-fin-search__card--no-art')", timeout=3500)
    assert page.locator('.threepic-fin-search article img').count() == 0
    assert page.locator('.threepic-fin-search nav').is_hidden()
    box = page.locator('.threepic-fin-search article').bounding_box()
    assert box is not None and box['height'] < 200
    assert page.locator('link[href$="global-search-addon.css"]').count() == 1
    page.evaluate('''() => { window.oldSection = document.querySelector('.threepic-fin-search');
        document.querySelector('#searchPage').innerHTML = '<input id="searchTextInput" value="Alien"><div class="searchResults">Native rerendered</div>'; }''')
    page.wait_for_function("document.querySelector('.threepic-fin-search') && document.querySelector('.threepic-fin-search') !== oldSection", timeout=3500)
    assert page.locator('.searchResults').inner_text() == 'Native rerendered'
    assert page.locator('.threepic-fin-search article').count() == 1
    browser.close()
