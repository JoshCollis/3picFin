"""Rendered hero contract with captured, actual ElegantFin CSS (not hosted Jellyfin)."""
from base64 import b64encode
from hashlib import sha256
from io import BytesIO
import os
from pathlib import Path
import tempfile
from PIL import Image, ImageDraw
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
theme_dir = Path(os.environ['ELEGANTFIN_CSS_DIR'])
theme = [theme_dir / f'elegant-source-{i}.css' for i in range(2)]
assert all(p.is_file() for p in theme), theme
expected_hashes = ('779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643',
                   '525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841')
assert tuple(sha256(p.read_bytes()).hexdigest() for p in theme) == expected_hashes, 'ElegantFin capture changed'
output = Path(os.environ.get('HERO_SCREENSHOT_DIR', tempfile.gettempdir()))
output.mkdir(parents=True, exist_ok=True)
image = Image.new('RGB', (1600, 900), '#243352')
draw = ImageDraw.Draw(image)
for y in range(900):
    draw.line((0, y, 1600, y), fill=(22 + y // 35, 44 + y // 20, 85 + y // 16))
draw.polygon([(200, 850), (450, 150), (800, 780), (1150, 120), (1600, 840)], fill='#885c53')
buffer = BytesIO()
image.save(buffer, format='JPEG', quality=88)
encoded = b64encode(buffer.getvalue()).decode()

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (2560, 1280, 390):
        page = browser.new_page(viewport={'width': width, 'height': 900}, device_scale_factor=1)
        page.set_content('''<meta name="viewport" content="width=device-width, initial-scale=1"><body style="margin:0">
            <div class="skinHeader"></div><main id="indexPage"><div id="hero"></div>
            <div class="sections"><h2>My Media</h2><div class="itemsContainer">Movies · Shows</div></div></main>''')
        for source in theme:
            page.add_style_tag(content=source.read_text())
        page.add_style_tag(content=(web / 'static-hero.css').read_text())
        page.add_script_tag(content=(web / 'static-hero.js').read_text())
        page.evaluate('''encoded => {
            const bytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
            window.deliver = null;
            window.disposeHero = RowanStaticHero.mount(document.querySelector('#hero'), {
                getCurrentUserId: () => 'user', accessToken: () => 'token',
                getUrl: path => '/jellyfin/' + path,
                getJSON: () => new Promise(resolve => { window.deliver = resolve; }),
                fetch: async () => new Response(new Blob([bytes], {type: 'image/jpeg'}), {headers: {'content-type': 'image/jpeg'}}),
                openItem: () => {}
            });
        }''', encoded)
        page.wait_for_function('window.deliver !== null')
        def metrics():
            return page.evaluate('''() => {
                const rect = selector => {const r = document.querySelector(selector).getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height,bottom:r.bottom};};
                return {hero:rect('.rowan-static-hero'), row:rect('.sections'), title:rect('.rowan-static-hero h2'),
                    description:rect('.rowan-static-hero p'), counter:rect('.rowan-static-hero > span:not(.rowan-hero-progress)'),
                    dots:rect('.rowan-hero-pagination'), progress:rect('.rowan-hero-progress'), open:rect('.rowan-hero-open'),
                    overflow:document.documentElement.scrollWidth - innerWidth,
                    titleLineHeight:parseFloat(getComputedStyle(document.querySelector('.rowan-static-hero h2')).lineHeight)};
            }''')
        reserved = metrics()
        page.evaluate('''() => deliver(Array.from({length:10}, (_,i) => ({
            id:`11111111-1111-1111-1111-11111111111${i}`,
            name:i===0?'Stuart Fails to Save the Universe':`Feature ${i+1}`,
            overview:'After accidentally creating a new multiverse, Stuart must locate the answer before reality unravels.',
            imageType:'Backdrop', imageIndex:0, imageTag:'a1'
        })))''')
        page.get_by_role('heading', name='Stuart Fails to Save the Universe').wait_for()
        page.wait_for_function("() => document.querySelector('.rowan-static-hero img').naturalWidth === 1600")
        result = metrics()
        page.screenshot(path=str(output / f'featured-elegant-{width}.png'))
        assert result['row']['y'] == reserved['row']['y'], (width, reserved['row'], result['row'])
        assert result['hero']['height'] == reserved['hero']['height'], (width, reserved['hero'], result['hero'])
        assert result['overflow'] <= 0, (width, result)
        if width > 600:
            assert abs(result['title']['x'] - result['open']['x']) <= 1, result
            assert result['open']['y'] > result['description']['bottom'], result
            assert result['open']['y'] < result['dots']['y'], result
            assert result['dots']['x'] > width * .65, result
            assert result['title']['y'] < result['hero']['height'] * .55, result
        else:
            assert result['hero']['height'] >= 700, result
            assert abs(result['title']['x'] + result['title']['width']/2 - width/2) <= 1, result
            assert result['open']['y'] > result['dots']['bottom'], result
            assert result['title']['y'] < result['hero']['height'] * .55, result
        assert page.locator('.rowan-hero-dot').count() == 10
        assert page.evaluate('''() => [...document.querySelectorAll('.rowan-hero-dot')].every(b => b.getBoundingClientRect().width >= 44)''')
        screenshot = output / f'featured-elegant-{width}.png'
        page.screenshot(path=str(screenshot))
        page.get_by_role('button', name='Slide 2: Feature 2').focus()
        page.keyboard.press('ArrowRight')
        assert page.get_by_role('heading', name='Feature 2').count() == 1
        assert page.get_by_role('button', name='Slide 2: Feature 2').evaluate('(el) => document.activeElement === el')
        page.get_by_role('button', name='Next').click()
        assert page.get_by_role('heading', name='Feature 3').count() == 1
        print(f'{width}px: {result}; screenshot={screenshot}')
        page.evaluate('disposeHero()')
        page.evaluate('''() => {
            window.disposeHero = RowanStaticHero.mount(document.querySelector('#hero'), {
                getCurrentUserId: () => 'user', accessToken: () => 'token',
                getUrl: path => '/jellyfin/' + path,
                fetch: async () => ({ok:false}), openItem: () => {}
            }, {slides:[{id:'11111111-1111-1111-1111-111111111111',name:'Only feature',imageType:'Backdrop',imageIndex:0,imageTag:'a1'}]});
        }''')
        page.get_by_role('heading', name='Only feature').wait_for()
        assert page.evaluate('''() => ['.rowan-hero-previous','.rowan-hero-next','.rowan-hero-pagination','.rowan-hero-progress'].every(s => getComputedStyle(document.querySelector(s)).display === 'none')''')
        page.evaluate('disposeHero()')
        page.close()
    browser.close()
