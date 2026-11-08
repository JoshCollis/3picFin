/* Separate catalog section; native Jellyfin results are never changed. */
(function (global) {
    'use strict';
    const field = (x, name) => x?.[name] ?? x?.[name[0].toLowerCase() + name.slice(1)];
    function createSearchAddon() {
        let current = null, restoration = null;
        function dispose() {
            if (!current) return;
            const old = current; current = null; ++old.generation;
            restoration = old.sessionUserId() === old.userId && old.feed ? {
                userId: old.userId, apiClient: old.apiClient, token: old.apiClient.accessToken?.(),
                query: old.query, feed: old.feed.snapshot()
            } : null;
            clearTimeout(old.timer);
            old.feed?.dispose();
            old.section.remove();
        }
        function syncLayout(state, measure = false) {
            const root = state.section.parentElement;
            const groups = root?.querySelectorAll?.(':scope > .searchResults > .verticalSection');
            if (!groups) return;
            let before = 0;
            // Jellyfin 12.1 translates the heading, but cardBuilder keeps the
            // native BaseItemKind in data-type. Wait for its effect-built cards;
            // an unpopulated/ambiguous row must not be inferred from its title.
            for (const group of groups) {
                if (before === 2) break;
                const cards = group.querySelectorAll('.itemsContainer .card');
                const expected = before === 0 ? ['Movie', 'Series'] : ['Series'];
                if (!cards.length) break;
                const kind = cards[0].getAttribute('data-type');
                if (!expected.includes(kind) || ![...cards].every(card => card.getAttribute('data-type') === kind)) break;
                before++;
            }
            const reference = groups[0]?.querySelector('.card') || groups[0]?.querySelector('h2, h3');
            let gutter = state.gutter;
            // Read before writing, and only when the native reference or viewport
            // changes. Image/custom-element mutations cannot change this anchor.
            if ((measure || reference !== state.reference) && reference?.getBoundingClientRect && root.getBoundingClientRect) {
                const left = reference.getBoundingClientRect().left - root.getBoundingClientRect().left;
                if (left >= 0) gutter = `${left}px`;
            }
            state.reference = reference;
            const value = String(before);
            if (state.section.dataset.nativeBefore !== value) state.section.dataset.nativeBefore = value;
            if (gutter !== undefined && gutter !== state.gutter) {
                state.section.style.setProperty('--search-rail-gutter', gutter); state.gutter = gutter;
            }
        }
        function mount({ root, apiClient, userId, sessionUserId, query, parentId, collectionType, enabled = false,
            requestAction, detailsAction, nativeResultLimit }) {
            dispose();
            if (!enabled || !root?.appendChild || !apiClient?.getUrl || !apiClient?.getJSON || !userId ||
                typeof sessionUserId !== 'function' || sessionUserId() !== userId ||
                typeof requestAction !== 'function' || typeof query !== 'string' ||
                parentId !== null || collectionType !== null) return false;
            const section = document.createElement('section');
            section.className = 'threepic-fin-search'; section.setAttribute('aria-label', 'Seerr catalog');
            const heading = document.createElement('h2'); heading.textContent = 'Available to request'; section.appendChild(heading);
            const body = document.createElement('div'); body.className = 'threepic-fin-search__cards itemsContainer'; section.appendChild(body);
            const status = document.createElement('div'); status.className = 'threepic-fin-search__status'; section.appendChild(status);
            const limitNote = document.createElement('p'); limitNote.className = 'threepic-fin-search__limit-note';
            limitNote.hidden = true; section.appendChild(limitNote);
            // Search's native rows are React-owned. Insert a sibling ahead of
            // them rather than appending after an arbitrarily long result list.
            const nativeResults = root.querySelector?.('.searchResults');
            const nativeColumn = nativeResults && [...root.children].find(child => child.contains(nativeResults));
            if (nativeColumn && !nativeColumn.contains(root.querySelector('#searchTextInput')))
                root.insertBefore(section, nativeColumn);
            else {
                const input = root.querySelector?.('#searchTextInput');
                const inputColumn = input && [...root.children].find(child => child.contains(input));
                if (inputColumn) inputColumn.after(section);
                else root.appendChild(section);
            }
            const state = { section, body, apiClient, userId, sessionUserId, requestAction, detailsAction,
                query: '', generation: 0, timer: null, limitNote };
            state.setNativeLimit = limit => {
                const text = Number.isInteger(limit) && limit > 0
                    ? `Jellyfin search is limited to ${limit} results per request. Refine your search for more.` : '';
                if (limitNote.textContent !== text) limitNote.textContent = text;
                if (limitNote.hidden !== !text) limitNote.hidden = !text;
            };
            state.setNativeLimit(nativeResultLimit);
            current = state;
            syncLayout(state);
            function valid() {
                if (current !== state) return false;
                if (sessionUserId() !== userId) { dispose(); return false; }
                return true;
            }
            state.feed = global.ThreePicFinContinuousRail.create({
                rail: body, status, title: 'Seerr results', isCurrent: valid,
                loadPage: async (page, signal) => {
                    const data = await apiClient.getJSON(apiClient.getUrl('3picFin/Search', { query: state.query, page }), { signal });
                    const items = field(data, 'Items');
                    if (Array.isArray(items) && items.some(item => !['movie', 'tv'].includes(field(item, 'MediaType')) || !Number.isInteger(field(item, 'TmdbId')) || field(item, 'TmdbId') < 1)) throw Error('Invalid identity');
                    return data;
                },
                renderItem: item => {
                    const generation = state.generation;
                    const id = field(item, 'TmdbId'), type = field(item, 'MediaType');
                    const card = document.createElement('article');
                    card.className = 'threepic-fin-search__card threepic-fin-search__card--no-art card overflowPortraitCard card-hoverable show-animation';
                    const name = String(field(item, 'Title') || `${type === 'tv' ? 'TV' : 'Movie'} · TMDb #${id}`);
                    const date = field(item, 'Date');
                    const year = typeof date === 'string' && /^(?:18|19|20|21)\d{2}(?:-\d{2}-\d{2})?$/.test(date) ? date.slice(0, 4) : null;
                    const metadata = year || `TMDb #${id}`;
                    const box = document.createElement('div'); box.className = 'cardBox visualCardBox';
                    const scalable = document.createElement('div'); scalable.className = 'cardScalable';
                    const padder = document.createElement('div'); padder.className = 'cardPadder cardPadder-overflowPortrait';
                    const media = document.createElement('div'); media.className = 'threepic-fin-search__poster cardContent cardImageContainer';
                    const fallback = document.createElement('span'); fallback.className = 'threepic-fin-search__poster-label';
                    fallback.textContent = name; fallback.setAttribute('aria-hidden', 'true');
                    media.appendChild(fallback);
                    scalable.append(padder, media);
                    const footer = document.createElement('div'); footer.className = 'cardFooter';
                    const title = document.createElement('div'); title.className = 'cardText'; title.textContent = name;
                    const subtitle = document.createElement('div'); subtitle.className = 'threepic-fin-search__metadata'; subtitle.textContent = metadata;
                    footer.append(title, subtitle); box.append(scalable, footer); card.appendChild(box);
                    const poster = field(item, 'PosterPath');
                    if (typeof poster === 'string' && /^\/[a-zA-Z0-9_/-]+\.(?:jpg|jpeg|png|webp)$/.test(poster) && !poster.includes('..')) {
                        const image = document.createElement('img');
                        image.alt = ''; image.loading = 'lazy';
                        card.classList.replace('threepic-fin-search__card--no-art', 'threepic-fin-search__card--has-art');
                        image.addEventListener('error', () => { image.remove(); card.classList.replace('threepic-fin-search__card--has-art', 'threepic-fin-search__card--no-art'); });
                        image.src = `https://image.tmdb.org/t/p/w342${poster}`;
                        media.appendChild(image);
                    }
                    const action = document.createElement('button'); action.type = 'button';
                    const hasDetails = typeof state.detailsAction === 'function';
                    action.textContent = 'ⓘ';
                    action.setAttribute('aria-label', `Details for ${name} (${metadata})`);
                    card.tabIndex = 0;
                    card.setAttribute('aria-label', `Details for ${name} (${metadata})`);
                    const handler = () => {
                        if (valid() && generation === state.generation)
                            (hasDetails ? state.detailsAction : requestAction)(item, action);
                    };
                    card.addEventListener('click', handler);
                    card.addEventListener('keydown', event => {
                        if (event.target === card && ['Enter', ' '].includes(event.key)) { event.preventDefault(); handler(); }
                    });
                    media.appendChild(action);
                    return card;
                }
            });
            state.setQuery = value => {
                if (!valid()) return;
                clearTimeout(state.timer); state.timer = null;
                state.feed.reset();
                state.query = typeof value === 'string' ? value.trim() : '';
                ++state.generation;
                if (!state.query || state.query.length > 200) status.querySelector('span').textContent = 'Enter a search query to see Seerr results.';
                else {
                    // Mount's first query is already settled; only subsequent typing waits.
                    if (state.generation === 1) state.feed.load();
                    else state.timer = setTimeout(() => { state.timer = null; state.feed.load(); }, 160);
                }
            };
            if (restoration?.userId === userId && restoration?.apiClient === apiClient &&
                restoration?.token === apiClient.accessToken?.() && restoration?.query === query.trim()) {
                state.query = query.trim(); ++state.generation; state.feed.restore(restoration.feed);
            } else { restoration = null; state.setQuery(query); }
            return true;
        }
        function update(changes = {}) {
            const { query, parentId, collectionType } = changes;
            const state = current;
            if (!state) return;
            if (state.sessionUserId() !== state.userId) { dispose(); return; }
            if (changes.layout !== false) syncLayout(state, changes.measure === true);
            if (Object.hasOwn(changes, 'nativeResultLimit')) state.setNativeLimit(changes.nativeResultLimit);
            if (Object.hasOwn(changes, 'parentId') && parentId !== null ||
                Object.hasOwn(changes, 'collectionType') && collectionType !== null ||
                Object.hasOwn(changes, 'query') && typeof query !== 'string') { dispose(); return; }
            if (query !== undefined && query.trim() !== state.query) state.setQuery(query);
        }
        return { mount, update, dispose };
    }
    global.ThreePicFinSearchAddon = { createSearchAddon };
    if (typeof module !== 'undefined' && module.exports) module.exports = { createSearchAddon };
})(typeof globalThis !== 'undefined' ? globalThis : this);
