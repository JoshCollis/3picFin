"""Rendered Discovery navigation, carousel and lifecycle regression at phone/desktop widths."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
fragment = (web / 'discovery.html').read_text()
css = (web / 'discovery.css').read_text()
script = (web / 'discovery.js').read_text()

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (390, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 800})
        page.on('pageerror', lambda error: print('pageerror', error))
        page.route('https://image.tmdb.org/t/p/**', lambda route: route.fulfill(
            status=200, content_type='image/svg+xml' if 'broken' not in route.request.url else 'image/jpeg',
            body='not an image' if 'broken' in route.request.url else '<svg xmlns="http://www.w3.org/2000/svg" width="240" height="360"><rect width="240" height="360" fill="#36546d"/><circle cx="120" cy="130" r="65" fill="#bc9677"/><text x="25" y="280" fill="white" font-size="28">FILM</text></svg>'))
        page.set_content('<meta name="viewport" content="width=device-width,initial-scale=1"><body style="margin:0">' + fragment)
        page.add_style_tag(content=css)
        page.add_script_tag(content=script)
        page.evaluate("""() => {
            window.calls = []; window.user = 'a';
            window.api = {
                getCurrentUserId: () => window.user,
                getUrl: (path, args = {}) => '/jellyfin/' + path + '?' + new URLSearchParams(args),
                getJSON: async url => {
                    calls.push(url);
                    const params = new URL(url, 'https://example.test').searchParams;
                    if (url.includes('/Discovery?')) return {
                        Movies: {Items: Array.from({length: 12}, (_, i) => ({Title: 'Film ' + (i + 1), MediaType: 'movie', TmdbId: i + 1, PosterPath: '/film.jpg'})), TotalPages: 2},
                        Tv: {Items: [{Title: 'A Series', MediaType: 'tv', TmdbId: 40}], TotalPages: 1},
                        Requests: {Items: [{Title: 'My Film', MediaType: 'movie', TmdbId: 78, Status: 2}], TotalPages: 2}
                    };
                    if (url.includes('/TitleDetails?')) return {Title: params.get('mediaId') === '1' ? 'Film 1' : 'My Film', MediaType: params.get('mediaType'), TmdbId: Number(params.get('mediaId')), Overview: 'Story', PosterPath: params.get('mediaId') === '40' ? '/broken.jpg' : '/film.jpg', CanRequest: true};
                    if (url.includes('/RequestOptions?')) return {CanRequest: true, CanRequest4k: false, Seasons: [], MediaStatus: 1};
                    if (url.includes('/SharedRequests?')) return {Items: [], TotalPages: 1};
                    if (url.includes('/Downloads?')) return {Radarr: {Items: [{Title:'Example film', State:'Downloading', Progress:0.5}]}, Sonarr: {Items: []}};
                    if (url.includes('/Calendar?')) return {Radarr: {Items: []}, Sonarr: {Items: []}};
                    if (url.includes('/Search?')) return {Items: [{Title:'Found', MediaType:'movie', TmdbId:99}], TotalPages: 2};
                }
            };
            window.mount = () => window.dispose = ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'), api, {userId: user, isCurrent: () => user === api.getCurrentUserId()});
            mount();
        }""")
        page.get_by_role('heading', name='Movies', exact=True).first.wait_for()
        page.get_by_text('Film 12').wait_for()
        rail = page.locator('#threepic-fin-movies')
        assert rail.evaluate('(el) => getComputedStyle(el).overflowX === "auto" && el.scrollWidth > el.clientWidth')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        assert rail.locator('.threepic-fin-discovery__card').first.evaluate('(el) => el.getBoundingClientRect().width') < 190
        assert page.locator('#threepic-fin-tv .threepic-fin-discovery__poster-fallback').evaluate('(el) => el.getBoundingClientRect().height <= 85')
        assert page.locator('#threepic-fin-movies .threepic-fin-discovery__card-info > button').count() == 0
        page.get_by_role('button', name='Scroll movies right').click()
        page.wait_for_function('document.querySelector("#threepic-fin-movies").scrollLeft > 0')
        page.get_by_role('button', name='Scroll movies left').click()
        page.wait_for_function('document.querySelector("#threepic-fin-movies").scrollLeft === 0')
        page.locator('#threepic-fin-movies-next').click()
        page.get_by_text('Page 2 of 2').first.wait_for()
        assert 'moviePage=2' in page.evaluate('calls.filter(c => c.includes("/Discovery?")).at(-1)')
        page.locator('#threepic-fin-movies .threepic-fin-discovery__title-button').first.click()
        page.get_by_text('Story').wait_for()
        dialog = page.locator('#threepic-fin-details-dialog')
        assert dialog.evaluate('(el) => getComputedStyle(el).display === "grid"')
        assert dialog.evaluate('(el) => el.getBoundingClientRect().width <= Math.min(innerWidth - 24, 680)')
        page.wait_for_function('document.querySelector("#threepic-fin-details-body img").naturalWidth > 0')
        page.screenshot(path=f'/home/josh/.hermes/cache/scratch/discovery-details-{width}.png')
        page.get_by_role('button', name='Request', exact=True).click()
        page.get_by_text('Select and confirm request.', exact=False).wait_for()
        page.locator('#threepic-fin-request-cancel').click()
        page.locator('#threepic-fin-tv .threepic-fin-discovery__title-button').click()
        page.wait_for_function('document.querySelector("#threepic-fin-details-title").textContent === "My Film"')
        page.wait_for_function('!document.querySelector("#threepic-fin-details-body img")')
        assert dialog.evaluate('(el) => el.getBoundingClientRect().height < 300')
        page.locator('#threepic-fin-details-close').click()
        page.get_by_role('tab', name='Downloads', exact=True).click()
        assert page.locator('#threepic-fin-downloads-panel').is_visible()
        assert page.get_by_role('tab', name='Downloads').get_attribute('aria-selected') == 'true'
        assert not page.locator('#threepic-fin-discover-panel').is_visible()
        page.get_by_text('Example film').wait_for()
        page.get_by_role('tab', name='Downloads').press('ArrowRight')
        assert page.get_by_role('tab', name='Calendar').get_attribute('aria-selected') == 'true'
        page.locator('#threepic-fin-calendar-window').get_by_text('to', exact=False).wait_for()
        page.get_by_role('tab', name='Discover', exact=True).click()
        assert page.locator('#threepic-fin-discover-panel').is_visible()
        page.locator('#threepic-fin-search').fill('found')
        page.locator('#threepic-fin-search-form button').click()
        page.get_by_text('Found', exact=True).wait_for()
        page.evaluate('user = "b"; dispose(); void mount()')
        page.wait_for_function('document.querySelector("#threepic-fin-movies").textContent.includes("Film 12")')
        assert not page.locator('#threepic-fin-details-dialog').evaluate('(el) => el.open')
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        page.close()
    browser.close()
print('Discovery rendered carousels/navigation: 390px and 1280px pass')
