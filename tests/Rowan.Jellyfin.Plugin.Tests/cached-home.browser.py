"""Legacy router caches an inactive Home alongside the newly rendered Home.

Reproduces renderer-observed 12.3 query navigation without household data.
Only a single visible page may mount; two visible pages must fail closed.
"""
from pathlib import Path
from urllib.parse import urlsplit
from playwright.sync_api import sync_playwright, expect

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page()
    page.route('https://fixture.invalid/**', lambda r: r.fulfill(body=(web / urlsplit(r.request.url).path.split('/')[-1]).read_text(), content_type='text/javascript' if r.request.url.endswith('.js') else 'text/html'))
    page.set_content('''<base href="https://fixture.invalid/web/index.html"><div style="display:none"><div id="indexPage"><div id="homeTab" data-index="0"><div class="sections">Cached</div></div><div id="favoritesTab" data-index="1"></div></div></div><div id="indexPage" class="current"><div id="homeTab" data-index="0"><div class="sections">Current</div></div><div id="favoritesTab" data-index="1"></div></div>''')
    page.evaluate('''() => {
        window.user='synthetic'; location.hash='#/home?fin=1';
        performance.getEntriesByType=()=>[{initiatorType:'script',name:'https://fixture.invalid/web/hometab.123abc.chunk.js'}];
        window.__threePicFinGlobalNav={};
        window.ApiClient={getCurrentUserId:()=>user,getUrl:p=>'https://fixture.invalid/'+p,
            getJSON:async url=>url.includes('System/Info') ? {Version:'12.3.0'} : url.includes('Home/Mode') ? {DiscoveryEnabled:true} :
                {Movies:{Items:[{Title:'Populated Fin',MediaType:'movie',TmdbId:9}]},Tv:{Items:[]},Requests:{Items:[]}}};
    }''')
    page.add_script_tag(path=str(web / 'home-adapter.js'))
    panel = page.locator('.current .threepic-fin-host__panel')
    expect(panel).to_be_visible()
    expect(panel).to_contain_text('Populated Fin')
    assert page.locator('#indexPage:not(.current) .threepic-fin-host__panel').count() == 0
    # Back restores a cached view by attributes + native lifecycle, without a
    # child-list mutation. Hashchange can arrive while the view is still hidden.
    page.evaluate('document.querySelector(".current").style.display="none";location.hash="#/movies"')
    expect(panel).to_have_count(0)
    page.evaluate('location.hash="#/home?fin=1"')
    page.wait_for_timeout(50)  # allow the hidden-pane hash refresh to finish
    page.evaluate('document.querySelector(".current").style.display="";document.querySelector(".current").dispatchEvent(new CustomEvent("pageshow",{bubbles:true}))')
    expect(panel).to_be_visible()
    page.evaluate('document.querySelector("#indexPage:not(.current)").parentElement.style.display="block";dispatchEvent(new Event("resize"))')
    expect(panel).to_have_count(0)  # two visible pages are genuinely ambiguous
    page.evaluate('__threePicFinHomeAdapter.dispose()')
    browser.close()
print('cached native Home identity/visibility contract PASS')
