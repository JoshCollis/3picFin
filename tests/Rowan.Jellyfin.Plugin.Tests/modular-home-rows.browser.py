"""Exercise the real native Home renderer at narrow and wide viewports."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True)
    for width in (320, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_content('<html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0"><main id="home"></main></body></html>')
        page.add_style_tag(path=str(web / 'native-home-rows.css'))
        page.add_script_tag(path=str(web / 'native-home-rows.js'))
        page.evaluate('''() => {
            window.observers = [];
            class Observer {
                constructor(callback) { this.callback = callback; window.observers.push(this); }
                observe() {}
                disconnect() { this.closed = true; }
                fire(target) { this.callback([{ target, isIntersecting: true }]); }
            }
            window.opened = [];
            window.rows = RowanNativeHomeRows.createRows({
                document, IntersectionObserver: Observer,
                enabledRows: ['BecauseYouWatched', 'DiscoverMovies'],
                openItem: item => window.opened.push(item.Id)
            });
            const api = {
                getUrl: path => path,
                getJSON: path => {
                    if (path === 'Rowan/Home/BecauseYouWatched') return [
                        { SeedId: 'one', Seed: { Id: 'one' }, Heading: 'Because You Watched One', Items: [
                            { Id: 'a', Name: 'First film' }, { Id: 'b', Name: 'Second film' }] },
                        { SeedId: 'two', Seed: { Id: 'two' }, Heading: 'Because You Watched Two', Items: [
                            { Id: 'c', Name: 'Third film' }] }
                    ];
                    if (path === '3picFin/HomeDiscover/DiscoverMovies') return { Items: [
                        { TmdbId: 7, MediaType: 'movie', Title: 'Candidate', PosterPath: '/poster.jpg' }
                    ] };
                    throw Error('Unexpected request: ' + path);
                }
            };
            if (!window.rows.mount(document.querySelector('#home'), api, 'alice')) throw Error('Mount failed');
            const sections = document.querySelectorAll('#home > .rowan-native-row');
            window.observers[0].fire(sections[5]);
            window.observers[0].fire(sections[6]);
        }''')
        page.wait_for_function('''() => document.querySelectorAll('.rowan-native-row__seed').length === 2 &&
            document.querySelectorAll('.rowan-native-row__card--portrait').length === 1''')
        seeds = page.locator('.rowan-native-row__seed')
        assert seeds.count() == 2
        assert seeds.nth(0).locator('h2').inner_text() == 'Because You Watched One'
        assert seeds.nth(1).locator('h2').inner_text() == 'Because You Watched Two'
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
        cards.nth(0).focus()
        assert cards.nth(0).evaluate('el => el === document.activeElement')
        cards.nth(0).click()
        assert page.evaluate('window.opened') == ['a']
        label = page.locator('.rowan-native-row__card--portrait span').inner_text()
        assert label == 'Candidate · Seerr candidate', label
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.evaluate('window.rows.dispose()')
        assert page.locator('#home > .rowan-native-row').count() == 0
        page.close()
    browser.close()
print('Generated modular rows at 320px and 1280px: pass')
