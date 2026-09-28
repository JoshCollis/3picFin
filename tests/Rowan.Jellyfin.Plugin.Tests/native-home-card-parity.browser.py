"""Compare renderer cards with a native-shape reference under captured theme CSS.

Set FIN_THEME_CSS_DIR to a private directory containing 0.css (ElegantFin
lscambo13 main), 1.css (mihaif7 elegantfin-jf12 main), 2.css (the
served main.jellyfin.62ea10d695d4592fccae.css), 3.css (the served
home.193c6fa7a64e52078d35.css). Hashes below pin the captured bytes.
No theme source is redistributed by this fixture; this is an offline
native-shape reference, not a signed-in stock/client DOM acceptance test.
"""
import os
import hashlib
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
theme = Path(os.environ['FIN_THEME_CSS_DIR'])
expected = {
    '0.css': '779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643',
    '1.css': '525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841',
    '2.css': '13df0f9bcd9aee577b141ba242fc8f0e74c95dbce2881abc3155de44564ee143',
    '3.css': 'c304b43eb5143e332d99d975ada4e1446b5f9693ecdf10298df5a4b9bbfbe42c',
}
for name, digest in expected.items():
    assert hashlib.sha256((theme / name).read_bytes()).hexdigest() == digest, name
item = '0123456789abcdef0123456789abcdef'
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width, layout in ((390, 'mobile'), (1280, 'desktop'), (1920, 'desktop')):
        page = browser.new_page(viewport={'width': width, 'height': 800}, reduced_motion='reduce')
        page.route('**/jellyfin/Items/**/Images/**', lambda r: r.fulfill(content_type='image/svg+xml', body='<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect width="100" height="100" fill="#5889ab"/><rect x="20" width="25" height="100" fill="#ecb23a"/></svg>'))
        page.set_content(f'<html class="layout-{layout}"><head><base href="https://fixture.invalid/"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#101622;color:white"><main id="home"><div class="sections"><div class="itemsContainer"><div class="card overflowPortraitCard card-hoverable show-animation" id="native" tabindex="0"><div class="cardBox visualCardBox"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait"></div><div class="cardContent cardImageContainer"><img src="/jellyfin/Items/{item}/Images/Primary" alt=""></div></div><div class="cardFooter"><div class="cardText">Native film</div></div></div></div></div><div id="plugin" class="rowan-native-rows"></div></main></body></html>')
        for name in ('2.css', '3.css', '0.css', '1.css'):
            page.add_style_tag(path=str(theme / name))
        page.add_style_tag(content='#native .cardContent img { display:block;width:100%;height:100%;object-fit:cover; }')
        page.add_style_tag(path=str(web / 'native-home-rows.css'))
        page.add_script_tag(path=str(web / 'native-home-rows.js'))
        page.evaluate('''id => {
            window.opened = []; window.observers = [];
            class Observer { constructor(callback) { this.callback = callback; observers.push(this); }
                observe() {} disconnect() {} fire(target) { this.callback([{ target, isIntersecting: true }]); } }
            window.rows = RowanNativeHomeRows.createRows({ document, IntersectionObserver: Observer,
                enabledRows: ['LatestMovies', 'ContinueWatching'], openItem: item => opened.push(item.Id) });
            const api = { getCurrentUserId: () => window.user, getUrl: path => '/jellyfin/' + path,
                getJSON: url => ({ Kind: url.endsWith('LatestMovies') ? 'LatestMovies' : 'ContinueWatching',
                    Items: [{ Id: id, Type: 'Movie', Name: 'Plugin film', ImageTags: { Primary: 'ab' } }] }) };
            window.user = 'alice'; rows.mount(document.querySelector('#plugin'), api, 'alice');
            document.querySelectorAll('.rowan-native-row').forEach(section => observers[0].fire(section));
        }''', item)
        page.wait_for_selector('.rowan-native-row__card--landscape img')
        page.locator('#native img').evaluate('(img) => img.decode()')
        page.locator('.rowan-native-row__card--portrait img').evaluate('(img) => img.decode()')
        native = page.locator('#native')
        poster = page.locator('.rowan-native-row__card--portrait')
        landscape = page.locator('.rowan-native-row__card--landscape')
        assert poster.get_attribute('class').split().count('card-hoverable') == 1
        assert poster.locator('.cardBox.visualCardBox .cardScalable .cardPadder-overflowPortrait + .cardContent.cardImageContainer img').count() == 1
        assert poster.locator('.cardFooter .cardText').inner_text() == 'Plugin film'
        def metrics(card):
            return card.evaluate('''el => { const box=el.querySelector('.cardBox'), scale=el.querySelector('.cardScalable'), art=el.querySelector('img'), footer=el.querySelector('.cardFooter'); const r=x=>x.getBoundingClientRect(); return {width:r(el).width, artWidth:r(art).width, boxWidth:r(box).width, margin:getComputedStyle(box).margin, padding:getComputedStyle(el).padding, artRatio:r(art).width/r(art).height, footerBg:getComputedStyle(footer).backgroundColor, backing:getComputedStyle(scale).backgroundColor, border:getComputedStyle(box).borderRadius, scale:getComputedStyle(scale).transform}; }''')
        before_native, before_poster = metrics(native), metrics(poster)
        assert abs(before_native['width'] - before_poster['width']) < 2, (width, before_native, before_poster)
        assert abs(before_native['artWidth'] - before_poster['artWidth']) < 2, (width, before_native, before_poster)
        assert abs(before_poster['artRatio'] - 2/3) < .02
        assert abs(landscape.locator('img').evaluate('(el) => el.getBoundingClientRect().width / el.getBoundingClientRect().height') - 16/9) < .02
        assert before_native['footerBg'] == before_poster['footerBg']
        assert before_native['border'] == before_poster['border']
        assert before_native['backing'] == before_poster['backing']
        assert abs(before_native['artRatio'] - before_poster['artRatio']) < .02
        page.screenshot(path=str(theme / f'card-parity-{width}.png'), full_page=True)
        native.hover(); page.wait_for_timeout(450); native_scale = metrics(native)['scale']
        poster.hover(); page.wait_for_timeout(450); poster_scale = metrics(poster)['scale']
        assert native_scale == poster_scale and poster_scale != before_poster['scale'], (width, native_scale, poster_scale)
        page.screenshot(path=str(theme / f'card-hover-{width}.png'), full_page=True)
        poster.focus()
        assert poster.evaluate('(el) => el.matches(":focus-visible")')
        page.screenshot(path=str(theme / f'card-focus-{width}.png'), full_page=True)
        poster.press('Enter')
        assert page.evaluate('opened') == [item]
        page.evaluate('user = "bob"')
        poster.press('Enter')
        assert page.evaluate('opened') == [item]
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.close()
    browser.close()
print('Captured-theme native-shape card parity: pass')
