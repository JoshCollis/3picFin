/* Supplementary user-scoped rows. Never relocates Jellyfin's .sections. */
(function (global) {
    'use strict';
    const allowed = ['ContinueWatching', 'NextUp', 'LatestMovies', 'LatestShows', 'MyMedia',
        'ContinueWatchingNextUp', 'Collections', 'BecauseYouWatched', 'MyRequests'];
    const field = (item, key) => item?.[key] ?? item?.[key[0].toLowerCase() + key.slice(1)];
    const guid = id => typeof id === 'string' && /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i.test(id);
    const title = name => ({ ContinueWatchingNextUp: 'Continue Watching / Next Up',
        BecauseYouWatched: 'Because You Watched' })[name] || name.replace(/([a-z])([A-Z])/g, '$1 $2');
    function createRows({ document, IntersectionObserver, enabledRows = [], openItem } = {}) {
        const kinds = [...new Set(enabledRows)].filter(name => allowed.includes(name)).slice(0, 9);
        let current = null, generation = 0;
        function dispose() {
            ++generation;
            if (!current) return;
            current.observer.disconnect();
            current.root.replaceChildren();
            current = null;
        }
        function mount(root, apiClient, userId) {
            dispose();
            if (!root || !userId || !kinds.length || typeof apiClient?.getUrl !== 'function' ||
                typeof apiClient?.getJSON !== 'function' || !IntersectionObserver) return false;
            const ticket = generation;
            const active = () => ticket === generation && current?.userId === userId &&
                (!apiClient.getCurrentUserId || apiClient.getCurrentUserId() === userId);
            const makeCard = (item, landscape = false) => {
                const id = field(item, 'Id'), type = field(item, 'Type');
                if (!guid(id) || !['Movie', 'Series', 'Episode', 'BoxSet', 'Folder', 'CollectionFolder'].includes(type)) return null;
                const card = document.createElement(typeof openItem === 'function' &&
                    ['Movie', 'Series', 'Episode', 'BoxSet'].includes(type) ? 'button' : 'article');
                card.className = 'rowan-native-row__card ' + (landscape ? 'rowan-native-row__card--landscape' : 'rowan-native-row__card--portrait');
                if (card.tagName.toLowerCase() === 'button') {
                    card.type = 'button';
                    card.addEventListener('click', () => { if (active()) openItem({ Id: id, Type: type }); });
                }
                const tags = field(item, 'ImageTags');
                const primary = field(tags, 'Primary');
                const backdrop = field(item, 'BackdropImageTags');
                const tag = landscape && Array.isArray(backdrop) ? backdrop[0] : primary;
                if (typeof tag === 'string' && /^[0-9a-f]{1,64}$/i.test(tag)) {
                    const image = document.createElement('img');
                    image.src = apiClient.getUrl(`Items/${id}/Images/${landscape ? 'Backdrop/0' : 'Primary'}`, { tag, maxWidth: landscape ? 480 : 240 });
                    image.alt = ''; image.loading = 'lazy'; card.appendChild(image);
                }
                const label = document.createElement('span'); label.textContent = String(field(item, 'Name') || 'Untitled').slice(0, 180);
                card.appendChild(label); return card;
            };
            const sections = kinds.map(kind => {
                const section = document.createElement('section'); section.className = 'rowan-native-row';
                const heading = document.createElement('h2'); heading.textContent = title(kind);
                const body = document.createElement('div'); body.className = kind === 'BecauseYouWatched' ? 'rowan-native-row__seeds' : 'rowan-native-row__items';
                section.append(heading, body); root.appendChild(section); return section;
            });
            const requested = new Set(), attempts = new Map();
            const observer = new IntersectionObserver(entries => {
                for (const entry of entries) {
                    const index = sections.indexOf(entry.target);
                    if (!entry.isIntersecting || index < 0 || requested.has(index) || !active() ||
                        (attempts.get(index) || 0) >= 2) continue;
                    requested.add(index);
                    attempts.set(index, (attempts.get(index) || 0) + 1);
                    const body = sections[index].children[1], kind = kinds[index];
                    body.textContent = 'Loading…';
                    const path = kind === 'BecauseYouWatched' ? 'Rowan/Home/BecauseYouWatched' : `Rowan/Home/Rows/${kind}`;
                    Promise.resolve().then(() => active() ? apiClient.getJSON(apiClient.getUrl(path)) : null)
                        .then(row => {
                            if (!active()) return;
                            if (kind === 'BecauseYouWatched') {
                                if (!Array.isArray(row) || row.length > 5) throw Error('Invalid row');
                                const groups = row.map(seed => {
                                    const items = field(seed, 'Items'), headingText = field(seed, 'Heading');
                                    if (!Array.isArray(items) || items.length > 16 || typeof headingText !== 'string' ||
                                        !headingText.startsWith('Because You Watched ')) throw Error('Invalid seed');
                                    const group = document.createElement('section'); group.className = 'rowan-native-row__seed';
                                    const heading = document.createElement('h3'); heading.textContent = headingText.slice(0, 180);
                                    const cards = document.createElement('div'); cards.className = 'rowan-native-row__items';
                                    for (const item of items) { const card = makeCard(item, true); if (card) cards.appendChild(card); }
                                    group.append(heading, cards); return group;
                                });
                                body.replaceChildren(...groups); if (!groups.length) body.textContent = 'No items'; return;
                            }
                            const items = field(row, 'Items');
                            if (field(row, 'Error') || !Array.isArray(items) || items.length > 64 || field(row, 'Kind') !== kind)
                                throw Error('Invalid row');
                            const cards = items.map(item => makeCard(item, ['ContinueWatching', 'NextUp', 'ContinueWatchingNextUp'].includes(kind))).filter(Boolean);
                            body.replaceChildren(...cards);
                            if (!cards.length) body.textContent = 'No items';
                        }).catch(() => {
                            if (active()) { requested.delete(index); body.textContent = 'Row unavailable'; }
                        });
                }
            });
            current = { observer, root, userId };
            sections.forEach(section => observer.observe(section));
            return true;
        }
        return { mount, dispose };
    }
    global.RowanNativeHomeRows = { createRows };
    if (typeof module !== 'undefined' && module.exports) module.exports = { createRows };
})(typeof globalThis !== 'undefined' ? globalThis : this);
