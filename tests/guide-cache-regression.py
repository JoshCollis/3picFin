"""Real HTTP cache regression, called only by the disposable hosted recipe.

No Playwright routing (which disables HTTP cache). A loopback proxy controls only
the lab guide response/index; it never forwards to production or exposes tokens.
"""
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import re
import threading
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


def run(browser, base, info, login, seed, original, transformed, asset, output):
    assert urlsplit(base).hostname == '127.0.0.1'
    state = {'warm': True, 'preload': False}
    upstream_guide_reads = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args): pass

        def do_GET(self): self.forward()
        def do_POST(self): self.forward()
        def do_HEAD(self): self.forward()

        def forward(self):
            path = urlsplit(self.path).path
            if any(part in path.lower() for part in ('/stream', '/playbackinfo', 'never-play')):
                self.send_error(403); return
            if path == '/cache-seed':
                body = ('<html><body><script src="/web/'+asset+'"></script></body></html>').encode()
                content_type = 'text/html'
            elif path == '/web/'+asset:
                upstream_guide_reads.append({'warm': state['warm'], 'revision': 'fin-guide=' in self.path})
                body = original if state['warm'] else transformed
                content_type = 'text/javascript'
            else:
                headers = {k: v for k, v in self.headers.items() if k.lower() not in ('host', 'connection', 'accept-encoding', 'content-length')}
                data = self.rfile.read(int(self.headers['Content-Length'])) if self.headers.get('Content-Length') else None
                try:
                    response = urlopen(Request(base+self.path, data=data, headers=headers, method=self.command), timeout=30)
                except HTTPError as error: response = error
                with response:
                    body = response.read()
                    content_type = response.headers.get('Content-Type', 'application/octet-stream')
                    status = response.status
                if status != 200:
                    self.send_response(status); self.send_header('Content-Length', str(len(body))); self.end_headers()
                    if self.command != 'HEAD': self.wfile.write(body)
                    return
                if path in ('/web/', '/web/index.html') and not state['preload']:
                    body = re.sub(rb'<script data-threepic-fin-guide-module[^>]*></script>', b'', body)
            self.send_response(200)
            self.send_header('Content-Type', content_type)
            self.send_header('Cache-Control', 'public, max-age=31536000, immutable' if path == '/web/'+asset else 'no-cache')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            if self.command != 'HEAD': self.wfile.write(body)

    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
    proxy = 'http://127.0.0.1:'+str(server.server_port)
    receipts = []
    try:
        for label, preload in [('red-cached-original', False), ('green-versioned-guide', True)]:
            context = browser.new_context(service_workers='block', viewport={'width': 1440, 'height': 950},
                user_agent='Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36')
            try:
                page = context.new_page(); seed(page, proxy, info, login)
                cdp = context.new_cdp_session(page)
                cdp.send('Network.enable'); cdp.send('Debugger.enable')
                scripts = []; responses = []; cache_hits = set(); errors = []
                cdp.on('Debugger.scriptParsed', lambda event: scripts.append(event))
                cdp.on('Network.requestServedFromCache', lambda event: cache_hits.add(event['requestId']))
                cdp.on('Network.responseReceived', lambda event: responses.append(event))
                page.on('pageerror', lambda error: errors.append(str(error)))
                state.update(warm=True, preload=preload)
                page.goto(proxy+'/cache-seed'); page.wait_for_function('!!window.webpackChunk')
                scripts.clear(); responses.clear(); cache_hits.clear()
                state['warm'] = False
                # Server now returns transformed bytes for a fresh canonical GET,
                # while the browser still has the original immutable response.
                page.goto(proxy+'/web/#/livetv?collectionType=livetv&tab=1')
                page.wait_for_selector('.programCell', timeout=60000)
                executed = []
                for script in scripts:
                    if asset in script.get('url', ''):
                        source = cdp.send('Debugger.getScriptSource', {'scriptId': script['scriptId']})['scriptSource']
                        executed.append({'url': script['url'], 'sha256': hashlib.sha256(source.encode()).hexdigest()})
                network = [{'url': event['response']['url'], 'fromDiskCache': event['response'].get('fromDiskCache', False),
                    'servedFromCache': event['requestId'] in cache_hits, 'fromServiceWorker': event['response'].get('fromServiceWorker', False)}
                    for event in responses if asset in event['response']['url']]
                expected = hashlib.sha256(transformed if preload else original).hexdigest()
                assert [entry['sha256'] for entry in executed] == [expected], executed
                assert page.locator('.finGuideSearchInput:visible').count() == int(preload)
                if preload:
                    assert 'fin-guide=' in executed[0]['url']
                    assert page.locator('.channelPrograms:visible').count() == 50
                    assert not any(not ('fin-guide=' in entry['url']) for entry in network)
                else:
                    assert any(entry['fromDiskCache'] or entry['servedFromCache'] for entry in network), network
                    assert page.locator('.channelPrograms:visible').count() == 500
                assert not errors, errors
                page.screenshot(path=str(output/(label+'.png')))
                receipts.append({'case': label, 'executed': executed, 'network': network, 'rows': page.locator('.channelPrograms:visible').count(), 'errors': errors})
                (output/'http-cache-regression.json').write_text(json.dumps({'scope': 'real Chromium HTTP cache, isolated lab proxy, service workers blocked; NOT owner Mac root-cause proof', 'cases': receipts}, indent=2))
            finally: context.close()
    finally:
        server.shutdown(); server.server_close(); thread.join(timeout=5)
    return receipts
