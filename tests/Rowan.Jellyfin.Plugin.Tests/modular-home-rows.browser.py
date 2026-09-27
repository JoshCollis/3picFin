"""Generated supplementary Home rows with real DOM at phone and desktop widths."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
id_a = '0123456789abcdef0123456789abcdef'
id_b = 'abcdef0123456789abcdef0123456789'
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True)
    for width in (320, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_content('<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0"><main id="home"></main></body></html>')
        page.add_style_tag(path=str(web / 'native-home-rows.css'))
        page.add_script_tag(path=str(web / 'native-home-rows.js'))
        page.evaluate('''ids => {
            window.observers = [];
            class Observer {
                constructor(callback) { this.callback = callback; observers.push(this); }
                observe() {} disconnect() { this.closed = true; }
                fire(target) { this.callback([{ target, isIntersecting: true }]); }
            }
            window.opened = [];
            window.rows = RowanNativeHomeRows.createRows({ document, IntersectionObserver: Observer,
                enabledRows: ['BecauseYouWatched', 'LatestMovies'],
                openItem: item => opened.push(item.Id) });
            const api = { getUrl: path => '/jellyfin/' + path, getJSON: path => {
                if (path.endsWith('BecauseYouWatched')) return [
                    { Heading: 'Because You Watched One', Items: [
                        { Id: ids[0], Type: 'Movie', Name: 'First film', BackdropImageTags: ['ab'] },
                        { Id: ids[1], Type: 'Movie', Name: 'Second film', BackdropImageTags: ['cd'] }] },
                    { Heading: 'Because You Watched Two', Items: [
                        { Id: ids[0], Type: 'Movie', Name: 'Third film' }] }
                ];
                if (path.endsWith('LatestMovies')) return { Kind: 'LatestMovies', Items: [
                    { Id: ids[1], Type: 'Movie', Name: 'Poster film', ImageTags: { Primary: 'ef' } }
                ] };
                throw Error('Unexpected request: ' + path);
            } };
            if (!rows.mount(document.querySelector('#home'), api, 'alice')) throw Error('Mount failed');
            const sections = document.querySelectorAll('#home > .rowan-native-row');
            observers[0].fire(sections[0]); observers[0].fire(sections[1]);
        }''', [id_a, id_b])
        page.wait_for_function('''() => document.querySelectorAll('.rowan-native-row__seed').length === 2 &&
            document.querySelectorAll('.rowan-native-row__card--portrait').length === 1''')
        seeds = page.locator('.rowan-native-row__seed')
        assert seeds.nth(0).locator('h3').inner_text() == 'Because You Watched One'
        assert seeds.nth(1).locator('h3').inner_text() == 'Because You Watched Two'
        first, second = seeds.nth(0).bounding_box(), seeds.nth(1).bounding_box()
        assert first and second and second['y'] >= first['y'] + first['height'], (width, first, second)
        cards = seeds.nth(0).locator('.rowan-native-row__card--landscape')
        assert cards.count() == 2
        first_card, next_card = cards.nth(0).bounding_box(), cards.nth(1).bounding_box()
        assert first_card and next_card and next_card['x'] > first_card['x']
        portrait = page.locator('.rowan-native-row__card--portrait')
        portrait_box = portrait.bounding_box()
        image_box = portrait.locator('img').bounding_box()
        assert portrait_box and image_box and first_card['width'] > portrait_box['width']
        assert image_box['height'] > image_box['width']
        cards.nth(0).click()
        assert page.evaluate('opened') == [id_a]
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.evaluate('rows.dispose()')
        assert page.locator('#home > .rowan-native-row').count() == 0
        page.close()
    browser.close()
print('Generated modular rows at 320px and 1280px: pass')
