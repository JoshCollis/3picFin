"""Disposable-only HDHR inventory and authenticated Dispatcharr metadata. NO streams."""
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import time
from urllib.parse import urlsplit, parse_qs

BASE = os.environ.get('STUB_BASE', 'http://fixtures:8080')
KEY = 'disposable-metadata-key'
mode = 'normal'
reads = []
uuid = lambda i: f'{i:08x}-1111-4111-8111-{i:012x}'


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_POST(self):
        global mode
        if urlsplit(self.path).path == '/test/mode':
            mode = parse_qs(urlsplit(self.path).query)['value'][0]
            return self.send(200, {'mode': mode})
        self.send(405, {})
    def send(self, status, data, content_type='application/json'):
        body = data if isinstance(data, bytes) else json.dumps(data).encode()
        self.send_response(status); self.send_header('Content-Type', content_type); self.send_header('Content-Length', str(len(body))); self.end_headers()
        try: self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError): pass
    def do_GET(self):
        path = urlsplit(self.path).path
        if path == '/test/state': return self.send(200, {'mode':mode, 'reads':reads})
        if path.startswith('/proxy/'):
            reads.append({'path':'STREAM-REQUEST-FORBIDDEN'}); return self.send(500,{})
        if path == '/discover.json': return self.send(200, {'FriendlyName':'Disposable HDHR', 'Manufacturer':'Fixture','ModelNumber':'HDHR3-US',
            'FirmwareName':'hdhomerun3_atsc','TunerCount':2,'DeviceID':'12345678','DeviceAuth':'fixture','BaseURL':BASE,'LineupURL':BASE+'/lineup.json'})
        if path == '/lineup_status.json': return self.send(200, {'ScanInProgress':0,'ScanPossible':0,'Source':'Cable','SourceList':['Cable']})
        if path == '/lineup.json': return self.send(200,[{'GuideNumber':str(i),'GuideName':'Synthetic station '+str(i),
            'URL':BASE+'/proxy/ts/stream/'+uuid(i)} for i in range(1,595)])
        if path == '/guide.xml':
            start=datetime.now(timezone.utc).replace(hour=0,minute=0,second=0,microsecond=0); end=start+timedelta(days=3)
            xml=['<?xml version="1.0"?><tv>']
            for i in range(1,595):
                xml.append(f'<channel id="{i}"><display-name>Synthetic station {i}</display-name></channel>')
                xml.append(f'<programme channel="{i}" start="{start:%Y%m%d%H%M%S} +0000" stop="{end:%Y%m%d%H%M%S} +0000"><title>Synthetic programme {i}</title></programme>')
            return self.send(200,(''.join(xml)+'</tv>').encode(),'application/xml')
        if path in ('/api/channels/channels/summary/', '/api/channels/groups/'):
            authenticated = self.headers.get('X-API-Key') == KEY
            reads.append({'path':path,'authenticated':authenticated})
            if not authenticated or mode == 'unauthorized': return self.send(401,{})
            if mode == 'dead': time.sleep(20)
            if mode == 'slow': time.sleep(1)
            if mode == 'oversized': return self.send(200,b'['+b' '*4_194_305+b']')
            if mode == 'redirect':
                self.send_response(302);self.send_header('Location',BASE+'/test/credential-trap');self.send_header('Content-Length','0');return self.end_headers()
            if path.endswith('/summary/'):
                rows=[{'uuid':uuid(i),'channel_group_id':None if i==594 or mode=='empty' else 4 if i<=300 else 5} for i in range(1,595)]
                if mode=='duplicate': rows.append(rows[0])
                if mode=='missing': rows.pop(0)
                if mode=='pagination': return self.send(200,{'next':'https://untrusted.invalid/','results':rows})
                return self.send(200,rows)
            groups=[] if mode=='empty' else [{'id':4,'name':'Synthetic A'},{'id':5,'name':'Synthetic B'}]
            if mode=='group-duplicate': groups.append(groups[0])
            return self.send(200,groups)
        if path == '/test/credential-trap': reads.append({'path':'REDIRECT-CREDENTIAL-TRAP'})
        self.send(404,{})


ThreadingHTTPServer(('0.0.0.0',8080),Handler).serve_forever()
