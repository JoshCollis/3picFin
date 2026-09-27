"""Disposable Chromium fragment smoke at mobile and desktop widths."""
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
        page.evaluate("""() => {
            window.calendarCalls = [];
            window.dispose = ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'), {
                getUrl: (route, parameters) => '/jellyfin/' + route + (parameters ? '?' + new URLSearchParams(parameters) : ''),
                getJSON: url => {
                    if (url.includes('/Calendar')) {
                        calendarCalls.push(url);
                        return Promise.resolve({ Radarr: { Items: [{ Title: 'Film.2024.REMUX', TitleId: 42, EventType: 'Digital', Date: '2026-09-28T00:00:00Z' }] }, Sonarr: { Items: [{ Title: 'A Show', TitleId: 45, EventType: 'Episode', SeasonNumber: 1, EpisodeNumber: 3, EpisodeTitle: 'Pilot', Date: '2026-09-29T00:00:00Z' }], Partial: true } });
                    }
                    return Promise.resolve({ Movies: {Items: []}, Tv: {Items: []}, Requests: {Items: []} });
                }
            });
        }""")
        assert page.evaluate('calendarCalls.length') == 0
        page.locator('#threepic-fin-calendar-tab').focus()
        page.keyboard.press('Enter')
        assert page.locator('#threepic-fin-calendar-panel').is_visible()
        assert not page.locator('#threepic-fin-discover-panel').is_visible()
        assert page.locator('#threepic-fin-calendar-radarr h4').all_text_contents() == ['Movie · TMDb #42']
        assert 'S01E03 · Pilot' in page.locator('#threepic-fin-calendar-sonarr').inner_text()
        assert 'Partial results' in page.locator('#threepic-fin-calendar-sonarr').inner_text()
        assert page.evaluate('calendarCalls.length') == 1
        assert '/jellyfin/3picFin/Calendar?start=' in page.evaluate('calendarCalls[0]')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        box = page.locator('#threepic-fin-calendar-tab').bounding_box()
        assert box is not None and box['height'] >= 44
        page.locator('#threepic-fin-calendar-next').click()
        assert page.evaluate('calendarCalls.length') == 2
        page.locator('#threepic-fin-discover-tab').click()
        assert page.locator('#threepic-fin-discover-panel').is_visible()
        page.evaluate('dispose()')
        page.close()
    browser.close()
print('Calendar fragment browser smoke: 360px and 1280px pass')
