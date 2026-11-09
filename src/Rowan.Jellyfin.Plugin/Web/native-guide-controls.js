/* Embedded only inside a structurally verified native Guide module. No global hooks or renderer. */
function finCreateGuideController(options) {
    'use strict';
    const root = options.element, limit = options.limit;
    let revision = 0, paused = true, identity = null, active = null, pending = null;
    let total = 0, start = 0, returned = 0, heldFocus = null;
    let search = '', searchTimer = null, busy = false, catalogue = null, catalogueApi = null, catalogueUser = null;
    let category = '', categories = null, categoryApi = null, categoryUser = null, categoryExpires = 0;
    let rowHeight = 112, windowMove = false, movingLeft = 0, movingFocus = null, gridDate = null;
    let frame = null, clock = null, resize = null, mounted = false;
    const responses = new Map();
    const find = selector => root.querySelector(selector);
    function controls() {
        const clear = find('.finGuideClear');
        if (clear) clear.disabled = !search;
    }
    function lock(value) {
        busy = value;
        root.setAttribute('aria-busy', String(value));
        const state = find('.finGuideState');
        if (state) {
            state.textContent = value ? 'Loading guide…' : !total ? 'No channels match. Clear the search or change the category or guide filters.' : '';
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
    function categoryFeedback(message, loading = false) {
        const label = find('.finGuideCategoryState'); if (label) label.textContent = message;
        const retry = find('.finGuideCategoryRetry');
        if (retry) { retry.hidden = !message || loading; retry.disabled = loading; }
        const select = find('.finGuideCategory');
        if (select) { select.setAttribute('aria-busy', String(loading)); select.setAttribute('aria-description', message || 'Assigned channel groups'); if (loading) select.disabled = true; }
    }
    function clearCategoryOptions() {
        const select = find('.finGuideCategory');
        if (select) {
            select.replaceChildren(); const all = document.createElement('option');
            all.value = ''; all.textContent = 'All categories'; select.appendChild(all); select.disabled = true;
        }
    }
    async function channelPage(entry) {
        const { api, ticket } = entry;
        if (categoryApi !== api || categoryUser !== ticket.user) {
            categories = null; categoryExpires = 0; categoryApi = api; categoryUser = ticket.user;
        }
        if (!categories || Date.now() >= categoryExpires) {
            categoryFeedback('Loading categories…', true);
            let data;
            try { data = await api.getJSON(api.getUrl('3picFin/Guide/Categories')); }
            catch (_) { data = null; }
            if (!valid(ticket)) return null;
            const groups = new Map();
            let members = 0; const seen = new Set();
            try {
                if (data?.Available !== true || !['jellyfin-m3u-group-title-cache', 'jellyfin-assigned-channel-groups'].includes(data.Source) || !Array.isArray(data.Items) || data.Items.length > 256) throw Error();
                for (const group of data.Items) {
                    if (!/^group-[a-f0-9]{64}$/.test(group.Id) || groups.has(group.Id) || typeof group.Name !== 'string' ||
                        !group.Name.trim() || group.Name.length > 200 || /[\x00-\x1f\x7f]/.test(group.Name) || group.Name.includes('://') ||
                        !Array.isArray(group.ChannelIds) || (members += group.ChannelIds.length) > 10000 ||
                        group.ChannelIds.some(id => { if (!/^[a-f0-9]{32}$/.test(id) || seen.has(id)) return true; seen.add(id); return false; })) throw Error();
                    groups.set(group.Id, { name: group.Name, ids: new Set(group.ChannelIds) });
                }
                categories = groups;
            } catch (_) { categories = new Map(); }
            categoryExpires = Date.now() + (categories.size ? 30000 : 5000);
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
            categoryFeedback(ticket.category && !categories.has(ticket.category) ? 'Selected category unavailable. Choose All or retry categories.' :
                categories.size ? '' : data?.Available === true && Array.isArray(data.Items) && !data.Items.length ?
                    'No assigned categories for these channels. All channels remain searchable.' :
                    'Categories unavailable. All channels remain searchable. Retry or ask an administrator to check the assigned-group source.');
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
            else result = entry.api[entry.method](entry.method === 'getLiveTvPrograms' ?
                { ...entry.query, Fields: [entry.query.Fields, 'Overview'].filter(Boolean).join(',') } : entry.query);
        }
        catch (error) { result = Promise.reject(error); }
        Promise.resolve(result).then(data => {
            if (valid(entry.ticket)) {
                responses.set(entry.key, data);
                while (responses.size > 8) responses.delete(responses.keys().next().value);
            }
            active = null; entry.resolve(data); pump();
        }, error => {
            active = null; entry.reject(error); pump();
        });
    }
    function read(api, method, query, ticket) {
        const key = method + ':' + JSON.stringify(query) + ':' + ticket.search + ':' + ticket.category;
        if (windowMove && responses.has(key)) return { then: callback => Promise.resolve().then(() => { if (valid(ticket)) return callback(responses.get(key)); }) };
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
                if (label) { label.textContent = 'Guide unavailable. Retry, or change the search or category.'; label.classList.add('finGuideNotice'); }
                const retry = find('.finGuideRetry'); if (retry) retry.hidden = false;
                options.onError();
            });
        } };
    }
    function begin() {
        const focused = typeof document === 'undefined' ? null : document.activeElement;
        if (focused && root.contains(focused) && focused.closest('.programGrid, .channelsContainer')) heldFocus = focused;
        else if (focused && focused !== document.body) heldFocus = null;
        ++revision; clearPending(); paused = false;
        if (!windowMove) responses.clear();
        if (mounted && clock === null) clock = setInterval(updateNow, 60000);
        resize?.observe(find('.guideVerticalScroller'));
        const api = options.getApi();
        if (identity && (identity.api !== api || identity.user !== api.getCurrentUserId())) {
            category = ''; categories = null; responses.clear(); clearCategoryOptions(); categoryFeedback('Loading categories…', true);
        }
        identity = { api, user: api.getCurrentUserId(), revision, search, category };
        lock(true);
        return identity;
    }
    function pause() {
        ++revision; paused = true; heldFocus = null; clearPending();
        if (searchTimer !== null) clearTimeout(searchTimer);
        searchTimer = null; catalogue?.dispose(); catalogue = null; categories = null; lock(true);
        clearCategoryOptions(); categoryFeedback('', true);
        responses.clear(); resize?.disconnect();
        if (clock !== null) clearInterval(clock); clock = null;
        if (frame !== null) cancelAnimationFrame(frame); frame = null;
    }
    function update(count, offset, countReturned) {
        total = Number.isSafeInteger(count) && count >= 0 ? count : 0;
        start = offset; returned = countReturned;
        if (offset > 0 && offset >= total && !returned) {
            options.jump(Math.max(0, total - windowSize()));
            return;
        }
        const label = find('.finGuideRange');
        if (label) label.textContent = total.toLocaleString() + (search || category ? ' matching channels' : ' channels');
        root.setAttribute('data-fin-guide-start', String(start)); root.setAttribute('data-fin-guide-total', String(total));
        controls();
    }
    function mount() {
        mounted = true; root.classList.add('finGuideContinuous');
        const scroller = find('.guideVerticalScroller');
        scroller.addEventListener('scroll', schedule, { passive: true });
        scroller.addEventListener('scrollanimate', schedule, { passive: true });
        root.addEventListener('focusin', schedule);
        root.addEventListener('command', event => boundary(event, event.detail?.command), true);
        root.addEventListener('keydown', event => boundary(event, ({ ArrowDown: 'down', ArrowUp: 'up', Home: 'home', End: 'end', PageDown: 'pagedown', PageUp: 'pageup' })[event.key]), true);
        if (typeof ResizeObserver !== 'undefined') resize = new ResizeObserver(schedule);
        find('.finGuideRetry').addEventListener('click', () => options.jump(start));
        find('.finGuideCategoryRetry')?.addEventListener('click', () => {
            categories = null; categoryExpires = 0; windowMove = false; responses.clear(); categoryFeedback('Loading categories…', true); begin(); options.jump(0);
        });
        syncGutter();
        const field = find('.finGuideSearchInput'), searchForm = find('.finGuideSearch');
        field.placeholder = 'Search channels';
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
        windowMove = false; movingFocus = null; resetPosition();
        search = String(value || '').trim().slice(0,100);
        if (searchTimer !== null) clearTimeout(searchTimer);
        begin();
        const run = () => { searchTimer = null; options.jump(0); };
        if (immediate) run(); else searchTimer = setTimeout(run, 300);
    }
    function setCategory(value) {
        windowMove = false; movingFocus = null; resetPosition();
        category = String(value || '');
        if (searchTimer !== null) clearTimeout(searchTimer);
        searchTimer = null; begin(); options.jump(0);
    }
    function resetPosition() {
        for (const selector of ['.programGrid', '.channelsContainer']) {
            const node = find(selector); if (node?.style) { node.style.paddingTop = '0px'; node.style.paddingBottom = '0px'; }
        }
        find('.guideVerticalScroller')?.scrollToPosition?.(0, true);
    }
    function windowSize() {
        const height = find('.guideVerticalScroller')?.clientHeight;
        return height ? Math.min(limit, Math.max(28, Math.ceil(height / rowHeight) + 12)) : limit;
    }
    function position() { const el = find('.guideVerticalScroller'); return el?.getScrollPosition?.() ?? el?.scrollTop ?? 0; }
    function scrollPosition(value) {
        const el = find('.guideVerticalScroller');
        if (el?.getScrollEventName?.() === 'scrollanimate') el.scrollToPosition(value, true);
        else if (el) el.scrollTop = value;
    }
    function ensureIndex(index) {
        if (!Number.isSafeInteger(index) || index < 0 || index >= total) return false;
        const size = windowSize(), offset = Math.max(0, Math.min(index - 6, total - size));
        if (offset === start) return false;
        windowMove = true; movingLeft = find('.programGrid')?.scrollLeft || 0;
        options.jump(offset); return true;
    }
    function schedule() {
        if (paused || frame !== null || typeof requestAnimationFrame === 'undefined') return;
        frame = requestAnimationFrame(() => {
            frame = null;
            syncGutter();
            if (paused || busy || !returned) return;
            const first = Math.floor(position() / rowHeight), visible = Math.ceil((find('.guideVerticalScroller')?.clientHeight || rowHeight) / rowHeight);
            if (first < start + 3 && start > 0 || first + visible > start + returned - 3 && start + returned < total) ensureIndex(first);
        });
    }
    function boundary(event, command) {
        if (!command || paused || busy || event.target.closest('input,select,textarea,.finGuideToolbar,.guideHeaderDateSelection')) return;
        const row = event.target.closest('.channelPrograms,.guide-channelHeaderCell');
        if (!row) return;
        const rows = [...root.querySelectorAll(row.matches('.channelPrograms') ? '.channelPrograms' : '.guide-channelHeaderCell')];
        const local = rows.indexOf(row), index = start + local;
        let next;
        if (command === 'home') next = 0;
        else if (command === 'end') next = total - 1;
        else if (command === 'pagedown') next = Math.min(total - 1, index + 10);
        else if (command === 'pageup') next = Math.max(0, index - 10);
        else if (command === 'down' && local === rows.length - 1) next = index + 1;
        else if (command === 'up' && local === 0) next = index - 1;
        else return;
        if (next < 0 || next >= total) return;
        event.preventDefault(); event.stopPropagation();
        movingFocus = { index: next, channel: row.matches('.guide-channelHeaderCell'), left: parseFloat(event.target.style.left) || 0 };
        if (!ensureIndex(next)) focusLogical();
    }
    function focusLogical() {
        if (!movingFocus) return;
        const local = movingFocus.index - start;
        const row = root.querySelectorAll(movingFocus.channel ? '.guide-channelHeaderCell' : '.channelPrograms')[local];
        const target = movingFocus.channel ? row : [...(row?.querySelectorAll('.programCell') || [])].find(cell => parseFloat(cell.style.left) + parseFloat(cell.style.width) > movingFocus.left);
        movingFocus = null;
        if (target) { target.focus({ preventScroll: true }); find('.guideVerticalScroller')?.toCenter?.(target, true); }
    }
    function syncGutter() {
        // Measure the native header, not theme-specific generated class names.
        // Only controls/metadata consume these values; the Guide grid is unchanged.
        if (typeof document === 'undefined' || !root.getBoundingClientRect) return;
        const bounds = find('.finGuideToolbar').getBoundingClientRect();
        const anchors = [...document.querySelectorAll('header [aria-controls="library-view-menu"]'), ...document.querySelectorAll('header a[href="#/"] img'), ...document.querySelectorAll('.skinHeader .headerTop .pageTitle')];
        for (const anchor of anchors) {
            const rect = anchor.getBoundingClientRect(), start = rect.left - bounds.left;
            if (!rect.width || start < 0 || start > bounds.width / 4) continue;
            const bar = anchor.closest('.padded-right, .MuiToolbar-root');
            const end = bar ? parseFloat(getComputedStyle(bar).paddingRight) + Math.max(0, bounds.right - bar.getBoundingClientRect().right) : start;
            root.style.setProperty('--fin-guide-gutter-start', start + 'px');
            root.style.setProperty('--fin-guide-gutter-end', Math.max(0, Math.min(bounds.width / 4, end)) + 'px');
            return;
        }
        const bar = [...document.querySelectorAll('header .MuiToolbar-root')].find(node => node.getBoundingClientRect().width);
        if (bar) {
            const rect = bar.getBoundingClientRect(), style = getComputedStyle(bar);
            root.style.setProperty('--fin-guide-gutter-start', Math.max(0, rect.left + parseFloat(style.paddingLeft) - bounds.left) + 'px');
            root.style.setProperty('--fin-guide-gutter-end', Math.max(0, bounds.right - rect.right + parseFloat(style.paddingRight)) + 'px');
        }
    }
    function updateNow() {
        if (!gridDate || paused) return;
        const pct = (Date.now() - gridDate) / 86400000 * 100;
        root.classList.toggle('finGuideHasNow', pct >= 0 && pct < 100);
        root.style.setProperty('--fin-guide-now', pct + '%');
        find('.finGuideNowMarker')?.setAttribute('aria-label', 'Current time ' + new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }));
    }
    function rendered(ticket, channels, programs, date) {
        if (!valid(ticket)) return;
        gridDate = date.getTime();
        const ruler = find('.timeslotHeadersInner');
        if (ruler && !ruler.querySelector('.finGuideNowMarker')) {
            const marker = document.createElement('span'); marker.className = 'finGuideNowMarker'; marker.textContent = 'Now'; ruler.appendChild(marker);
        }
        for (const header of root.querySelectorAll('.guide-channelHeaderCell')) {
            if (!header.querySelector('.guideChannelImage,.finGuideChannelFallback')) {
                const fallback = document.createElement('span'); fallback.className = 'finGuideChannelFallback';
                fallback.textContent = 'TV'; fallback.setAttribute('aria-hidden', 'true'); header.prepend(fallback);
            }
            const identity = document.createElement('span'); identity.className = 'finGuideChannelIdentity';
            const name = header.querySelector('.guideChannelName'), number = header.querySelector('.guideChannelNumber');
            if (name) identity.appendChild(name);
            if (number) identity.appendChild(number);
            header.appendChild(identity);
        }
        for (const date of root.querySelectorAll('.guide-date-tab-button'))
            date.setAttribute('aria-pressed', String(date.classList.contains('emby-tab-button-active')));
        syncGutter();
        const rows = [...root.querySelectorAll('.channelPrograms')];
        if (rows[0]) rowHeight = rows[0].getBoundingClientRect().height;
        const before = start * rowHeight, after = Math.max(0, total - start - channels.length) * rowHeight;
        for (const selector of ['.programGrid', '.channelsContainer']) {
            const node = find(selector); node.style.paddingTop = before + 'px'; node.style.paddingBottom = after + 'px';
        }
        const map = new Map(programs.map(program => [program.Id, program]));
        rows.forEach((row, index) => { row.setAttribute('role', 'group'); row.setAttribute('aria-label', channels[index].Name + ', channel ' + (start + index + 1) + ' of ' + total); });
        for (const cell of root.querySelectorAll('.programCell')) {
            const program = map.get(cell.getAttribute('data-id')); if (!program) continue;
            const time = value => new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
            const range = time(program.StartDate) + ' – ' + time(program.EndDate), current = cell.classList.contains('programCell-active');
            cell.setAttribute('aria-label', program.Name + ', ' + range + (current ? ', on now' : ''));
            if (current) cell.setAttribute('aria-current', 'true');
            const name = cell.querySelector('.guideProgramNameText');
            if (name) {
                if (program.Overview) { const detail = document.createElement('div'); detail.className = 'finGuideSynopsis'; detail.textContent = program.Overview; name.appendChild(detail); }
                const times = document.createElement('div'); times.className = 'finGuideProgramTime'; times.textContent = (current ? 'On now · ' : '') + range; name.appendChild(times);
            }
        }
        const top = position();
        find('.guideVerticalScroller')?.scroller?.reload();
        scrollPosition(top);
        const returnId = heldFocus?.getAttribute('data-id');
        const returnChannel = !!heldFocus?.closest('.guide-channelHeaderCell');
        finish(ticket);
        if (windowMove) {
            find('.programGrid').scrollLeft = movingLeft; find('.timeslotHeaders').scrollLeft = movingLeft;
            if (!movingFocus && returnId) {
                let replacement = [...root.querySelectorAll('[data-id]')].find(node => node.getAttribute('data-id') === returnId);
                if (!replacement && channels.length) {
                    const local = Math.max(0, Math.min(channels.length - 1, Math.floor(top / rowHeight) - start));
                    const pct = movingLeft / Math.max(1, find('.programGrid').scrollWidth) * 100;
                    replacement = returnChannel ? root.querySelectorAll('.guide-channelHeaderCell')[local] :
                        [...rows[local].querySelectorAll('.programCell')].find(cell => parseFloat(cell.style.left) + parseFloat(cell.style.width) > pct);
                }
                replacement?.focus({ preventScroll: true });
                scrollPosition(top);
                find('.programGrid').scrollLeft = movingLeft; find('.timeslotHeaders').scrollLeft = movingLeft;
            }
        }
        windowMove = false; updateNow();
        const retry = find('.finGuideRetry'); if (retry) retry.hidden = true;
        focusLogical(); schedule();
    }
    function finish(ticket) {
        if (valid(ticket)) {
            lock(false);
            if (heldFocus && heldFocus.isConnected) heldFocus.focus({preventScroll:true});
            heldFocus = null;
        }
    }
    return { begin, read, valid, update, windowSize, ensureIndex, rendered, mount, pause, setSearch, setCategory,
        loading(show) { if (!windowMove) show(); },
        reset() { windowMove = false; movingFocus = null; resetPosition(); },
        focusProgramOnRender(nativeFocus) { return nativeFocus && !windowMove && (typeof document === 'undefined' || !document.activeElement?.closest('.finGuideSearch, .finGuideCategoryLabel, .finGuideCategoryRetry')); },
        finish,
        needsRefresh() { return paused || !identity || identity.api !== options.getApi() || identity.user !== identity.api.getCurrentUserId(); }
    };
}
