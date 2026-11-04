/* Shared name/number search. In the guide this is embedded in its verified module;
 * in main Search it is a public, user-independent helper. No global data cache. */
var ThreePicFinChannelSearch = (function () {
    'use strict';
    const fold = value => String(value || '').normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase().replace(/\s+/g, '');
    function createCatalogue({ api, userId, isCurrent, now = () => Date.now() }) {
        const batch = 500, maximum = 10000, ttl = 60000;
        let cache = null, epoch = 0, stopped = false;
        function valid(ticket) {
            if (stopped || !userId || api.getCurrentUserId() !== userId || !isCurrent() || ticket !== epoch)
                throw Error('Channel search scope changed');
        }
        function metadata(item) {
            if (!/^[a-f0-9-]{32,36}$/i.test(item?.Id || '') || !['TvChannel', 'LiveTvChannel'].includes(item.Type) ||
                typeof item.Name !== 'string' || item.Name.length > 300) throw Error('Invalid channel metadata');
            return { Id: item.Id, Name: item.Name, ChannelNumber: String(item.ChannelNumber || '').slice(0,64),
                Type: item.Type, IsFolder: false, ServerId: String(item.ServerId || '').slice(0,64),
                ImageTags: item.ImageTags?.Primary ? { Primary: String(item.ImageTags.Primary).slice(0,256) } : {} };
        }
        async function load(query, key, ticket) {
            const items = [], ids = new Set(); let total = null;
            for (let offset = 0; offset < maximum; offset += batch) {
                valid(ticket);
                const data = await api.getLiveTvChannels({ ...query, UserId: userId, StartIndex: offset, Limit: batch,
                    AddCurrentProgram: false, EnableUserData: false, EnableImageTypes: 'Primary' });
                valid(ticket);
                if (!Number.isSafeInteger(data?.TotalRecordCount) || data.TotalRecordCount < 0 || data.TotalRecordCount > maximum ||
                    total !== null && total !== data.TotalRecordCount || !Array.isArray(data.Items))
                    throw Error('Channel list changed or exceeds the search limit');
                total = data.TotalRecordCount;
                if (data.Items.length !== Math.min(batch, total - offset)) throw Error('Incomplete channel list');
                for (const raw of data.Items) {
                    const item = metadata(raw);
                    if (ids.has(item.Id)) throw Error('Channel list changed');
                    ids.add(item.Id); items.push({ item, name: fold(item.Name), number: fold(item.ChannelNumber) });
                }
                if (items.length === total) break;
            }
            valid(ticket);
            cache = { key, items, expires: now() + ttl };
            return cache;
        }
        return {
            async page(query, term, start, limit, allowedIds = null) {
                const ticket = epoch; valid(ticket);
                if (String(term).length > 100 || !Number.isInteger(start) || start < 0 || !Number.isInteger(limit) || limit < 1 || limit > 500)
                    throw Error('Invalid channel search');
                const scope = { ...query }; delete scope.StartIndex; delete scope.Limit; delete scope.UserId; delete scope.SearchTerm;
                const key = JSON.stringify(Object.keys(scope).sort().map(k => [k, scope[k]]));
                const snapshot = cache?.key === key && cache.expires > now() ? cache : await load(scope, key, ticket);
                valid(ticket);
                // The guide supplies only server-authorized assigned-group IDs. Main Search is unchanged.
                const needle = fold(term), found = snapshot.items.filter(row => (!allowedIds || allowedIds.has(row.item.Id.replace(/-/g, '').toLowerCase())) &&
                    (row.name.includes(needle) || row.number.includes(needle)));
                return { Items: found.slice(start, start + limit).map(row => row.item), TotalRecordCount: found.length, StartIndex: start };
            },
            dispose() { stopped = true; ++epoch; cache = null; }
        };
    }
    function createSection() {
        let state = null;
        function dispose() {
            if (!state) return;
            state.stopped = true; clearTimeout(state.timer); state.catalogue?.dispose(); state.section.remove(); state = null;
        }
        function mount({ root, apiClient: api, userId, sessionUserId, isCurrent, query }) {
            if (state && state.api === api && state.userId === userId) {
                state.root = root; state.isCurrent = isCurrent; state.sessionUserId = sessionUserId;
                root.insertBefore(state.section, root.querySelector(':scope > .threepic-fin-search'));
                state.section.hidden = false; state.update(query); return;
            }
            dispose();
            if (!userId || !api?.getLiveTvChannels || !api?.getCurrentUser || !root) return;
            const section = document.createElement('section'); section.className = 'threepic-fin-channels';
            section.setAttribute('aria-label', 'Channels');
            const heading = document.createElement('h2'); heading.textContent = 'Channels';
            const body = document.createElement('div'); body.setAttribute('aria-live','polite');
            section.append(heading, body);
            const before = root.querySelector(':scope > .threepic-fin-search');
            root.insertBefore(section, before);
            const valid = () => state === current && !current.stopped && current.root.isConnected && current.sessionUserId() === userId && current.isCurrent();
            const current = { section, body, api, root, userId, isCurrent, sessionUserId, query: '', timer: null, stopped: false, running: false, pending: null };
            state = current;
            current.catalogue = createCatalogue({ api, userId, isCurrent: valid });
            function message(text) { body.replaceChildren(); body.textContent = text; }
            async function run() {
                if (current.running || !valid()) return;
                current.running = true;
                try {
                    const user = await api.getCurrentUser();
                    if (!valid()) return;
                    if (user?.Policy?.EnableLiveTvAccess !== true) { section.hidden = true; return; }
                    while (current.pending !== null && valid()) {
                        const term = current.pending; current.pending = null;
                        try {
                            const result = await current.catalogue.page({ EnableFavoriteSorting: true }, term, 0, 8);
                            if (!valid() || term !== current.query) continue;
                            body.replaceChildren();
                            const list = document.createElement('div'); list.className = 'finChannelResults';
                            for (const item of result.Items) {
                                const link = document.createElement('a'); link.className = 'finChannelResult';
                                link.href = '#/details?id=' + encodeURIComponent(item.Id) + '&serverId=' + encodeURIComponent(api.serverId());
                                link.addEventListener('click', event => {
                                    if (!valid() || term !== current.query || section.inert || !link.isConnected) event.preventDefault();
                                });
                                if (item.ImageTags.Primary && api.getScaledImageUrl) {
                                    const image = document.createElement('img'); image.alt = ''; image.loading = 'lazy';
                                    image.src = api.getScaledImageUrl(item.Id, { type:'Primary', maxWidth:120, tag:item.ImageTags.Primary }); link.appendChild(image);
                                }
                                const text = document.createElement('span'), name = document.createElement('strong'), detail = document.createElement('span');
                                name.textContent = item.Name; detail.textContent = (item.ChannelNumber ? 'Channel '+item.ChannelNumber+' · ' : '') + 'Channel details';
                                text.append(name, detail); link.appendChild(text); list.appendChild(link);
                            }
                            const note = document.createElement('p'); note.textContent = result.TotalRecordCount ?
                                `${result.Items.length} of ${result.TotalRecordCount} matching channels` : 'No channel names or numbers match.';
                            const guide = document.createElement('a'); guide.className = 'finChannelGuide'; guide.textContent = 'Search in guide';
                            guide.href = '#/livetv?tab=1&channelSearch='+encodeURIComponent(term);
                            guide.addEventListener('click', event => {
                                if (!valid() || term !== current.query || section.inert || !guide.isConnected) event.preventDefault();
                            });
                            body.append(list, note, guide); section.inert = false; section.removeAttribute('aria-busy'); section.dataset.complete = 'true';
                        } catch (_) {
                            if (valid() && term === current.query) {
                                message('Channel search unavailable. Change the search to retry.'); section.inert = false; section.removeAttribute('aria-busy');
                            }
                        }
                    }
                } catch (_) { if (valid()) message('Channel search unavailable.'); }
                finally { current.running = false; }
            }
            current.update = value => {
                const term = String(value || '').trim();
                if (term === current.query) return;
                current.query = term; current.pending = null; clearTimeout(current.timer);
                section.inert = true; delete section.dataset.complete; section.setAttribute('aria-busy','true'); message('Searching channel names…');
                if (term.length > 100) {
                    message('Channel search supports up to 100 characters.'); section.inert = false; section.removeAttribute('aria-busy'); return;
                }
                current.timer = setTimeout(() => { current.pending = term; run(); }, 300);
            };
            current.update(query);
        }
        return { mount, dispose, update({ query }) {
                if (state && !state.section.isConnected && state.root.isConnected)
                    state.root.insertBefore(state.section, state.root.querySelector(':scope > .threepic-fin-search'));
                state?.update(query);
            },
            checkIdentity(api, userId) { if (state && (state.api !== api || state.userId !== userId)) dispose(); },
            suspend() {
                if (!state) return;
                clearTimeout(state.timer); state.query = ''; state.pending = null;
                state.body.replaceChildren(); state.section.inert = true; state.section.hidden = true; delete state.section.dataset.complete;
            }
        };
    }
    return { fold, createCatalogue, createSection };
})();
