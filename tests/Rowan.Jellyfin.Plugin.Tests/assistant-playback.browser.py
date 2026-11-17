"""Rendered playback lifecycle regression; optional assets extracted from an accepted DLL.

The native footer classes/queue route follow jellyfin-web 12.3 c06cb17b.
This fixture is deliberately synthetic, not signed-in live playback acceptance.
"""
import functools
import http.server
import json
import os
import shutil
import threading
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(os.environ['AI_SCREENSHOT_DIR']) if os.environ.get('AI_SCREENSHOT_DIR') else None
ASSETS = Path(os.environ['AI_ASSET_DIR']) if os.environ.get('AI_ASSET_DIR') else None


class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *_):
        pass


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Quiet, directory=str(ROOT)))
threading.Thread(target=server.serve_forever, daemon=True).start()
receipts = []
try:
    with sync_playwright() as p:
        channel = os.environ.get('AI_BROWSER_CHANNEL') or ('chrome' if shutil.which('google-chrome') else None)
        browser = p.chromium.launch(channel=channel)
        for width in (390, 1280):
            page = browser.new_page(viewport={'width': width, 'height': 800}, reduced_motion='reduce')
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            if ASSETS:
                page.route('**/src/Rowan.Jellyfin.Plugin/Web/*', lambda route: route.fulfill(
                    path=str(ASSETS / route.request.url.rsplit('/', 1)[-1])))
            page.goto(f'http://127.0.0.1:{server.server_port}/tests/fixtures/assistant.html')
            launcher = page.get_by_role('button', name='Open 3pic Fin assistant', include_hidden=True)
            expect(launcher).to_be_visible()
            page.evaluate('''() => {
                const style=document.createElement('style');style.textContent=`
                    .hide {display:none!important} .appfooter {position:fixed;bottom:0;left:0;right:0;z-index:1000}
                    .nowPlayingBar {background:#252a31;padding:12px;display:flex;justify-content:flex-end;gap:12px}
                    .nowPlayingBar button {min-width:56px;height:56px}
                    .nowPlayingBar-hidden {transform:translateY(100%)}
                `;document.head.appendChild(style);
                const footer=document.createElement('div');footer.className='appfooter';
                footer.innerHTML='<div class="nowPlayingBar hide nowPlayingBar-hidden"><span class="nowPlayingBarText">Track one</span><button class="playPauseButton">Pause</button><button class="nextTrackButton">Next</button><button class="stopButton">Stop</button></div>';
                document.body.appendChild(footer);
                const audio=document.createElement('audio');audio.hidden=true;audio.dataset.type='Audio';document.body.appendChild(audio);
                const result=document.createElement('div');result.dataset.type='Audio';result.textContent='Unrelated music result';document.body.appendChild(result);
            }''')
            # Neither a dormant player nor audio metadata/results should hide chat.
            page.wait_for_timeout(650)  # Cover the existing lifecycle poll as well as the footer observer.
            expect(launcher).to_be_visible()
            launcher.click()
            box = page.get_by_role('textbox', name='Message the assistant')
            box.fill('Keep my draft'); box.focus()
            page.evaluate("document.querySelector('.nowPlayingBar').classList.remove('hide','nowPlayingBar-hidden')")
            expect(page.locator('.fin-ai')).to_be_hidden()
            expect(page.locator('.fin-ai-panel')).to_be_hidden()
            expect(launcher).to_have_attribute('aria-expanded', 'false')
            assert page.evaluate("!document.querySelector('.fin-ai').contains(document.activeElement)")
            for state, route in [('start', '#/home'), ('pause', '#/search?query=music'),
                                 ('track-change', '#/details?id=cccccccccccccccccccccccccccccccc'),
                                 ('return', '#/home?fin=1')]:
                page.evaluate('''({state,route})=>{
                    location.hash=route;
                    if(state==='pause')document.querySelector('.playPauseButton').textContent='Resume';
                    if(state==='track-change')document.querySelector('.nowPlayingBarText').textContent='Track two';
                }''', {'state': state, 'route': route})
                expect(page.locator('.fin-ai')).to_be_hidden()
                assert page.locator('.nowPlayingBar button').evaluate_all('''buttons=>buttons.every(b=>{
                    const r=b.getBoundingClientRect();return b.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2));
                })'''), f'Obstructed {state} controls at {width}px'
                if OUT:
                    OUT.mkdir(parents=True, exist_ok=True)
                    page.screenshot(path=str(OUT/f'music-{state}-{width}.png'))
            # Full player disables the footer; it must still suppress chat.
            page.evaluate("location.hash='#/queue';document.querySelector('.nowPlayingBar').classList.add('hide','nowPlayingBar-hidden')")
            expect(page.locator('.fin-ai')).to_be_hidden()
            page.evaluate("location.hash='#/home';document.querySelector('.nowPlayingBar').classList.remove('hide','nowPlayingBar-hidden')")
            expect(page.locator('.fin-ai')).to_be_hidden()
            # Native stop first slides out, then adds .hide; stale metadata remains.
            page.evaluate("document.querySelector('.nowPlayingBar').classList.add('nowPlayingBar-hidden')")
            expect(launcher).to_be_visible()
            expect(page.locator('.fin-ai-panel')).to_be_hidden()
            if OUT:
                page.screenshot(path=str(OUT/f'music-stopped-{width}.png'))
            launcher.click(); expect(box).to_have_value('Keep my draft')
            # An assistant-owned title dialog must be disposed too, with no focus/overlay left behind.
            page.evaluate("fixture.states.get(fixture.user).threads=[{id:'dddddddddddddddddddddddddddddddd',title:'Test',messages:[{role:'assistant',cards:[{key:'tv:95396',mediaType:'tv',tmdbId:95396,title:'Severance'}]}]}]")
            page.get_by_role('button', name='Conversations', exact=True).click()
            page.get_by_role('button', name='Close', exact=True).click(); launcher.click()
            page.locator('.fin-ai-history-title').click()
            page.get_by_role('button', name='Severance', exact=True).click()
            expect(page.locator('.fin-ai-flow #threepic-fin-details-dialog')).to_be_visible()
            page.evaluate("document.querySelector('.nowPlayingBar').classList.remove('nowPlayingBar-hidden')")
            expect(page.locator('.fin-ai-flow')).to_have_count(0)
            expect(page.locator('.fin-ai')).to_be_hidden()
            page.evaluate("document.querySelector('.appfooter').remove()")
            expect(launcher).to_be_visible()
            expect(page.locator('.fin-ai-panel')).to_be_hidden()
            for route in ('#/video', '#/login', '#/dashboard', '#/webconfigurationpage?name=3pic'):
                page.evaluate('(route)=>location.hash=route', route)
                expect(page.locator('.fin-ai')).to_be_hidden()
            page.evaluate("location.hash='#/home';fixture.user='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'")
            expect(launcher).to_be_visible()
            launcher.click(); expect(box).to_have_value('')
            page.evaluate('ApiClient.logout()')
            expect(page.locator('.fin-ai')).to_have_count(0)
            assert not errors, errors
            receipts.append({'width': width, 'states': ['start', 'pause', 'track-change', 'routes', 'full-player', 'stop', 'dialog-disposal', 'identity', 'logout'], 'errors': errors})
            page.close()
        browser.close()
    if OUT:
        (OUT/'playback-regression.json').write_text(json.dumps(receipts, indent=2))
    print('Assistant playback lifecycle desktop/mobile PASS')
finally:
    server.shutdown()
    server.server_close()
