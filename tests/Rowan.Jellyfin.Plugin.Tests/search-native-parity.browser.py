"""Offline ElegantFin Search card geometry/interaction regression; not hosted acceptance."""
import hashlib
import os
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'
theme = Path(os.environ['ELEGANTFIN_CSS_DIR'])
out = Path(os.environ.get('SEARCH_SCREENSHOT_DIR', str(Path(os.environ['TMPDIR']) / 'search-native-parity')))
out.mkdir(parents=True, exist_ok=True)
expected = {'elegant-source-0.css': '779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643',
            'elegant-source-1.css': '525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841'}
assert {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(theme.glob('elegant-source-*.css'))} == expected

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    for width in (390, 1280, 2560):
        page = browser.new_page(viewport={'width': width, 'height': 900}, reduced_motion='reduce')
        page.route('https://image.tmdb.org/**', lambda r: r.fulfill(content_type='image/svg+xml', body='<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="#438ab1"/><circle cx="100" cy="120" r="65" fill="#e4a452"/></svg>'))
        native = '<div class="verticalSection"><h2>Movies</h2><div class="itemsContainer">' + ''.join(
            f'<div class="card overflowPortraitCard card-hoverable show-animation" data-type="Movie"><div class="cardBox visualCardBox"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait"></div><div class="cardContent cardImageContainer"></div></div><div class="cardFooter"><div class="cardText">Native movie {i}</div></div></div></div>'
            for i in range(12)) + '</div></div>'
        layout = 'mobile' if width == 390 else 'desktop'
        groups = native + native.replace('Movies', 'Shows').replace('data-type="Movie"', 'data-type="Series"') + native.replace('Movies', 'Episodes').replace('data-type="Movie"', 'data-type="Episode"')
        page.set_content(f'<html class="layout-{layout}"><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#111827;color:white"><main id="searchPage"><div class="searchField"><input id="searchTextInput" value="Big Buck Bunny"></div><div class="searchResults">{groups}</div></main></body></html>')
        for i in (0, 1):
            page.add_style_tag(path=str(theme / f'elegant-source-{i}.css'))
        # Minimal stock Jellyfin padder rule absent from the private theme-only capture.
        page.add_style_tag(content='.searchResults { padding-inline: 3.3%; } .searchResults .itemsContainer { display:flex; overflow:hidden; } .searchResults .itemsContainer>.card { flex-shrink:0; --itemColumnGap: .5em; } .cardPadder-overflowPortrait { padding-top: 150%; }')
        page.add_style_tag(path=str(web / 'global-search-addon.css'))
        # The hosted theme paints visualCardBox later in the cascade; the owned
        # card must stay unboxed even when this rule follows the addon CSS.
        page.add_style_tag(content='#searchPage .threepic-fin-search__card .cardBox { background: rgba(30,40,54,.8); border: 1px solid rgb(71,80,92); }')
        page.add_script_tag(path=str(web / 'global-search-addon.js'))
        page.evaluate('''() => {
            window.opened = [];
            const addon = window.searchAddon = ThreePicFinSearchAddon.createSearchAddon();
            addon.mount({root: document.querySelector('#searchPage'), userId:'alice', sessionUserId:()=> 'alice',
                query:'Big Buck Bunny',parentId:null,collectionType:null,enabled:true,requestAction:item=>opened.push(item.TmdbId),
                detailsAction:item=>opened.push(item.TmdbId), apiClient:{getUrl:p=>p,getJSON:async()=>({TotalPages:1,Items:[
                    {TmdbId:10,MediaType:'movie',Title:'Big Buck Bunny',Date:'2008-04-10',PosterPath:'/poster.jpg'},
                    {TmdbId:11,MediaType:'movie',Title:'Big Buck Bunny',Date:null,PosterPath:null}]})}});
        }''')
        page.wait_for_selector('.threepic-fin-search__card:nth-child(2)')
        page.locator('.threepic-fin-search__card img').evaluate('(img) => img.decode()')
        cards = page.locator('.threepic-fin-search__card')
        assert cards.count() == 2
        assert cards.nth(0).inner_text() != cards.nth(1).inner_text(), (width, [c.inner_text() for c in cards.all()])
        assert '2008' in cards.nth(0).inner_text()
        assert 'TMDb #11' in cards.nth(1).inner_text()
        assert '2008' in (cards.nth(0).locator('button').get_attribute('aria-label') or '')
        assert 'TMDb #11' in (cards.nth(1).locator('button').get_attribute('aria-label') or '')
        metrics = page.evaluate('''() => { const n=document.querySelector('.searchResults .card'), s=document.querySelector('.threepic-fin-search__card');
            const art=e=>e.querySelector('.cardContent').getBoundingClientRect(), box=e=>e.getBoundingClientRect();
            return {native:box(n).width,seerr:box(s).width,nativeArt:art(n).width,seerrArt:art(s).width,
                nativeX:box(n).x,seerrX:box(s).x,nativeFooter:getComputedStyle(n.querySelector('.cardFooter')).backgroundColor,
                seerrFooter:getComputedStyle(s.querySelector('.cardFooter')).backgroundColor,
                nativeBorder:getComputedStyle(n.querySelector('.cardBox')).borderRadius,
                seerrBorder:getComputedStyle(s.querySelector('.cardBox')).borderRadius,
                seerrBoxBackground:getComputedStyle(s.querySelector('.cardBox')).backgroundColor,
                seerrBoxBorder:getComputedStyle(s.querySelector('.cardBox')).borderColor,
                fallback:box(document.querySelectorAll('.threepic-fin-search__card')[1]).width,
                overflow:document.documentElement.scrollWidth>innerWidth}; }''')
        print(width, metrics)
        assert abs(metrics['native'] - metrics['seerr']) < .5, (width, metrics)
        assert abs(metrics['nativeArt'] - metrics['seerrArt']) < .5, (width, metrics)
        assert abs(metrics['nativeX'] - metrics['seerrX']) < 8, (width, metrics)
        assert abs(metrics['fallback'] - metrics['seerr']) < 2
        assert metrics['nativeFooter'] == metrics['seerrFooter']
        assert metrics['nativeBorder'] == metrics['seerrBorder']
        assert metrics['seerrBoxBackground'] == 'rgba(0, 0, 0, 0)'
        assert metrics['seerrBoxBorder'] == 'rgba(0, 0, 0, 0)'
        assert not metrics['overflow']
        first_native = page.locator('.searchResults .verticalSection').first.bounding_box()
        second_native = page.locator('.searchResults .verticalSection').nth(1).bounding_box()
        episodes = page.locator('.searchResults .verticalSection').nth(2).bounding_box()
        rail_box = page.locator('.threepic-fin-search').bounding_box()
        assert first_native and second_native and episodes and rail_box
        assert first_native['y'] < second_native['y'] < rail_box['y'] < episodes['y'], (width, first_native, second_native, rail_box, episodes)
        assert page.evaluate('''() => { const n=document.querySelector('.searchResults .cardFooter'), s=document.querySelector('.threepic-fin-search .cardFooter');
          const b=document.querySelector('.threepic-fin-search__poster button').getBoundingClientRect(), p=document.querySelector('.threepic-fin-search__poster').getBoundingClientRect();
          return getComputedStyle(s).backgroundColor === getComputedStyle(n).backgroundColor && b.width < p.width * .55 && b.height < p.height * .27; }'''), width
        page.screenshot(path=str(out / f'search-{width}.png'))
        native_card = page.locator('.searchResults .card').first
        native_card.hover(); page.wait_for_timeout(250)
        native_hover = native_card.locator('.cardImageContainer').evaluate('(e) => getComputedStyle(e).transform')
        cards.nth(0).hover(); page.wait_for_timeout(500)
        seerr_hover = cards.nth(0).locator('.cardImageContainer').evaluate('(e) => getComputedStyle(e).transform')
        assert abs(float(native_hover.split('(')[1].split(',')[0]) - float(seerr_hover.split('(')[1].split(',')[0])) < .002, (width, native_hover, seerr_hover)
        assert float(seerr_hover.split('(')[1].split(',')[0]) > 1.01
        assert cards.nth(0).locator('button').evaluate('(e) => getComputedStyle(e).opacity') == '1'
        page.screenshot(path=str(out / f'search-hover-{width}.png'))
        action = cards.nth(1).locator('button')
        action.focus(); page.keyboard.press('Enter')
        assert page.evaluate('opened') == [11]
        cards.nth(1).click(position={'x': 10, 'y': 10}); assert page.evaluate('opened') == [11, 11]
        page.locator('.threepic-fin-search__card img').evaluate('(img) => img.dispatchEvent(new Event("error"))')
        assert cards.nth(0).locator('img').count() == 0
        failed_box = cards.nth(0).bounding_box()
        assert failed_box and abs(failed_box['width'] - metrics['native']) < 2
        cards.nth(0).locator('button').click()
        assert page.evaluate('opened') == [11, 11, 10]
        card_action = cards.nth(0)
        card_action.focus(); page.keyboard.press('Enter')
        assert page.evaluate('opened') == [11, 11, 10, 10]
        # Native row nodes remain owned by their original parent through changes.
        page.evaluate('''() => { const results=document.querySelector('.searchResults');
          window.originalNodes=[...results.children]; results.children[0].remove(); searchAddon.update(); }''')
        assert page.evaluate('''() => {const g=document.querySelector('.searchResults > .verticalSection'), r=document.querySelector('.threepic-fin-search');
          return g.querySelector('h2').textContent==='Shows' && g.getBoundingClientRect().y<r.getBoundingClientRect().y && originalNodes.every(n=>n.parentElement===document.querySelector('.searchResults')||!n.isConnected); }''')
        page.evaluate('''() => {document.querySelector('.searchResults > .verticalSection').remove(); searchAddon.update();}''')
        assert page.evaluate('''() => document.querySelector('.threepic-fin-search').getBoundingClientRect().y < document.querySelector('.searchResults > .verticalSection').getBoundingClientRect().y''')
        page.evaluate('''() => {document.querySelector('.searchResults').replaceChildren(); searchAddon.update();}''')
        assert page.locator('.threepic-fin-search__card').count() == 2
        page.evaluate('''() => {document.querySelector('.searchResults').replaceChildren(...originalNodes); searchAddon.update();}''')
        assert page.evaluate('''() => {const g=document.querySelectorAll('.searchResults > .verticalSection');
          return g[1].getBoundingClientRect().y < document.querySelector('.threepic-fin-search').getBoundingClientRect().y < g[2].getBoundingClientRect().y;}''')
        page.close()
    browser.close()
print('ElegantFin Search native parity: pass')
