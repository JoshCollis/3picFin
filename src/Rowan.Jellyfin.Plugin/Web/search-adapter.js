/* Jellyfin 12.1/12.3 Search integration. */
(function (global) {
    'use strict';
    const NATIVE_SEARCH_LIMIT = 50;
    const SUPPORTED_VERSION = /^12\.(?:1|3)\.\d+(?:\.\d+)?$/;
    function cachedSearchCompatibility(api) {
        try {
            // The connection handshake sets this before native Search starts.
            const version = api?.serverVersion?.();
            return typeof version === 'string' && version ? SUPPORTED_VERSION.test(version) : null;
        } catch (_) { return null; }
    }
    function boundedSearchUrl(world, input, method, api) {
        try {
            if (!api?.getUrl || !api.getCurrentUserId?.() || String(method).toUpperCase() !== 'GET') return null;
            const location = new URL(world.location.href);
            const route = location.hash.startsWith('#/') ? new URL(location.hash.slice(1), location.origin) : location;
            if (!/\/search\/?$/i.test(route.pathname) || route.searchParams.has('parentId') ||
                route.searchParams.has('collectionType') || route.searchParams.getAll('query').length !== 1) return null;
            const query = route.searchParams.get('query').trim();
            if (!query || query.length > 200) return null;
            const url = new URL(input, location);
            if (url.origin !== location.origin || url.username || url.password) return null;
            const endpoints = ['Items', 'Persons', 'Studios', 'Artists', 'Artists/AlbumArtists'];
            if (!endpoints.some(path => {
                const expected = new URL(api.getUrl(path), location);
                return url.origin === expected.origin && url.pathname === expected.pathname;
            })) return null;
            const values = new Map();
            const controls = new Set(['searchterm', 'userid', 'limit', 'parentid', 'collectiontype', 'startindex']);
            for (const [name, value] of url.searchParams) {
                const lower = name.toLowerCase();
                // Array parameters may repeat; policy controls must be unambiguous.
                if (controls.has(lower) && values.has(lower)) return null;
                values.set(lower, { name, value });
            }
            const user = api.getCurrentUserId();
            if (values.get('searchterm')?.value.trim() !== query ||
                values.get('userid')?.value.toLowerCase() !== user.toLowerCase() ||
                values.get('parentid')?.value || values.has('collectiontype') ||
                values.has('startindex') && values.get('startindex').value !== '0') return null;
            const limit = values.get('limit');
            // Leave smaller limits and existing paged requests alone.
            if (!limit || !['100', '800'].includes(limit.value)) return null;
            const original = url.href;
            url.searchParams.set(limit.name, String(NATIVE_SEARCH_LIMIT));
            return { url: url.href, original, api, user, query };
        } catch (_) { return null; }
    }
    function installSearchBudget(world, supported, onApplied) {
        let stopped = false, applied = null;
        const originalFetch = world.fetch, prototype = world.XMLHttpRequest?.prototype, originalOpen = prototype?.open;
        function bound(input, method) {
            if (stopped || !supported()) return null;
            const next = boundedSearchUrl(world, input, method, world.ApiClient);
            if (next) { applied = next; onApplied(); }
            return next;
        }
        function fetch(input, init) {
            const request = typeof world.Request === 'function' && input instanceof world.Request;
            const next = bound(request ? input.url : input, init?.method || (request ? input.method : 'GET'));
            // Preserve Request options and cancellation when replacing its URL.
            return originalFetch.call(this, next ? request ? new world.Request(next.url, input) : next.url : input, init);
        }
        function open(method, url, ...args) {
            const next = bound(url, method);
            return originalOpen.call(this, method, next?.url || url, ...args);
        }
        if (typeof originalFetch === 'function') world.fetch = fetch;
        if (typeof originalOpen === 'function') prototype.open = open;
        return {
            limit() {
                if (!applied || stopped || !supported()) return undefined;
                const current = boundedSearchUrl(world, applied.original, 'GET', world.ApiClient);
                return current && current.api === applied.api && current.user === applied.user && current.query === applied.query
                    ? NATIVE_SEARCH_LIMIT : undefined;
            },
            dispose() {
                stopped = true; applied = null;
                if (world.fetch === fetch) world.fetch = originalFetch;
                if (prototype?.open === open) prototype.open = originalOpen;
            }
        };
    }
    if (typeof module !== 'undefined' && module.exports) module.exports = { boundedSearchUrl, installSearchBudget, cachedSearchCompatibility };
    if (!global.document) return;
    global.__threePicFinSearchAdapter?.dispose();
    const document = global.document;
    const addon = global.ThreePicFinSearchAddon?.createSearchAddon();
    if (!addon) return;
    let disposed = false, generation = 0, key = null, flow = null, loadingFlow = null;
    let hookedApi = null, logoutWrapper = null, originalLogout = null, logoutPending = false;
    let scheduled = false, mutationTimer = null, version = null, versionPromise = null, css = null;
    let verifiedApi = null, verifyingApi = null;
    let layoutDirty = true, measureDirty = false;
    function compatible(api) {
        // A known unsupported connection must not inherit an older HTTP result.
        const cached = cachedSearchCompatibility(api);
        return cached !== null ? cached : version === true && verifiedApi === api;
    }
    const budget = installSearchBudget(global, () => !disposed && !logoutPending &&
        compatible(global.ApiClient), schedule);
    const owned = [];
    function ensureStyle(api) {
        if (css || typeof api?.getUrl !== 'function') return;
        css = document.createElement('link'); css.rel = 'stylesheet';
        css.href = api.getUrl('3picFin/Web/global-search-addon.css');
        css.addEventListener('load', resized);
        document.head.appendChild(css); owned.push(css);
    }
    function route() {
        const url = new URL(global.location.href);
        const path = url.hash.startsWith('#/') ? new URL(url.hash.slice(1), url.origin) : url;
        if (!/\/search\/?$/i.test(path.pathname) || path.searchParams.has('parentId') ||
            path.searchParams.has('collectionType')) return null;
        const query = path.searchParams.get('query');
        const inputs = document.querySelectorAll('#searchPage #searchTextInput');
        if (inputs.length !== 1 || inputs[0].value.trim() !== query?.trim()) return null;
        return query && query.trim().length <= 200 ? query.trim() : null;
    }
    function page(checkVisibility = false) {
        const pages = document.querySelectorAll('#searchPage');
        if (pages.length !== 1 || pages[0].closest('.hide, [hidden], [aria-hidden="true"]') ||
            pages[0].style.display === 'none' || pages[0].style.visibility === 'hidden') return null;
        // An already validated active route/root needs no repeated style flush.
        if (!checkVisibility && pages[0] === key?.root) return pages[0];
        return pages[0].getClientRects().length && global.getComputedStyle(pages[0]).display !== 'none' ? pages[0] : null;
    }
    function teardown() {
        ++generation; key = null; addon.dispose();
        if (hookedApi?.logout === logoutWrapper) hookedApi.logout = originalLogout;
        hookedApi = logoutWrapper = originalLogout = null;
        try { flow?.cleanup(); } catch (_) { /* A fragment cleanup error cannot retain user content. */ }
        finally { flow?.root.remove(); flow = null; loadingFlow = null; }
    }
    function hookLogout(api) {
        if (typeof api.logout !== 'function') return;
        hookedApi = api; originalLogout = api.logout;
        logoutWrapper = function (...args) {
            logoutPending = true; teardown();
            let result;
            try { result = original.apply(this, args); }
            catch (error) { logoutPending = false; schedule(); throw error; }
            Promise.resolve(result).then(() => { logoutPending = false; schedule(); },
                () => { logoutPending = false; schedule(); });
            return result;
        };
        const original = originalLogout;
        api.logout = logoutWrapper;
    }
    function script(src) {
        return new Promise((resolve, reject) => {
            const node = document.createElement('script'); node.src = src;
            node.onload = () => { node.remove(); resolve(); };
            node.onerror = () => { node.remove(); reject(Error('Asset unavailable')); };
            document.head.appendChild(node);
        });
    }
    async function openFlow(kind, item, button, api, id, ticket) {
        if (disposed || ticket !== generation || api.getCurrentUserId?.() !== id || page(true) !== key?.root || route() !== key?.query) return;
        if (!flow && !loadingFlow) {
            loadingFlow = (async () => {
                if (!global.ThreePicFinDiscovery) await script(api.getUrl('3picFin/Web/discovery.js'));
                const response = await global.fetch(api.getUrl('3picFin/Web/discovery.html'));
                if (!response.ok) throw Error('Dialog unavailable');
                const html = await response.text();
                if (disposed || ticket !== generation || api.getCurrentUserId?.() !== id) return;
                const root = document.createElement('div'); root.className = 'threepic-fin-search__flow';
                const style = document.createElement('link'); style.rel = 'stylesheet';
                style.href = api.getUrl('3picFin/Web/discovery.css'); document.head.appendChild(style); owned.push(style);
                root.innerHTML = html;
                document.body.appendChild(root);
                const cleanup = global.ThreePicFinDiscovery.mount(root, api, { deferInitialLoad: true, userId: id,
                    isCurrent: () => !disposed && ticket === generation && global.ApiClient === api &&
                        api.getCurrentUserId?.() === id && route() === key?.query && page(true) === key?.root });
                flow = { root, cleanup };
            })().finally(() => { loadingFlow = null; });
        }
        try { await loadingFlow; } catch (_) { return; }
        if (disposed || ticket !== generation || api.getCurrentUserId?.() !== id || page(true) !== key?.root || route() !== key?.query) return;
        flow?.cleanup[kind === 'details' ? 'openDetails' : 'openRequest'](item, button);
    }
    async function refresh() {
        if (disposed || logoutPending) return;
        const api = global.ApiClient, id = api?.getCurrentUserId?.(), query = route(), root = query ? page() : null;
        // A separate Seerr section does not infer TMDb identities from native cards.
        // Scope/query/user transitions clear detached actions before any async work.
        const next = api && id && query && root && api.getUrl && api.getJSON ? { api, id, query, root } : null;
        if (!next) { if (key) teardown(); return; }
        if (key && Object.keys(next).every(k => next[k] === key[k]) &&
            root.querySelectorAll(':scope > .threepic-fin-search').length === 1) {
            const updateLayout = layoutDirty && mutationTimer === null;
            addon.update({ layout: updateLayout, measure: updateLayout && measureDirty, nativeResultLimit: budget.limit() });
            if (updateLayout) layoutDirty = measureDirty = false;
            return;
        }
        if (key && key.api === api && key.id === id && key.root === root &&
            root.querySelectorAll(':scope > .threepic-fin-search').length === 1) {
            // Keep the addon alive across typing so its bounded timer can coalesce
            // queries; invalidate dialogs and detached actions synchronously.
            ++generation;
            try { flow?.cleanup(); } catch (_) { /* still remove the dialog */ }
            finally { flow?.root.remove(); flow = null; loadingFlow = null; }
            key = next;
            addon.update({ query, layout: false, nativeResultLimit: budget.limit() });
            return;
        }
        // React can replace Search's children without replacing #searchPage. In that
        // case dispose detached actions and mount a fresh user-bound section.
        teardown();
        const ticket = generation;
        if (!compatible(api)) {
            if (!versionPromise || verifyingApi !== api) {
                verifyingApi = api;
                const pending = api.getJSON(api.getUrl('System/Info/Public'))
                    .then(info => SUPPORTED_VERSION.test(info?.Version || ''))
                    .catch(() => false).finally(() => { if (versionPromise === pending) versionPromise = null; });
                versionPromise = pending;
            }
            const supported = await versionPromise;
            if (disposed || ticket !== generation) return;
            if (!supported) return;
            version = true;
            verifiedApi = api;
        }
        if (api !== global.ApiClient || api.getCurrentUserId?.() !== id || route() !== query || page() !== root) return;
        key = next;
        layoutDirty = measureDirty = false;
        ensureStyle(api);
        hookLogout(api);
        addon.mount({ root, apiClient: api, userId: id, sessionUserId: () => api.getCurrentUserId?.(),
            query, parentId: null, collectionType: null, enabled: true, nativeResultLimit: budget.limit(),
            requestAction: (item, button) => openFlow('request', item, button, api, id, generation),
            detailsAction: (item, button) => openFlow('details', item, button, api, id, generation) });
    }
    function schedule() {
        if (scheduled || disposed) return;
        scheduled = true; global.queueMicrotask(() => { scheduled = false; refresh(); });
    }
    // React re-renders, router navigation, user changes and logout need independent
    // observation. Polling is a fallback for pushState and ApiClient identity changes.
    // Native Search updates many result nodes in separate tasks. Limit expensive
    // route/layout scans to one per burst; input and navigation still invalidate
    // detached actions on the next microtask.
    function scheduleMutation(records) {
        if (disposed) return;
        if (records && key && !records.some(record => {
            const target = record.target.nodeType === 1 ? record.target : record.target.parentElement;
            if (!target || target.closest('.threepic-fin-search, .threepic-fin-search__flow, .card')) return false;
            return target === key.root || key.root.contains(target) && !!target.closest('.searchResults') ||
                [...record.removedNodes].some(node => node === key.root || node.contains?.(key.root));
        })) return;
        layoutDirty = true;
        if (mutationTimer !== null) global.clearTimeout(mutationTimer);
        mutationTimer = global.setTimeout(() => { mutationTimer = null; schedule(); }, 120);
    }
    const observer = new MutationObserver(scheduleMutation);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    global.addEventListener('popstate', schedule);
    global.addEventListener('hashchange', schedule);
    document.addEventListener('input', schedule, true);
    function resized() { measureDirty = true; scheduleMutation(); }
    global.addEventListener('resize', resized);
    const interval = global.setInterval(schedule, 400);
    global.__threePicFinSearchAdapter = { dispose() {
        disposed = true; observer.disconnect(); global.clearInterval(interval);
        budget.dispose();
        if (mutationTimer !== null) global.clearTimeout(mutationTimer);
        global.removeEventListener('popstate', schedule); global.removeEventListener('hashchange', schedule);
        global.removeEventListener('resize', resized);
        document.removeEventListener('input', schedule, true); teardown(); owned.forEach(node => node.remove());
    } };
    schedule();
})(typeof globalThis !== 'undefined' ? globalThis : this);
