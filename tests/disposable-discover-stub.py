"""Seerr v3.4.1-shaped, loopback-only Discover fixture for disposable HTTP probe."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit, parse_qs

shared = Path('/shared')
class Stub(BaseHTTPRequestHandler):
    def do_GET(self):
        url = urlsplit(self.path)
        users = json.loads((shared/'users.json').read_text())
        identity = self.headers.get('X-API-User')
        with (shared/'calls').open('a') as log:
            log.write(json.dumps({'path': self.path, 'acting': identity, 'key_present': self.headers.get('X-Api-Key') == 'disposable-only'})+'\n')
        body = None
        status = 200
        if self.headers.get('X-Api-Key') != 'disposable-only': status = 403
        elif url.path.startswith('/api/v1/user/jellyfin/'):
            guid = url.path.rsplit('/',1)[1]
            status = 200 if guid in users else 404
            body = {'id':users[guid], 'permissions':32} if status == 200 else {}
            if identity is not None: status = 403
        elif identity not in [str(n) for n in users.values()]: status = 403
        elif url.path.startswith('/api/v1/discover/'):
            kind = url.path.rsplit('/',1)[1]
            page = int(parse_qs(url.query).get('page',['1'])[0])
            movie = [
                {'id':101,'mediaType':'movie','title':'Family Film','adult':False,'posterPath':'/family.jpg','privatePath':'SECRET_PATH'},
                {'id':102,'mediaType':'movie','title':'Adult Film','adult':True},
                {'id':103,'mediaType':'movie','title':'Unrated Film'},
                {'id':104,'mediaType':'movie','title':'Blocked Film','adult':False,'mediaInfo':{'status':6}},
            ]
            tv = [
                {'id':201,'mediaType':'tv','name':'Family Series'},
                {'id':202,'mediaType':'tv','name':'Teen Series'},
                {'id':203,'mediaType':'tv','name':'Unrated Series'},
                {'id':204,'mediaType':'tv','name':'Adult Series','adult':True},
                {'id':205,'mediaType':'tv','name':'Blocked Series','mediaInfo':{'status':6}},
                {'id':206,'mediaType':'tv','name':'Mismatched Series'},
            ]
            if page == 2: entries = [{'id':300+i,'mediaType':'tv','name':f'Failed Detail {i}'} for i in range(25)]
            else: entries = (movie if kind == 'movies' else tv if kind == 'tv' else movie+tv) if page == 1 else []
            body = {'page':page,'totalPages':3 if page == 2 else 1,'totalResults':len(entries),'results':entries}
            if kind not in ('movies','tv','trending'): status = 404
        elif url.path.startswith('/api/v1/tv/'):
            id = int(url.path.rsplit('/',1)[1]); rating = {201:'TV-PG',202:'TV-14',203:'',206:'TV-G'}.get(id, '')
            body = {'id':999 if id == 206 else id,'contentRatings':{'results':[{'iso_3166_1':'US','rating':rating}] if rating else []},'privatePath':'SECRET_PATH'}
            if id >= 300: status = 503
        else: status = 404
        raw = json.dumps(body or {}).encode()
        self.send_response(status); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def log_message(self, format, *args): pass
ThreadingHTTPServer(('127.0.0.1',19877),Stub).serve_forever()
