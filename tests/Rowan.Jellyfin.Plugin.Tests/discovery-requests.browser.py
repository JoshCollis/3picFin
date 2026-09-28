"""Renderer-generated request cards at phone and desktop sizes; no live server writes."""
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
fragment = (web / 'discovery.html').read_text()
css = (web / 'discovery.css').read_text()
script = (web / 'discovery.js').read_text()
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (320, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.route('https://image.tmdb.org/**', lambda route: route.fulfill(status=200, content_type='image/svg+xml', body='<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="blue"/></svg>'))
        page.set_content('<meta name="viewport" content="width=device-width,initial-scale=1">' + fragment)
        page.add_style_tag(content=css)
        page.add_script_tag(content=script)
        page.evaluate("""() => {
            window.calls = [];
            const api = {
              getUrl: (route, params) => '/jellyfin/' + route + (params ? '?' + new URLSearchParams(params) : ''),
              getJSON: url => {
                calls.push(url);
                if (url.includes('TitleDetails')) return Promise.resolve(url.includes('mediaType=tv')
                  ? {MediaType:'tv', TmdbId:12, Title:'Real Series', PosterPath:'/series.jpg'}
                  : {MediaType:'movie', TmdbId:11, Title:'Real Film', PosterPath:'/film.jpg'});
                if (url.includes('SharedRequests')) return Promise.resolve({Items:[{Id:3,Status:2,Type:'tv',TmdbId:12}]});
                return Promise.resolve({Movies:{Items:[]},Tv:{Items:[]},Requests:{Items:[
                  {Id:1,Status:1,Type:'movie',MediaType:'movie',TmdbId:11,Title:null,PosterPath:null},
                  {Id:2,Status:2,Type:'tv',MediaType:'tv',TmdbId:12,Title:null,PosterPath:null}]}});
              }
            };
            window.dispose = ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'), api);
        }""")
        cards = page.locator('#threepic-fin-requests .threepic-fin-discovery__card')
        expect(cards).to_have_count(2)
        expect(cards.first).to_contain_text('Real Film')
        expect(cards.nth(1)).to_contain_text('Real Series')
        expect(cards.first).to_contain_text('Pending')
        expect(cards.nth(1)).to_contain_text('Approved')
        assert page.evaluate("calls.filter(c => c.includes('TitleDetails')).length") == 2
        for card in (cards.first, cards.nth(1)):
            poster = card.locator('img')
            expect(poster).to_have_count(1)
            box = poster.bounding_box()
            assert box and abs(box['width'] / box['height'] - 2 / 3) < .02, (box, poster.evaluate("e => ({aspect: getComputedStyle(e).aspectRatio, width: getComputedStyle(e).width, height: getComputedStyle(e).height, parent: e.parentElement.outerHTML.slice(0,200)})"))
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        shared = page.locator('#threepic-fin-shared-requests .threepic-fin-discovery__card')
        expect(shared).to_contain_text('Real Series')
        assert page.get_by_role('region', name='All Requests').is_visible()
        assert page.get_by_role('button', name='Show household requests').count() == 0
        assert page.evaluate("calls.filter(c => c.includes('TitleDetails')).length") == 2
        page.evaluate('dispose()')
        page.close()
    browser.close()
print('Request portrait cards: 320px and 1280px pass')
