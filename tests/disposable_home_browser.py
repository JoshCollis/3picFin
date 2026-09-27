"""Signed-in automatic Home integration in a throwaway Jellyfin instance."""
from playwright.sync_api import sync_playwright


def sign_in(page, base, username, password):
    page.goto(base + '/web/index.html', wait_until='domcontentloaded')
    page.locator('#txtManualName').wait_for(timeout=20000)
    page.locator('#txtManualName').fill(username)
    page.locator('#txtManualPassword').fill(password)
    page.locator('#txtManualPassword').press('Enter')
    page.locator('#indexPage #homeTab > .sections').wait_for(timeout=30000)
    page.wait_for_url('**/web/index.html#/home', timeout=30000)


def run_browser(base, username, password):
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True)
        try:
            page = browser.new_page(viewport={'width': 390, 'height': 700})
            sign_in(page, base, username, password)
            tabs = page.locator('#homeTab .threepic-fin-host__tabs')
            tabs.wait_for(timeout=30000)
            assert page.evaluate('document.querySelector("#homeTab .threepic-fin-host__tabs").getBoundingClientRect().right <= innerWidth')
            page.set_viewport_size({'width': 1280, 'height': 800})
            assert page.evaluate('document.querySelector("#homeTab .threepic-fin-host__tabs").getBoundingClientRect().right <= innerWidth')
            assert tabs.count() == 1
            assert page.locator('#homeTab .threepic-fin-host__panel').count() == 1
            assert page.locator('#homeTab > .sections').count() == 1
            assert page.locator('#favoritesTab').count() == 1
            page.locator('.threepic-fin-host__tab').filter(has_text='3pic Fin').click()
            assert page.locator('#homeTab .threepic-fin-host__panel').is_visible()
            assert not page.locator('#homeTab > .sections').is_visible()
            page.evaluate("location.hash = '#/home?tab=1'")
            tabs.wait_for(state='detached', timeout=10000)
            page.evaluate("location.hash = '#/home'")
            tabs.wait_for(timeout=15000)
            page.evaluate("location.hash = '#/search'")
            tabs.wait_for(state='detached', timeout=10000)
            page.evaluate("location.hash = '#/home'")
            tabs.wait_for(timeout=15000)
            page.evaluate("""() => {
                ApiClient.getCurrentUserId = () => null;
                dispatchEvent(new Event('hashchange'));
            }""")
            tabs.wait_for(state='detached', timeout=10000)
            page.close()
            for mode, init in {
                'missing-timing': """(() => {
                    const original = performance.getEntriesByType.bind(performance);
                    performance.getEntriesByType = type => type === 'resource'
                        ? original(type).filter(entry => !entry.name.includes('/hometab.')) : original(type);
                })()""",
                'ambiguous-timing': """(() => {
                    const original = performance.getEntriesByType.bind(performance);
                    performance.getEntriesByType = type => type === 'resource'
                        ? [...original(type), ...original(type).filter(entry => entry.name.includes('/hometab.'))]
                        : original(type);
                })()""",
            }.items():
                context = browser.new_context()
                negative = context.new_page()
                negative.add_init_script(init)
                sign_in(negative, base, username, password)
                negative.wait_for_timeout(2000)
                assert negative.locator('#homeTab .threepic-fin-host__tabs').count() == 0, mode
                context.close()
            print('BROWSER AUTO MOUNT/LIFECYCLE/DRIFT: pass')
        finally:
            browser.close()
