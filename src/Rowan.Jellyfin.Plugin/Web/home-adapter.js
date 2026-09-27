/* Optional compatibility gate. Production distribution manifest remains unset. */
(function (global) {
    'use strict';
    // Only insert a manifest after testing the *distributed* Home chunk and final DOM.
    // This is a drift check, NOT attestation of the script bytes already executed.
    const VERIFIED_HOME_DISTRIBUTION = null;
    function homeRoute(route) {
        return /^#\/?home\/?(?:\?tab=0)?$/i.test(route || '');
    }
    function createAdapter({ bundleHash, getState, loadHost }) {
        let host = null, identity = null, generation = 0, disposed = false;
        async function refresh() {
            if (disposed) return;
            const state = getState();
            const allowed = state.compatibilityVerified === true && state.activePane === true &&
                bundleHash && state.bundleHash === bundleHash && homeRoute(state.route) &&
                state.userId && state.pane && state.favorites && state.pane !== state.favorites &&
                state.apiClient?.getCurrentUserId?.() === state.userId &&
                state.apiClient?.getUrl && state.apiClient?.getJSON;
            const next = allowed ? { userId: state.userId, pane: state.pane, apiClient: state.apiClient } : null;
            if (next && identity && Object.keys(next).every(key => next[key] === identity[key])) return;
            ++generation;
            host?.dispose(); host = null; identity = null;
            if (!next) return;
            identity = next;
            const ticket = generation;
            try {
                const loaded = await loadHost(state.apiClient);
                if (ticket !== generation || disposed) { loaded.dispose(); return; }
                // A route or identity can change while the host script is loading, before
                // the observer gets a chance to schedule a refresh.
                const now = getState();
                if (!now.compatibilityVerified || !now.activePane || !homeRoute(now.route) ||
                    now.userId !== next.userId || now.pane !== next.pane ||
                    now.apiClient !== next.apiClient || now.apiClient?.getCurrentUserId?.() !== next.userId) {
                    loaded.dispose(); identity = null; return;
                }
                host = loaded;
                const mounted = await loaded.mount({ pane: state.pane, favorites: state.favorites,
                    apiClient: state.apiClient, fingerprint: bundleHash, userId: state.userId,
                    enabled: true, heroEnabled: true });
                if (!mounted || ticket !== generation || disposed) {
                    loaded.dispose(); if (host === loaded) { host = null; identity = null; }
                }
            } catch (_) { if (ticket === generation) { host?.dispose(); host = null; identity = null; } }
        }
        function invalidate() { ++generation; host?.dispose(); host = null; identity = null; }
        function dispose() { disposed = true; invalidate(); }
        return { refresh, invalidate, dispose };
    }
    function resolveHomeNodes(page) {
        const children = page ? [...page.children] : [];
        const homes = children.filter(node => node.id === 'homeTab' && node.getAttribute('data-index') === '0');
        const favorites = children.filter(node => node.id === 'favoritesTab' && node.getAttribute('data-index') === '1');
        if (homes.length !== 1 || favorites.length !== 1 ||
            children.filter(node => node.id === 'homeTab' || node.id === 'favoritesTab').length !== 2 ||
            !homes[0].querySelector(':scope > .sections')) return { pane: null, favorites: null };
        return { pane: homes[0], favorites: favorites[0] };
    }
    if (typeof module !== 'undefined' && module.exports) module.exports = { createAdapter, resolveHomeNodes };
    if (!global.document || !VERIFIED_HOME_DISTRIBUTION) return;
    global.__threePicFinHomeAdapter?.dispose();
    const document = global.document;
    let bundleHash = null, verification = null, rejected = false, scheduled = false, stopped = false;
    let scriptPromise = null, watchTimer = null, watchedUser = null, watchedApi = null, logoutHook = null;
    let logoutPending = false;

    const loadHost = api => {
        if (!scriptPromise) scriptPromise = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = api.getUrl('3picFin/Web/home-tab-host.js');
            script.onload = () => { script.remove(); resolve(global.ThreePicFinHomeHost.createHost); };
            script.onerror = () => { script.remove(); reject(Error('Home host unavailable')); };
            document.head.appendChild(script);
        }).catch(error => { scriptPromise = null; throw error; });
        // Never share a mutable host across overlapping identity generations.
        return scriptPromise.then(createHost => createHost({ document }));
    };
    const adapter = createAdapter({ bundleHash: VERIFIED_HOME_DISTRIBUTION.sha256,
        getState: () => {
            const api = global.ApiClient;
            const page = document.querySelector('#indexPage');
            const { pane, favorites } = resolveHomeNodes(page);
            return { route: global.location.hash, bundleHash, compatibilityVerified: !stopped && !rejected && !!bundleHash,
                apiClient: api, activePane: !!pane && pane.getClientRects().length > 0 &&
                    global.getComputedStyle(pane).display !== 'none' &&
                    global.getComputedStyle(pane).visibility !== 'hidden',
                userId: api?.getCurrentUserId?.() || null, pane, favorites };
        }, loadHost });
    async function verify() {
        if (verification || stopped || rejected || bundleHash) return verification;
        verification = (async () => {
            try {
                // Check *all* Home script entries, not just entries matching the pin.
                const loaded = global.performance.getEntriesByType('resource').filter(entry =>
                    entry.initiatorType === 'script' && /\/hometab\.[a-f0-9]+\.chunk\.js(?:[?#]|$)/i.test(entry.name));
                if (!loaded.length) return; // Home has not loaded yet.
                const expected = new URL(VERIFIED_HOME_DISTRIBUTION.chunk, document.baseURI).href;
                if (loaded.length !== 1 || loaded[0].name !== expected) { rejected = true; return; }
                const response = await global.fetch(expected, { credentials: 'same-origin', cache: 'no-store' });
                if (!response.ok || response.url !== expected) { rejected = true; return; }
                const hash = Array.from(new Uint8Array(await global.crypto.subtle.digest('SHA-256', await response.arrayBuffer())))
                    .map(byte => byte.toString(16).padStart(2, '0')).join('');
                if (hash !== VERIFIED_HOME_DISTRIBUTION.sha256) { rejected = true; return; }
                if (!stopped) bundleHash = hash;
            } catch (_) { rejected = true; /* Unknown distribution: leave native UI intact. */ }
        })().finally(() => { verification = null; if (!stopped) { if (bundleHash) schedule(); else adapter.refresh(); } });
        return verification;
    }
    function unhook() {
        if (logoutHook) {
            logoutHook.active = false;
            if (watchedApi?.logout === logoutHook) watchedApi.logout = logoutHook.original;
        }
        logoutHook = null;
    }
    function unwatch() {
        if (watchTimer !== null) { global.clearInterval(watchTimer); watchTimer = null; }
        unhook(); watchedApi = null; watchedUser = null;
    }
    function watchIdentity() {
        if (logoutPending) return;
        const api = global.ApiClient;
        const id = api?.getCurrentUserId?.() || null;
        if (!bundleHash || stopped || !homeRoute(global.location.hash)) { unwatch(); return; }
        if (watchedApi !== api || watchedUser !== id) {
            unhook(); watchedApi = api; watchedUser = id;
        }
        if (id && !logoutHook && typeof api.logout === 'function') {
            const original = api.logout;
            logoutHook = function homeLogoutHook(...args) {
                if (!homeLogoutHook.active) return original.apply(this, args);
                // Dispose before Jellyfin's logout work, even if it keeps Home mounted.
                logoutPending = true;
                adapter.invalidate(); unhook();
                let result;
                try { result = original.apply(this, args); }
                catch (error) { logoutPending = false; schedule(); throw error; }
                if (result?.then) {
                    result.then(() => { logoutPending = false; schedule(); },
                        () => { logoutPending = false; schedule(); });
                } else { logoutPending = false; schedule(); }
                return result;
            };
            logoutHook.original = original;
            logoutHook.active = true;
            api.logout = logoutHook;
        }
        if (watchTimer !== null) return;
        watchTimer = global.setInterval(() => {
            if (logoutPending) return;
            const next = global.ApiClient?.getCurrentUserId?.() || null;
            if (global.ApiClient !== watchedApi || next !== watchedUser) {
                adapter.invalidate(); unhook(); watchedApi = global.ApiClient; watchedUser = next;
                schedule();
            }
        }, 500);
    }
    function schedule() {
        if (scheduled || stopped) return;
        scheduled = true;
        global.queueMicrotask(() => { scheduled = false; if (!stopped && !logoutPending) {
            watchIdentity(); adapter.refresh(); if (!verification && !bundleHash) verify();
        } });
    }
    const observer = new MutationObserver(schedule);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    global.addEventListener('hashchange', schedule);
    global.addEventListener('popstate', schedule);
    global.__threePicFinHomeAdapter = { dispose() {
        stopped = true; observer.disconnect(); global.removeEventListener('hashchange', schedule);
        global.removeEventListener('popstate', schedule); unwatch(); adapter.dispose();
    } };
    verify();
})(typeof globalThis !== 'undefined' ? globalThis : this);
