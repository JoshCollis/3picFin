import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
class Stub(BaseHTTPRequestHandler):
    def do_GET(self):
        with open('/shared/calls', 'a') as log: log.write(json.dumps([self.path,self.headers.get('X-API-User')])+'\n')
        body = json.dumps({'pageInfo': {'page':1,'pages':1,'results':1},'results':[{'id':42,'status':2,'type':'movie','requestedBy':{'username':'PRIVATE_OWNER'},'media':{'tmdbId':17,'mediaType':'movie','path':'PRIVATE_PATH'}}]}).encode()
        self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, format, *args): pass
ThreadingHTTPServer(('127.0.0.1',19876), Stub).serve_forever()
