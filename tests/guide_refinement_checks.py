"""Geometry over native markup and native sticky transforms, not a second renderer."""
import json


def check_refinement(page, root, args):
    field=root.locator('.finGuideSearchInput')
    before=root.locator('.programContainer').bounding_box()['y']
    field.fill('1879')
    busy=root.locator('.programContainer').bounding_box()['y']
    assert abs(before-busy)<1, 'Loading status shifted the Guide viewport'
    root.get_by_role('button',name='Search channels',exact=True).focus()
    page.keyboard.press('Enter')
    page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="1"&&document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
    root.get_by_role('button',name='Clear channel search',exact=True).click()
    page.wait_for_function('document.querySelector(".tvguide")?.dataset.finGuideTotal==="1879"&&document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
    assert field.evaluate('el=>document.activeElement===el')
    assert abs(before-root.locator('.programContainer').bounding_box()['y'])<1
    root.locator('.btnGuideViewSettings').click()
    box=page.locator('.chkColorCodedBackgrounds')
    if not box.is_checked(): box.locator('xpath=..').click()
    assert box.is_checked(), 'Native category background setting did not toggle'
    page.locator('.formDialog .btnCancel').click()
    page.wait_for_selector('.dialogContainer',state='hidden')
    try:
        page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"&&document.querySelector(".programCell-movie,.programCell-kids,.programCell-news,.programCell-sports")')
    except Exception:
        page.screenshot(path=str(args.output/'native-settings-failure.png'))
        state=root.evaluate('''el=>({busy:el.getAttribute('aria-busy'),data:{...el.dataset},state:el.querySelector('.finGuideState')?.textContent,
          classes:Array.from(el.querySelectorAll('.programCell')).map(n=>n.className),
          settings:Object.fromEntries(Object.entries(localStorage).filter(([key])=>key.includes('guide-'))),focus:document.activeElement?.outerHTML})''')
        (args.output/'native-settings-failure.json').write_text(json.dumps(state,indent=2))
        raise
    root.locator('.programGrid').evaluate('el=>{el.scrollLeft=el.scrollWidth*40/1440;el.dispatchEvent(new Event("scroll"));}')
    page.wait_for_timeout(250)
    data=root.evaluate('''el=>{
      const rect=n=>{const r=n.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,right:r.right,bottom:r.bottom};};
      const headers=Array.from(el.querySelectorAll('.guide-channelHeaderCell'));
      const identities=headers.map(h=>{const name=h.querySelector('.guideChannelName'),num=h.querySelector('.guideChannelNumber'),
        logo=h.querySelector('.guideChannelImage,.finGuideChannelFallback'),group=h.querySelector('.finGuideChannelIdentity');
        return {row:rect(h),name:rect(name),number:rect(num),logo:rect(logo),group:group?rect(group):null};});
      const cells=Array.from(el.querySelectorAll('.programCell')).map(c=>{
        const r=rect(c),row=rect(c.closest('.channelPrograms')),s=getComputedStyle(c),body=getComputedStyle(c,'::before'),
          content=c.querySelector('.guideProgramName'),text=content?getComputedStyle(content):null;
        return {class:c.className,action:c.dataset.action,row,r,left:parseFloat(c.style.left),width:parseFloat(c.style.width),
          border:Math.max(...['Top','Right','Bottom','Left'].map(d=>parseFloat(s['border'+d+'Width']))),
          body:{content:body.content,top:parseFloat(body.top),bottom:parseFloat(body.bottom),left:parseFloat(body.left),right:parseFloat(body.right),shadow:body.boxShadow},
          content:content?rect(content):null,textBackground:text?.backgroundColor,transform:content?.style.transform,
          text:content?.textContent};});
      const toolbar=el.querySelector('.finGuideToolbar'),search=el.querySelector('.finGuideSearch'),category=el.querySelector('.finGuideCategory'),
        icon=el.querySelector('.finGuideCategoryIcon'),active=el.querySelector('.guide-date-tab-button.emby-tab-button-active'),
        range=el.querySelector('.finGuideRange');
      return {identities,cells,toolbar:rect(toolbar),search:rect(search),category:rect(category),chevron:icon?rect(icon):null,
        groups:Array.from(toolbar.children).map(n=>n.className),timeButtons:el.querySelectorAll('.finGuideEarlier,.finGuideNow,.finGuideLater').length,
        rangeInToolbar:toolbar.contains(range),range:rect(range),
        icons:Array.from(el.querySelectorAll('.finGuideFind,.finGuideClear')).map(b=>{const icon=b.querySelector('.material-icons');return {label:b.getAttribute('aria-label'),title:b.title,rect:rect(b),icon:icon?(icon.textContent||getComputedStyle(icon,'::before').content):null};}),
        controlBorders:[search,category,el.querySelector('.finGuideSearchInput'),el.querySelector('.finGuideFind'),el.querySelector('.finGuideClear')].map(n=>parseFloat(getComputedStyle(n).borderTopWidth)),
        selected:active?{rect:rect(active),decoration:getComputedStyle(active).textDecorationLine,aria:active.getAttribute('aria-selected'),pressed:active.getAttribute('aria-pressed'),
          descendants:Array.from(active.querySelectorAll('*')).map(n=>getComputedStyle(n).textDecorationLine),indicator:getComputedStyle(active,'::after').content}:null,
        anchors:Array.from(document.querySelectorAll('.headerLogo,.pageTitle,.headerLeft,.headerTop,.viewMenuBar,.sectionTabs,.skinHeader,header,h1,h2,img,[class*="MuiToolbar"]')).filter(n=>n.getBoundingClientRect().width).map(n=>({class:n.className,tag:n.tagName,r:rect(n)})),
        headerMarkup:Array.from(document.querySelectorAll('header,[class*="MuiToolbar"]')).map(n=>n.outerHTML.slice(0,9000)),
        headerAnchor:(()=>{const menu=document.querySelector('header [aria-controls="library-view-menu"]');
          if(menu&&menu.getBoundingClientRect().width)return rect(menu).x;
          const title=document.querySelector('.skinHeader .headerTop .pageTitle');
          if(title&&title.getBoundingClientRect().width)return rect(title).x;
          const bar=Array.from(document.querySelectorAll('header .MuiToolbar-root')).find(n=>n.getBoundingClientRect().width);
          return bar?rect(bar).x+parseFloat(getComputedStyle(bar).paddingLeft):null;})(),
        grid:rect(el.querySelector('.programGrid')),ruler:rect(el.querySelector('.timeslotHeadersInner'))};
    }''')
    failures=[]
    def require(value,label):
        if not value: failures.append(label)
    require(len(data['groups'])==2 and data['timeButtons']==0,'exactly two toolbar groups, no time buttons')
    require(not data['rangeInToolbar'],'count outside toolbar')
    require(abs(data['search']['h']-data['category']['h'])<1,'matched search/category control heights')
    require(data['controlBorders']==[1,1,0,0,0],'single thin control outline, no nested borders')
    require(data['headerAnchor'] is not None and abs(data['search']['x']-data['headerAnchor'])<2,'native header/toolbar gutter alignment')
    for row in data['identities']:
        require(row['group'] is not None,'identity wrapper')
        if row['group']:
            require(abs(row['group']['y']+row['group']['h']/2-row['row']['y']-row['row']['h']/2)<2,'centered identity')
            require(row['number']['y']>=row['name']['bottom']-1 and row['number']['y']-row['name']['bottom']<8,'secondary number beside name')
        require(abs(row['logo']['y']+row['logo']['h']/2-row['row']['y']-row['row']['h']/2)<2,'centered logo')
    for cell in data['cells']:
        require(cell['body']['content'] not in ('none','normal'),'separate programme body')
        require(cell['body']['top']==4 and cell['body']['bottom']==4,'consistent programme inset')
        require(cell['border']<=1,'no heavy/doubled programme border')
        require(abs(cell['r']['x']-cell['row']['x']-cell['row']['w']*cell['left']/100)<2,'native duration x')
        require(abs(cell['r']['w']-cell['row']['w']*cell['width']/100)<2,'native duration width')
        if cell['content']: require(cell['textBackground'] in ('rgba(0, 0, 0, 0)','transparent'),'sticky wrapper has no independent fill')
        if 'programCell-active' in cell['class']: require(cell['body']['shadow']!='none','current accent attached to body')
    require(any('programCell-movie' in cell['class'] for cell in data['cells']) and any('programCell-active' in cell['class'] for cell in data['cells']),'native category/current classes exercised')
    require(any(cell['transform'] and cell['transform']!='none' for cell in data['cells']),'native sticky translation exercised')
    for button in data['icons']:
        require(bool(button['label'] and button['title'] and button['icon']),'accessible native-family icon action')
        require(button['rect']['h']>=44 and button['rect']['w']>=44,'44px icon target')
    if data['chevron']:
        require(abs(data['chevron']['y']+data['chevron']['h']/2-data['category']['y']-data['category']['h']/2)<1,'centered category chevron')
        require(data['category']['right']-data['chevron']['right']>=12,'symmetric chevron inset')
    else: require(False,'category chevron')
    selected=data['selected']
    require(selected and selected['decoration']=='none' and all(d=='none' for d in selected['descendants']),'no weekday/day underlines')
    require(selected and selected['indicator'] not in ('none','normal'),'single date selection indicator')
    require(selected and (selected['pressed']=='true' or selected['aria']=='true'),'accessible selected date state')
    data['failures']=failures
    (args.output/'refinement-geometry.json').write_text(json.dumps(data,indent=2))
    page.screenshot(path=str(args.output/'refinement.png'))
    assert not failures,failures
