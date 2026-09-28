/* Persistent navigation belongs to the app shell, never to the Home host. */
(function (global) {
    'use strict';
    if (!global.document) return;
    global.__threePicFinGlobalNav?.dispose();
    const document = global.document;
    let identity = null, ticket = 0, stopped = false, scheduled = false, allowed = false;
    let links = [], timer;
    const finRoute = '#/home?fin=1';
    function remove() {
        for (const link of links) link.remove();
        links = [];
    }
    function action(drawer) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'threepic-fin-global-nav';
        button.setAttribute('aria-label', '3pic Fin');
        button.setAttribute('aria-current', global.location.hash === finRoute ? 'page' : 'false');
        // Own simple vector geometry; no distribution icon asset or font dependency.
        button.innerHTML = '<svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v10m-3.5-3.5L12 13l3.5-3.5M5 17v2h14v-2"/><path d="m19 4 .4 1.6L21 6l-1.6.4L19 8l-.4-1.6L17 6l1.6-.4z"/></svg><span>3pic Fin</span>';
        button.addEventListener('click', () => {
            if (stopped || !allowed || identity?.api !== global.ApiClient ||
                identity?.user !== global.ApiClient?.getCurrentUserId?.()) return;
            global.location.hash = finRoute;
            if (drawer) document.querySelector('.MuiToolbar-root button[aria-label="Open Menu"]')?.click();
        });
        return button;
    }
    function sync() {
        if (stopped) return;
        const api = global.ApiClient, user = api?.getCurrentUserId?.() || null;
        if (!identity || api !== identity.api || user !== identity.user) {
            ++ticket; identity = user && api?.getUrl && api?.getJSON ? { api, user } : null;
            allowed = false; remove();
            if (identity) {
                const current = identity, currentTicket = ticket;
                (async () => {
                    try {
                        const info = await api.getJSON(api.getUrl('System/Info/Public'));
                        if (!/^12\.1\.\d+(?:\.\d+)?$/.test(info?.Version || '')) return;
                        const mode = await api.getJSON(api.getUrl('Rowan/Home/Mode'));
                        if (currentTicket !== ticket || stopped || current !== identity ||
                            api !== global.ApiClient || api.getCurrentUserId?.() !== user) return;
                        allowed = mode?.DiscoveryEnabled === true || mode?.discoveryEnabled === true;
                        schedule();
                    } catch (_) { /* No navigation on unavailable/unsupported server. */ }
                })();
            }
        }
        if (!allowed) { remove(); return; }
        const toolbar = [...document.querySelectorAll('.MuiToolbar-root > .MuiStack-root')].filter(stack =>
            stack.getBoundingClientRect().width > 0 &&
            stack.querySelectorAll(':scope > a[href="#/"]').length === 1 &&
            stack.querySelectorAll(':scope > a[href="#/home?tab=1"]').length === 1);
        const favorites = [...document.querySelectorAll('.MuiListItem-root > a[href="#/home?tab=1"]')];
        const drawer = favorites.length === 1 && favorites[0].parentElement?.parentElement?.querySelectorAll(':scope > .MuiListItem-root > a[href="#/"]').length === 1
            ? favorites[0].parentElement : null;
        // Refuse ambiguous shell anchors; Home's native slider is not ours to modify.
        if (toolbar.length > 1 || favorites.length > 1) { remove(); return; }
        const targets = [];
        if (toolbar.length === 1) targets.push({ anchor: toolbar[0].querySelector(':scope > a[href="#/home?tab=1"]'), drawer: false });
        if (drawer) targets.push({ anchor: drawer, drawer: true });
        if (!targets.length) { remove(); return; }
        for (const old of links) if (!old.isConnected || !targets.some(t => old.previousElementSibling === t.anchor)) old.remove();
        links = targets.map(({ anchor, drawer: isDrawer }) => {
            let node = anchor.nextElementSibling;
            if (!links.includes(node) || !node?.classList.contains(isDrawer ? 'threepic-fin-global-nav-item' : 'threepic-fin-global-nav')) {
                // A framework clone can retain the class but lose its event listener.
                if (node?.classList.contains(isDrawer ? 'threepic-fin-global-nav-item' : 'threepic-fin-global-nav')) node.remove();
                const button = action(isDrawer);
                node = isDrawer ? document.createElement('li') : button;
                if (isDrawer) { node.className = 'threepic-fin-global-nav-item MuiListItem-root'; node.appendChild(button); }
                anchor.insertAdjacentElement('afterend', node);
            }
            (isDrawer ? node.querySelector('button') : node).setAttribute('aria-current', global.location.hash === finRoute ? 'page' : 'false');
            return node;
        });
    }
    function schedule() {
        if (scheduled || stopped) return;
        scheduled = true;
        global.queueMicrotask(() => { scheduled = false; sync(); });
    }
    const observer = new MutationObserver(schedule);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    global.addEventListener('hashchange', schedule);
    global.addEventListener('popstate', schedule);
    timer = global.setInterval(schedule, 500);
    global.__threePicFinGlobalNav = { dispose() {
        stopped = true; ++ticket; observer.disconnect(); global.clearInterval(timer);
        global.removeEventListener('hashchange', schedule); global.removeEventListener('popstate', schedule);
        remove(); identity = null; allowed = false;
    } };
    schedule();
})(typeof globalThis !== 'undefined' ? globalThis : this);
