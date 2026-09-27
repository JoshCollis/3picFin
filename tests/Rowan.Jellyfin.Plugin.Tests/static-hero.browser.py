"""Disposable browser geometry check for the standalone full-screen hero."""
from pathlib import Path
from playwright.sync_api import sync_playwright

css = (Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web/static-hero.css').read_text()
html = '''<html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head>
<body style="margin:0"><section class="rowan-static-hero"><img alt="" src="data:image/jpeg;base64,/9j/2Q==">
<h2>Film title</h2><p>Film description</p><button>Previous</button><button>Next</button></section></body></html>'''
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width, height in ((360, 740), (1280, 800)):
        page = browser.new_page(viewport={'width': width, 'height': height})
        page.set_content(html)
        page.add_style_tag(content=css)
        hero = page.locator('.rowan-static-hero').bounding_box()
        image = page.locator('.rowan-static-hero img').bounding_box()
        assert hero is not None and hero['width'] == width and hero['height'] >= height
        assert image is not None and image['width'] == width and image['height'] == hero['height']
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        assert all(page.locator('button').nth(i).bounding_box()['height'] >= 44 for i in range(2))
        page.close()
    browser.close()
print('360px and 1280px hero geometry: pass')
