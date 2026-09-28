"""Rendered admin settings under the two captured ElegantFin stylesheets.
Run: ADMIN_ELEGANT_CSS_DIR=<captured-theme-dir> python3 tests/Rowan.Jellyfin.Plugin.Tests/configPage.elegant.browser.py
"""
import json
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2]
HTML = ROOT / 'src/Rowan.Jellyfin.Plugin/Configuration/configPage.html'
THEME = Path(os.environ['ADMIN_ELEGANT_CSS_DIR'])
OUT = Path(os.environ.get('ADMIN_SCREENSHOT_DIR', os.environ.get('TMPDIR', '/tmp')))
A = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'


def run(page, width):
    page.set_viewport_size({'width': width, 'height': 800})
    page.set_content(HTML.read_text())
    for css in ('elegant-source-0.css', 'elegant-source-1.css'):
        page.add_style_tag(path=str(THEME / css))
    page.evaluate('''() => {
      window.__saved = {HomeEnabled: true, NativeHomeRowsEnabled: true,
        NativeHomeRowKinds: ['LatestMovies'], RecentlyAddedLibraryIds: ['aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'],
        HeroLibraryIds: ['bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'], SeerrApiKey: 'stored-secret'};
      window.Dashboard = {showLoadingMsg(){},hideLoadingMsg(){},processPluginConfigurationUpdateResult(){}};
      window.ApiClient = {
        getUrl: x => x, getJSON: async () => [{Name:'Movies',ItemId:'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'},
          {Name:'Shows',ItemId:'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'}],
        getPluginConfiguration: async () => structuredClone(window.__saved),
        updatePluginConfiguration: async (_id, config) => { window.__saved = structuredClone(config); return {}; }
      };
      document.querySelector('#RowanConfigPage').dispatchEvent(new Event('pageshow'));
    }''')
    page.locator('#RowanSaveButton').wait_for(state='visible')
    page.wait_for_function("!document.querySelector('#RowanSaveButton').disabled")
    assert page.locator('#SeerrApiKey').input_value() == ''
    assert page.locator('#SeerrApiKey').get_attribute('placeholder').startswith('Saved key')
    assert page.locator('#RecentlyAddedLibraries input').count() == 2
    assert page.locator('#RecentlyAddedLibraries input').first.is_checked()
    assert page.locator('#HeroLibraries input').last.is_checked()
    # The primary controls must be scannable without a large nested explanation/fieldset.
    assert page.locator('#RowanConfigPage .fin-home-primary').count() == 1
    assert page.locator('#RowanConfigPage .fin-home-rows').count() == 1
    assert page.locator('#RowanConfigPage .fin-home-rows .fin-choice').count() >= 5
    assert page.locator('#RowanConfigPage .fin-home-primary').bounding_box()['height'] < 300
    assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), 'horizontal overflow'
    page.get_by_text('Featured hero', exact=True).click()
    page.get_by_text('Libraries', exact=True).click()
    page.get_by_text('Seerr & Discovery', exact=True).click()
    page.get_by_text('Arr & household activity', exact=True).click()
    page.locator('#CombinedPlaybackRowEnabled').check()
    page.locator('#CombinedPlaybackHideWatched').check()
    page.locator('#NativeRowNextUp').check()
    page.locator('#HeroTrustedFilesystemEnabled').check()
    page.locator('#RecentlyAddedSelected').check()
    page.locator('#RecentlyAddedLibraries input').last.check()
    page.locator('#GlobalSearchEnabled').check()
    page.locator('#SeerrEnabled').check()
    page.locator('#CalendarEnabled').check()
    page.locator('#UpcomingMoviesRowEnabled').check()
    page.locator('#RadarrBaseUrl').fill('https://arr.example/radarr/')
    page.locator('#RowanSaveButton').click()
    page.wait_for_function("document.querySelector('#RowanConfigStatus').textContent === 'Configuration saved.'")
    saved = page.evaluate('window.__saved')
    assert saved['CombinedPlaybackRowEnabled'] and saved['CombinedPlaybackHideWatched']
    assert saved['NativeHomeRowKinds'] == ['NextUp', 'LatestMovies']
    assert saved['HeroTrustedFilesystemEnabled']
    assert saved['RecentlyAddedLibraryIds'] == [A, B.replace('-', '')]
    assert saved['SeerrEnabled'] and saved['GlobalSearchEnabled']
    assert saved['CalendarEnabled'] and saved['UpcomingMoviesRowEnabled']
    assert saved['RadarrBaseUrl'] == 'https://arr.example/radarr/'
    assert saved['SeerrApiKey'] == 'stored-secret'
    page.locator('#RowanConfigPage').dispatch_event('pageshow')
    page.wait_for_function("!document.querySelector('#RowanSaveButton').disabled")
    assert page.locator('#CombinedPlaybackRowEnabled').is_checked()
    assert page.locator('#NativeRowNextUp').is_checked()
    assert page.locator('#RecentlyAddedLibraries input').last.is_checked()
    assert page.locator('#SeerrApiKey').input_value() == ''
    assert page.locator('#CalendarEnabled').is_checked()
    assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    OUT.mkdir(parents=True, exist_ok=True)
    page.screenshot(path=str(OUT / f'{width}-settings.png'), full_page=True)
    return {'width': width, 'saved_fields': len(saved), 'overflow': page.evaluate('document.documentElement.scrollWidth-innerWidth')}


if __name__ == '__main__':
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        try:
            print(json.dumps([run(browser.new_page(), width) for width in (390, 1280)]))
        finally:
            browser.close()
