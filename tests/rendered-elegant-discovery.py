"""Rendered fragment contract against captured ElegantFin 12 CSS (isolated, no server writes).
Run: ELEGANTFIN_CSS=/path/to/elegant-source-0.css python tests/rendered-elegant-discovery.py
"""
import json
import os
from pathlib import Path
import struct
import zlib
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / 'src/Rowan.Jellyfin.Plugin/Web'
THEME = Path(os.environ['ELEGANTFIN_CSS'])
assert THEME.is_file(), THEME


def poster_png():
    def chunk(kind, data):
        return struct.pack('!I', len(data)) + kind + data + struct.pack('!I', zlib.crc32(kind + data) & 0xffffffff)
    # A decoded image fixture; pixel validity matters, not metadata.
    scanlines = b''.join(b'\0' + b''.join(bytes((x * 3 % 256, y * 4 % 256, 80)) for x in range(80)) for y in range(120))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!2I5B', 80, 120, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(scanlines)) + chunk(b'IEND', b'')


def geometry(page):
    return page.evaluate('''() => {
      const rect = e => { const r=e.getBoundingClientRect(); return {x:r.x,y:r.y,w:r.width,h:r.height,right:r.right,bottom:r.bottom}; };
      const q = s => document.querySelector(s);
      return {sections:[...document.querySelectorAll('#threepic-fin-discover-panel > section')].map(e=>({heading:e.querySelector('h3')?.textContent, rect:rect(e)})),
       missing:rect(q('#threepic-fin-movies .threepic-fin-discovery__card:first-child')),
       fallback:rect(q('#threepic-fin-movies .threepic-fin-discovery__poster-fallback')),
       title:rect(q('#threepic-fin-movies .threepic-fin-discovery__title-button')),
       poster:rect(q('#threepic-fin-movies .threepic-fin-discovery__card:nth-child(2) img')),
       decoded:q('#threepic-fin-movies .threepic-fin-discovery__card:nth-child(2) img').naturalWidth,
       overflow:document.documentElement.scrollWidth-innerWidth};
    }''')


def run():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        try:
            for width in (390, 1280):
                page = browser.new_page(viewport={'width': width, 'height': 800})
                page.route('https://image.tmdb.org/t/p/**', lambda route: route.fulfill(body=poster_png(), content_type='image/png'))
                page.goto((WEB / 'discovery.html').as_uri())
                page.add_style_tag(content=THEME.read_text())
                page.add_style_tag(content=(WEB / 'discovery.css').read_text())
                # A late theme rule must not reintroduce the filled card backing.
                page.add_style_tag(content='.cardBox { background: rgba(30,40,54,.8) !important; border: 1px solid red !important; }')
                page.add_script_tag(content=(WEB / 'discovery.js').read_text())
                page.evaluate('''() => {
                  const missing={MediaType:'movie',TmdbId:101,Title:'Hacksaw Ridge',Date:'2016-01-01'};
                  const pictured={MediaType:'movie',TmdbId:102,Title:'Poster Title',PosterPath:'/valid.jpg',Date:'2020-01-01'};
                  const empty={Items:[],Page:1,TotalPages:1};
                  const all={Items:[{Id:7,Status:2,Type:'movie',TmdbId:101}],Page:1,TotalPages:2};
                  const api={getCurrentUserId:()=> 'alice',getUrl:(path, params) => path+'?'+new URLSearchParams(params),getJSON:async url=>{
                    if(url.startsWith('3picFin/Discovery')) return {Movies:{Items:[missing,pictured],Page:1,TotalPages:1},Tv:{Items:[pictured,missing],Page:1,TotalPages:1},Requests:{Items:[{Type:'movie',TmdbId:102,Status:2}],Page:1,TotalPages:1}};
                    if(url.startsWith('3picFin/TitleDetails')) return {MediaType:'movie',TmdbId:101,Title:'Hacksaw Ridge',Overview:'A story about the ridge.',Date:'2016-01-01',MediaStatus:1,CanRequest:true,CanRequest4k:false};
                    if(url.startsWith('3picFin/RequestOptions')) return {CanRequest:true,CanRequest4k:false,Seasons:[],MediaStatus:1};
                    if(url.startsWith('3picFin/SharedRequests')) return url.includes('page=2') ? {...empty,Page:2,TotalPages:2} : all;
                    throw Error('Unexpected '+url);
                  }};
                  window.testCleanup=ThreePicFinDiscovery.mount(document.querySelector('.threepic-fin-discovery'),api,{userId:'alice',isCurrent:()=>true});
                }''')
                page.locator('#threepic-fin-movies .threepic-fin-discovery__card').nth(1).wait_for()
                page.locator('#threepic-fin-shared-requests .threepic-fin-discovery__card').first.wait_for()
                page.wait_for_function("document.querySelector('#threepic-fin-movies img')?.naturalWidth > 0")
                assert page.get_by_role('region', name='All Requests').is_visible()
                assert page.locator('#threepic-fin-shared-requests-next').is_enabled()
                assert page.evaluate("getComputedStyle(document.querySelector('.threepic-fin-discovery')).backgroundColor") == 'rgba(0, 0, 0, 0)', 'fragment must not paint its own page background over ElegantFin'
                g = geometry(page)
                if os.environ.get('ELEGANTFIN_SCREENSHOTS'):
                    page.screenshot(path=str(Path(os.environ['ELEGANTFIN_SCREENSHOTS']) / f'{width}-rows.png'), full_page=True)
                print(json.dumps({'width':width,'geometry':g}))
                assert g['decoded'] == 80
                for name in ('movies', 'tv', 'requests', 'shared-requests', 'trending'):
                    card_style = page.locator(f'#threepic-fin-{name} .threepic-fin-discovery__card').first.evaluate('''e => {
                      const shape = n => n && ({background:getComputedStyle(n).backgroundColor,border:getComputedStyle(n).borderWidth});
                      return {box:shape(e.querySelector('.cardBox')),title:shape(e.querySelector('.cardText')),footer:shape(e.querySelector('.cardFooter'))};
                    }''')
                    assert card_style['box'] == {'background':'rgba(0, 0, 0, 0)','border':'0px'}, (name, card_style)
                    assert card_style['title']['background'] == 'rgba(0, 0, 0, 0)' and card_style['footer'] is None, (name,card_style)
                page.locator('#threepic-fin-shared-requests-next').click()
                page.wait_for_function("document.querySelector('#threepic-fin-shared-requests-page').textContent === 'Page 2 of 2'")
                assert abs(g['poster']['h'] / g['poster']['w'] - 1.5) < .02, 'decoded poster crop differs from native 2:3'
                assert abs(g['poster']['h'] - g['fallback']['h']) < 2, 'mixed poster and no-art frames must align'
                assert page.locator('#threepic-fin-movies .threepic-fin-discovery__poster-fallback').first.inner_text() == 'Artwork unavailable'
                assert g['overflow'] <= 1, 'horizontal overflow'
                assert g['fallback']['h'] >= g['fallback']['w'] * 1.3, 'missing artwork collapses into gray strip'
                assert g['title']['h'] >= 25, 'missing-art title is not readable/tappable'
                assert page.locator('#threepic-fin-movies .threepic-fin-discovery__poster-button').first.evaluate("e => getComputedStyle(e, '::after').content") == '"Details"'
                poster_button = page.locator('#threepic-fin-movies .threepic-fin-discovery__poster-button').first
                poster_button.hover()
                assert poster_button.evaluate("e => getComputedStyle(e, '::after').opacity") == '1', 'hover must expose Details, not fake Play'
                poster_button.focus()
                assert poster_button.evaluate("e => document.activeElement === e"), 'poster Details stays keyboard focusable'
                assert g['missing']['h'] >= g['poster']['h'], 'missing-art card should hold the poster rhythm'
                gaps = [b['rect']['y'] - a['rect']['bottom'] for a,b in zip(g['sections'],g['sections'][1:])]
                assert max(gaps) <= 32, f'oversized section gaps: {gaps}'
                page.locator('#threepic-fin-movies .threepic-fin-discovery__title-button').first.click()
                dialog = page.locator('#threepic-fin-details-dialog')
                dialog.wait_for(state='visible')
                surface = dialog.evaluate("e => ({shadow:getComputedStyle(e).boxShadow,blur:getComputedStyle(e).backdropFilter})")
                assert surface['shadow'] != 'none' and surface['blur'] != 'none', 'details surface lacks native depth'
                page.get_by_role('button', name='Request', exact=True).wait_for(state='visible')
                if width == 390:
                    actions = page.evaluate('''() => { const d=document.querySelector('#threepic-fin-details-dialog').getBoundingClientRect(), a=document.querySelector('#threepic-fin-details-request').getBoundingClientRect(); return {dialogBottom:d.bottom,actionBottom:a.bottom,actionWidth:a.width,viewport:innerHeight}; }''')
                    print(json.dumps({'mobileActions':actions}))
                    assert actions['actionWidth'] >= 120, 'mobile primary action too small'
                    assert actions['actionBottom'] <= actions['viewport'], 'primary action outside mobile viewport'
                page.get_by_role('button', name='Request', exact=True).click()
                request = page.locator('#threepic-fin-request-dialog')
                request.wait_for(state='visible')
                page.get_by_role('button', name='Submit request').wait_for(state='visible')
                page.get_by_role('button', name='Close', exact=True).click()
                request.wait_for(state='hidden')
                poster = page.locator('#threepic-fin-movies .threepic-fin-discovery__poster-button').first
                poster.focus()
                page.keyboard.press('Enter')
                dialog.wait_for(state='visible')
                page.keyboard.press('Escape')
                dialog.wait_for(state='hidden')
                assert poster.evaluate('e => e === document.activeElement'), 'Escape should restore poster focus'
                page.close()
        finally:
            browser.close()

if __name__ == '__main__': run()
