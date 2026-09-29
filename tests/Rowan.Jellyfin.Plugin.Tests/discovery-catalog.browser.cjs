// Synthetic, offline catalog regression; public theme is optional and hash-checked by the shared fixture.
const {chromium}=require('../../.qa-tools/node_modules/playwright');
const {fixture,open}=require('./discovery-pilot.browser.cjs');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const output=process.env.FIN_EVIDENCE_DIR || '.qa-tools/evidence';
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  for(const width of [320,390,1280]) {
   const {page,errors}=await fixture(browser,width);
   // Representative late host constraints: inspect the actual input, not just the form.
   await page.addStyleTag({content:'form{max-width:36rem} input[type=search]{width:12rem;max-width:18rem}'});
   await page.evaluate(()=>{
    dispose(); window.catalogCalls=[]; window.searchResolvers={}; window.feedResolvers={}; window.catalogMode='normal';
    const source=(Items,page=1)=>({Items,Page:page,TotalPages:3});
    const movie=i=>({TmdbId:i,MediaType:'movie',Title:`Synthetic film ${i}`,PosterPath:'/synthetic.jpg'});
    const tv=i=>({TmdbId:i,MediaType:'tv',Title:`Synthetic series ${i}`,PosterPath:'/synthetic.jpg'});
    const original=fixtureApi.getJSON;
    fixtureApi.getJSON=async(url,opts)=>{
     const [route,query]=url.split('?'), params=new URLSearchParams(query), page=Number(params.get('page')||1);
     catalogCalls.push(url);
     if(route==='3picFin/Search') {
      const q=params.get('query');
      if(q==='old') return new Promise(resolve=>searchResolvers.old=resolve);
      if(q==='empty') return source([]);
      if(q==='error') throw Error('Synthetic upstream failure');
      return source([{...tv(page),Title:`Search ${q} page ${page}`}],page);
     }
     if(route==='3picFin/Discovery') {
      const mp=Number(params.get('moviePage')),tp=Number(params.get('tvPage'));
      return {Movies:source(catalogMode==='sparse'?[]:Array.from({length:10},(_,i)=>movie(mp*100+i)),mp),Tv:source(catalogMode==='sparse'?[]:Array.from({length:10},(_,i)=>tv(tp*100+i)),tp),Requests:source([])};
     }
     if(route.startsWith('3picFin/Discovery/')) {
      if(catalogMode==='failure' && route.endsWith('UpcomingMovies')) throw Error('Synthetic feed failure');
      if(catalogMode==='sparse') return source([]);
      if(catalogMode==='stale' && route.endsWith('Trending')) return new Promise(resolve=>feedResolvers.trending=resolve);
      if(route.endsWith('Trending')) return source(page===1 ? [movie(100),tv(100),movie(101)] : [tv(300),tv(301)],page);
      if(route.endsWith('UpcomingMovies')) return source([movie(500+page)],page);
      if(route.endsWith('UpcomingTV')) return source([tv(600+page)],page);
     }
     return original(url,opts);
    };
    mountFixture();
   });
   await page.locator('#threepic-fin-upcoming-tv article').waitFor();
   const geometry=await page.evaluate(()=>{
    const q=id=>document.querySelector('#threepic-fin-'+id),r=e=>e.getBoundingClientRect();
    const panel=r(q('discover-panel')),form=r(q('search-form')),input=r(q('search')),button=r(q('search-form').querySelector('button'));
    const results=q('search-results').closest('section'),trending=q('trending').closest('section');
    return {panel:panel.width,form:form.width,input:input.width,occupied:button.right-input.left,dom:q('search-form').nextElementSibling===results,visible:r(results).top>=form.bottom&&r(results).bottom<=r(trending).top,overflow:document.documentElement.scrollWidth-innerWidth};
   });
   assert(Math.abs(geometry.panel-geometry.form)<2);
   assert(Math.abs(geometry.form-geometry.occupied)<2);
   assert(geometry.input>geometry.form-110,'input should fill the remaining search row');
   assert(geometry.dom&&geometry.visible);
   assert(geometry.overflow<=1);
   assert.deepEqual(await page.locator('#threepic-fin-trending .threepic-fin-discovery__title-button').allTextContents(),['Synthetic film 100','Synthetic series 100','Synthetic film 101']);
   const more=await page.locator('#threepic-fin-recommendations .threepic-fin-discovery__title-button').allTextContents();
   assert(more.length>=12);assert.equal(new Set(more).size,more.length);
   for(const [rail,type,id] of [['trending','movie',100],['upcoming-movies','movie',501],['upcoming-tv','tv',601]]) {
    await open(page,rail);
    const detail=await page.evaluate(()=>calls.filter(call=>call.url.startsWith('3picFin/TitleDetails')).at(-1).url);
    assert(detail.includes(`mediaType=${type}`)&&detail.includes(`mediaId=${id}`));await page.keyboard.press('Escape');
   }
   await page.locator('#threepic-fin-trending-next').click();
   await page.waitForFunction(()=>document.querySelector('#threepic-fin-trending-page').textContent==='Page 2 of 3');
   assert.deepEqual(await page.locator('#threepic-fin-trending .threepic-fin-discovery__title-button').allTextContents(),['Synthetic series 300','Synthetic series 301']);
   assert.equal(await page.evaluate(()=>catalogCalls.filter(url=>url.includes('/Trending')).length),2,'no requests to fill a type quota');
   await open(page,'trending');
   assert((await page.evaluate(()=>calls.filter(call=>call.url.startsWith('3picFin/TitleDetails')).at(-1).url)).includes('mediaType=tv&mediaId=300'));await page.keyboard.press('Escape');
   for(const rail of ['movies','tv','upcoming-movies','upcoming-tv']) {
    await page.locator(`#threepic-fin-${rail}-next`).click();
    await page.waitForFunction(name=>document.querySelector(`#threepic-fin-${name}-page`).textContent==='Page 2 of 3',rail);
   }
   for(const rail of ['movies','tv','upcoming-movies','upcoming-tv','trending']) assert.equal(await page.locator(`#threepic-fin-${rail}-page`).innerText(),'Page 2 of 3');
   const search=async q=>{await page.locator('#threepic-fin-search').fill(q);await page.locator('#threepic-fin-search').press('Enter');};
   await search('old');await search('new');await page.locator('#threepic-fin-search-results article').waitFor();
   await page.evaluate(()=>searchResolvers.old({Items:[{TmdbId:1,MediaType:'movie',Title:'STALE'}],TotalPages:99}));
   assert.equal(await page.locator('#threepic-fin-search-results .threepic-fin-discovery__title-button').innerText(),'Search new page 1');
   await page.locator('#threepic-fin-search-next').click();await page.waitForFunction(()=>document.querySelector('#threepic-fin-search-page').textContent==='Page 2 of 3');
   assert.match(await page.locator('#threepic-fin-search-results').innerText(),/Search new page 2/);
   if(width!==320) {await page.locator('#threepic-fin-search').scrollIntoViewIfNeeded();await page.screenshot({path:`${output}/catalog-${width}.png`,fullPage:true});}
   await search('empty');await page.waitForFunction(()=>document.querySelector('#threepic-fin-search-results').textContent.includes('No Search results'));
   await search('error');await page.waitForFunction(()=>document.querySelector('#threepic-fin-search-results').textContent.includes('unavailable'));assert(await page.locator('#threepic-fin-search-next').isDisabled());
   await search('old');await search('');await page.evaluate(()=>searchResolvers.old({Items:[{TmdbId:1,MediaType:'movie',Title:'STALE CLEARED'}]}));
   assert.equal(await page.locator('#threepic-fin-search-results article').count(),0);
   for(const mode of ['failure','sparse']) {
    await page.evaluate(mode=>{dispose();catalogMode=mode;mountFixture();},mode);
    if(mode==='failure') {
     await page.waitForFunction(()=>document.querySelector('#threepic-fin-upcoming-movies').textContent.includes('unavailable'));
     assert.equal(await page.locator('#threepic-fin-upcoming-tv article').count(),1);
     assert((await page.locator('#threepic-fin-recommendations article').count())>=12);
    } else {
     await page.waitForFunction(()=>document.querySelector('#threepic-fin-recommendations').textContent.includes('No titles'));
     assert.equal(await page.locator('#threepic-fin-trending article').count(),0);
    }
   }
   await page.evaluate(()=>{dispose();catalogMode='stale';mountFixture();dispose();feedResolvers.trending({Items:[{TmdbId:8,MediaType:'tv',Title:'STALE FEED'}]});});
   assert.equal(await page.locator('#threepic-fin-trending article').count(),0);
   assert.deepEqual(errors,[]);
   console.log(`PASS ${width}px: host-constrained search ${JSON.stringify(geometry)}; order, mixed identity, TV-only page, five independent pagers, ${more.length} unique suggestions, search race/clear/error, feed failure and teardown`);
   await page.close();
  }
 } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
