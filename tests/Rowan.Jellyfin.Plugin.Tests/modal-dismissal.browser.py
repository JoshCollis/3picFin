"""All four plugin surfaces, real HTML dialog top layer, no network or requests."""
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
with sync_playwright() as p:
    browser = p.chromium.launch()
    for width in (390, 1280):
        page = browser.new_page(viewport={'width': width, 'height': 850}, has_touch=True)
        page.set_default_timeout(4000)
        page.set_content('<button id="trigger">Inspect</button>' + (web / 'discovery.html').read_text())
        page.add_style_tag(path=str(web / 'discovery.css'))
        page.add_script_tag(path=str(web / 'discovery.js'))
        page.evaluate('''() => {
            window.calls=[]; window.posts=[]; window.pending={};
            window.item={MediaType:'movie',TmdbId:9,Title:'Synthetic title'};
            window.api={getCurrentUserId:()=> 'synthetic', getUrl:p=>p,
                getJSON:(url,opts)=>{calls.push(url); return new Promise(resolve=>pending[url]=resolve)},
                ajax:args=>{posts.push(args);return new Promise(resolve=>window.finishPost=resolve)}};
            window.controller=ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'),api,{userId:'synthetic',deferInitialLoad:true});
            document.querySelector('#trigger').onclick=()=>controller.openDetails(item,document.querySelector('#trigger'));
        }''')
        def outside():
            if width == 390:
                page.touchscreen.tap(2, 2)
            else:
                page.mouse.click(2, 2)
        def opened(name):
            dialog = page.locator('#threepic-fin-' + name)
            expect(dialog).to_be_visible()
            return dialog
        def open_surface(name):
            if name == 'details-dialog':
                page.locator('#trigger').click()  # opening click cannot dismiss
            elif name == 'request-dialog':
                page.evaluate('controller.openRequest(item,document.querySelector("#trigger"))')
            else:
                page.locator('#threepic-fin-' + name.replace('-panel', '-tab')).click()
        for name in ('details-dialog', 'request-dialog', 'downloads-panel', 'calendar-panel'):
            open_surface(name)
            dialog = opened(name)
            assert page.evaluate('getComputedStyle(document.documentElement).overflow') == 'hidden'
            rect = dialog.bounding_box()
            page.mouse.click(rect['x'] + 8, rect['y'] + 8)
            expect(dialog).to_be_visible()
            page.mouse.move(rect['x'] + 8, rect['y'] + 8)
            page.mouse.down(); page.mouse.move(2, 2); page.mouse.up()
            expect(dialog).to_be_visible()  # down inside / drag out
            page.mouse.move(2, 2); page.mouse.down()
            dialog.dispatch_event('pointercancel')
            page.mouse.up()
            expect(dialog).to_be_visible()
            # A nested native HTML modal owns the top layer; underlying plugin stays open.
            dialog.evaluate('''parent => {const d=document.createElement('dialog'); d.id='nested';d.innerHTML='<button>Nested</button>';parent.append(d); d.showModal()}''')
            outside()
            expect(dialog).to_be_visible()
            page.keyboard.press('Escape')
            expect(page.locator('#nested').last).not_to_be_visible()
            expect(dialog).to_be_visible()
            outside()
            expect(dialog).not_to_be_visible()
            assert page.evaluate('getComputedStyle(document.documentElement).overflow') != 'hidden'
            assert page.evaluate('posts.length') == 0
            # Late completion must not revive UI/actions or trigger any POST.
            page.evaluate('''() => {for(const resolve of Object.values(pending))resolve({MediaType:'movie',TmdbId:9,Title:'Late',CanRequest:true,CanRequest4k:false,MediaStatus:1,Seasons:[],Radarr:{Items:[]},Sonarr:{Items:[]}}); pending={}}''')
            expect(dialog).not_to_be_visible()
            if name in ('details-dialog', 'request-dialog'):
                expect(page.locator('#trigger')).to_be_focused()
            open_surface(name)
            page.keyboard.press('Escape')
            expect(dialog).not_to_be_visible()
            assert page.evaluate('posts.length') == 0
        # Two plugin-owned surfaces: dismiss only the top one and retain the lock.
        open_surface('downloads-panel')
        page.evaluate('controller.openDetails(item,document.querySelector("#threepic-fin-downloads-refresh"))')
        outside()
        expect(page.locator('#threepic-fin-details-dialog')).not_to_be_visible()
        expect(page.locator('#threepic-fin-downloads-panel')).to_be_visible()
        expect(page.locator('#threepic-fin-downloads-refresh')).to_be_focused()
        assert page.evaluate('getComputedStyle(document.documentElement).overflow') == 'hidden'
        outside()
        expect(page.locator('#threepic-fin-downloads-panel')).not_to_be_visible()
        # Native remote Back commands bubble from the focused control, not document.
        page.evaluate('controller.openDetails(item,document.querySelector("#trigger"))')
        page.evaluate('document.activeElement.dispatchEvent(new CustomEvent("command",{bubbles:true,cancelable:true,detail:{command:"back"}}))')
        expect(page.locator('#threepic-fin-details-dialog')).not_to_be_visible()
        # Closing a submitted request detaches UI work, not a new or repeated submission.
        page.evaluate('controller.openRequest(item,document.querySelector("#trigger")); pending["3picFin/RequestOptions"]({CanRequest:true,CanRequest4k:false,MediaStatus:1,Seasons:[]})')
        submit = page.locator('#threepic-fin-request-submit')
        expect(submit).to_be_enabled()
        submit.click()
        outside()
        expect(page.locator('#threepic-fin-request-dialog')).not_to_be_visible()
        before = page.evaluate('calls.length')
        page.evaluate('finishPost({Id:42})')
        page.wait_for_timeout(50)
        assert page.evaluate('posts.length') == 1
        assert page.evaluate('calls.length') == before, 'closed request started stale read-back'
        page.evaluate('controller()')
        page.close()
        print('modal dismissal', width, 'PASS', flush=True)
    browser.close()
