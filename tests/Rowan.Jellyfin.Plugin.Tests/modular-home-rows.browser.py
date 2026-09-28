"""Generated supplementary Home rows with real DOM at phone and desktop widths."""
from pathlib import Path
from tempfile import gettempdir
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
id_a = '0123456789abcdef0123456789abcdef'
id_b = 'abcdef0123456789abcdef0123456789'
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True)
    for width in (390, 1280, 1920):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.route('**/jellyfin/Items/**/Images/**', lambda route: route.fulfill(content_type='image/svg+xml', body='<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect width="100" height="100" fill="#58729a"/></svg>' if '/Primary' in route.request.url else '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="#58729a"/></svg>'))
        page.set_content('<html><head><base href="https://fixture.invalid/"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#101622;color:white"><main id="home"></main></body></html>')
        # Minimal stock card geometry for this offline renderer fixture; the
        # separate captured-theme fixture checks native hover and exact width.
        page.add_style_tag(content='''.overflowPortraitCard { width: 12vw; } .overflowBackdropCard { width: 26vw; }
            .cardScalable { position: relative; } .cardPadder-overflowPortrait { padding-bottom: 150%; }
            .cardPadder-overflowBackdrop { padding-bottom: 56.25%; }
            .cardContent { position: absolute; inset: 0; } .cardFooter { padding: .3em; }
            @media(max-width:600px) { .overflowPortraitCard { width: 38vw; } .overflowBackdropCard { width: 68vw; } }''')
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
        assert page.locator('#home > .rowan-native-row > h2').all_inner_texts() == ['Latest Movies']
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
        cards.nth(0).locator('img').evaluate('(img) => img.decode()')
        portrait.locator('img').evaluate('(img) => img.decode()')
        for card, ratio in ((cards.nth(0), 16 / 9), (portrait, 2 / 3)):
            image = card.locator('img')
            box = image.bounding_box()
            card_box = card.bounding_box()
            assert box is not None and card_box is not None
            assert abs(box['width'] / box['height'] - ratio) < .02, (width, box, ratio)
            assert image.evaluate('(img) => getComputedStyle(img).objectFit') == 'cover'
            assert card.evaluate('(el) => getComputedStyle(el).backgroundColor') == 'rgba(0, 0, 0, 0)'
            scalable_box = card.locator('.cardScalable').bounding_box()
            assert scalable_box and abs(box['width'] - scalable_box['width']) < 2
        assert portrait.locator('.cardText').inner_text() == 'Poster film'
        no_art = seeds.nth(1).locator('.rowan-native-row__card--no-art')
        assert no_art.count() == 1
        assert no_art.locator('.cardText').inner_text() == 'Third film'
        assert no_art.get_attribute('aria-label') is None
        assert no_art.inner_text() == 'Third film'
        controls = seeds.nth(0).locator('.rowan-native-row__arrow')
        assert controls.count() == 2
        assert controls.nth(0).get_attribute('aria-label').startswith('Previous')
        heading_box = seeds.nth(0).locator('h3').bounding_box()
        control_box = controls.nth(0).bounding_box()
        assert heading_box
        if width == 390:
            assert control_box and abs(control_box['y'] - heading_box['y']) < 12, (heading_box, control_box)
        assert page.locator('.rowan-native-row__seed').nth(1).locator('.rowan-native-row__controls').is_hidden()
        assert page.locator('.rowan-native-row__items').first.evaluate('(el) => el.tabIndex') == 0
        if width == 390:
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
print('Generated modular rows at 390px, 1280px and 1920px: pass')
