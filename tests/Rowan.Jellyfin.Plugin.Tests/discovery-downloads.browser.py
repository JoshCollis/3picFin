"""Disposable browser smoke for the shared Downloads fragment at narrow/wide widths."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
fragment = (web / 'discovery.html').read_text()
css = (web / 'discovery.css').read_text()
script = (web / 'discovery.js').read_text()
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (360, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.set_content('<html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head><body style="margin:0">' + fragment + '</body></html>')
        page.add_style_tag(content=css)
        page.add_script_tag(content=script)
        page.evaluate("""() => ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'), {
            getUrl: route => '/jellyfin/' + route,
            getJSON: url => Promise.resolve(url.endsWith('/Downloads') ? {
                Radarr: { Items: [
                    { Title: 'Film.2024.REMUX', TitleId: 7, State: 'Downloading', Progress: .5 },
                    { Title: 'Film.2024.HEVC', TitleId: 8 },
                    { Title: 'Film.mkv!', TitleId: 9 },
                    { Title: 'Dr. Strangelove', TitleId: 10 }
                ], Partial: false },
                Sonarr: { Items: [], Partial: true }
            } : { Movies: {Items: []}, Tv: {Items: []}, Requests: {Items: []} })
        })""")
        page.locator('#threepic-fin-downloads-tab').focus()
        page.keyboard.press('Enter')
        assert page.locator('#threepic-fin-downloads-panel').is_visible()
        assert page.locator('#threepic-fin-discover-panel').is_visible()
        assert page.locator('#threepic-fin-downloads-radarr h4').all_text_contents() == [
            'Movie · TMDb #7', 'Movie · TMDb #8', 'Movie · TMDb #9', 'Dr. Strangelove'
        ]
        assert 'Film.' not in page.locator('#threepic-fin-downloads-radarr').inner_text()
        assert page.locator('#threepic-fin-downloads-sonarr').get_by_text('Partial results', exact=False).is_visible()
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        box = page.locator('#threepic-fin-downloads-tab').bounding_box()
        assert box is not None and box['height'] >= 44
        page.locator('#threepic-fin-downloads-refresh').click()
        assert page.locator('#threepic-fin-downloads-radarr .threepic-fin-discovery__progress').is_visible()
        page.keyboard.press('Escape')
        assert not page.locator('#threepic-fin-downloads-panel').is_visible()
        assert page.locator('#threepic-fin-discover-panel').is_visible()
        page.close()
    browser.close()
print('Downloads fragment browser smoke: 360px and 1280px pass')
