"""Loopback-only Seerr fixture for disposable-my-requests.py."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

shared = Path('/shared')
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        fixture = json.loads((shared / 'fixture.json').read_text())
        path = urlsplit(self.path)
        mapping = next((value for value in fixture['users'].values() if path.path == '/api/v1/user/jellyfin/' + value['jellyfin']), None)
        if mapping:
            body = {'id': mapping['seerr'], 'permissions': 64 if (shared / 'mismatch').exists() else 32}
        elif path.path == '/api/v1/request':
            params = parse_qs(path.query)
            mapped = params.get('requestedBy', [None])[0]
            identity = next((value for value in fixture['users'].values() if str(value['seerr']) == mapped), None)
            if not identity or self.headers.get('X-API-User') != mapped or params.get('take') != ['100'] or params.get('skip') != ['0']:
                self.send_error(403); return
            def row(item, *, status=2, media_status=5, status4k=0):
                return {'status': status, 'requestedBy': {'id': identity['seerr']}, 'media': {
                    'jellyfinMediaId': fixture['items'][item], 'status': media_status, 'status4k': status4k,
                    'privatePath': 'NEVER_EXPOSE_PATH'}}
            label = identity['label']
            other = 'bob' if label == 'alice' else 'alice'
            body = {'results': [row(label + '-own'), row(other + '-own'),
                row('restricted') if label == 'alice' else row(label + '-own'),
                row('unrequested4k', status=1, media_status=2, status4k=5)]}
            if (shared / 'mismatch').exists(): body['results'][0]['requestedBy']['id'] = 99999
        else:
            self.send_error(404); return
        with (shared / 'calls').open('a') as log:
            log.write(json.dumps({'path': self.path, 'user': self.headers.get('X-API-User')}) + '\n')
        raw = json.dumps(body).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)
    def log_message(self, format, *args): pass

ThreadingHTTPServer(('127.0.0.1', 19876), Handler).serve_forever()
