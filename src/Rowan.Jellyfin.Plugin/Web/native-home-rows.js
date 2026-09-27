/* Opt-in, isolated Home rows; never touches HSS's .sections. */
(function (global) {
    'use strict';
    const names = ['ContinueWatching', 'NextUp', 'LatestMovies', 'LatestShows', 'MyMedia'];
    const optional = ['ContinueWatchingNextUp', 'Collections', 'LiveTV', 'MyRequests', 'UpcomingMovies', 'UpcomingShows', 'BecauseYouWatched', 'Discover', 'DiscoverMovies', 'DiscoverTV'];
    const field = (item, key) => item?.[key] ?? item?.[key[0].toLowerCase() + key.slice(1)];
    const discover = name => ['Discover', 'DiscoverMovies', 'DiscoverTV'].includes(name);
    function createRows({ document, IntersectionObserver, enabledRows = [], openItem } = {}) {
        const activeNames = names.concat(optional.filter(name => enabledRows.includes(name)));
        let current = null, generation = 0;
        function dispose() {
            ++generation;
            if (!current) return;
            current.observer.disconnect();
            current.sections.forEach(section => section.remove());
            current = null;
        }
        function mount(root, apiClient, userId) {
            dispose();
            if (!root || !userId || typeof apiClient?.getUrl !== 'function' ||
                typeof apiClient?.getJSON !== 'function' || !IntersectionObserver) return false;
            const ticket = generation;
            const sections = activeNames.map(name => {
                const section = document.createElement('section');
                section.className = 'rowan-native-row';
                const heading = document.createElement('h2'); heading.textContent = name === 'ContinueWatchingNextUp' ? 'Continue Watching / Next Up' : name === 'LiveTV' ? 'Live TV' : name === 'DiscoverTV' ? 'Discover TV' : name === 'BecauseYouWatched' ? 'Because You Watched' : name.replace(/([a-z])([A-Z])/g, '$1 $2');
                const body = document.createElement('div');
                body.className = name === 'BecauseYouWatched' ? 'rowan-native-row__seeds' : 'rowan-native-row__items';
                section.appendChild(heading); section.appendChild(body); root.appendChild(section);
                return section;
            });
            const requested = new Set();
            const observer = new IntersectionObserver(entries => {
                for (const entry of entries) {
                    const index = sections.indexOf(entry.target);
                    if (!entry.isIntersecting || index < 0 || requested.has(index)) continue;
                    requested.add(index);
                    const body = sections[index].children[1];
                    body.textContent = 'Loading…';
                    const name = activeNames[index];
                    const path = name === 'BecauseYouWatched' ? 'Rowan/Home/BecauseYouWatched' :
                        discover(name) ? `3picFin/HomeDiscover/${name}` : `Rowan/Home/Rows/${name}`;
                    Promise.resolve().then(() => {
                        if (ticket !== generation || current?.userId !== userId) return null;
                        return apiClient.getJSON(apiClient.getUrl(path));
                    })
                        .then(row => {
                            if (ticket !== generation || !current || current.userId !== userId) return;
                            if (name === 'BecauseYouWatched') {
                                if (!Array.isArray(row) || row.length > 5) throw Error('Invalid seeds');
                                const seedSections = row.map(seed => {
                                    const items = field(seed, 'Items');
                                    if (typeof field(seed, 'Heading') !== 'string' || !field(seed, 'Heading').startsWith('Because You Watched ') ||
                                        !field(seed, 'SeedId') || !field(field(seed, 'Seed'), 'Id') ||
                                        field(seed, 'SeedId') !== field(field(seed, 'Seed'), 'Id') ||
                                        !Array.isArray(items) || items.length > 16 || !items.every(item => field(item, 'Id'))) throw Error('Invalid seed row');
                                    const group = document.createElement('section'); group.className = 'rowan-native-row__seed';
                                    const title = document.createElement('h2'); title.textContent = field(seed, 'Heading'); group.appendChild(title);
                                    const cards = document.createElement('div'); cards.className = 'rowan-native-row__items';
                                    for (const item of items) {
                                        const card = document.createElement(typeof openItem === 'function' ? 'button' : 'article');
                                        card.className = 'rowan-native-row__card rowan-native-row__card--landscape';
                                        if (card.tagName?.toLowerCase() === 'button') {
                                            card.type = 'button'; card.addEventListener('click', () => {
                                                if (ticket === generation && current?.userId === userId) openItem(item);
                                            });
                                        }
                                        card.textContent = String(field(item, 'Name') || 'Untitled'); cards.appendChild(card);
                                    }
                                    group.appendChild(cards); return group;
                                });
                                body.replaceChildren(...seedSections);
                                if (!row.length) body.textContent = 'No items';
                                return;
                            }
                            if (activeNames[index].startsWith('Upcoming') && (row?.Error || row?.error)) throw Error('Source unavailable');
                            const items = field(row, 'Items');
                            if (field(row, 'Error') || !Array.isArray(items) || items.length > (discover(name) ? 20 : 64)) throw Error('Invalid row');
                            if (discover(name) && !items.every(item => ['movie', 'tv'].includes(field(item, 'MediaType')) &&
                                Number.isInteger(field(item, 'TmdbId')) && field(item, 'TmdbId') > 0)) throw Error('Invalid candidate');
                            body.replaceChildren();
                            for (const item of items) {
                                if (discover(name)) {
                                    const type = field(item, 'MediaType'), id = field(item, 'TmdbId');
                                    if (!['movie', 'tv'].includes(type) || !Number.isInteger(id) || id <= 0) throw Error('Invalid candidate');
                                    const card = document.createElement('article');
                                    card.className = 'rowan-native-row__card rowan-native-row__card--portrait';
                                    const poster = field(item, 'PosterPath');
                                    if (typeof poster === 'string' && /^\/[a-zA-Z0-9_/-]+\.(?:jpg|jpeg|png|webp)$/.test(poster) && !poster.includes('..') && !poster.startsWith('//')) {
                                        const image = document.createElement('img'); image.src = `https://image.tmdb.org/t/p/w342${poster}`;
                                        image.alt = ''; image.loading = 'lazy'; card.appendChild(image);
                                    }
                                    const label = document.createElement('span');
                                    label.textContent = `${String(field(item, 'Title') || `${type === 'tv' ? 'TV' : 'Movie'} · TMDb #${id}`)} · Seerr candidate`;
                                    card.appendChild(label); body.appendChild(card); continue;
                                }
                                const card = document.createElement('span');
                                card.className = 'rowan-native-row__item';
                                card.textContent = activeNames[index].startsWith('Upcoming')
                                    ? `${String(item?.Title || item?.title || 'Untitled')} — ${String(item?.Date || item?.date || 'Date unavailable').slice(0, 10)}` +
                                        (activeNames[index] === 'UpcomingShows' ? ` — ${String(item?.EpisodeTitle || item?.episodeTitle || 'Episode')}` : '')
                                    : activeNames[index] === 'LiveTV'
                                    ? `${String(item?.Title || 'Live program')} — ${String(item?.ChannelName || 'Live TV channel')}`
                                    : String(item?.Name || item?.name || 'Untitled');
                                body.appendChild(card);
                            }
                            if (!items.length) body.textContent = 'No items';
                        }).catch(() => {
                            if (ticket === generation && current?.userId === userId)
                                body.textContent = 'Row unavailable';
                        });
                }
            });
            current = { observer, sections, userId };
            sections.forEach(section => observer.observe(section));
            return true;
        }
        return { mount, dispose };
    }
    global.RowanNativeHomeRows = { createRows };
    if (typeof module !== 'undefined' && module.exports) module.exports = { createRows };
})(typeof globalThis !== 'undefined' ? globalThis : this);
