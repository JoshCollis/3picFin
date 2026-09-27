"""Chromium proof with a locally generated JPEG, not a black image stub."""
from base64 import b64encode
from io import BytesIO
from pathlib import Path
import os
import tempfile
from PIL import Image, ImageDraw
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
# Original, deterministic landscape fixture: real JPEG bytes through ApiClient.fetch -> blob -> img.
landscape = Image.new('RGB', (960, 540))
draw = ImageDraw.Draw(landscape)
for y in range(540):
    draw.line((0, y, 960, y), fill=(25 + y // 22, 90 + y // 14, 165 - y // 12))
draw.polygon([(0, 360), (210, 135), (400, 330), (650, 170), (960, 395), (960, 540), (0, 540)], fill=(54, 85, 91))
draw.polygon([(0, 430), (340, 275), (700, 435), (960, 290), (960, 540), (0, 540)], fill=(30, 65, 62))
draw.ellipse((680, 55, 770, 145), fill=(251, 204, 120))
buffer = BytesIO()
landscape.save(buffer, format='JPEG', quality=88)
image_b64 = b64encode(buffer.getvalue()).decode('ascii')

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (360, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800}, has_touch=True, device_scale_factor=1)
        page.set_content('<meta name="viewport" content="width=device-width, initial-scale=1"><body style="margin:0"><div id="root"></div><input id="outside"></body>')
        page.add_style_tag(content=(web / 'static-hero.css').read_text())
        page.add_script_tag(content=(web / 'static-hero.js').read_text())
        page.evaluate('''async jpeg => {
            const bytes = Uint8Array.from(atob(jpeg), c => c.charCodeAt(0));
            window.opened = [];
            window.cleanupHero = RowanStaticHero.mount(document.querySelector('#root'), {
                getCurrentUserId: () => 'user', accessToken: () => 'token',
                getUrl: route => '/subpath/' + route,
                getJSON: async () => Array.from({length: 10}, (_, i) => ({ id: `11111111-1111-1111-1111-11111111111${i}`, name: `Feature ${i + 1}`, overview: 'An original landscape image.', imageType: 'Backdrop', imageIndex: 0, imageTag: 'a1' })),
                fetch: async () => new Response(new Blob([bytes], {type:'image/jpeg'}), {headers:{'content-type':'image/jpeg'}}),
                openItem: id => window.opened.push(id)
            });
        }''', image_b64)
        title = page.locator('.rowan-static-hero h2')
        title.get_by_text('Feature 1').wait_for()
        page.wait_for_function("() => {const img = document.querySelector('.rowan-static-hero img'); return img.complete && img.naturalWidth === 960}")
        assert page.locator('.rowan-hero-pagination button').count() == 10
        assert page.evaluate('''() => [...document.querySelectorAll('.rowan-hero-pagination button')].every(b => b.getBoundingClientRect().width >= 44 && b.getBoundingClientRect().height >= 44)''')
        assert page.evaluate('''() => new Set([...document.querySelectorAll('.rowan-hero-pagination button')].map(b => Math.round(b.getBoundingClientRect().top))).size === 1''')
        if width == 360:
            assert page.evaluate('''() => {const a=document.querySelector('.rowan-hero-previous').getBoundingClientRect(), b=document.querySelector('.rowan-static-hero h2').getBoundingClientRect(); return a.bottom < b.top}''')
        page.locator('#outside').focus()
        page.keyboard.press('ArrowRight')
        assert title.inner_text() == 'Feature 1'
        dots = page.locator('.rowan-hero-pagination button')
        dots.nth(1).focus()
        page.keyboard.press('Enter')
        assert title.inner_text() == 'Feature 2'
        assert dots.nth(1).evaluate('(el) => document.activeElement === el')
        page.keyboard.press('ArrowRight')
        assert title.inner_text() == 'Feature 3'
        assert dots.nth(1).evaluate('(el) => document.activeElement === el')
        page.evaluate('''() => {
            const panel = document.querySelector('.rowan-static-hero');
            const touch = (x, y) => new Touch({identifier: 1, target: panel, clientX: x, clientY: y});
            panel.dispatchEvent(new TouchEvent('touchstart', {bubbles: true, touches: [touch(280, 100)]}));
            panel.dispatchEvent(new TouchEvent('touchend', {bubbles: true, changedTouches: [touch(120, 103)]}));
        }''')
        assert title.inner_text() == 'Feature 4'
        page.locator('.rowan-hero-open').click()
        assert page.evaluate('opened') == ['11111111-1111-1111-1111-111111111113']
        for _ in range(6):
            page.locator('.rowan-hero-next').click()
        assert title.inner_text() == 'Feature 10'
        assert page.evaluate('''() => {const nav=document.querySelector('.rowan-hero-pagination').getBoundingClientRect(), active=document.querySelector('.rowan-hero-dot[aria-current="true"]').getBoundingClientRect(); return active.left >= nav.left && active.right <= nav.right}''')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        open_box = page.locator('.rowan-hero-open').bounding_box()
        assert open_box is not None and open_box['height'] >= 44
        output = Path(os.environ.get('TMPDIR') or tempfile.gettempdir()) / f'featured-hero-rendered-{width}.png'
        page.screenshot(path=str(output))
        screenshot = Image.open(output).convert('RGB')
        pixel = screenshot.getpixel((width // 2, 30))
        assert isinstance(pixel, tuple) and pixel[2] > pixel[0] and pixel[2] > 70, (width, pixel)
        page.evaluate('cleanupHero()')
        assert page.locator('.rowan-static-hero').count() == 0
        assert page.evaluate('document.querySelectorAll("img[src^=blob]").length') == 0
        page.close()
        print(f'{width}px: JPEG decoded, rendered pixel {pixel}, 10 targets >=44px, focus/keyboard/touch/Open/teardown; {output}')
    page = browser.new_page(reduced_motion='reduce')
    page.set_content('<div id="root"></div>')
    page.add_script_tag(content=(web / 'static-hero.js').read_text())
    page.evaluate('''() => { window.cleanupHero = RowanStaticHero.mount(document.querySelector('#root'), {
        getCurrentUserId: () => 'user', accessToken: () => 'token', getUrl: route => route,
        getJSON: async () => [0,1].map(i => ({id:`11111111-1111-1111-1111-11111111111${i}`,name:`Feature ${i}`,imageType:'Backdrop',imageIndex:0,imageTag:'a1'})),
        fetch: async () => ({ok:false})
    }); }''')
    page.locator('h2').get_by_text('Feature 0').wait_for()
    page.wait_for_timeout(250)
    assert page.locator('h2').inner_text() == 'Feature 0'
    page.evaluate('cleanupHero()')
    page.close()
    browser.close()
print('Reduced-motion and responsive rendered-image browser proof: pass')
