// Synthetic host styles only; this does not claim parity with a hosted ElegantFin installation.
// PLAYWRIGHT_BROWSERS_PATH="$PWD/.qa-tools/browsers" node tests/Rowan.Jellyfin.Plugin.Tests/discovery-pilot.browser.cjs
const { chromium } = require('../../.qa-tools/node_modules/playwright');
const fs = require('node:fs');
const { execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const web = 'src/Rowan.Jellyfin.Plugin/Web/';
const themed = process.env.FIN_PUBLIC_THEME === '1';
const output = '.qa-tools/evidence' + (themed ? '/public-theme' : '');
const crypto = require('node:crypto');
const themes = themed ? ['779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643','525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841'].map((hash,i) => {
 const css = fs.readFileSync(`.qa-tools/elegantfin/elegant-source-${i}.css`,'utf8');
 assert.equal(crypto.createHash('sha256').update(css).digest('hex'),hash); return css;
}) : [];
const base = 'af068b5d24499536241cbb28fa806fc6b8125caa';
fs.mkdirSync(output, {recursive: true});
const hostCss = `body{margin:0;background:#121923;color:#e5e7eb;font:16px/1.4 system-ui} :root{--textColor:#e5e7eb;--drawerColor:#202a39;--borderColor:#445065;--btnSubmitColor:#5952bc;--dimTextColor:#aebacc} .overflowPortraitCard{width:160px} @media(max-width:600px){.overflowPortraitCard{width:132px}}`;
async function fixture(browser, width, before = false) {
  const page = await browser.newPage({viewport:{width,height:900}});
  page.setDefaultTimeout(5000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => route.request().url().startsWith('https://image.tmdb.org/') ? route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="240" height="360"><rect width="240" height="360" fill="#364a68"/><circle cx="120" cy="140" r="65" fill="#ac8561"/><path d="M0 340L240 200V360H0" fill="#202e46"/></svg>'}) : route.abort());
  const read = name => before ? execFileSync('git',['show',`${base}:${web}${name}`], {encoding:'utf8'}) : fs.readFileSync(web+name,'utf8');
  await page.setContent('<meta name="viewport" content="width=device-width,initial-scale=1">'+read('discovery.html'));
  await page.addStyleTag({content:hostCss});
  for (const content of themes) await page.evaluate(css => { const style=document.createElement('style'); style.textContent=css; document.head.appendChild(style); },content);
  await page.addStyleTag({content:read('discovery.css')});
  await page.addScriptTag({content:read('discovery.js')});
  await page.evaluate(() => {
    window.calls=[]; window.posts=[]; window.mediaState=3; window.failDetails=false; window.pending=false; window.trendingMode='ok'; window.owned=[];
    const movie={TmdbId:11,MediaType:'movie',Title:'The Lantern Expedition',PosterPath:'/synthetic.jpg',Date:'2026-01-01'};
    const tv={TmdbId:22,MediaType:'tv',Title:'A Very Long Series Title Across Many Uncharted Constellations',PosterPath:'/synthetic.jpg',Date:'2026-02-01'};
    const missing={TmdbId:33,MediaType:'movie',Title:'No Artwork — An Unusually Long Title With Missing Details'};
    const source=Items=>({Items,Page:1,TotalPages:1});
    const api={getCurrentUserId:()=> 'synthetic-user',getUrl:(path,params)=>path+'?'+new URLSearchParams(params),getJSON:async(url,opts)=>{
      calls.push({url,opts});
      if((url.startsWith('3picFin/HomeDiscover/') || url.startsWith('3picFin/Discovery/Trending'))) {if(trendingMode==='fail') throw Error('fixture'); return source(trendingMode==='empty'?[]:[movie,tv]);}
      if(url.startsWith('3picFin/Discovery')) return {Movies:source([movie,missing]),Tv:source([tv]),Requests:source([{Id:1,Type:'movie',TmdbId:11,Status:2}])};
      if(url.startsWith('3picFin/SharedRequests')) return source([{Id:2,Type:'tv',TmdbId:22,Status:1,RequesterDisplayName:'Alex <synthetic>'}]);
      if(url.startsWith('3picFin/Search')) return source([tv]);
      if(url.startsWith('3picFin/TitleDetails')) {
        if(pending) return new Promise(resolve=>window.resolveOld=resolve);
        if(failDetails) throw Error('fixture');
        const id=Number(new URLSearchParams(url.split('?')[1]).get('mediaId'));
        const item=id===11?movie:id===22?tv:missing;
        return {...item,Overview:id===33?null:'A synthetic story about curiosity, distant places, and returning home. '.repeat(12),MediaStatus:id===33?null:mediaState,CanRequest:true,CanRequest4k:true,Seasons:id===22?Array.from({length:60},(_,i)=>i+1):[]};
      }
      if(url.startsWith('3picFin/RequestOptions')) return {CanRequest:true,CanRequest4k:true,Seasons:Array.from({length:60},(_,i)=>i+1),MediaStatus:1,MediaStatus4k:1};
      if(url.startsWith('3picFin/Requests')) return source(owned);
      throw Error('Unexpected fixture route: '+url);
    },ajax:async options=>{posts.push(JSON.parse(options.data));const x=posts.at(-1);owned=[{Id:99,Status:1,Type:x.mediaType,MediaType:x.mediaType,TmdbId:x.mediaId,Is4k:x.is4k,Seasons:x.seasons||[]}];return {Id:99};}};
    window.mountFixture=()=>window.dispose=ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'),api,{userId:'synthetic-user',isCurrent:()=>true});mountFixture();
  });
  await page.locator('#threepic-fin-movies article').first().waitFor();
  await page.waitForFunction(()=>document.querySelector('#threepic-fin-shared-requests').textContent.includes('Constellations'));
  return {page,errors};
}
async function open(page, rail, index=0) {await page.locator(`#threepic-fin-${rail} .threepic-fin-discovery__title-button`).nth(index).click();await page.waitForFunction(()=>!document.querySelector('#threepic-fin-details-status').textContent.includes('Loading'));}
async function checkHome(browser, width) {
 const page=await browser.newPage({viewport:{width,height:900},reducedMotion:'reduce'});
 await page.route('**/*',route=>route.abort());
 await page.setContent('<main id="home" class="rowan-native-rows"></main>');
 await page.addStyleTag({content:hostCss+`.cardScalable{position:relative;aspect-ratio:2/3}.cardContent{position:absolute;inset:0}.cardText{text-align:center}.cardBox{margin:0}.card{padding:0}`});
 for(const css of themes) await page.evaluate(css=>{const el=document.createElement('style');el.textContent=css;document.head.appendChild(el);},css);
 await page.addStyleTag({content:fs.readFileSync(web+'native-home-rows.css','utf8')});
 await page.addScriptTag({content:fs.readFileSync(web+'native-home-rows.js','utf8')});
 await page.evaluate(()=>{
  const observers=[];
  class Observer { constructor(callback){this.callback=callback;observers.push(this);} observe(){} disconnect(){} }
  const rows=RowanNativeHomeRows.createRows({document,IntersectionObserver:Observer,enabledRows:['LatestMovies'],openItem:()=>{}});
  rows.mount(document.querySelector('#home'),{getCurrentUserId:()=> 'synthetic',getUrl:p=>p,getJSON:async()=>({Kind:'LatestMovies',Items:[{Id:'0123456789abcdef0123456789abcdef',Type:'Movie',Name:'A Synthetic Home Film'}]})},'synthetic');
  document.querySelectorAll('.rowan-native-row').forEach(target=>observers[0].callback([{target,isIntersecting:true}]));
 });
 await page.locator('.rowan-native-row__card').waitFor();
 const before=await page.screenshot({path:`${output}/home-before-${width}.png`});
 await page.addStyleTag({content:fs.readFileSync(web+'discovery.css','utf8')});
 const after=await page.screenshot({path:`${output}/home-after-${width}.png`});
 assert(before.equals(after),'Discover styles change Home pixels');
 await page.close();
 console.log(`PASS Home ${width}px: unchanged render after Discover CSS`);
}
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  for(const before of [true,false]) for(const width of [390,1280]) {
   const {page,errors}=await fixture(browser,width,before);const prefix=`${before?'before':'after'}-${width}`;
   await page.screenshot({path:`${output}/${prefix}-rails.png`,fullPage:true});
   for(const [name,rail,index] of [['movie','movies',0],['tv','tv',0],['missing','movies',1]]) {
    await open(page,rail,index);await page.screenshot({path:`${output}/${prefix}-${name}.png`});await page.locator('#threepic-fin-details-close').click();
   }
   assert.deepEqual(errors,[]);await page.close();
  }
  for(const width of [320,390,1280]) {
   await checkHome(browser,width);
   const {page,errors}=await fixture(browser,width);
   assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'page overflow');
   assert(await page.evaluate(()=>Math.abs(document.querySelector('#threepic-fin-search-form').getBoundingClientRect().width-document.querySelector('#threepic-fin-discover-panel').getBoundingClientRect().width)<2),'full width search');
   assert(await page.evaluate(()=>document.querySelector('#threepic-fin-discover-panel').firstElementChild.id==='threepic-fin-search-form'));
   assert.equal(await page.locator('#threepic-fin-trending-movies article').count(),1);assert.equal(await page.locator('#threepic-fin-trending-tv article').count(),1);
   for(const rail of ['requests','shared-requests']) {
    await open(page,rail);assert.match(await page.locator('#threepic-fin-details-status').innerText(),/Request: (Approved|Pending)/);
    assert.match(await page.locator('#threepic-fin-details-status').innerText(),rail==='requests'?/Requested by: Unknown/:/Requested by: Alex <synthetic>/);
    assert.equal(await page.locator('#threepic-fin-details-status synthetic').count(),0);
    await page.screenshot({path:`${output}/requester-${rail}-${width}.png`});
    assert(await page.locator('#threepic-fin-details-dialog').evaluate(e=>e.scrollWidth<=e.clientWidth),'modal overflow');
    const url=await page.evaluate(()=>calls.filter(c=>c.url.includes('TitleDetails')).at(-1).url);assert(url.includes(rail==='requests'?'mediaId=11':'mediaId=22'));
    await page.keyboard.press('Escape');assert(await page.locator(`#threepic-fin-${rail} .threepic-fin-discovery__title-button`).evaluate(e=>e===document.activeElement));
   }
   for(const [state,label] of [[1,'Not requested'],[2,'Requested · Pending'],[3,'Requested · Processing'],[4,'Partially available'],[5,'Available in Seerr'],[6,'Blocklisted'],[null,'Request status unknown'],[99,'Request status unknown']]) {
    await page.evaluate(x=>window.mediaState=x,state);await open(page,'movies');assert((await page.locator('#threepic-fin-details-status').innerText()).includes(label));await page.keyboard.press('Escape');
   }
   await page.evaluate(()=>window.failDetails=true);await open(page,'movies');assert.match(await page.locator('#threepic-fin-details-status').innerText(),/unavailable.*unknown/);await page.keyboard.press('Escape');await page.evaluate(()=>window.failDetails=false);
   await page.evaluate(()=>{window.pending=true;});await page.locator('#threepic-fin-movies .threepic-fin-discovery__title-button').first().click();await page.keyboard.press('Escape');
   assert(await page.evaluate(()=>calls.filter(c=>c.url.includes('TitleDetails')).at(-1).opts.signal.aborted));await page.evaluate(()=>{resolveOld({Title:'STALE SECRET',MediaType:'movie',TmdbId:11});window.pending=false;});assert(!(await page.locator('body').innerText()).includes('STALE SECRET'));
   await page.evaluate(()=>window.mediaState=1);await open(page,'tv');
   await page.locator('#threepic-fin-details-close').focus();await page.keyboard.press('Shift+Tab');assert(await page.locator('#threepic-fin-details-request').evaluate(e=>e===document.activeElement));await page.keyboard.press('Tab');assert(await page.locator('#threepic-fin-details-close').evaluate(e=>e===document.activeElement));
   await page.locator('#threepic-fin-details-overview').focus();assert(await page.locator('#threepic-fin-details-overview').evaluate(e=>e.textContent.length>800&&e.scrollHeight>e.clientHeight));
   await page.locator('#threepic-fin-details-request').click();await page.locator('#threepic-fin-request-seasons input').last().waitFor();assert.equal(await page.locator('#threepic-fin-request-seasons input').count(),60);
   assert(await page.locator('#threepic-fin-request-dialog').evaluate(e=>e.scrollWidth<=e.clientWidth),'season dialog overflow');
   await page.screenshot({path:`${output}/seasons-${width}.png`});
   await page.locator('#threepic-fin-request-seasons input').last().check();await page.locator('#threepic-fin-request-4k').check();await page.locator('#threepic-fin-request-submit').click();await page.waitForFunction(()=>document.querySelector('#threepic-fin-request-status').textContent.includes('Check My Requests'));
   assert.deepEqual(await page.evaluate(()=>posts),[{mediaType:'tv',mediaId:22,is4k:true,seasons:[60]}]);assert(await page.locator('#threepic-fin-request-submit').isDisabled());await page.locator('#threepic-fin-request-cancel').click();
   await page.locator('#threepic-fin-search').fill('constellations');await page.locator('#threepic-fin-search-form button').click();await page.locator('#threepic-fin-search-results article').waitFor();
   for(const mode of ['empty','fail']) {await page.evaluate(x=>{dispose();window.trendingMode=x;mountFixture();},mode);await page.waitForFunction(()=>document.querySelector('#threepic-fin-trending-tv').textContent.match(/No trending|unavailable/));assert(await page.locator('#threepic-fin-search').isVisible());}
   assert.deepEqual(errors,[]);await page.evaluate(()=>dispose());await page.close();console.log(`PASS synthetic browser ${width}px: requests, statuses, POST/read-back, 4K, 60 seasons, focus, stale response, feed failures, overflow`);
  }
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
