"""Real generated fragment: catalog detail behavior and responsive keyboard flow."""
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
fragment = (web / 'discovery.html').read_text()
css = (web / 'discovery.css').read_text()
script = (web / 'discovery.js').read_text()

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (320, 390, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_default_timeout(5000)
        page.set_content('<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0">' + fragment + '</body></html>')
        page.add_style_tag(content=css)
        page.add_script_tag(content=script)
        page.evaluate("""() => {
          window.calls = []; window.opened = []; window.pending = null;
          const movie = {Title:'Film', MediaType:'movie', TmdbId:9, PosterPath:'/film.jpg'};
          const tv = {Title:'Series', MediaType:'tv', TmdbId:7};
          const api = {
            getUrl: (route, params) => '/jellyfin/' + route + (params ? '?' + new URLSearchParams(params) : ''),
            getJSON: (url, opts) => {
              calls.push([url, opts]);
              if (url.includes('TitleDetails')) {
                if (window.pending) return window.pending;
                const isTv = url.includes('mediaType=tv');
                return Promise.resolve({ Title: isTv ? 'Series' : 'Film', Overview:'An honest overview', PosterPath:'/film.jpg', MediaType:isTv?'tv':'movie', TmdbId:isTv?7:9, Date:'2026-09-27', MediaStatus:window.detailStatus ?? 1, LibraryItemId:window.libraryId ?? null, CanRequest:window.canRequest ?? true, CanRequest4k:false, Seasons:isTv?[1,2]:[] });
              }
              if (url.includes('RequestOptions')) return Promise.resolve({CanRequest:true, CanRequest4k:false, Seasons:[1,2], MediaStatus:1});
              if (url.includes('Search')) return Promise.resolve({Items:[tv]});
              return Promise.resolve({Movies:{Items:[movie]}, Tv:{Items:[tv]}, Requests:{Items:[]}});
            }
          };
          window.dispose = ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'), api, {openItem: item => { opened.push(item); return Promise.resolve(true); }});
        }""")
        card = page.locator('#threepic-fin-movies .threepic-fin-discovery__card')
        expect(card).to_have_count(1)
        assert page.evaluate("calls.filter(c => c[0].includes('TitleDetails')).length") == 0
        title = card.get_by_role('button', name='Film', exact=True)
        title.focus()
        expect(title).to_be_focused()
        # Dispatch on the keyboard, not locator.press: showModal transfers focus
        # during the click default action, which can time out locator.press.
        page.keyboard.press('Enter')
        modal = page.locator('#threepic-fin-details-dialog')
        expect(modal).to_be_visible()
        expect(modal).to_contain_text('An honest overview')
        assert page.evaluate("calls.filter(c => c[0].includes('TitleDetails')).length") == 1
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.keyboard.press('Escape')
        expect(modal).not_to_be_visible()
        expect(card.get_by_role('button', name='Film', exact=True)).to_be_focused()
        page.locator('#threepic-fin-tv .threepic-fin-discovery__card').get_by_role('button', name='Series', exact=True).click()
        expect(modal).to_contain_text('Series')
        expect(modal).to_contain_text('Season 1')
        page.locator('#threepic-fin-details-request').click()
        expect(page.locator('#threepic-fin-request-dialog')).to_be_visible()
        expect(page.locator('#threepic-fin-request-seasons')).to_be_visible()
        page.locator('#threepic-fin-request-cancel').click()
        page.evaluate('window.libraryId = "01234567-89ab-cdef-0123-456789abcdef"')
        page.locator('#threepic-fin-tv .threepic-fin-discovery__card').get_by_role('button', name='Series', exact=True).click()
        expect(page.locator('#threepic-fin-details-open')).to_be_visible()
        expect(page.locator('#threepic-fin-details-request')).to_be_visible()
        page.locator('#threepic-fin-details-close').click()
        card.get_by_role('button', name='Film', exact=True).click()
        expect(page.locator('#threepic-fin-details-open')).to_be_visible()
        expect(page.locator('#threepic-fin-details-request')).to_be_visible()
        page.locator('#threepic-fin-details-open').click()
        assert page.evaluate('opened') == [{'mediaType': 'movie', 'mediaId': 9, 'libraryItemId': '01234567-89ab-cdef-0123-456789abcdef'}]
        page.evaluate('window.libraryId = null; window.detailStatus = 6')
        card.get_by_role('button', name='Film', exact=True).click()
        expect(modal).to_contain_text('Blocklisted')
        assert not page.locator('#threepic-fin-details-request').is_visible()
        page.locator('#threepic-fin-details-close').click()
        page.evaluate('window.detailStatus = 3; window.canRequest = false')
        card.get_by_role('button', name='Film', exact=True).click()
        expect(modal).to_contain_text('Requested')
        assert not page.locator('#threepic-fin-details-request').is_visible()
        page.locator('#threepic-fin-details-close').click()
        page.locator('#threepic-fin-search').fill('series')
        page.locator('#threepic-fin-search-form').get_by_role('button', name='Search').click()
        page.locator('#threepic-fin-search-results .threepic-fin-discovery__card').get_by_role('button', name='Series', exact=True).click()
        expect(modal).to_contain_text('Series')
        page.locator('#threepic-fin-details-close').click()
        page.locator('#threepic-fin-recommendations .threepic-fin-discovery__card').first.get_by_role('button', name='Film', exact=True).click()
        expect(modal).to_contain_text('Film')
        page.locator('#threepic-fin-details-close').click()
        page.evaluate("""() => { window.pending = new Promise(resolve => window.resolveOld = resolve); }""")
        before = page.evaluate("calls.filter(c => c[0].includes('TitleDetails')).length")
        card.get_by_role('button', name='Film', exact=True).dblclick()
        assert page.evaluate("calls.filter(c => c[0].includes('TitleDetails')).length") == before + 1
        page.locator('#threepic-fin-details-close').click()
        assert page.evaluate("calls.filter(c => c[0].includes('TitleDetails')).at(-1)[1].signal.aborted")
        page.evaluate("resolveOld({Title:'Private old user', MediaType:'movie', TmdbId:9})")
        assert 'Private old user' not in page.locator('body').inner_text()
        page.evaluate("() => { window.pending = new Promise(resolve => window.resolveOld = resolve); }")
        card.get_by_role('button', name='Film', exact=True).click()
        expect(modal).to_be_visible()
        page.evaluate('dispose()')
        assert page.evaluate("calls.filter(c => c[0].includes('TitleDetails')).at(-1)[1].signal.aborted")
        page.evaluate('window.pending = null; window.libraryId = "0123456789abcdef0123456789abcdef"; window.canRequest = true')
        page.evaluate("""() => {
          window.dispose = ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'), {
            getUrl: (route, params) => '/jellyfin/' + route + (params ? '?' + new URLSearchParams(params) : ''),
            getJSON: url => url.includes('TitleDetails') ? Promise.resolve({Title:'Film', MediaType:'movie', TmdbId:9, LibraryItemId:'0123456789abcdef0123456789abcdef', MediaStatus:5, CanRequest:true, CanRequest4k:false, Seasons:[]}) : Promise.resolve({Movies:{Items:[{Title:'Film', MediaType:'movie', TmdbId:9}]}, Tv:{Items:[]}, Requests:{Items:[]}})
          });
        }""")
        page.evaluate("resolveOld({Title:'Private old user', MediaType:'movie', TmdbId:9})")
        assert 'Private old user' not in page.locator('body').inner_text()
        card.get_by_role('button', name='Film', exact=True).click()
        expect(modal).to_contain_text('Available in your library')
        assert not page.locator('#threepic-fin-details-open').is_visible()
        expect(page.locator('#threepic-fin-details-request')).to_be_visible()
        page.locator('#threepic-fin-details-close').click()
        page.evaluate('dispose()')
        assert not modal.is_visible()
        assert 'Private old user' not in page.locator('body').inner_text()
        page.close()
    browser.close()
print('Details fragment browser smoke: 320px, 390px and 1280px pass')
