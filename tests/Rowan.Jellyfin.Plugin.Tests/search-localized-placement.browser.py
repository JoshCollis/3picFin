"""Pinned Jellyfin 12.1 Search row shape: translated h2 and card data-type."""
from pathlib import Path
from playwright.sync_api import sync_playwright

web = Path(__file__).resolve().parents[2] / 'src/Rowan.Jellyfin.Plugin/Web'


def row(title, kind):
    # SearchResultsRow.tsx creates the heading/scroller; cardBuilder supplies data-type.
    return (f'<div class="verticalSection"><h2 class="sectionTitle">{title}</h2>'
            f'<div is="emby-scroller"><div is="emby-itemscontainer" class="itemsContainer">'
            f'<div class="card" data-id="native-{kind}" data-type="{kind}">Native</div>'
            '</div></div></div>')


def check(page, expected):
    assert page.locator('.searchResults > .verticalSection').count() == len(expected)
    # No reparenting or native group mutation, even across result replacements.
    assert page.evaluate('''() => [...document.querySelectorAll('.searchResults > .verticalSection')]
        .every(el => el.parentElement === document.querySelector('.searchResults') && !el.dataset.nativeBefore)''')
    assert page.evaluate('''() => [...document.querySelectorAll('.searchResults .card')]
        .map(el => el.getAttribute('data-type'))''') == expected
    ys = page.evaluate('''() => [...document.querySelectorAll('.searchResults > .verticalSection')]
        .map(el => el.getBoundingClientRect().top)''')
    seerr_y = page.locator('.threepic-fin-search').bounding_box()['y']
    return ys, seerr_y


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={'width': 1280, 'height': 800})
    page.set_content('<main id="searchPage"><div><input id="searchTextInput"></div>'
                     '<div class="searchResults"></div></main>')
    page.add_style_tag(content=(web / 'global-search-addon.css').read_text())
    page.add_script_tag(content=(web / 'global-search-addon.js').read_text())
    page.evaluate('''() => { window.addon = ThreePicFinSearchAddon.createSearchAddon();
        window.render = html => { document.querySelector('.searchResults').innerHTML = html; addon.update(); };
        addon.mount({root:document.querySelector('#searchPage'), apiClient:{getUrl:()=>'/search',
          getJSON:async()=>({Items:[{TmdbId:1,MediaType:'movie',Title:'Same'},
                                   {TmdbId:2,MediaType:'movie',Title:'Same'}],TotalPages:1})},
          userId:'a',sessionUserId:()=> 'a',query:'same',parentId:null,
          collectionType:null,enabled:true,requestAction:()=>{}}); }''')
    page.wait_for_selector('.threepic-fin-search__card:nth-child(2)')
    assert page.locator('.threepic-fin-search__card').count() == 2  # no title-only dedup
    cases = [
        ([('Filme', 'Movie'), ('Serien', 'Series'), ('Folgen', 'Episode')], 2),
        ([('Séries', 'Series'), ('Épisodes', 'Episode')], 1),
        ([('Filmes', 'Movie'), ('Episódios', 'Episode')], 1),
        ([('Épisodes', 'Episode'), ('Personnes', 'Person')], 0),
        ([], 0),
        ([('Películas', 'Movie'), ('Series', 'Series'), ('Episodios', 'Episode')], 2),
    ]
    for entries, before in cases:
        html = ''.join(row(title, kind) for title, kind in entries)
        page.evaluate('(html) => render(html)', html)
        ys, seerr_y = check(page, [kind for _, kind in entries])
        assert page.locator('.threepic-fin-search').get_attribute('data-native-before') == str(before), entries
        assert all(y < seerr_y for y in ys[:before]), (entries, ys, seerr_y)
        assert all(y > seerr_y for y in ys[before:]), (entries, ys, seerr_y)
    # Effect-built cards can appear after the React row. Unknown/mixed cards
    # must not classify a row based on one matching item or translated title.
    page.evaluate('(html) => render(html)', row('Filme', 'Movie').replace(
        '<div class="card" data-id="native-Movie" data-type="Movie">Native</div>',
        '<div class="card" data-id="native-Movie" data-type="Movie">Native</div><div class="card">Unknown</div>'))
    assert page.locator('.threepic-fin-search').get_attribute('data-native-before') == '0'
    page.evaluate('''() => { const results = document.querySelector('.searchResults');
        results.innerHTML = '<div class="verticalSection"><h2>Filme</h2><div class="itemsContainer"></div></div>';
        addon.update(); }''')
    assert page.locator('.threepic-fin-search').get_attribute('data-native-before') == '0'
    page.evaluate('''() => { document.querySelector('.searchResults .itemsContainer').innerHTML =
        '<div class="card" data-type="Movie">Late</div>'; addon.update(); }''')
    assert page.locator('.threepic-fin-search').get_attribute('data-native-before') == '1'
    page.evaluate('addon.dispose()')
    assert page.locator('.threepic-fin-search').count() == 0
    assert page.locator('.searchResults > .verticalSection').count() == 1
    browser.close()
print('localized Search placement, absent groups, rerenders, and native DOM: OK')
