/* Embedded only inside a structurally verified native Guide module. No global hooks or renderer. */
function finCreateGuideController(options) {
    'use strict';
    const root = options.element, limit = options.limit;
    let revision = 0, paused = true, identity = null, active = null, pending = null;
    let total = 0, start = 0, pages = 1, heldFocus = null;
    let search = '', searchTimer = null, busy = false, catalogue = null, catalogueApi = null, catalogueUser = null;
    let category = '', categories = null, categoryApi = null, categoryUser = null, categoryExpires = 0;
    const find = selector => root.querySelector(selector);
    function controls() {
        for (const selector of ['.finGuideFirst', '.btnPreviousPage', '.finGuideLast', '.btnNextPage']) {
            const button = find(selector);
            if (button) button.disabled = busy || (selector === '.finGuideFirst' || selector === '.btnPreviousPage' ? start === 0 : start + limit >= total);
        }
        const clear = find('.finGuideClear');
        if (clear) clear.disabled = !search;
    }
    function lock(value) {
        busy = value;
        root.setAttribute('aria-busy', String(value));
        const state = find('.finGuideState');
        if (state) {
            state.textContent = value ? 'Loading guide…' : (search || category) && !total ? 'No channels match. Clear the search or change the category or guide filters.' : '';
            state.classList.toggle('finGuideNotice', !value && !!state.textContent);
        }
        for (const selector of ['.programGrid', '.channelsContainer']) {
            const node = find(selector);
            if (node) node.inert = value;
        }
        controls();
    }
    function valid(ticket) {
        return !!ticket && !paused && ticket.revision === revision && root.isConnected !== false &&
            ticket.api === options.getApi() && !!ticket.user && ticket.api.getCurrentUserId() === ticket.user;
    }
    function clearPending() { if (pending) { pending.resolve(null); pending = null; } }
    async function channelPage(entry) {
        const { api, ticket } = entry;
        if (categoryApi !== api || categoryUser !== ticket.user) {
            categories = null; categoryExpires = 0; categoryApi = api; categoryUser = ticket.user;
        }
        if (!categories || Date.now() >= categoryExpires) {
            let data;
            try { data = await api.getJSON(api.getUrl('3picFin/Guide/Categories')); }
            catch (_) { data = null; }
            if (!valid(ticket)) return null;
            const groups = new Map();
            let members = 0;
            try {
                if (data?.Available !== true || data.Source !== 'jellyfin-m3u-group-title-cache' || !Array.isArray(data.Items) || data.Items.length > 256) throw Error();
                for (const group of data.Items) {
                    if (!/^group-[a-f0-9]{64}$/.test(group.Id) || groups.has(group.Id) || typeof group.Name !== 'string' ||
                        !group.Name.trim() || group.Name.length > 200 || !Array.isArray(group.ChannelIds) || (members += group.ChannelIds.length) > 10000 ||
                        group.ChannelIds.some(id => !/^[a-f0-9]{32}$/.test(id))) throw Error();
                    groups.set(group.Id, { name: group.Name, ids: new Set(group.ChannelIds) });
                }
                categories = groups;
            } catch (_) { categories = new Map(); }
            categoryExpires = Date.now() + 60000;
            const select = find('.finGuideCategory');
            if (select) {
                select.replaceChildren();
                const all = document.createElement('option'); all.value = ''; all.textContent = 'All categories'; select.appendChild(all);
                for (const [id, group] of categories) {
                    const option = document.createElement('option'); option.value = id; option.textContent = group.name; select.appendChild(option);
                }
                if (ticket.category && !categories.has(ticket.category)) {
                    const missing = document.createElement('option'); missing.value = ticket.category; missing.textContent = 'Category unavailable'; select.appendChild(missing);
                }
                select.value = ticket.category; select.disabled = !categories.size && !ticket.category;
                select.title = categories.size ? 'Assigned channel groups' : 'Assigned categories unavailable; all visible channels remain searchable';
            }
        }
        if (!valid(ticket)) return null;
        // Missing/stale/ambiguous selected groups must never widen silently to All.
        const allowed = ticket.category ? categories.get(ticket.category)?.ids : null;
        if (ticket.category && !allowed) throw Error('Assigned category unavailable');
        if (!ticket.search && !ticket.category) return api.getLiveTvChannels(entry.query);
        if (!catalogue || catalogueApi !== api || catalogueUser !== ticket.user) {
            catalogue?.dispose(); catalogueApi = api; catalogueUser = ticket.user;
            catalogue = ThreePicFinChannelSearch.createCatalogue({ api, userId: ticket.user,
                isCurrent: () => !paused && root.isConnected !== false && options.getApi() === catalogueApi });
        }
        return catalogue.page(entry.query, ticket.search, entry.query.StartIndex, entry.query.Limit, allowed);
    }
    function pump() {
        if (active || !pending) return;
        const entry = pending; pending = null;
        if (!valid(entry.ticket)) { entry.resolve(null); return; }
        active = entry;
        let result;
        try {
            if (entry.method === 'getLiveTvChannels') result = channelPage(entry);
            else result = entry.api[entry.method](entry.query);
        }
        catch (error) { result = Promise.reject(error); }
        Promise.resolve(result).then(data => {
            active = null; entry.resolve(data); pump();
        }, error => {
            active = null; entry.reject(error); pump();
        });
    }
    function read(api, method, query, ticket) {
        const key = method + ':' + JSON.stringify(query) + ':' + ticket.search + ':' + ticket.category;
        let entry = [active, pending].find(item => item && item.key === key && item.ticket === ticket);
        if (!entry) {
            let resolve, reject;
            const promise = new Promise((a, b) => { resolve = a; reject = b; });
            entry = { api, method, query, ticket, key, promise, resolve, reject };
            clearPending(); pending = entry; pump();
        }
        // Native has one callback per read. Discard stale responses and consume
        // rejections here; no unresolved promise/transport is described as aborted.
        return { then(callback) {
            return entry.promise.then(data => { if (valid(ticket)) return callback(data); }).catch(() => {
                if (!valid(ticket)) return;
                const label = find('.finGuideState');
                if (label) { label.textContent = 'Guide unavailable. Press Find, select All categories or change the page to retry.'; label.classList.add('finGuideNotice'); }
                options.onError();
            });
        } };
    }
    function begin() {
        const focused = typeof document === 'undefined' ? null : document.activeElement;
        if (focused && root.contains(focused) && focused.closest('.programGrid, .channelsContainer')) heldFocus = focused;
        else if (focused && focused !== document.body) heldFocus = null;
        ++revision; clearPending(); paused = false;
        const api = options.getApi();
        if (identity && (identity.api !== api || identity.user !== api.getCurrentUserId())) category = '';
        identity = { api, user: api.getCurrentUserId(), revision, search, category };
        lock(true);
        return identity;
    }
    function pause() {
        ++revision; paused = true; heldFocus = null; clearPending();
        if (searchTimer !== null) clearTimeout(searchTimer);
        searchTimer = null; catalogue?.dispose(); catalogue = null; categories = null; lock(true);
    }
    function update(count, offset, returned) {
        total = Number.isSafeInteger(count) && count >= 0 ? count : 0;
        start = offset; pages = Math.max(1, Math.ceil(total / limit));
        if (offset > 0 && offset >= total && returned === 0) {
            options.jump(Math.max(0, (pages - 1) * limit));
            return;
        }
        const input = find('.finGuidePage'), label = find('.finGuideRange');
        if (input) { input.max = String(pages); input.value = String(Math.floor(start / limit) + 1); }
        if (label) label.textContent = (total && returned ? start + 1 : 0) + '–' + (returned ? Math.min(start + returned, total) : 0) + ' of ' + total + (search || category ? ' matches' : ' channels');
        const pageCount = find('.finGuidePages');
        if (pageCount) pageCount.textContent = 'of ' + pages;
        controls();
    }
    function go(value) {
        if (!/^[0-9]+$/.test(String(value))) return false;
        const page = Number(value), offset = (page - 1) * limit;
        if (!Number.isSafeInteger(page) || page < 1 || page > pages || offset === start) return false;
        options.jump(offset);
        return true;
    }
    function mount() {
        const form = find('.finGuideJump'), input = find('.finGuidePage');
        form.addEventListener('submit', event => {
            event.preventDefault(); event.stopPropagation();
            if (!go(input.value)) input.value = String(Math.floor(start / limit) + 1);
        });
        find('.finGuideFirst').addEventListener('click', () => go('1'));
        find('.finGuideLast').addEventListener('click', () => go(String(pages)));
        const field = find('.finGuideSearchInput'), searchForm = find('.finGuideSearch');
        find('.finGuideCategory').addEventListener('change', event => setCategory(event.target.value));
        field.addEventListener('input', () => setSearch(field.value));
        searchForm.addEventListener('submit', event => { event.preventDefault(); setSearch(field.value, true); });
        find('.finGuideClear').addEventListener('click', () => { field.value = ''; field.focus(); setSearch('', true); });
        // Native route parameter, no shared/global selected-channel state.
        try {
            const route = new URL(location.hash.slice(1), location.origin);
            search = (route.searchParams.get('channelSearch') || '').trim().slice(0,100); field.value = search;
        } catch (_) { /* A host without hash routing starts with the full guide. */ }
    }
    function setSearch(value, immediate = false) {
        search = String(value || '').trim().slice(0,100);
        if (searchTimer !== null) clearTimeout(searchTimer);
        begin();
        const run = () => { searchTimer = null; options.jump(0); };
        if (immediate) run(); else searchTimer = setTimeout(run, 300);
    }
    function setCategory(value) {
        category = String(value || '');
        if (searchTimer !== null) clearTimeout(searchTimer);
        searchTimer = null; begin(); options.jump(0);
    }
    return { begin, read, valid, update, go, mount, pause, setSearch, setCategory,
        focusProgramOnRender(nativeFocus) { return nativeFocus && (typeof document === 'undefined' || !document.activeElement?.closest('.finGuideSearch, .finGuideCategoryLabel')); },
        finish(ticket) {
            if (valid(ticket)) {
                lock(false);
                if (heldFocus && heldFocus.isConnected) heldFocus.focus({preventScroll:true});
                heldFocus = null;
            }
        },
        needsRefresh() { return paused || !identity || identity.api !== options.getApi() || identity.user !== identity.api.getCurrentUserId(); }
    };
}
