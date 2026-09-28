"""Chromium regression: native Search churn must not turn adapter scans into typing stalls."""
import json
from pathlib import Path
from playwright.sync_api import sync_playwright

WEB = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
URL = 'http://localhost:8765/web/index.html#/search?query=Alien'
HTML = '''<!doctype html><html><head></head><body><main id="searchPage">
<input id="searchTextInput" value="Alien"><div id="native-results"></div></main></body></html>'''


def run_case(browser, enabled):
    page = browser.new_page()
    page.route('http://localhost:8765/**', lambda route: route.fulfill(status=200, content_type='text/html', body=HTML))
    page.goto(URL)
    page.evaluate('''() => {
        window.identity = 'alice'; window.requests = []; window.pending = [];
        window.ApiClient = { getCurrentUserId:()=>identity, getUrl:path=>'/jellyfin/'+path,
            getJSON:url => url.includes('System/Info/Public') ? Promise.resolve({Version:'12.1.0'}) :
                new Promise(resolve => { requests.push(url); pending.push(resolve); }), logout:()=>Promise.resolve() };
        const native = document.querySelector('#native-results');
        const fragment = document.createDocumentFragment();
        for (let i=0; i<1800; i++) {
            const card=document.createElement('div'); card.className='card'; card.textContent='Native '+i;
            fragment.append(card);
        }
        native.append(fragment);
        window.scans=0;
        const original=Document.prototype.querySelectorAll;
        Document.prototype.querySelectorAll=function(...args) { if (args[0]==='#searchPage' || args[0]==='#searchPage #searchTextInput') scans++; return original.apply(this,args); };
        window.longTasks=[];
        new PerformanceObserver(list=>longTasks.push(...list.getEntries().map(e=>e.duration))).observe({entryTypes:['longtask']});
    }''')
    if enabled:
        page.add_script_tag(content=(WEB / 'global-search-addon.js').read_text())
        page.add_script_tag(content=(WEB / 'search-adapter.js').read_text())
        page.wait_for_selector('.threepic-fin-search')
        assert page.evaluate('requests.length') == 1
    result = page.evaluate('''async () => {
        const input=document.querySelector('#searchTextInput');
        const native=document.querySelector('#native-results');
        const delays=[]; let mutations=0;
        const start=performance.now();
        await new Promise(resolve => {
            function churn() {
                const cell=native.children[mutations % native.children.length];
                cell.replaceChildren(document.createTextNode('Result '+mutations));
                // Simulate React's incremental Search result updates and native input events.
                if (mutations % 4 === 0) {
                    const query='Alien'+mutations;
                    const before=performance.now();
                    input.value=query;
                    history.replaceState({},'', '#/search?query='+query);
                    input.dispatchEvent(new Event('input',{bubbles:true}));
                    requestAnimationFrame(() => delays.push(performance.now()-before));
                }
                mutations++;
                if (mutations<120) setTimeout(churn, 0); else resolve();
            }
            churn();
        });
        await new Promise(resolve=>setTimeout(resolve, 250));
        return {elapsed:performance.now()-start, scans, longTasks, maxFrameDelay:Math.max(...delays),
            input:input.value, query:new URL(location.hash.slice(1),location.origin).searchParams.get('query'),
            mounted:!!document.querySelector('.threepic-fin-search'), requests:requests.length};
    }''')
    if enabled:
        page.evaluate('''() => {
            window.oldSection=document.querySelector('.threepic-fin-search');
            document.querySelector('#searchPage').replaceChildren(document.querySelector('#searchTextInput'), document.createElement('div'));
        }''')
        page.wait_for_function("document.querySelector('.threepic-fin-search') && document.querySelector('.threepic-fin-search') !== oldSection")
        result['rerenderRemounted'] = True
        page.evaluate("window.__threePicFinSearchAdapter.dispose()")
        result['disposed'] = page.evaluate("!document.querySelector('.threepic-fin-search')")
    page.close()
    return result


with sync_playwright() as p:
    browser=p.chromium.launch(headless=True)
    try:
        baseline=run_case(browser, False)
        plugin=run_case(browser, True)
    finally:
        browser.close()
    print(json.dumps({'baseline':baseline,'plugin':plugin}, indent=2))
    assert plugin['input'] == plugin['query'] == 'Alien116'
    assert plugin['mounted'] and plugin['requests'] >= 1
    assert plugin['rerenderRemounted'] and plugin['disposed']
    # Input changes may scan, but native results must not scan on every mutation task.
    assert plugin['scans'] - baseline['scans'] < 180, 'Search adapter scanned the whole document on native result mutations'
    assert plugin['maxFrameDelay'] < 200, 'Search input lost frame responsiveness'
