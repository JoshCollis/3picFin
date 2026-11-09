"""Synthetic browser acceptance for the actual assistant + existing title/request modal.
No real Jellyfin/Seerr/model calls. Run from the repository root with Python Playwright.
"""
import functools
import http.server
import os
import shutil
import threading
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(os.environ['AI_SCREENSHOT_DIR']) if os.environ.get('AI_SCREENSHOT_DIR') else None
if OUT:
    OUT.mkdir(parents=True, exist_ok=True)
LIBRARY_POSTERS = Path(os.environ['AI_LIBRARY_POSTERS_DIR']) if os.environ.get('AI_LIBRARY_POSTERS_DIR') else None

def screenshot(page, name):
    if OUT:
        page.screenshot(path=str(OUT/name))

class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *_):
        pass

server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Quiet, directory=str(ROOT)))
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    with sync_playwright() as p:
        channel = os.environ.get('AI_BROWSER_CHANNEL') or ('chrome' if shutil.which('google-chrome') else None)
        browser = p.chromium.launch(channel=channel)
        for width in (320, 390, 1280):
            page = browser.new_page(viewport={'width': width, 'height': 900}, reduced_motion='reduce')
            page.set_default_timeout(8000)
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            if LIBRARY_POSTERS:
                # Read-only samples fetched separately; no library credentials enter the browser fixture.
                page.route('**/tests/fixtures/assistant-poster.svg', lambda route: route.fulfill(content_type='image/jpeg', body=(LIBRARY_POSTERS/'arrival-thumbnail.jpg').read_bytes()))
                page.route('https://image.tmdb.org/**', lambda route: route.fulfill(content_type='image/jpeg', body=(LIBRARY_POSTERS/'severance-thumbnail.jpg').read_bytes()))
            else:
                page.route('https://image.tmdb.org/**', lambda route: route.fulfill(content_type='image/svg+xml', body=(ROOT/'tests/fixtures/assistant-series-poster.svg').read_text()))
            page.goto(f'http://127.0.0.1:{server.server_port}/tests/fixtures/assistant.html')
            # Native legacy/MUI shell layers must not cover the full-screen mobile chat.
            page.evaluate('''() => {
                const header=document.createElement('header');header.id='native-header-fixture';
                header.className='skinHeader MuiAppBar-root';header.textContent='Native Jellyfin header';
                header.style.cssText='position:fixed;top:0;left:0;right:0;height:76px;z-index:1100;background:#202634';
                document.body.appendChild(header);
            }''')
            page.get_by_role('button', name='Open 3pic Fin assistant').click()
            expect(page.get_by_role('heading', name='What are we watching?')).to_be_visible()
            for native_z in (999, 1100, 1200):
                page.locator('#native-header-fixture').evaluate('(el,z)=>el.style.zIndex=String(z)', native_z)
                assert page.locator('.fin-ai-tools button').evaluate_all('''buttons=>buttons.every(b=>{
                    const r=b.getBoundingClientRect();return b.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2));
                })'''), f'Chat controls occluded by native header at {width}px, z-index {native_z}'
            # Native modal surfaces stay above chat, rather than escalating to an arbitrary maximum z-index.
            assert page.evaluate('''() => {
                const modal=document.createElement('div');modal.style.cssText='position:fixed;inset:0;z-index:1300';
                document.body.appendChild(modal);const r=document.querySelector('.fin-ai-tools button').getBoundingClientRect();
                const above=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)===modal;modal.remove();return above;
            }''')
            expect(page.locator('.fin-ai-footer,.fin-ai-notice,.fin-ai-disclosure')).to_have_count(0)
            expect(page.get_by_role('button', name='Send', exact=True)).to_be_disabled()
            screenshot(page, f'welcome-{width}.png')
            box = page.get_by_role('textbox', name='Message the assistant')
            assert page.locator('.fin-ai-tools button').evaluate_all('(buttons)=>buttons.every(b=>b.getAttribute("aria-label") && b.clientWidth>=40 && b.clientHeight>=40)')
            page.wait_for_function('Array.from(document.querySelectorAll(".fin-ai-prompt")).some(p=>p.textContent.includes("Arrival"))')
            ideas = page.locator('.fin-ai-prompt').all_text_contents()
            page.get_by_role('button', name='Try other ideas', exact=True).click()
            assert page.locator('.fin-ai-prompt').all_text_contents() != ideas
            expect(page.get_by_role('button', name='Try other ideas', exact=True)).to_be_focused()
            selected_idea = page.locator('.fin-ai-prompt').first.inner_text()
            page.locator('.fin-ai-prompt').first.click()
            expect(box).to_have_value(selected_idea)
            expect(box).to_be_focused()
            assert page.evaluate('fixture.states.get(fixture.user).turnsToday') == 0
            box.fill('A line of text\n' * 12)
            assert box.evaluate('(e)=>e.clientHeight<=120 && e.scrollHeight>e.clientHeight')
            box.fill('   ')
            expect(page.get_by_role('button', name='Send', exact=True)).to_be_disabled()
            # Failed sends preserve the draft, remove the typing state and allow retry.
            page.evaluate('fixture.failMessage=true')
            box.fill('Something thoughtful tonight')
            page.get_by_role('button', name='Send', exact=True).click()
            expect(page.locator('.fin-ai-status')).to_contain_text('Could not reach the assistant')
            expect(box).to_have_value('Something thoughtful tonight')
            expect(page.locator('.fin-ai-typing')).to_have_count(0)
            expect(box).to_be_focused()
            page.evaluate('fixture.failMessage=false;fixture.delayMessage=true')
            box.fill('A thoughtful movie, and request seasons 2 and 3 of Severance')
            box.press('Shift+Enter')
            assert page.evaluate('fixture.releaseMessage === undefined')
            box.press('Enter')
            page.wait_for_function('typeof fixture.releaseMessage === "function"')
            expect(page.locator('.fin-ai-message--user')).to_contain_text('A thoughtful movie')
            expect(page.get_by_role('status', name='Finding your next watch')).to_be_visible()
            expect(box).to_have_value('')
            expect(box).to_be_disabled()
            screenshot(page, f'thinking-{width}.png')
            page.evaluate('fixture.releaseMessage()')
            confirm = page.get_by_role('button', name='Confirm request', exact=True)
            expect(confirm).to_be_visible()
            assert page.evaluate('fixture.posts.length') == 0
            expect(page.locator('.fin-ai-card')).to_have_count(2)
            page.locator('.fin-ai-poster img').evaluate_all('images => Promise.all(images.map(image => image.decode()))')
            # Every poster keeps the same portrait frame, independent of the card's text height.
            poster_sizes = page.locator('.fin-ai-poster').evaluate_all('''posters => posters.map(p => {
                const r=p.getBoundingClientRect(); const image=p.querySelector('img');
                return {width:r.width,height:r.height,fit:getComputedStyle(image).objectFit,
                    naturalWidth:image.naturalWidth,naturalHeight:image.naturalHeight};
            })''')
            for poster_size in poster_sizes:
                assert poster_size['width'] == (56 if width <= 520 else 64), poster_sizes
                assert abs(poster_size['height'] / poster_size['width'] - 1.5) < 0.01, poster_sizes
                assert poster_size['fit'] == 'cover', poster_sizes  # Uniform scaling; never fill/stretch.
                if LIBRARY_POSTERS:
                    assert (poster_size['naturalWidth'], poster_size['naturalHeight']) == (160, 240), poster_sizes
                    assert abs(poster_size['width']/160 - poster_size['height']/240) < 0.001, poster_sizes
            if LIBRARY_POSTERS:
                print(f"library posters {width}px: {poster_sizes}", flush=True)
            expect(page.locator('.fin-ai-selection')).to_contain_text('Seasons 2, 3')
            markdown = '''## Tonight’s picks

Try **Arrival**, *a thoughtful film*, or ~~skip spoilers~~. Use `inline code`.

- First pick
  - Nested pick
- Second pick

1. Start here
2. Then here

> A short quotation.

| Title | Why watch? | Runtime | Genre | Availability |
| --- | --- | --- | --- | --- |
| Arrival | Thoughtful science fiction | 116 min | Science fiction | In your library |

- [x] In the library
- [ ] Watch next

```html
<img src=x onerror=alert(1)>
```

---

[Safe link](https://example.test/info) [Unsafe](javascript:alert(1))

![Poster](https://tracker.invalid/image.jpg)

<script>window.markdownExecuted=true</script><iframe src="https://tracker.invalid/"></iframe>
'''
            page.evaluate('(text)=>fixture.states.get(fixture.user).threads[0].messages[1].text=text', markdown)
            page.get_by_role('button', name='Conversations', exact=True).click()
            page.locator('.fin-ai-history-title').click()
            rendered = page.locator('.fin-ai-markdown')
            expect(rendered.locator('h2')).to_have_text('Tonight’s picks')
            expect(rendered.locator('strong')).to_have_text('Arrival')
            expect(rendered.locator('em')).to_have_count(1)
            expect(rendered.locator('del')).to_have_count(1)
            expect(rendered.locator('ul ul li')).to_have_text('Nested pick')
            expect(rendered.locator('ol li')).to_have_count(2)
            expect(rendered.locator('blockquote')).to_contain_text('A short quotation')
            expect(rendered.locator('table tbody tr')).to_have_count(1)
            if width < 600:
                assert rendered.locator('.fin-ai-table').evaluate('(e)=>e.scrollWidth>e.clientWidth')
            expect(rendered.locator('input[type=checkbox]:disabled')).to_have_count(2)
            expect(rendered.locator('pre code')).to_contain_text('<img src=x onerror=alert(1)>')
            expect(rendered.locator('script,iframe,img,[onerror],[onclick]')).to_have_count(0)
            expect(rendered.locator('a[href^="javascript:"]')).to_have_count(0)
            expect(rendered.get_by_role('link', name='Safe link')).to_have_attribute('rel', 'noopener noreferrer')
            assert page.evaluate('window.markdownExecuted !== true')
            assert page.locator('.fin-ai-panel').evaluate('(e)=>e.scrollWidth<=e.clientWidth')
            screenshot(page, f'confirmation-{width}.png')
            page.locator('.fin-ai-transcript').evaluate('(e)=>e.scrollTop=0')
            screenshot(page, f'chat-{width}.png')
            # Composer and controls stay reachable in a short mobile viewport (e.g. an open keyboard).
            if width < 600:
                page.set_viewport_size({'width': width, 'height': 480})
                expect(page.get_by_role('button', name='Send', exact=True)).to_be_in_viewport()
                expect(page.get_by_role('button', name='Close', exact=True)).to_be_in_viewport()
                assert page.locator('.fin-ai-panel').evaluate('(e)=>e.scrollWidth<=e.clientWidth')
                page.set_viewport_size({'width': width, 'height': 900})
            confirm.click()
            expect(page.locator('.fin-ai-result')).to_contain_text('Request recorded')
            assert page.evaluate('fixture.posts.length') == 1
            # Direct local navigation is revalidated and returns to Jellyfin, not a model URL.
            page.get_by_role('button', name='Open in Jellyfin').click()
            expect(page.locator('.fin-ai-panel')).to_be_hidden()
            assert page.evaluate('fixture.opened.Id') == 'cccccccccccccccccccccccccccccccc'
            page.get_by_role('button', name='Open 3pic Fin assistant').click()
            # Missing-title actions reuse the real 3pic Fin discovery modal/controller.
            page.get_by_role('button', name='Severance', exact=True).click()
            dialog = page.locator('.fin-ai-flow #threepic-fin-details-dialog')
            expect(dialog).to_be_visible()
            expect(dialog).to_contain_text('Severance')
            page.locator('.fin-ai-flow #threepic-fin-details-request').click()
            expect(page.locator('.fin-ai-flow #threepic-fin-request-dialog')).to_be_visible()
            assert page.evaluate('fixture.ordinaryPosts.length') == 0
            screenshot(page, f'existing-modal-{width}.png')
            page.locator('.fin-ai-flow #threepic-fin-request-cancel').click()
            page.get_by_role('button', name='Preferences', exact=True).click()
            expect(page.get_by_role('heading', name='Your preferences')).to_be_visible()
            page.get_by_label('Use my watched titles, favourites and progress').uncheck()
            expect(page.get_by_label('Use my watched titles, favourites and progress')).not_to_be_checked()
            expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
            personalization = page.get_by_label('Use my watched titles, favourites and progress')
            learning = page.get_by_label('Learn my entertainment tastes between chats')
            personalization.check()
            expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
            # A delayed state read from opening the panel must not undo a confirmed save.
            page.evaluate('fixture.holdNextState=true')
            launcher = page.get_by_role('button', name='Open 3pic Fin assistant')
            page.get_by_role('button', name='Close', exact=True).click()
            launcher.click()
            page.wait_for_function('typeof fixture.releaseState === "function"')
            personalization.uncheck()
            expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
            page.evaluate('async () => { fixture.releaseState(); await new Promise(r=>setTimeout(r,50)); }')
            expect(personalization).not_to_be_checked()
            personalization.check()
            expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
            # Another tab disables activity while this tab still has a cached true value.
            page.evaluate('fixture.states.get(fixture.user).personalization=false')
            learning.uncheck()
            expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
            assert page.evaluate('fixture.lastSettings') == {'learnMemory': False}
            assert page.evaluate('fixture.states.get(fixture.user).personalization') is False
            expect(personalization).not_to_be_checked()
            personalization.check()
            expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
            # A failed save must restore the server value, even when reconciliation also fails.
            for fail_state in (False, True):
                page.evaluate('(failState)=>{fixture.failSettings=true;fixture.failState=failState}', fail_state)
                # Rollback is immediate; uncheck() would retry because its postcondition is intentionally false.
                personalization.click()
                expect(page.locator('.fin-ai-status')).to_contain_text('Privacy change could not be confirmed')
                expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
                expect(personalization).to_be_checked()
                assert page.evaluate('fixture.states.get(fixture.user).personalization') is True
            # An accepted write with a lost response is reconciled without retrying the POST.
            page.evaluate('fixture.failSettings=false;fixture.failState=false;fixture.loseSettingsResponse=true')
            personalization.uncheck()
            expect(page.locator('.fin-ai')).to_have_attribute('aria-busy', 'false')
            expect(personalization).not_to_be_checked()
            assert page.evaluate('fixture.states.get(fixture.user).personalization') is False
            page.evaluate('fixture.loseSettingsResponse=false')
            page.get_by_role('button', name='Back to chat', exact=True).click()
            page.get_by_role('button', name='Preferences', exact=True).click()
            screenshot(page, f'preferences-{width}.png')
            page.keyboard.press('Escape')
            expect(page.locator('.fin-ai-transcript')).to_be_visible()
            # History is an on-demand view; opening, starting fresh and restoring a chat all work.
            page.get_by_role('button', name='Conversations', exact=True).click()
            expect(page.get_by_role('heading', name='Conversations', exact=True)).to_be_visible()
            expect(page.locator('.fin-ai-history-row')).to_have_count(1)
            screenshot(page, f'history-{width}.png')
            page.get_by_role('button', name='New conversation', exact=True).click()
            expect(page.get_by_role('heading', name='What are we watching?')).to_be_visible()
            page.get_by_role('button', name='Conversations', exact=True).click()
            page.locator('.fin-ai-history-title').click()
            expect(page.locator('.fin-ai-message--user')).to_contain_text('A thoughtful movie')
            page.get_by_role('button', name='Conversations', exact=True).click()
            page.once('dialog', lambda dialog: dialog.accept())
            page.get_by_role('button', name='Delete conversation:', exact=False).click()
            expect(page.locator('.fin-ai-history-row')).to_have_count(0)
            expect(page.locator('.fin-ai-history')).to_contain_text('Your conversations will appear here')
            # Guard against poll-driven remounts and late responses across account switches.
            page.get_by_role('button', name='Back to chat').click()
            # Channel cards use the native channel identity and revalidate access before navigation.
            page.get_by_role('button', name='New conversation', exact=True).click()
            page.wait_for_function('!Array.from(document.querySelectorAll(".fin-ai-prompt")).some(p=>p.textContent.includes("Arrival"))')
            page.evaluate('fixture.channelReply=true;fixture.channelDenied=true')
            box.fill('What is on BBC One?')
            page.get_by_role('button', name='Send', exact=True).click()
            expect(page.get_by_role('button', name='BBC One', exact=True)).to_be_visible()
            page.get_by_role('button', name='Open in Jellyfin', exact=True).click()
            expect(page.locator('.fin-ai-status')).to_contain_text('Channel is no longer visible')
            expect(page.locator('.fin-ai-panel')).to_be_visible()
            page.evaluate('fixture.channelDenied=false')
            page.get_by_role('button', name='Open in Jellyfin', exact=True).click()
            expect(page.locator('.fin-ai-panel')).to_be_hidden()
            assert page.evaluate('fixture.opened.Type') == 'LiveTvChannel'
            assert page.evaluate('fixture.channelReads') == 2
            page.evaluate('fixture.channelReply=false')
            page.get_by_role('button', name='Open 3pic Fin assistant').click()
            page.evaluate("window.oldNewButton=document.querySelector('[aria-label=\"New conversation\"]')")
            page.get_by_role('button', name='New conversation', exact=True).click()
            page.evaluate('fixture.holdNext=true')
            box.fill('Alice private delayed question')
            page.get_by_role('button', name='Send', exact=True).click()
            page.wait_for_function('fixture.release !== null')
            page.evaluate("fixture.user='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';location.hash='#/movies'")
            page.wait_for_function("!document.querySelector('.fin-ai-panel')?.textContent.includes('Alice private')")
            page.evaluate('fixture.release()')
            page.get_by_role('button', name='Open 3pic Fin assistant').click()
            expect(page.locator('.fin-ai-panel')).not_to_contain_text('Alice private')
            box = page.get_by_role('textbox', name='Message the assistant')
            box.fill('<img src=x onerror="window.executed=true">')
            page.get_by_role('button', name='Send', exact=True).click()
            expect(page.locator('.fin-ai-message--user')).to_contain_text('<img src=x')
            assert page.locator('.fin-ai-message--user img').count() == 0
            page.evaluate('oldNewButton.click()')
            expect(page.locator('.fin-ai-message--user')).to_contain_text('<img src=x')
            # A logout clears private DOM synchronously, before its promise settles.
            page.evaluate('ApiClient.logout()')
            expect(page.locator('.fin-ai')).to_have_count(0)
            assert not errors, errors
            page.close()
            # Exercise the real admin markup and persisted selectors without any live configuration writes.
            admin = browser.new_page(viewport={'width': width, 'height': 900})
            admin.add_init_script('''
                window.adminConfig={RecentlyAddedLibraryIds:null,AiEnabled:true,AiBaseUrl:'https://provider.test/v1',AiModel:'gpt-6-sol'};
                window.ApiClient={getUrl:p=>p,getJSON:async()=>[],getPluginConfiguration:async()=>structuredClone(adminConfig),
                    updatePluginConfiguration:async(_id,c)=>{window.adminConfig=structuredClone(c);return {};}};
                window.Dashboard={showLoadingMsg(){},hideLoadingMsg(){},processPluginConfigurationUpdateResult(){}};
            ''')
            admin.goto(f'http://127.0.0.1:{server.server_port}/src/Rowan.Jellyfin.Plugin/Configuration/configPage.html')
            admin.locator('#RowanConfigPage').dispatch_event('pageshow')
            expect(admin.locator('#RowanSaveButton')).to_be_enabled()
            admin.locator('details').filter(has=admin.locator('#AiApiMode')).first.locator('summary').first.click()
            expect(admin.locator('#AiApiMode')).to_have_value('auto')
            expect(admin.locator('#AiReasoningEffort')).to_have_value('low')
            admin.locator('#AiApiMode').select_option('responses')
            admin.locator('#AiReasoningEffort').select_option('medium')
            admin.locator('#RowanSaveButton').click()
            expect(admin.locator('#RowanConfigStatus')).to_have_text('Configuration saved.')
            assert admin.evaluate('({mode:adminConfig.AiApiMode,effort:adminConfig.AiReasoningEffort})') == {'mode':'responses','effort':'medium'}
            admin.locator('#RowanConfigPage').dispatch_event('pageshow')
            expect(admin.locator('#AiApiMode')).to_have_value('responses')
            expect(admin.locator('#AiReasoningEffort')).to_have_value('medium')
            admin.close()
            print(f'assistant {width}px: confirmation, navigation, modal, preferences, account isolation PASS', flush=True)
        browser.close()
finally:
    server.shutdown()
    server.server_close()
