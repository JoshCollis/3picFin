"""Control-only source-capability regression, NOT native renderer/device/live acceptance.

Consumes the real generated toolbar, metadata, CSS and JS from a transformed Guide
asset. Only the surrounding host and API are synthetic; no network is permitted.
--require-usable retains the reported RED expectation for an HDHR-only response.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--asset', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--browser', choices=['chromium', 'webkit'], default='chromium')
parser.add_argument('--touch', action='store_true')
parser.add_argument('--require-usable', action='store_true')
parser.add_argument('--dispatcharr', action='store_true', help='Exercise the opt-in assigned-source response contract')
args = parser.parse_args()
assert os.environ.get('TMPDIR'), 'Set TMPDIR to approved scratch storage'
assert not args.output.resolve().is_relative_to(Path(__file__).resolve().parents[1])
os.umask(0o077)
args.output.mkdir(parents=True, exist_ok=True)
asset = args.asset.read_text()
script = asset[asset.index('var ThreePicFinChannelSearch ='):asset.index('\nvar L=!1;')]
toolbar = asset[asset.index('<div class="finGuideToolbar">'):asset.index('<div class="tvGuideHeader">')]
meta_start = asset.index('<div class="finGuideMeta">')
metadata = asset[meta_start:asset.index('</div>', meta_start) + len('</div>')]
css_start = asset.rindex('<style>', 0, asset.index('<div class="finGuideToolbar">'))
css = asset[css_start:asset.index('</style>', css_start) + len('</style>')]
receipt = {'scope': __doc__.splitlines()[0], 'assetSha256': hashlib.sha256(args.asset.read_bytes()).hexdigest(),
           'browser': args.browser, 'touch': args.touch, 'checks': []}

with sync_playwright() as p:
    kind = getattr(p, args.browser)
    executable = os.environ.get('FIN_GUIDE_CHROME', '/usr/bin/google-chrome') if args.browser == 'chromium' else os.environ.get('FIN_GUIDE_WEBKIT')
    browser = kind.launch(headless=True, **({'executable_path': executable} if executable else {}))
    try:
        page = browser.new_page(viewport={'width': 390 if args.touch else 1280, 'height': 850}, has_touch=args.touch)
        page.route('**/*', lambda route: route.abort())
        errors = []
        page.on('pageerror', lambda error: errors.append(str(error)))
        for mode in (['supported'] if args.dispatcharr else ['unavailable', 'empty', 'error', 'invalid', 'supported']):
            page.set_content(css + '<main class="tvguide">' + toolbar + metadata +
                             '<div class="guideVerticalScroller"><div class="channelsContainer"></div><div class="programGrid"></div></div></main>')
            page.add_script_tag(content=script)
            page.evaluate('''({mode,source}) => {
                window.rows = Array.from({length:594}, (_,i) => ({Id:(i+1).toString(16).padStart(32,'0'),
                    Name:'Synthetic station '+(i+1),ChannelNumber:String(i+1),Type:'TvChannel'}));
                window.user='alice'; window.mode=mode; window.reads=[]; window.categoryReads=0; window.failures=0;
                window.hold=false; window.release=null; window.commits=0;
                window.api={getCurrentUserId:()=>user,getUrl:path=>path,
                    getJSON:async()=>{categoryReads++; if(hold) await new Promise(resolve=>release=resolve);
                        if(window.mode==='error') throw Error('synthetic request failure');
                        return {Available:window.mode!=='unavailable',Source:window.mode==='invalid'?'unknown':source,
                            Items:window.mode==='supported'?[{Id:'group-'+'a'.repeat(64),Name:'Synthetic A',ChannelIds:rows.slice(0,300).map(c=>c.Id)},
                                {Id:'group-'+'b'.repeat(64),Name:'Synthetic B',ChannelIds:rows.slice(300).map(c=>c.Id)}]:[]};},
                    getLiveTvChannels:async q=>{reads.push(q);return {Items:rows.slice(q.StartIndex,q.StartIndex+q.Limit),TotalRecordCount:rows.length};}};
                window.refresh=offset=>{const ticket=scope.begin();scope.read(api,'getLiveTvChannels',{StartIndex:offset,Limit:50},ticket).then(data=>{
                    window.result=data;commits++;scope.update(data.TotalRecordCount,offset,data.Items.length);scope.finish(ticket);});};
                window.scope=finCreateGuideController({element:document.querySelector('.tvguide'),getApi:()=>api,limit:50,
                    jump:refresh,onError:()=>failures++});
                scope.mount();refresh(0);
            }''', {'mode':mode,'source':'jellyfin-assigned-channel-groups' if args.dispatcharr else 'jellyfin-m3u-group-title-cache'})
            page.wait_for_function('window.result && document.querySelector(".tvguide").getAttribute("aria-busy")==="false"')
            select = page.locator('.finGuideCategory')
            assert select.evaluate('el=>el.tagName') == 'SELECT'
            assert page.evaluate('result.TotalRecordCount') == 594
            state = {'mode': mode, 'disabled': select.is_disabled(), 'title': select.get_attribute('title'),
                     'visibleStatus': page.locator('.finGuideState').inner_text(), 'total': 594}
            receipt['checks'].append(state)
            (args.output/'receipt.json').write_text(json.dumps(receipt, indent=2))
            if mode != 'supported':
                assert select.is_disabled()
                assert select.locator('option').all_text_contents() == ['All categories']
                assert page.evaluate('failures') == 0  # Source failure is swallowed; not a working filter.
                if args.touch:
                    box = select.bounding_box(); page.touchscreen.tap(box['x']+20, box['y']+20)
                assert select.input_value() == ''
                if args.require_usable:
                    assert not select.is_disabled(), 'RED: no usable categories with the HDHR-only/unavailable response'
            else:
                assert not select.is_disabled()
                select.focus()
                page.keyboard.press('ArrowDown'); page.keyboard.press('Enter')
                page.wait_for_function('result.TotalRecordCount===300')
                assert page.evaluate('result.Items.every(c=>parseInt(c.Id,16)<=300)')
                select.select_option(label='Synthetic B')
                page.wait_for_function('result.TotalRecordCount===294')
                assert page.evaluate('result.Items.every(c=>parseInt(c.Id,16)>300)')
                field = page.locator('.finGuideSearchInput'); field.fill('Synthetic station 31'); field.press('Enter')
                page.wait_for_function('result.TotalRecordCount===10')
                assert page.evaluate('result.Items.every(c=>parseInt(c.Id,16)>=310 && parseInt(c.Id,16)<=319)')
                select.select_option(label='Synthetic A')
                page.wait_for_function('result.TotalRecordCount===1')
                assert page.evaluate('result.Items[0].ChannelNumber') == '31'
                page.locator('.finGuideClear').click()
                page.wait_for_function('result.TotalRecordCount===300')
                select.select_option('')
                page.wait_for_function('result.TotalRecordCount===594')
                assert page.evaluate('result.Items.length') <= 50
                # Identity changes invalidate an in-flight category response, including its DOM update.
                page.evaluate("scope.pause();hold=true;mode='supported';refresh(0)")
                page.wait_for_function('window.release!==null')
                before = page.evaluate('commits')
                page.evaluate("user='bob';scope.begin();release()")
                page.wait_for_timeout(100)
                assert page.evaluate('commits') == before
                page.evaluate('hold=false;refresh(0)')
                page.wait_for_function('result.TotalRecordCount===594 && document.querySelector(".tvguide").getAttribute("aria-busy")==="false"')
                assert select.input_value() == ''
            # Route pause/reentry is an existing way to retry source loading without the cached failure.
            page.evaluate("scope.pause();mode='supported';refresh(0)")
            page.wait_for_function('!document.querySelector(".finGuideCategory").disabled')
            assert select.locator('option').count() == 3
            state['routeRetrySupported'] = True
            page.evaluate('scope.pause();document.querySelector(".tvguide").remove()')
        assert not errors, errors
        receipt.update(result='PASS: generated control/source contract (synthetic metadata; not live/device acceptance)', errors=errors, browserVersion=browser.version)
        (args.output/'receipt.json').write_text(json.dumps(receipt, indent=2))
        print(receipt['result'])
    finally:
        browser.close()
