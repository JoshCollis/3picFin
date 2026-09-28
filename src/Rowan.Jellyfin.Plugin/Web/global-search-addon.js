/* Separate catalog section; native Jellyfin results are never changed. */
(function (global) {
    'use strict';
    const field = (x, name) => x?.[name] ?? x?.[name[0].toLowerCase() + name.slice(1)];
    function createSearchAddon() {
        let current = null;
        function dispose() {
            if (!current) return;
            const old = current; current = null; ++old.generation;
            clearTimeout(old.timer); old.abort?.abort();
            old.listeners.forEach(([node, type, handler]) => node.removeEventListener(type, handler));
            old.resultListeners.forEach(([node, type, handler]) => node.removeEventListener(type, handler));
            old.resultListeners.length = 0;
            old.section.remove();
        }
        function mount({ root, apiClient, userId, sessionUserId, query, parentId, collectionType, enabled = false,
            requestAction, detailsAction }) {
            dispose();
            if (!enabled || !root?.appendChild || !apiClient?.getUrl || !apiClient?.getJSON || !userId ||
                typeof sessionUserId !== 'function' || sessionUserId() !== userId ||
                typeof requestAction !== 'function' || typeof query !== 'string' ||
                parentId !== null || collectionType !== null) return false;
            const section = document.createElement('section');
            section.className = 'threepic-fin-search'; section.setAttribute('aria-label', 'Seerr catalog');
            const heading = document.createElement('h2'); heading.textContent = 'Available to request'; section.appendChild(heading);
            const body = document.createElement('div'); body.setAttribute('aria-live', 'polite'); section.appendChild(body);
            const pager = document.createElement('nav'); pager.setAttribute('aria-label', 'Seerr search pages');
            const prev = document.createElement('button'), next = document.createElement('button');
            prev.type = next.type = 'button'; prev.textContent = '‹'; next.textContent = '›';
            prev.setAttribute('aria-label', 'Previous results'); next.setAttribute('aria-label', 'Next results');
            const pageLabel = document.createElement('span'); pager.appendChild(prev); pager.appendChild(next); pager.appendChild(pageLabel);
            section.appendChild(pager);
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
            const state = { section, body, prev, next, pageLabel, apiClient, userId, sessionUserId, requestAction, detailsAction,
                query: '', page: 1, max: 1, generation: 0, listeners: [], resultListeners: [], timer: null, abort: null };
            current = state;
            const listen = (node, type, handler) => { node.addEventListener(type, handler); state.listeners.push([node,type,handler]); };
            function clearResults() {
                state.resultListeners.forEach(([node, type, handler]) => node.removeEventListener(type, handler));
                state.resultListeners.length = 0;
            }
            function rail() { return body.querySelector?.('.threepic-fin-search__cards'); }
            function canScroll(el) { return el && el.scrollWidth > el.clientWidth + 2; }
            listen(prev, 'click', () => {
                if (!valid()) return;
                const el = rail();
                if (el && el.scrollLeft > 2) el.scrollBy({ left: -el.clientWidth * .8, behavior: 'smooth' });
                else if (state.page > 1) load(state.page - 1);
            });
            listen(next, 'click', () => {
                if (!valid()) return;
                const el = rail();
                if (canScroll(el) && el.scrollLeft + el.clientWidth < el.scrollWidth - 2)
                    el.scrollBy({ left: el.clientWidth * .8, behavior: 'smooth' });
                else if (state.page < state.max) load(state.page + 1);
            });
            function valid() {
                if (current !== state) return false;
                if (sessionUserId() !== userId) { dispose(); return false; }
                return true;
            }
            function message(text) { body.replaceChildren(); body.textContent = text; }
            function controls() {
                const el = rail();
                prev.disabled = state.page <= 1 && (!el || el.scrollLeft <= 2);
                next.disabled = state.page >= state.max && (!canScroll(el) || el.scrollLeft + el.clientWidth >= el.scrollWidth - 2);
                pager.hidden = state.max <= 1 && !canScroll(el);
                pageLabel.textContent = `Page ${state.page} of ${state.max}`;
            }
            async function load(page) {
                if (!valid()) return;
                state.abort?.abort();
                const abort = new AbortController(); state.abort = abort;
                const generation = ++state.generation;
                clearResults();
                state.page = page; state.max = 1; controls(); message('Loading Seerr results…');
                try {
                    const data = await apiClient.getJSON(apiClient.getUrl('3picFin/Search', { query: state.query, page }), { signal: abort.signal });
                    if (!valid() || generation !== state.generation) return;
                    const items = field(data, 'Items');
                    if (field(data, 'Error') || !Array.isArray(items)) throw Error('Invalid Seerr response');
                    const seen = new Set(), cards = document.createElement('div');
                    cards.className = 'threepic-fin-search__cards itemsContainer';
                    for (const item of items.slice(0, 20)) {
                        const id = field(item, 'TmdbId'), type = field(item, 'MediaType');
                        const key = `${type}:${id}`;
                        if (!['movie','tv'].includes(type) || !Number.isInteger(id) || id < 1 || seen.has(key)) continue;
                        seen.add(key);
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
                        const handler = () => {
                            if (valid() && generation === state.generation)
                                (hasDetails ? state.detailsAction : requestAction)(item, action);
                        };
                        action.addEventListener('click', handler);
                        state.resultListeners.push([action, 'click', handler]);
                        media.appendChild(action);
                        cards.appendChild(card);
                    }
                    body.replaceChildren();
                    if (cards.children.length) {
                        body.appendChild(cards);
                        cards.addEventListener('scroll', controls);
                        state.resultListeners.push([cards, 'scroll', controls]);
                    } else message('No Seerr catalog results found.');
                    const total = field(data, 'TotalPages');
                    state.max = Number.isInteger(total) && total > 0 ? Math.min(total, 100) : 1;
                    controls();
                } catch (_) {
                    if (!valid() || generation !== state.generation) return;
                    message('Seerr results unavailable right now.'); state.max = 1; controls();
                }
            }
            state.setQuery = value => {
                if (!valid()) return;
                clearTimeout(state.timer); state.timer = null;
                state.abort?.abort(); state.abort = null;
                clearResults();
                state.query = typeof value === 'string' ? value.trim() : '';
                ++state.generation; state.page = state.max = 1; controls();
                if (!state.query || state.query.length > 200) message('Enter a search query to see Seerr results.');
                else {
                    message('Loading Seerr results…');
                    // Mount's first query is already settled; only subsequent typing waits.
                    if (state.generation === 1) load(1);
                    else state.timer = setTimeout(() => { state.timer = null; load(1); }, 160);
                }
            };
            state.setQuery(query);
            return true;
        }
        function update(changes = {}) {
            const { query, parentId, collectionType } = changes;
            const state = current;
            if (!state) return;
            if (state.sessionUserId() !== state.userId) { dispose(); return; }
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
