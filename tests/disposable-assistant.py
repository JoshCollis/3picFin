"""Real Jellyfin HTTP/DI + synthetic AI/Seerr acceptance. No live provider/media requests.
Build the Release DLL first. AI_JELLYFIN_IMAGE selects an already-cached image (--pull=never).
The temporary internal Docker network has no internet route; all fixtures are removed on exit.
"""
import http.server
import contextlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT/'src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll'
IMAGE = os.environ.get('AI_JELLYFIN_IMAGE', 'jellyfin/jellyfin:12.3')
SHARED = Path(sys.argv[2]) if len(sys.argv)>2 and sys.argv[1]=='--stub' else None

def records(directory, name):
    path = directory/name
    return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

def record(name, value):
    with (SHARED/name).open('a') as output: output.write(json.dumps(value)+'\n')

def docker(*args):
    return subprocess.check_output(['docker', *args], text=True, stderr=subprocess.PIPE).strip()

def norm(value):
    if isinstance(value, list): return [norm(v) for v in value]
    if isinstance(value, dict): return {k[0].lower()+k[1:]: norm(v) for k,v in value.items()}
    return value

def call(base, path, method='GET', body=None, token=None, legacy=False):
    auth = 'MediaBrowser Client="AssistantLab", Device="Lab", DeviceId="ai-lab", Version="1"'
    if token: auth += f', Token="{token}"'
    headers = {'Authorization':auth,'Content-Type':'application/json'}
    if legacy:
        headers.pop('Authorization'); headers['X-Emby-Token'] = token
    req = urllib.request.Request(base+path, method=method, headers=headers, data=None if body is None else json.dumps(body).encode())
    try:
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=110) as response:
            raw = response.read()
            return response.status, norm(json.loads(raw)) if raw and 'json' in response.headers.get('Content-Type','') else None, response.headers
    except urllib.error.HTTPError as error:
        error.read()  # Never print raw server errors or authentication material.
        return error.code, None, error.headers

def ok(result):
    assert result[0] in (200,201,204), f'Unexpected HTTP status {result[0]}'
    return result[1]

class Stub(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def respond(self, body, status=200):
        raw = json.dumps(body).encode(); self.send_response(status); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def do_GET(self):
        if self.headers.get('X-Api-Key') != 'disposable-only': return self.respond({},403)
        path = urllib.parse.urlparse(self.path)
        if '/user/jellyfin/' in path.path:
            user = str(uuid.UUID(path.path.rsplit('/',1)[1])); return self.respond({'id':json.loads((SHARED/'users.json').read_text())[user], 'permissions':32})
        if path.path.endswith('/tv/9'):
            return self.respond({'id':9,'name':'Synthetic remote show','firstAirDate':'2025-01-01','overview':'Disposable fixture only.','seasons':[{'seasonNumber':i} for i in (1,2,3)]})
        if path.path.endswith('/request'):
            requested = urllib.parse.parse_qs(path.query).get('requestedBy')
            items = [r for r in records(SHARED,'created.jsonl') if not requested or r['requestedBy']['id'] == int(requested[0])]
            return self.respond({'pageInfo':{'page':1,'pages':1,'results':len(items)},'results':items})
        return self.respond({},404)
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))))
        if self.path == '/api/v1/request':
            assert self.headers.get('X-Api-Key') == 'disposable-only'
            acting = int(self.headers['X-API-User']); record('posts.jsonl',[acting,body])
            if (SHARED/'quota').exists(): return self.respond({},403)
            created = {'id':42,'status':2,'is4k':body['is4k'],'type':body['mediaType'],'media':{'tmdbId':body['mediaId'],'mediaType':body['mediaType']},'seasons':[{'seasonNumber':n} for n in body.get('seasons',[])],'requestedBy':{'id':acting,'displayName':'Synthetic Alice','email':'NEVER_EXPOSE@example.test'}}
            record('created.jsonl',created); return self.respond({'id':42,'status':2},201)
        responses = self.path == '/v1/responses'
        if self.path not in ('/v1/chat/completions','/v1/responses') or self.headers.get('Authorization') != 'Bearer disposable-only': return self.respond({},403)
        record('models.jsonl',1)
        assert body['store'] is False and body['stream'] is False
        if responses:
            assert body['reasoning']['effort'] == 'low' and body['max_output_tokens'] == 8192
            assert body['include'] == ['reasoning.encrypted_content']
            messages = [({'role':'tool','tool_call_id':m['call_id'],'content':m['output']} if m.get('type')=='function_call_output' else m) for m in body['input']]
            assert all(t['strict'] is False and 'function' not in t for t in body['tools'])
        else:
            assert body['reasoning_effort'] == 'low'
            messages = body['messages']
        text = next(m['content'] for m in reversed(messages) if m.get('role')=='user')
        def tool(name, arguments, ident):
            if responses:
                return self.respond({'status':'completed','output':[
                    {'type':'reasoning','id':'rs_'+ident,'summary':[],'encrypted_content':'disposable-cipher'},
                    {'type':'function_call','id':'fc_'+ident,'call_id':ident,'name':name,'arguments':json.dumps(arguments)}]})
            self.respond({'choices':[{'finish_reason':'tool_calls','message':{'role':'assistant','content':None,'tool_calls':[{'id':ident,'type':'function','function':{'name':name,'arguments':json.dumps(arguments)}}]}}]})
        first_tool = body['tools'][0] if responses else body['tools'][0]['function']
        if first_tool['name'] == 'connection_probe': return tool('connection_probe',{},'probe')
        if messages[-1].get('role') != 'tool':
            if text.startswith('native-search'): return tool('search_library',{'query':'fixture','page':1},'search')
            if text.startswith('request-seasons'): return tool('prepare_request',{'key':'tv:9','seasons':[2,3]},'prepare')
            if text.startswith('remember'): return tool('remember_taste',{'preference':'Likes gentle science fiction'},'remember')
        elif messages[-1]['tool_call_id'] == 'search':
            items = json.loads(messages[-1]['content'])['items']; return tool('show_titles',{'keys':[i['key'] for i in items]},'show')
        if responses:
            return self.respond({'status':'completed','output':[{'type':'message','role':'assistant','content':[{'type':'output_text','text':'Verified lab reply for '+text}]}]})
        self.respond({'choices':[{'finish_reason':'stop','message':{'role':'assistant','content':'Verified lab reply for '+text}}]})

def main():
    assert DLL.is_file(), 'Build Release first'
    api_mode = os.environ.get('AI_API_MODE','chat'); assert api_mode in ('chat','responses')
    network = docker('network','create','--internal','3pic-fin-ai-'+uuid.uuid4().hex[:12])
    cid = None; sidecar = None
    def stop_container():
        nonlocal cid, sidecar
        if sidecar:
            subprocess.run(['docker','rm','-f',sidecar],stdout=subprocess.DEVNULL,check=True)
            sidecar = None
        if cid:
            subprocess.run(['docker','rm','-f',cid],stdout=subprocess.DEVNULL,check=True)
            cid = None
    try:
        with tempfile.TemporaryDirectory(prefix='fin-ai-runtime-', dir='/tmp/opencode') as tmp, contextlib.ExitStack() as cleanup:
            tmp = Path(tmp); plugins = tmp/'config/plugins/Rowan'; plugins.mkdir(parents=True); (tmp/'cache').mkdir()
            shutil.copy2(DLL,plugins/DLL.name)
            settings = plugins.parent/'configurations'; settings.mkdir()
            endpoint = 'http://127.0.0.1:19879'
            (settings/'Rowan.Jellyfin.Plugin.xml').write_text(f'<PluginConfiguration><HomeEnabled>false</HomeEnabled><DiscoveryPageEnabled>false</DiscoveryPageEnabled><AiEnabled>true</AiEnabled><AiBaseUrl>{endpoint}/v1</AiBaseUrl><AiModel>synthetic-tools</AiModel><AiApiMode>{api_mode}</AiApiMode><AiReasoningEffort>low</AiReasoningEffort><AiApiKey>disposable-only</AiApiKey><SeerrEnabled>true</SeerrEnabled><SeerrBaseUrl>{endpoint}</SeerrBaseUrl><SeerrApiKey>disposable-only</SeerrApiKey><SharedRequestsEnabled>true</SharedRequestsEnabled></PluginConfiguration>')
            for owner in ('alice','bob'):
                folder = tmp/'media'/owner; folder.mkdir(parents=True)
                subprocess.run(['ffmpeg','-nostdin','-loglevel','error','-f','lavfi','-i','color=c=black:s=160x90:r=1','-t','1','-c:v','mpeg4',str(folder/'fixture.mp4')],check=True)
                (folder/'fixture.nfo').write_text(f'<movie><title>{owner} private fixture</title><uniqueid type="tmdb" default="true">999999{1 if owner=="alice" else 2}</uniqueid></movie>')
            cid = docker('run','-d','--pull=never','--network',network,'--user',f'{os.getuid()}:{os.getgid()}','-v',f'{tmp}/config:/config','-v',f'{tmp}/cache:/cache','-v',f'{tmp}/media:/media:ro',IMAGE)
            cleanup.callback(stop_container)
            sidecar = docker('run','-d','--pull=never','--network','container:'+cid,'--user',f'{os.getuid()}:{os.getgid()}','-v',f'{Path(__file__).resolve()}:/stub.py:ro','-v',f'{tmp}:/shared','python:3.11-alpine','python','/stub.py','--stub','/shared')
            address = docker('inspect',cid,'--format','{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}')
            base = 'http://'+address+':8096'
            for _ in range(90):
                try:
                    if call(base,'/Startup/User')[0] == 200: break
                except OSError: pass
                time.sleep(1)
            else: raise AssertionError('Disposable Jellyfin startup timeout')
            passwords = {name:secrets.token_urlsafe(18) for name in ('alice','bob')}
            ok(call(base,'/Startup/User','POST',{'Name':'alice','Password':passwords['alice']})); ok(call(base,'/Startup/Complete','POST',{}))
            login = ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'alice','Pw':passwords['alice']})); alice = login['user']['id']; at = login['accessToken']
            config = ok(call(base,'/System/Configuration',token=at)); config['enableLegacyAuthorization'] = False
            ok(call(base,'/System/Configuration','POST',config,at))
            assert ok(call(base,'/System/Configuration',token=at))['enableLegacyAuthorization'] is False
            assert call(base,'/3picFin/Ai/State',token=at,legacy=True)[0] == 401
            bob = ok(call(base,'/Users/New','POST',{'Name':'bob','Password':passwords['bob']},at))['id']
            bt = ok(call(base,'/Users/AuthenticateByName','POST',{'Username':'bob','Pw':passwords['bob']}))['accessToken']
            (tmp/'users.json').write_text(json.dumps({str(uuid.UUID(alice)):71,str(uuid.UUID(bob)):72}))
            for owner in ('alice','bob'):
                ok(call(base,f'/Library/VirtualFolders?name={owner}&collectionType=movies&refreshLibrary=false','POST',{},at))
                ok(call(base,'/Library/VirtualFolders/Paths','POST',{'Name':owner,'Path':'/media/'+owner},at))
            folders = {f['name']:f['itemId'] for f in ok(call(base,'/Library/VirtualFolders',token=at))}
            for uid,owner in ((alice,'alice'),(bob,'bob')):
                policy = ok(call(base,'/Users/'+uid,token=at))['policy']; policy['enableAllFolders']=False; policy['enabledFolders']=[folders[owner]]
                ok(call(base,'/Users/'+uid+'/Policy','POST',policy,at))
            ok(call(base,'/Library/Refresh','POST',{},at))
            for _ in range(60):
                items = ok(call(base,'/Items?Recursive=true&IncludeItemTypes=Movie&UserId='+alice,token=at))['items']
                if items: break
                time.sleep(1)
            assert items, 'Disposable library scan did not finish'
            assert call(base,'/3picFin/Ai/State')[0] == 401
            ok(call(base,'/Auth/Keys?app=AssistantLab','POST',{},at))
            key = next(x['accessToken'] for x in ok(call(base,'/Auth/Keys',token=at))['items'] if x['appName']=='AssistantLab')
            assert call(base,'/3picFin/Ai/State',token=key)[0] == 403
            state = call(base,'/3picFin/Ai/State',token=at); ok(state); assert state[2].get('Cache-Control')=='private, no-store'
            assert ok(call(base,'/3picFin/Ai/Test','POST',{},at))['ready']
            assert call(base,'/3picFin/Ai/Test','POST',{},bt)[0] == 403
            for token,own,other in ((at,'alice','bob'),(bt,'bob','alice')):
                thread = ok(call(base,'/3picFin/Ai/Message','POST',{'text':'native-search fixture'},token))
                cards = thread['messages'][-1]['cards']; assert cards and all(own in c['title'] and other not in c['title'] for c in cards)
                assert ok(call(base,'/3picFin/Ai/Library/'+cards[0]['libraryItemId'],token=token))['title']==cards[0]['title']
            private = ok(call(base,'/3picFin/Ai/Message','POST',{'text':'remember Alice-private-marker'},at))
            assert call(base,'/3picFin/Ai/Threads/'+private['id'],token=bt)[0] == 404
            assert ok(call(base,'/3picFin/Ai/State',token=bt))['memory']==[]
            assert len(ok(call(base,'/3picFin/Ai/State',token=at))['memory'])==1
            for quota in (True,False):
                if quota: (tmp/'quota').touch()
                else: (tmp/'quota').unlink()
                thread = ok(call(base,'/3picFin/Ai/Message','POST',{'text':'request-seasons 2 and 3'},at)); proposal=thread['messages'][-1]['proposals'][0]
                body={'threadId':thread['id'],'proposalId':proposal['id'],'confirm':True}
                assert call(base,'/3picFin/Ai/Confirm','POST',body,bt)[0]==404
                assert call(base,'/3picFin/Ai/Confirm','POST',{**body,'userId':bob},at)[0]==400
                result=ok(call(base,'/3picFin/Ai/Confirm','POST',body,at)); assert result['messages'][-1]['proposals'][0]['state']==('denied' if quota else 'submitted')
                ok(call(base,'/3picFin/Ai/Confirm','POST',body,at))
            posts = records(tmp,'posts.jsonl')
            assert len(posts)==2 and all(actor==71 and body['seasons']==[2,3] for actor,body in posts)
            policy=ok(call(base,'/Users/'+bob,token=at))['policy'];policy['maxParentalRating']=10
            ok(call(base,'/Users/'+bob+'/Policy','POST',policy,at)); before=len(records(tmp,'models.jsonl'))
            assert call(base,'/3picFin/Ai/State',token=bt)[0]==403
            assert call(base,'/3picFin/Ai/Message','POST',{'text':'bypass restriction'},bt)[0]==403
            assert len(records(tmp,'models.jsonl'))==before
            assert call(base,'/3picFin/Web/assistant.js')[0]==200
            print(f'Disposable Jellyfin ({api_mode}): modern-only auth, DI/auth, disjoint library search/cards, private history/memory, tool probe, partial confirmation, quota denial, replay prevention and restricted-account rejection PASS')
    finally:
        stop_container()
        subprocess.run(['docker','network','rm',network],stdout=subprocess.DEVNULL,check=True)

if __name__=='__main__':
    if SHARED: http.server.ThreadingHTTPServer(('127.0.0.1',19879),Stub).serve_forever()
    else: main()
