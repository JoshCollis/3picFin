"""Assertions over the actual transformed native renderer; never a substitute guide."""
import json
import time


def check(page, args, channels, requests, errors, second_user):
    root = page.locator('.tvguide:visible')
    scroller = root.locator('.guideVerticalScroller')
    page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')
    assert root.locator('.btnNextPage,.btnPreviousPage,.finGuideFirst,.finGuideLast,.finGuidePage,.finGuideJump').count() == 0
    assert root.get_by_role('button', name='Load more', exact=True).count() == 0
    expected = [c['Id'] for c in sorted(channels, key=lambda c: not c.get('UserData', {}).get('IsFavorite', False))]
    seen = set()
    maxima = {'rows': 0, 'cells': 0, 'nodes': 0}
    step_positions, windows = [], []

    def idle():
        page.wait_for_function('document.querySelector(".tvguide")?.getAttribute("aria-busy")==="false"')

    def measure():
        result = page.wait_for_function('''expected=>{const el=document.querySelector('.tvguide'),start=Number(el?.dataset.finGuideStart),
          ids=Array.from(el?.querySelectorAll('.channelPrograms')||[]).map(row=>row.dataset.channelid);
          if(el?.getAttribute('aria-busy')!=='false'||!ids.length||ids.some((id,i)=>id!==expected[start+i]))return false;
          return {start,total:Number(el.dataset.finGuideTotal),ids,rows:ids.length,cells:el.querySelectorAll('.programCell').length,
          nodes:el.querySelectorAll('*').length,height:el.querySelector('.channelPrograms')?.getBoundingClientRect().height};}''', arg=expected).json_value()
        seen.update(result['ids'])
        for key in maxima: maxima[key] = max(maxima[key], result[key])
        assert result['ids'] == expected[result['start']:result['start'] + result['rows']]
        assert 0 < result['rows'] <= 50
        assert result['cells'] <= 50 * 24
        return result

    def seek(index):
        scroller.evaluate('''(el,index)=>{const row=el.querySelector('.channelPrograms'),height=row.getBoundingClientRect().height;
          if(el.getScrollEventName()==='scrollanimate')el.scrollToPosition(index*height,true);else el.scrollTop=index*height;
          el.dispatchEvent(new Event('scroll'));}''', index)
        try:
            page.wait_for_function('''index=>{const el=document.querySelector('.tvguide'),start=Number(el?.dataset.finGuideStart),
              count=el?.querySelectorAll('.channelPrograms').length;return el?.getAttribute('aria-busy')==='false'&&index>=start&&index<start+count;}''', arg=index)
        except Exception:
            diagnostic = root.evaluate('''el=>{const s=el.querySelector('.guideVerticalScroller'),g=el.querySelector('.programGrid');return {data:{...el.dataset},busy:el.getAttribute('aria-busy'),position:s.getScrollPosition(),scrollTop:s.scrollTop,size:s.getScrollSize(),slider:s.querySelector('.scrollSlider').getBoundingClientRect().height,padding:g.style.paddingTop,rows:el.querySelectorAll('.channelPrograms').length,text:el.querySelector('.finGuideState').textContent}}''')
            diagnostic.update(target=index, errors=errors, requests=requests[-10:])
            (args.output/'seek-failure.json').write_text(json.dumps(diagnostic,indent=2))
            raise
        return measure()

    first = measure()
    if args.refinement:
        from guide_refinement_checks import check_refinement
        check_refinement(page, root, args)
    (args.output/'initial-layout.json').write_text(json.dumps(root.evaluate('''el=>Array.from(el.querySelectorAll('.guideVerticalScroller,.scrollSlider,.channelsContainer,.programGrid')).map(node=>({class:node.className,height:node.getBoundingClientRect().height,scrollHeight:node.scrollHeight,cssHeight:getComputedStyle(node).height,padding:node.style.paddingBottom,contain:getComputedStyle(node).contain,overflow:getComputedStyle(node).overflow}))'''),indent=2))
    assert root.locator('.guideChannelImage').count() > 0
    for header in root.locator('.guide-channelHeaderCell').all():
        assert header.locator('.guideChannelName').inner_text().strip()
    assert root.locator('.finGuideSynopsis').count() > 0
    assert root.locator('.finGuideProgramTime').count() > 0
    for control in root.locator('.finGuideToolbar input,.finGuideToolbar button,.finGuideToolbar select').all():
        box = control.bounding_box()
        if box: assert box['height'] >= 43.5
    assert page.evaluate('document.documentElement.scrollWidth<=innerWidth+1')

    def alignment():
        value = root.evaluate('''el=>{const cell=el.querySelector('.channelPrograms'),head=el.querySelector('.timeslotHeadersInner'),
          column=el.querySelector('.channelsContainer'),grid=el.querySelector('.programGrid'),
          header=el.querySelector('.guide-channelTimeslotHeader');return {
          x:Math.abs(cell.getBoundingClientRect().x-head.getBoundingClientRect().x),
          width:Math.abs(cell.getBoundingClientRect().width-head.getBoundingClientRect().width),
          column:Math.abs(column.getBoundingClientRect().width-header.getBoundingClientRect().width),
          scroll:Math.abs(grid.scrollLeft-el.querySelector('.timeslotHeaders').scrollLeft)};}''')
        assert max(value.values()) < 2, value
        return value

    before_alignment = alignment()
    vertical = root.evaluate('''el=>{
      const rows=Array.from(el.querySelectorAll('.channelPrograms')),headers=Array.from(el.querySelectorAll('.guide-channelHeaderCell'));
      return rows.map((row,i)=>{const r=row.getBoundingClientRect(),h=headers[i].getBoundingClientRect(),
        cell=row.querySelector('.programCell');if(!cell)return {rowDrift:Math.abs(r.top-h.top),height:Math.abs(r.height-h.height),cell:0,clippedTop:0,clippedBottom:0};const c=cell.getBoundingClientRect(),
        text=cell.querySelector('.guideProgramNameText').getBoundingClientRect();
        return {rowDrift:Math.abs(r.top-h.top),height:Math.abs(r.height-h.height),cell:Math.abs(r.height-c.height),
          clippedTop:Math.max(0,c.top-text.top),clippedBottom:Math.max(0,text.bottom-c.bottom)};});}''')
    assert all(max(row.values()) < 3 for row in vertical), vertical
    def visual_geometry():
        page.wait_for_function('''()=>{const images=Array.from(document.querySelectorAll('.guideChannelImage')).filter(el=>{
          const r=el.getBoundingClientRect();return r.bottom>0&&r.top<innerHeight;});
          return images.length&&images.every(el=>getComputedStyle(el).backgroundImage!=='none');}''')
        result=root.evaluate('''async el=>{
          const visible=node=>{const r=node.getBoundingClientRect();return r.bottom>0&&r.top<innerHeight;};
          const headers=Array.from(el.querySelectorAll('.guide-channelHeaderCell')).filter(header=>visible(header.querySelector('.guideChannelImage,.finGuideChannelFallback')));
          const channels=await Promise.all(headers.map(async header=>{
            const logo=header.querySelector('.guideChannelImage,.finGuideChannelFallback'),name=header.querySelector('.guideChannelName'),
              h=header.getBoundingClientRect(),l=logo.getBoundingClientRect(),n=name.getBoundingClientRect();
            let decoded=null;
            if(logo.matches('.guideChannelImage')){
              const image=new Image();image.src=getComputedStyle(logo).backgroundImage.slice(4,-1).replace(/^['"]|['"]$/g,'');
              await image.decode();decoded=[image.naturalWidth,image.naturalHeight];
            }
            return {fallback:logo.matches('.finGuideChannelFallback'),decoded,logo:[l.x,l.y,l.width,l.height],
              name:[n.x,n.y,n.width,n.height],nameInset:n.x-h.x,separation:n.x-l.right};
          }));
          const marker=el.querySelector('.finGuideNowMarker'),m=marker.getBoundingClientRect(),
            slots=Array.from(el.querySelectorAll('.timeslotHeader')).map(slot=>slot.getBoundingClientRect());
          const intersections=slots.filter(r=>m.left<r.right&&m.right>r.left&&m.top<r.bottom&&m.bottom>r.top).length;
          const row=el.querySelector('.channelPrograms'),cell=el.querySelector('.programCell'),
            line=getComputedStyle(row,'::after'),text=getComputedStyle(cell),body=getComputedStyle(cell,'::before'),field=el.querySelector('.finGuideSearchInput'),
            input=getComputedStyle(field),canvas=document.createElement('canvas'),context=canvas.getContext('2d');
          context.font=input.font;
          return {channels,nowLabel:marker.getAttribute('aria-label'),marker:[m.x,m.y,m.width,m.height],
            timeLabelIntersections:intersections,headerClearance:Math.min(...slots.map(r=>r.top))-m.bottom,
            lineLayer:Number(line.zIndex),textLayer:Number(text.zIndex),textMask:body.backgroundColor,
            placeholder:field.placeholder,accessibleSearchName:field.getAttribute('aria-label'),
            placeholderWidth:context.measureText(field.placeholder).width,
            inputTextWidth:field.clientWidth-parseFloat(input.paddingLeft)-parseFloat(input.paddingRight)};
        }''')
        assert result['timeLabelIntersections']==0 and result['headerClearance']>=3, result
        assert result['nowLabel'].startswith('Current time ')
        assert result['lineLayer']<result['textLayer'] and result['textMask'] not in ('transparent','rgba(0, 0, 0, 0)'),result
        assert result['placeholder']=='Search channels' and result['placeholderWidth']<=result['inputTextWidth']
        assert 'name or number' in result['accessibleSearchName']
        assert any(c['decoded'] and min(c['decoded'])>0 for c in result['channels']),result
        assert any(c['fallback'] for c in result['channels']),result
        assert max(c['nameInset'] for c in result['channels'])-min(c['nameInset'] for c in result['channels'])<1,result
        assert all(c['logo'][2]>=43.5 and c['logo'][3]>=43.5 and c['name'][2]>=60 and c['separation']>=4 for c in result['channels']),result
        return result

    visual=visual_geometry()
    (args.output/'visual-geometry.json').write_text(json.dumps(visual,indent=2))
    root.locator('.programGrid').evaluate('el=>{el.scrollLeft+=el.clientWidth*.75;el.dispatchEvent(new Event("scroll"));}')
    page.wait_for_timeout(150)
    after_alignment = alignment()
    root.locator('.programGrid').evaluate('el=>{el.scrollLeft=0;el.dispatchEvent(new Event("scroll"));}')
    page.wait_for_timeout(150)
    page.screenshot(path=str(args.output / 'continuous-before-traversal.png'))
    started = time.monotonic()
    indices = [0, len(expected)-1] if args.toolbar_only else range(0, len(expected), first['rows'])
    for index in indices:
        snapshot=seek(index);windows.append({'target':index,'start':snapshot['start'],'rows':snapshot['rows']})
        step_positions.append(index)
        if len(step_positions) % 16 == 0: print('continuous traversal:', len(seen), 'distinct channels', flush=True)
    tail = seek(len(expected) - 1)
    assert tail['ids'][-1] == expected[-1]
    if not args.toolbar_only and seen != set(expected):
        (args.output/'coverage-failure.json').write_text(json.dumps({'missingIndices':[i for i,id in enumerate(expected) if id not in seen],'windows':windows},indent=2))
    if not args.toolbar_only: assert seen == set(expected), (len(seen), len(expected))
    reverse_reads = len(requests)
    for index in reversed(step_positions[-3:]): seek(index)
    reverse_reads = len(requests) - reverse_reads
    seek(0)
    root.locator('.programCell').first.focus()
    seek(min(100,len(expected)-1))
    assert root.locator('.programGrid').evaluate('el=>el.contains(document.activeElement)'), 'Recycling discarded the focused programme instead of preserving a logical focus target'
    seek(0)
    # Native cell semantics and remote commands remain native within the window;
    # logical Home/End and row-boundary commands cross recycled windows.
    cell = root.locator('.programCell').first
    assert cell.get_attribute('data-action') in ('programdialog', 'link')
    cell.focus(); page.keyboard.press('End'); idle()
    page.wait_for_function('''()=>{const el=document.querySelector('.tvguide');return document.activeElement?.closest('.channelPrograms')?.dataset.channelid===Array.from(el.querySelectorAll('.channelPrograms')).at(-1)?.dataset.channelid&&Number(el.dataset.finGuideStart)>0;}''')
    page.keyboard.press('Home'); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideStart)===0')
    if args.layout == 'tv':
        current = page.evaluate('document.activeElement?.dataset.channelid')
        page.keyboard.press('ArrowDown')
        page.wait_for_function('id=>document.activeElement?.dataset.channelid!==id', arg=current)
        page.keyboard.press('ArrowUp')
        page.wait_for_function('id=>document.activeElement?.dataset.channelid===id', arg=current)

    # Search and assigned category intersection, including a name at the tail.
    field = root.locator('.finGuideSearchInput')
    select = root.locator('.finGuideCategory')
    options = select.locator('option').all()
    general = next(option.get_attribute('value') for option in options if option.inner_text() == 'Fixture General')
    news = next(option.get_attribute('value') for option in options if option.inner_text() == 'Fixture News')
    select.select_option(general); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===1001')
    field.fill('France2'); field.press('Enter'); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===1')
    assert root.locator('.guideChannelName').all_inner_texts() == ['France 2']
    select.select_option(news); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===1')
    assert root.locator('.guideChannelName').all_inner_texts() == ['France 24']
    field.fill('Station0001'); field.press('Enter'); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===0')
    assert root.locator('.programCell').count() == 0
    select.select_option(''); root.locator('.finGuideClear').click(); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===1879')
    assert root.get_attribute('data-fin-guide-start') == '0'
    for query in ('not-present-fixture', channels[-1]['ChannelNumber']):
        field.fill(query); field.press('Enter')
    page.wait_for_function('''()=>document.querySelector('.tvguide')?.getAttribute('aria-busy')==='false'&&document.querySelector('.guideChannelNumber')?.textContent==='1879' ''')
    root.locator('.finGuideClear').click(); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===1879')
    old_date = root.locator('.programCell').first.get_attribute('data-startdate')
    root.locator('.guide-date-tab-button').nth(1).click()
    page.wait_for_function('old=>document.querySelector(".programCell")?.dataset.startdate!==old', arg=old_date)
    root.locator('.guide-date-tab-button').first.click(); idle()
    page.wait_for_function('old=>document.querySelector(".programCell")?.dataset.startdate===old', arg=old_date)

    # Native programme/favourite settings still reset a tail window to the start.
    seek(len(expected) - 1)
    root.locator('.btnGuideViewSettings').click(); page.wait_for_selector('.chkCategory')
    for kind in ('movies', 'kids', 'news'):
        page.locator(f'.chkCategory[data-type={kind}]').locator('xpath=..').click()
        assert not page.locator(f'.chkCategory[data-type={kind}]').is_checked(), kind
    page.locator('.formDialog .btnCancel').click(); idle()
    try:
        page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideStart)===0&&Array.from(document.querySelectorAll(".channelPrograms")).every(row=>parseInt(row.dataset.channelid,16)%3===0)')
    except Exception:
        page.screenshot(path=str(args.output/'settings-failure.png'))
        state=root.evaluate('''el=>({data:{...el.dataset},busy:el.getAttribute('aria-busy'),focus:document.activeElement?.outerHTML,ids:Array.from(el.querySelectorAll('.channelPrograms')).map(row=>row.dataset.channelid),top:el.querySelector('.guideVerticalScroller').getScrollPosition()})''')
        state.update(requests=requests[-10:],errors=errors)
        (args.output/'settings-failure.json').write_text(json.dumps(state,indent=2))
        raise
    root.locator('.btnGuideViewSettings').click(); page.wait_for_selector('.chkCategory')
    for kind in ('movies', 'kids', 'news'):
        page.locator(f'.chkCategory[data-type={kind}]').locator('xpath=..').click()
    page.locator('.formDialog .btnCancel').click(); idle()
    page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===1879')
    visual=visual_geometry()
    (args.output/'visual-geometry.json').write_text(json.dumps(visual,indent=2))
    page.screenshot(path=str(args.output / 'continuous-controls.png'))
    assert all(r['channels'] <= 50 and r.get('restriction') != 'unrestricted' for r in requests if r['kind'] == 'programmes')
    assert all(r['limit'] <= 500 for r in requests if r['kind'] == 'channels')
    page.evaluate('Emby.Page.showSearch()')
    page.wait_for_selector('.programCell:visible', state='hidden')
    if args.layout == 'tv': page.keyboard.press('Escape')
    else: page.evaluate('Emby.Page.showGuide()')
    page.wait_for_selector('.programCell:visible'); idle()
    assert root.get_attribute('data-fin-guide-start') == '0'
    if args.layout == 'desktop' and args.width >= 1280:
        page.evaluate('(id)=>{ApiClient.setAuthenticationInfo("synthetic-local-only-second-identity",id);Emby.Page.showSearch();Emby.Page.showGuide();}', second_user)
        page.wait_for_function('Number(document.querySelector(".tvguide")?.dataset.finGuideTotal)===17')
        assert root.locator('.channelPrograms').count() <= 17
    assert not errors, errors
    result = {'scope':'actual transformed native chunk; synthetic identity/API fixture, not production login',
              'catalogue':len(channels),'distinctChannels':len(seen),'tailReached':True,'fullTraversal':not args.toolbar_only,
              'maxima':maxima,'programmeChannelLimit':max(r['channels'] for r in requests if r['kind']=='programmes'),
              'channelMetadataBatchLimit':max(r['limit'] for r in requests if r['kind']=='channels'),
              'reverseReadCount':reverse_reads,'traversalSeconds':time.monotonic()-started,
              'alignmentBefore':before_alignment,'alignmentAfterPan':after_alignment,
              'verticalGeometry':{key:max(row[key] for row in vertical) for key in vertical[0]},
              'visualGeometry':visual,
              'viewport':[args.width,850],'layout':args.layout,'theme':bool(args.theme),'errors':errors,
              'pagerAbsent':True,'nativeSettingsAndDate':True,'routeReturn':True}
    (args.output / 'continuous-result.json').write_text(json.dumps(result,indent=2))
    print(json.dumps(result),flush=True)
