/* Explicit host adapter only: no route polling, native result transforms or automatic injection. */
(function (global) {
    'use strict';
    // Pin only after validating exact deployed Jellyfin-web distribution in a disposable browser.
    const VERIFIED_WEB_BUNDLE_SHA256 = null;
    const field = (x, name) => x?.[name] ?? x?.[name[0].toLowerCase() + name.slice(1)];
    function createSearchAddon() {
        let current = null;
        function dispose() {
            if (!current) return;
            const old = current; current = null; ++old.generation;
            old.listeners.forEach(([node, type, handler]) => node.removeEventListener(type, handler));
            old.resultListeners.forEach(([node, type, handler]) => node.removeEventListener(type, handler));
            old.resultListeners.length = 0;
            old.section.remove();
        }
        function mount({ root, apiClient, userId, sessionUserId, query, parentId, collectionType, enabled = false, fingerprint,
            nativeTmdbKeys, requestAction }) {
            dispose();
            if (!enabled || !VERIFIED_WEB_BUNDLE_SHA256 || fingerprint !== VERIFIED_WEB_BUNDLE_SHA256 ||
                !root?.appendChild || !apiClient?.getUrl || !apiClient?.getJSON || !userId ||
                typeof sessionUserId !== 'function' || sessionUserId() !== userId ||
                typeof requestAction !== 'function' || !Array.isArray(nativeTmdbKeys) ||
                !nativeTmdbKeys.every(x => typeof x === 'string') || typeof query !== 'string' ||
                parentId !== null || collectionType !== null) return false;
            const section = document.createElement('section');
            section.className = 'threepic-fin-search'; section.setAttribute('aria-label', 'Seerr catalog');
            const heading = document.createElement('h2'); heading.textContent = 'Seerr catalog'; section.appendChild(heading);
            const body = document.createElement('div'); body.setAttribute('aria-live', 'polite'); section.appendChild(body);
            const pager = document.createElement('nav'); pager.setAttribute('aria-label', 'Seerr search pages');
            const prev = document.createElement('button'), next = document.createElement('button');
            prev.type = next.type = 'button'; prev.textContent = 'Previous'; next.textContent = 'Next';
            const pageLabel = document.createElement('span'); pager.appendChild(prev); pager.appendChild(next); pager.appendChild(pageLabel);
            section.appendChild(pager); root.appendChild(section);
            const state = { section, body, prev, next, pageLabel, apiClient, userId, sessionUserId, requestAction,
                native: new Set(nativeTmdbKeys), query: '', page: 1, max: 1, generation: 0, listeners: [], resultListeners: [] };
            current = state;
            const listen = (node, type, handler) => { node.addEventListener(type, handler); state.listeners.push([node,type,handler]); };
            function clearResults() {
                state.resultListeners.forEach(([node, type, handler]) => node.removeEventListener(type, handler));
                state.resultListeners.length = 0;
            }
            listen(prev, 'click', () => { if (valid() && state.page > 1) load(state.page - 1); });
            listen(next, 'click', () => { if (valid() && state.page < state.max) load(state.page + 1); });
            function valid() {
                if (current !== state) return false;
                if (sessionUserId() !== userId) { dispose(); return false; }
                return true;
            }
            function message(text) { body.replaceChildren(); body.textContent = text; }
            function controls() {
                prev.disabled = state.page <= 1; next.disabled = state.page >= state.max;
                pageLabel.textContent = `Page ${state.page} of ${state.max}`;
            }
            async function load(page) {
                if (!valid()) return;
                const generation = ++state.generation;
                clearResults();
                state.page = page; state.max = 1; controls(); message('Loading Seerr results…');
                try {
                    const data = await apiClient.getJSON(apiClient.getUrl('3picFin/Search', { query: state.query, page }));
                    if (!valid() || generation !== state.generation) return;
                    const items = field(data, 'Items');
                    if (field(data, 'Error') || !Array.isArray(items)) throw Error('Invalid Seerr response');
                    const seen = new Set(state.native), cards = document.createElement('div');
                    cards.className = 'threepic-fin-search__cards';
                    for (const item of items.slice(0, 20)) {
                        const id = field(item, 'TmdbId'), type = field(item, 'MediaType');
                        const key = `${type}:${id}`;
                        if (!['movie','tv'].includes(type) || !Number.isInteger(id) || id < 1 || seen.has(key)) continue;
                        seen.add(key);
                        const card = document.createElement('article'), title = document.createElement('h3');
                        title.textContent = String(field(item, 'Title') || `${type === 'tv' ? 'TV' : 'Movie'} · TMDb #${id}`);
                        card.appendChild(title);
                        const action = document.createElement('button'); action.type = 'button';
                        action.textContent = `Request ${type === 'tv' ? 'TV' : 'Movie'}`;
                        const handler = () => { if (valid() && generation === state.generation) requestAction(item, action); };
                        action.addEventListener('click', handler);
                        state.resultListeners.push([action, 'click', handler]);
                        card.appendChild(action); cards.appendChild(card);
                    }
                    body.replaceChildren();
                    if (cards.children.length) body.appendChild(cards);
                    else message('No Seerr-only results found.');
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
                clearResults();
                state.query = typeof value === 'string' ? value.trim() : '';
                ++state.generation; state.page = state.max = 1; controls();
                if (!state.query || state.query.length > 200) message('Enter a search query to see Seerr results.');
                else load(1);
            };
            state.setQuery(query);
            return true;
        }
        function update(changes = {}) {
            const { query, nativeTmdbKeys, parentId, collectionType } = changes;
            const state = current;
            if (!state) return;
            if (state.sessionUserId() !== state.userId) { dispose(); return; }
            if (Object.hasOwn(changes, 'parentId') && parentId !== null ||
                Object.hasOwn(changes, 'collectionType') && collectionType !== null) { dispose(); return; }
            if (Object.hasOwn(changes, 'query') && typeof query !== 'string') { dispose(); return; }
            // A new query invalidates the old native identity snapshot. The host must
            // wait for native search to resolve and supply its complete set atomically.
            if (query !== undefined && query.trim() !== state.query && nativeTmdbKeys === undefined) { dispose(); return; }
            if (nativeTmdbKeys !== undefined) {
                if (!Array.isArray(nativeTmdbKeys) || !nativeTmdbKeys.every(x => typeof x === 'string')) { dispose(); return; }
                state.native = new Set(nativeTmdbKeys);
            }
            if (query !== undefined && query !== state.query) state.setQuery(query);
            else if (nativeTmdbKeys !== undefined && state.query) state.setQuery(state.query);
        }
        return { mount, update, dispose };
    }
    global.ThreePicFinSearchAddon = { createSearchAddon };
    if (typeof module !== 'undefined' && module.exports) module.exports = { createSearchAddon };
})(typeof globalThis !== 'undefined' ? globalThis : this);
