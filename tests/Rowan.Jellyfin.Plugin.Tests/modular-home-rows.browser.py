"""Generated supplementary Home rows with real DOM at phone and desktop widths."""
from pathlib import Path
from tempfile import gettempdir
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
id_a = '0123456789abcdef0123456789abcdef'
id_b = 'abcdef0123456789abcdef0123456789'
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True)
    for width in (320, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.route('**/jellyfin/Items/**/Images/**', lambda route: route.fulfill(content_type='image/svg+xml', body='<svg xmlns="http://www.w3.org/2000/svg" width="480" height="270"><rect width="480" height="270" fill="#58729a"/></svg>'))
        page.set_content('<html><head><base href="https://fixture.invalid/"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#101622;color:white"><main id="home"></main></body></html>')
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
        controls = seeds.nth(0).locator('.rowan-native-row__arrow')
        assert controls.count() == 2
        assert controls.nth(0).get_attribute('aria-label').startswith('Previous')
        heading_box = seeds.nth(0).locator('h3').bounding_box()
        control_box = controls.nth(0).bounding_box()
        assert heading_box
        if width == 320:
            assert control_box and abs(control_box['y'] - heading_box['y']) < 12, (heading_box, control_box)
        assert page.locator('.rowan-native-row__seed').nth(1).locator('.rowan-native-row__controls').is_hidden()
        assert page.locator('.rowan-native-row__items').first.evaluate('(el) => el.tabIndex') == 0
        if width == 320:
            assert first_card['width'] > 200 and first_card['width'] < 300, first_card
            assert page.locator('.rowan-native-row__items').first.evaluate('(el) => el.scrollWidth > el.clientWidth')
            controls.nth(1).click()
            page.wait_for_function('document.querySelector(".rowan-native-row__items").scrollLeft > 0')
            page.locator('.rowan-native-row__items').first.focus()
            page.keyboard.press('ArrowLeft')
            page.wait_for_function('document.querySelector(".rowan-native-row__items").scrollLeft === 0')
        page.screenshot(path=str(Path(gettempdir()) / f'native-home-rows-{width}.png'), full_page=True)
        cards.nth(0).click()
        assert page.evaluate('opened') == [id_a]
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.evaluate('rows.dispose()')
        assert page.locator('#home > .rowan-native-row').count() == 0
        page.close()
    browser.close()
print('Generated modular rows at 320px and 1280px: pass')
