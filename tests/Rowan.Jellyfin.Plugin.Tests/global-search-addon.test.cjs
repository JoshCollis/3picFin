const {test} = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Rowan.Jellyfin.Plugin/Web/global-search-addon.js'), 'utf8');
class Node {
    constructor(tag) { this.tagName=tag; this.children=[]; this.handlers={}; this.attributes={}; this._text=''; this.parent=null;
        this.classList={replace:(oldName,newName)=>{ this.className=(this.className||'').split(' ').map(name=>name===oldName?newName:name).join(' '); }}; }
    get textContent() { return this._text || this.children.map(n=>n.textContent).join(''); }
    set textContent(v) { this._text=v; this.children=[]; }
    appendChild(n) { this.children.push(n); n.parent=this; return n; }
    insertBefore(n, before) { this.children.splice(this.children.indexOf(before),0,n); n.parent=this; return n; }
    replaceChildren(...nodes) { this.children=nodes; this._text=''; nodes.forEach(n=>n.parent=this); }
    remove() { if(this.parent) this.parent.children.splice(this.parent.children.indexOf(this),1); this.parent=null; }
    setAttribute(k,v) { this.attributes[k]=v; }
    addEventListener(k,f) { (this.handlers[k]??=[]).push(f); }
    removeEventListener(k,f) { this.handlers[k]=(this.handlers[k]||[]).filter(x=>x!==f); }
    click() { for(const f of this.handlers.click||[]) f(); }
}
const tick = () => new Promise(resolve=>setImmediate(resolve));
function fixture() {
    const context={document:{createElement:tag=>new Node(tag)},module:{exports:{}}};
    vm.runInNewContext(source,context);
    const root=new Node('div'), native=new Node('native'); root.appendChild(native);
    const pending=[], actions=[];
    const api={getUrl:(p,q)=>`/jellyfin/${p}?query=${q.query}&page=${q.page}`,
        getJSON:url=>new Promise((resolve,reject)=>pending.push({url,resolve,reject}))};
    let user='alice'; const addon=context.module.exports.createSearchAddon();
    const mount=(extra={})=>addon.mount({root,apiClient:api,userId:'alice',sessionUserId:()=>user,
        query:'Alien',parentId:null,collectionType:null,enabled:true,
        requestAction:(item,button)=>actions.push([item,button]),...extra});
    return {root,native,pending,actions,addon,mount,setUser:id=>user=id};
}
const result=items=>({Items:items,TotalPages:1});
const movie=(id,title='Alien')=>({TmdbId:id,MediaType:'movie',Title:title});
test('requires explicit global scope and signed-in host without touching native results',()=>{
    const f=fixture();
    for(const opts of [{enabled:false},{parentId:undefined},{parentId:'library'},{collectionType:'movies'},
        {query:null},{requestAction:null},{sessionUserId:()=>null}]) assert.equal(f.mount(opts),false);
    assert.deepEqual(f.root.children,[f.native]); assert.equal(f.pending.length,0);
});
test('renders catalog title and safe art, dedupes catalog only, leaves native card intact',async()=>{
    const f=fixture(); assert.equal(f.mount(),true);
    f.pending[0].resolve(result([movie(1),{...movie(2,'Second'),PosterPath:'/image.jpg'},movie(2,'Again')])); await tick();
    const cards=f.root.children[1].children[1].children[0];
    assert.equal(cards.children.length,2); assert.equal(f.root.children[0],f.native);
    assert.equal(cards.children[1].children[0].src,'https://image.tmdb.org/t/p/w342/image.jpg');
    assert.equal(cards.children[1].children[1].textContent,'Second');
});
test('query, scope, user and teardown invalidate detached request actions and late responses',async()=>{
    const f=fixture(); f.mount(); f.pending[0].resolve(result([movie(2)])); await tick();
    const button=f.root.children[1].children[1].children[0].children[0].children[1];
    f.addon.update({query:'New'}); button.click(); assert.equal(f.actions.length,0);
    f.pending[1].resolve(result([movie(3)])); await tick();
    const next=f.root.children[1].children[1].children[0].children[0].children[1];
    f.addon.update({collectionType:'movies'}); next.click(); assert.equal(f.actions.length,0);
    assert.deepEqual(f.root.children,[f.native]);
    f.mount(); f.setUser('bob'); f.pending[2].resolve(result([movie(4)])); await tick();
    assert.deepEqual(f.root.children,[f.native]);
});
test('malformed query fails closed rather than retaining old action',async()=>{
    const f=fixture(); f.mount(); f.pending[0].resolve(result([movie(2)])); await tick();
    const old=f.root.children[1].children[1].children[0].children[0].children[1];
    f.addon.update({query:null}); old.click(); assert.equal(f.actions.length,0);
    assert.deepEqual(f.root.children,[f.native]);
});
