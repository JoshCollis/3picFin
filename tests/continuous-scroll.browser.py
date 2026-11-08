"""Loopback-only rendered acceptance of embedded assets from the real candidate DLL.

No Jellyfin installation, production credentials, package installs or Docker pulls.
Seerr DTOs and navigation shell are deterministic fixtures, not live integration.
Run with TMPDIR set; optional --serve keeps the disposable fixture available.
"""
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from collections import Counter
import json
import os
from pathlib import Path
import subprocess
import shutil
import sys
import tempfile
import threading
import time
from urllib.parse import parse_qs, urlparse
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path(os.environ['TMPDIR'])
DLL = Path(os.environ.get('FIN_CANDIDATE_DLL', str(ROOT / 'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll')))
ROUTES = ['requests', 'shared-requests', 'trending', 'movies', 'tv', 'upcoming-movies', 'upcoming-tv', 'search-results']


def main():
    with tempfile.TemporaryDirectory(prefix='fin-scroll-', dir=SCRATCH) as temp:
        temp = Path(temp)
        assets = temp / 'assets'
        subprocess.run(['dotnet', 'build', str(ROOT / 'tests/ContinuousScrollAssets/ContinuousScrollAssets.csproj'),
                        '--artifacts-path', str(temp / 'build'), '-v', 'quiet'], check=True, env={**os.environ, 'TMPDIR': str(SCRATCH)})
        extractor = temp / 'build/bin/ContinuousScrollAssets/debug/ContinuousScrollAssets.dll'
        subprocess.run(['dotnet', str(extractor), str(DLL), str(assets)], check=True, env={**os.environ, 'TMPDIR': str(SCRATCH)})
        calls, failures = [], set()

        def source(page, kind='movie', query=''):
            return {'Page': page, 'TotalPages': 20, 'Items': [
                {'Title': f'{query or kind} title {(page - 1) * 20 + i}', 'TmdbId': (page - 1) * 20 + i + 1,
                 'MediaType': kind, 'Type': kind, 'Id': (page - 1) * 20 + i + 1, 'Status': 2,
                 'PosterPath': None} for i in range(20)]}

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                parsed = urlparse(self.path)
                if parsed.path.startswith('/assets/'):
                    path = assets / Path(parsed.path).name
                    data = path.read_bytes()
                    mime = 'text/css' if path.suffix == '.css' else 'text/javascript' if path.suffix == '.js' else 'text/html'
                elif parsed.path.startswith('/theme/') and os.environ.get('FIN_THEME_CSS_DIR'):
                    data = (Path(os.environ['FIN_THEME_CSS_DIR']) / Path(parsed.path).name).read_bytes()
                    mime = 'text/css'
                elif parsed.path.startswith('/jellyfin/3picFin/'):
                    params = parse_qs(parsed.query)
                    route = parsed.path.rsplit('/', 1)[-1]
                    page = int(params.get('page', ['1'])[0])
                    calls.append((route, params))
                    assert 1 <= page <= 100
                    if route == 'Search' and params.get('query') == ['old']:
                        time.sleep(.4)
                    # One incremental failure, after content already exists.
                    if route == 'UpcomingTV' and page == 2 and route not in failures:
                        failures.add(route)
                        self.send_response(502); self.end_headers(); return
                    if route == 'Discovery':
                        result = {name: source(int(params.get(arg, ['1'])[0]), kind) for name, arg, kind in [
                            ('Movies', 'moviePage', 'movie'), ('Tv', 'tvPage', 'tv'), ('Requests', 'requestsPage', 'movie')]}
                    elif route == 'TitleDetails':
                        result = {'Title': 'Resolved request ' + params['mediaId'][0], 'TmdbId': int(params['mediaId'][0]),
                                  'MediaType': params['mediaType'][0], 'Overview': 'Verified fixture synopsis.', 'MediaStatus': 1,
                                  'CanRequest': True, 'CanRequest4k': False, 'Seasons': [1]}
                    else:
                        result = source(page, 'tv' if route == 'UpcomingTV' else 'movie', params.get('query', [''])[0])
                    data, mime = json.dumps(result).encode(), 'application/json'
                else:
                    layout = parse_qs(parsed.query).get('layout', ['desktop'])[0]
                    data, mime = shell(layout).encode(), 'text/html'
                self.send_response(200); self.send_header('Content-Type', mime + '; charset=utf-8'); self.end_headers()
                try:
                    self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError):
                    pass  # Search cancellation is expected.

        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        base = f'http://127.0.0.1:{server.server_port}'
        try:
            if '--serve' in sys.argv:
                print(base, flush=True); thread.join()
            else:
                probe(base, calls)
                print(json.dumps({'candidate_sha256': hashlib.sha256(DLL.read_bytes()).hexdigest(), 'asset_source': 'candidate DLL', 'transport': 'loopback DTO fixture'}))
        finally:
            server.shutdown(); server.server_close()


def shell(layout='desktop'):
    assert layout in ('desktop', 'mobile', 'tv')
    theme = ''.join(f'<link rel="stylesheet" href="/theme/elegant-source-{n}.css">' for n in (0, 1)) if os.environ.get('FIN_THEME_CSS_DIR') else ''
    return '<!doctype html><html class="layout-' + layout + '"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">' + theme + '''
<link rel="stylesheet" href="/assets/discovery.css"><link rel="stylesheet" href="/assets/global-search-addon.css">
<style>body{margin:0;background:#18202a;color:white;font:16px sans-serif}.overflowPortraitCard{width:clamp(8.125rem,14vw,12.5rem)}.cardScalable{aspect-ratio:2/3;position:relative}.cardText{line-height:1.3}.cardBox{margin:0}.fin-continuous{min-height:270px}.cardPadder{display:none}.threepic-fin-search{margin-top:1rem}</style>
<script src="/assets/continuous-rail.js"></script><script src="/assets/discovery.js"></script><script src="/assets/global-search-addon.js"></script>
<div id="app"></div><div id="searchPage"></div><script>
window.user='alice'; window.restoration={feeds:{}}; window.errors=[];
window.api={getCurrentUserId:()=>user,getUrl:(path,args={})=>'/jellyfin/'+path+'?'+new URLSearchParams(args),getJSON:async(url,options)=>{const response=await fetch(url,options);if(!response.ok)throw {status:response.status};return response.json();}};
window.addon=ThreePicFinSearchAddon.createSearchAddon();
window.mount=async()=>{document.querySelector('#app').innerHTML=await(await fetch('/assets/discovery.html')).text();window.cleanup=ThreePicFinDiscovery.mount(document.querySelector('#app'),api,{userId:user,isCurrent:()=>user==='alice',restoration});cleanup.resume();};
window.openSearch=(query='fixture')=>addon.mount({root:document.querySelector('#searchPage'),apiClient:api,userId:user,sessionUserId:()=>user,query,parentId:null,collectionType:null,enabled:true,requestAction:()=>{},detailsAction:(item,button)=>cleanup.openDetails(item,button)});
addEventListener('hashchange',()=>{if(location.hash==='#/detail'){cleanup();document.querySelector('#app').hidden=true;}else{document.querySelector('#app').hidden=false;mount();}});
mount();</script>'''


def probe(base, calls):
    with sync_playwright() as p:
        # Explicit TMPDIR also reaches the browser subprocess, not just this shell.
        browser = p.chromium.launch(headless=True, executable_path=os.environ.get('FIN_BROWSER_EXECUTABLE') or shutil.which('google-chrome'), env={**os.environ, 'TMPDIR': str(SCRATCH)})
        try:
            for width, layout in ((390, 'mobile'), (1280, 'desktop'), (1280, 'tv')):
                page = browser.new_page(viewport={'width': width, 'height': 850}, reduced_motion='reduce')
                page.route('**/*', lambda route: route.continue_() if route.request.url.startswith(base) else route.abort())
                start_read = len(calls)
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.goto(base + '/?layout=' + layout + '#/home?fin=1')
                page.locator('#threepic-fin-movies article').first.wait_for()
                assert page.locator('[class*="__pager"], [id$="movies-next"], [id$="requests-prev"], [id$="search-page"]').count() == 0
                assert not page.get_by_role('button', name='Load more', exact=True).count()
                assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
                maxima, dom_maxima = {}, {}
                for name in ROUTES:
                    if name == 'search-results':
                        page.locator('#threepic-fin-search').fill('fixture')
                        page.locator('#threepic-fin-search-form').evaluate('(form)=>form.requestSubmit()')
                    rail = page.locator('#threepic-fin-' + name)
                    rail.scroll_into_view_if_needed()
                    rail.locator('article').first.wait_for()
                    maximum = dom_maximum = 0
                    for target in (40, 100, 200, 400):
                        for _ in range(25):
                            rail.evaluate('(el)=>{el.scrollLeft=el.scrollWidth;el.dispatchEvent(new Event("scroll"));}')
                            page.wait_for_timeout(35)
                            retry = page.get_by_role('button', name='Retry Upcoming TV', exact=True)
                            if name == 'upcoming-tv' and retry.is_visible():
                                assert rail.locator('article').count() > 0, 'incremental errors preserve content'
                                retry.click()
                            maximum = max(maximum, rail.locator('article').count())
                            dom_maximum = max(dom_maximum, rail.locator('*').count())
                            if rail.locator(f'article[data-fin-index="{target - 1}"]').count():
                                break
                        else:
                            raise AssertionError((name, target, rail.inner_text()))
                        assert maximum <= 30, (name, maximum)
                    maxima[name] = maximum
                    dom_maxima[name] = dom_maximum
                    assert dom_maximum <= 500
                    status_name = 'search' if name == 'search-results' else name
                    page.locator('#threepic-fin-' + status_name + '-status').get_by_text('End of', exact=False).wait_for()
                    # Cached reverse scrolling and keyboard focus cross recycled windows.
                    rail.evaluate('(el)=>{el.scrollLeft=0;el.dispatchEvent(new Event("scroll"));}')
                    page.wait_for_timeout(50)
                    rail.locator('article[data-fin-index="0"] button').first.focus()
                    for _ in range(30):
                        page.keyboard.press('ArrowRight')
                    assert rail.evaluate('(el)=>Number(document.activeElement.closest("article").dataset.finIndex)') == 30
                    page.keyboard.press('Tab'); page.keyboard.press('Tab')
                    assert rail.evaluate('(el)=>Number(document.activeElement.closest("article").dataset.finIndex)') == 31
                    page.keyboard.press('Shift+Tab')
                    assert rail.evaluate('(el)=>Number(document.activeElement.closest("article").dataset.finIndex)') == 30
                    # Real modal return keeps the original, connected trigger.
                    page.keyboard.press('Enter')
                    page.locator('#threepic-fin-details-dialog').wait_for(state='visible')
                    page.get_by_text('Verified fixture synopsis.').wait_for()
                    page.locator('#threepic-fin-details-close').click()
                    assert rail.evaluate('(el)=>el.contains(document.activeElement)')
                movie = page.locator('#threepic-fin-movies')
                movie.scroll_into_view_if_needed()
                before = movie.evaluate('(el)=>el.scrollLeft')
                reads = len(calls)
                page.evaluate('location.hash="/detail"')
                page.wait_for_function('document.querySelector("#app").hidden')
                page.go_back()
                page.locator('#threepic-fin-movies article').first.wait_for()
                page.wait_for_timeout(100)
                assert abs(movie.evaluate('(el)=>el.scrollLeft') - before) < 3
                assert len(calls) == reads, 'back restores cached pages, not a catalog refetch'
                # Discovery's own submitted search also discards an ignored late response.
                for query in ('old', 'new'):
                    page.locator('#threepic-fin-search').fill(query)
                    page.locator('#threepic-fin-search-form').evaluate('(form)=>form.requestSubmit()')
                page.locator('#threepic-fin-search-results article').first.wait_for()
                page.wait_for_timeout(450)
                assert 'old title' not in page.locator('#threepic-fin-search-results').inner_text()
                # Search changes cancel the slow old query and reset offsets immediately.
                page.evaluate('openSearch("old")')
                page.evaluate('addon.update({query:"new"})')
                page.locator('.threepic-fin-search article').first.wait_for()
                page.wait_for_timeout(450)
                assert 'old title' not in page.locator('.threepic-fin-search').inner_text()
                assert 'new title' in page.locator('.threepic-fin-search').inner_text()
                rail = page.locator('.threepic-fin-search__cards')
                rail.scroll_into_view_if_needed()
                maximum = 0
                for _ in range(50):
                    rail.evaluate('(el)=>{el.scrollLeft=el.scrollWidth;el.dispatchEvent(new Event("scroll"));}')
                    page.wait_for_timeout(35)
                    maximum = max(maximum, rail.locator('article').count())
                    if rail.locator('article[data-fin-index="399"]').count():
                        break
                assert rail.locator('article[data-fin-index="399"]').count()
                assert maximum <= 30
                assert page.locator('.threepic-fin-search nav').count() == 0
                assert 'Page ' not in page.locator('.threepic-fin-search').inner_text()
                assert page.locator('#threepic-fin-discover-panel [id$="-prev"], #threepic-fin-discover-panel [id$="-next"], #threepic-fin-discover-panel [id$="-page"]').count() == 0
                theme = 'elegant' if os.environ.get('FIN_THEME_CSS_DIR') else 'stock'
                page.screenshot(path=str(SCRATCH / f'fin-scroll-{theme}-{layout}-{width}.png'))
                # DOM remains bounded across all nine populated rails, not just one.
                assert page.locator('article').count() <= 9 * 30
                counts = Counter((route, json.dumps(params, sort_keys=True)) for route, params in calls[start_read:] if route not in ('TitleDetails', 'Search'))
                assert all(count == 1 or route == 'UpcomingTV' and count == 2 for (route, _), count in counts.items()), counts
                page.evaluate('user="bob";addon.dispose();cleanup()')
                assert page.locator('article').count() == 0
                assert not errors, errors
                print(json.dumps({'width': width, 'layout': layout, 'theme': theme, 'loaded_per_rail': 400, 'max_mounted_cards': maxima,
                                  'max_dom_descendants': dom_maxima,
                                  'global_search_max': maximum, 'pager_absent': True, 'back_restore': True,
                                  'stale_query_suppressed': True, 'pageerrors': errors}), flush=True)
                page.close()
        finally:
            browser.close()


if __name__ == '__main__':
    main()
