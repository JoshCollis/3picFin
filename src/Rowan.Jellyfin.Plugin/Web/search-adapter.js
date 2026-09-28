/* Portable Jellyfin 12.1 Search sibling; never edits native results. */
(function (global) {
    'use strict';
    if (!global.document) return;
    global.__threePicFinSearchAdapter?.dispose();
    const document = global.document;
    const addon = global.ThreePicFinSearchAddon?.createSearchAddon();
    if (!addon) return;
    let disposed = false, generation = 0, key = null, flow = null, loadingFlow = null;
    let hookedApi = null, logoutWrapper = null, originalLogout = null, logoutPending = false;
    let scheduled = false, mutationTimer = null, version = null, versionPromise = null, css = null;
    const owned = [];
    function ensureStyle(api) {
        if (css || typeof api?.getUrl !== 'function') return;
        css = document.createElement('link'); css.rel = 'stylesheet';
        css.href = api.getUrl('3picFin/Web/global-search-addon.css');
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
    function page() {
        const pages = document.querySelectorAll('#searchPage');
        return pages.length === 1 && pages[0].getClientRects().length &&
            global.getComputedStyle(pages[0]).display !== 'none' ? pages[0] : null;
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
        if (disposed || ticket !== generation || api.getCurrentUserId?.() !== id || page() !== key?.root || route() !== key?.query) return;
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
                        api.getCurrentUserId?.() === id && route() === key?.query && page() === key?.root });
                flow = { root, cleanup };
            })().finally(() => { loadingFlow = null; });
        }
        try { await loadingFlow; } catch (_) { return; }
        if (disposed || ticket !== generation || api.getCurrentUserId?.() !== id || page() !== key?.root || route() !== key?.query) return;
        flow?.cleanup[kind === 'details' ? 'openDetails' : 'openRequest'](item, button);
    }
    async function refresh() {
        if (disposed || logoutPending) return;
        const api = global.ApiClient, id = api?.getCurrentUserId?.(), query = route(), root = page();
        // A separate Seerr section does not infer TMDb identities from native cards.
        // Scope/query/user transitions clear detached actions before any async work.
        const next = api && id && query && root && api.getUrl && api.getJSON ? { api, id, query, root } : null;
        if (!next) { if (key) teardown(); return; }
        if (key && Object.keys(next).every(k => next[k] === key[k]) &&
            root.querySelectorAll(':scope > .threepic-fin-search').length === 1) {
            addon.update(); // Native groups can be replaced without changing the query.
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
            addon.update({ query });
            return;
        }
        // React can replace Search's children without replacing #searchPage. In that
        // case dispose detached actions and mount a fresh user-bound section.
        teardown();
        const ticket = generation;
        if (version !== true) {
            if (!versionPromise) versionPromise = api.getJSON(api.getUrl('System/Info/Public'))
                .then(info => /^12\.1\.\d+(?:\.\d+)?$/.test(info?.Version || ''))
                .catch(() => false).finally(() => { versionPromise = null; });
            const supported = await versionPromise;
            if (disposed || ticket !== generation) return;
            if (!supported) return;
            version = true;
        }
        if (api !== global.ApiClient || api.getCurrentUserId?.() !== id || route() !== query || page() !== root) return;
        key = next;
        ensureStyle(api);
        hookLogout(api);
        addon.mount({ root, apiClient: api, userId: id, sessionUserId: () => api.getCurrentUserId?.(),
            query, parentId: null, collectionType: null, enabled: true,
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
    function scheduleMutation() {
        if (mutationTimer !== null || disposed) return;
        mutationTimer = global.setTimeout(() => { mutationTimer = null; schedule(); }, 80);
    }
    const observer = new MutationObserver(scheduleMutation);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    global.addEventListener('popstate', schedule);
    global.addEventListener('hashchange', schedule);
    document.addEventListener('input', schedule, true);
    const interval = global.setInterval(schedule, 400);
    global.__threePicFinSearchAdapter = { dispose() {
        disposed = true; observer.disconnect(); global.clearInterval(interval);
        if (mutationTimer !== null) global.clearTimeout(mutationTimer);
        global.removeEventListener('popstate', schedule); global.removeEventListener('hashchange', schedule);
        document.removeEventListener('input', schedule, true); teardown(); owned.forEach(node => node.remove());
    } };
    schedule();
})(typeof globalThis !== 'undefined' ? globalThis : this);
