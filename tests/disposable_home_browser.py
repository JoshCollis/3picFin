"""Signed-in automatic Home integration in a throwaway Jellyfin instance."""
import os
from pathlib import Path
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
    screenshot_dir = Path(os.environ['FIN_SCREENSHOT_DIR']) if os.environ.get('FIN_SCREENSHOT_DIR') else None
    if screenshot_dir:
        screenshot_dir.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True)
        try:
            page = browser.new_page(viewport={'width': 1280, 'height': 800})
            sign_in(page, base, username, password)
            tabs = page.locator('.MuiStack-root > .threepic-fin-host__nav')
            tabs.wait_for(timeout=30000)
            assert page.evaluate('document.querySelector(".MuiStack-root > .threepic-fin-host__nav").getBoundingClientRect().right <= innerWidth')
            page.set_viewport_size({'width': 1280, 'height': 800})
            assert page.evaluate('document.querySelector(".MuiStack-root > .threepic-fin-host__nav").getBoundingClientRect().right <= innerWidth')
            assert tabs.count() == 1
            assert page.locator('.headerTabs .emby-tab-button').count() == 2
            assert page.locator('#indexPage .tabContent').count() == 2
            assert page.locator('#homeTab .threepic-fin-host__tabs').count() == 0
            assert page.locator('#homeTab .threepic-fin-host__panel').count() == 1
            assert page.locator('#homeTab > .sections').count() == 1
            assert page.locator('.MuiStack-root > a[href="#/home?tab=1"]').count() == 1
            if screenshot_dir:
                page.screenshot(path=str(screenshot_dir / '3pic-fin-native-nav-desktop.png'))
            page.set_viewport_size({'width': 390, 'height': 700})
            page.locator('button[aria-label="Open Menu"]').click()
            mobile_nav = page.locator('.threepic-fin-host__nav-item > .threepic-fin-host__nav')
            mobile_nav.wait_for(timeout=10000)
            page.wait_for_function('document.querySelector(".threepic-fin-host__nav-item > .threepic-fin-host__nav").getBoundingClientRect().left >= 0')
            assert mobile_nav.is_visible()
            assert page.evaluate('document.querySelector(".threepic-fin-host__nav-item > .threepic-fin-host__nav").getBoundingClientRect().right <= innerWidth')
            if screenshot_dir:
                page.screenshot(path=str(screenshot_dir / '3pic-fin-native-nav-mobile.png'))
            mobile_nav.click()
            assert page.locator('#homeTab .threepic-fin-host__panel').is_visible()
            page.set_viewport_size({'width': 1280, 'height': 800})
            tabs.wait_for(timeout=10000)
            assert tabs.get_attribute('aria-pressed') == 'true'
            assert page.locator('#favoritesTab').count() == 1
            tabs.click()
            assert page.locator('#homeTab > .sections').is_visible()
            tabs.click()
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
                assert negative.locator('.MuiStack-root > .threepic-fin-host__nav').count() == 0, mode
                context.close()
            print('BROWSER AUTO MOUNT/LIFECYCLE/DRIFT: pass')
        finally:
            browser.close()
