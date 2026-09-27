"""Loopback Seerr v3.4.1-shaped title-detail fixture; no real credentials."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

shared = Path('/shared')
class Stub(BaseHTTPRequestHandler):
    def do_GET(self):
        path = urlsplit(self.path).path
        users = json.loads((shared/'users.json').read_text())
        acting = self.headers.get('X-API-User')
        with (shared/'calls').open('a') as log:
            log.write(json.dumps({'path': path, 'acting': acting, 'key_present': self.headers.get('X-Api-Key') == 'disposable-only'}) + '\n')
        status, body = 200, {}
        if self.headers.get('X-Api-Key') != 'disposable-only': status = 403
        elif path.startswith('/api/v1/user/jellyfin/'):
            uid = path.rsplit('/', 1)[1]
            if uid not in users: status = 404
            elif acting: status = 403
            else: body = {'id': users[uid], 'permissions': 32}
        elif acting not in [str(x) for x in users.values()]: status = 403
        elif path.startswith('/api/v1/discover/'):
            kind = path.rsplit('/', 1)[1]
            movie = [{'id': 101, 'mediaType': 'movie', 'title': 'Probe movie', 'adult': False},
                     {'id': 104, 'mediaType': 'movie', 'title': 'Hidden child', 'adult': False}]
            tv = [{'id': 201, 'mediaType': 'tv', 'name': 'Probe series'}]
            body = {'page': 1, 'totalPages': 1, 'totalResults': 3,
                    'results': movie if kind == 'movies' else tv if kind == 'tv' else movie+tv}
        elif path.startswith('/api/v1/request'):
            body = {'pageInfo': {'pages': 1, 'results': 0}, 'results': []}
        elif path.startswith(('/api/v1/movie/', '/api/v1/tv/')):
            kind, raw = path.split('/')[-2:]
            ident = int(raw)
            body = {'id': ident, 'title': 'Probe movie', 'name': 'Probe series',
                    'overview': 'Safe overview', 'posterPath': '/probe.jpg',
                    'releaseDate': '2020-01-02', 'firstAirDate': '2020-01-02',
                    'mediaInfo': {'status': 5, 'jellyfinMediaId': json.loads((shared/'hints.json').read_text()).get(str(ident), '')},
                    'seasons': [{'seasonNumber': 0}, {'seasonNumber': 1}, {'seasonNumber': 2}],
                    'privatePath': 'NEVER_EXPOSE_PATH', 'secret': 'NEVER_EXPOSE_SECRET'}
            if kind == 'movie': body.pop('seasons')
            else: body['contentRatings'] = {'results': [{'iso_3166_1': 'US', 'rating': 'TV-G'}]}
        else: status = 404
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def log_message(self, format, *args): pass
ThreadingHTTPServer(('127.0.0.1', 19878), Stub).serve_forever()
