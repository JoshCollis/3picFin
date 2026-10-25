/* Persistent navigation belongs to the app shell, never to the Home host. */
(function (global) {
    'use strict';
    if (!global.document) return;
    global.__threePicFinGlobalNav?.dispose();
    const document = global.document;
    let identity = null, ticket = 0, stopped = false, scheduled = false, allowed = false;
    let links = [], timer, pending = null, shellDirty = true, shells = [];
    const hiddenFavorites = new Set();
    const finRoute = '#/home?fin=1';
    function remove() {
        for (const link of links) link.remove();
        // A framework remount may clone our markup without its listeners, before
        // shell rediscovery runs. Identity teardown must remove those orphans too.
        for (const node of document.querySelectorAll('.threepic-fin-global-nav, .threepic-fin-global-nav-item')) node.remove();
        links = [];
        for (const node of document.querySelectorAll('.threepic-fin-hidden-favorite')) node.classList.remove('threepic-fin-hidden-favorite');
        hiddenFavorites.clear();
    }
    function action(favorite, drawer, legacy) {
        // Adopt the sibling's native component shell, but not React's owned descendants
        // or handlers. This lets the active theme supply geometry, hover and focus.
        const button = legacy === 'tabs' || legacy === 'header' ? document.createElement('a') : favorite.cloneNode(false);
        if (legacy === 'header') {
            button.className = favorite.className;
            button.classList.remove('headerHomeButton', 'barsMenuButton', 'hide');
            button.classList.add('threepic-fin-global-nav--header');
        }
        button.classList.add('threepic-fin-global-nav');
        button.classList.remove('threepic-fin-hidden-favorite');
        if (legacy === 'tabs') button.classList.add('emby-button', 'button-link', 'threepic-fin-global-nav--tabs');
        button.removeAttribute('data-index');
        button.removeAttribute('id');
        button.removeAttribute('aria-hidden');
        button.tabIndex = 0;
        button.href = finRoute;
        button.setAttribute('aria-label', '3pic Fin');
        const icon = favorite.querySelector(drawer ? '.MuiListItemIcon-root' : '.MuiButton-startIcon')?.cloneNode(false)
            || document.createElement('span');
        icon.classList.add(drawer ? 'MuiListItemIcon-root' : 'MuiButton-startIcon');
        // Own simple vector geometry; no distribution icon asset or font dependency.
        icon.innerHTML = '<svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v10m-3.5-3.5L12 13l3.5-3.5M5 17v2h14v-2"/><path d="m19 4 .4 1.6L21 6l-1.6.4L19 8l-.4-1.6L17 6l1.6-.4z"/></svg>';
        const label = favorite.querySelector(drawer ? '.MuiListItemText-root' : '.MuiTypography-root')?.cloneNode(false)
            || document.createElement('span');
        label.textContent = '3pic Fin';
        button.replaceChildren(icon, label);
        if (legacy) {
            label.className = legacy === 'drawer' ? 'navMenuOptionText' : '';
            button.replaceChildren(label);
        }
        button.addEventListener('click', event => {
            event.preventDefault();
            if (stopped || !allowed || identity?.api !== global.ApiClient ||
                identity?.user !== global.ApiClient?.getCurrentUserId?.()) return;
            global.location.hash = finRoute;
            if (drawer) document.querySelector('.MuiToolbar-root button[aria-label="Open Menu"]')?.click();
            if (legacy === 'drawer') document.querySelector('.skinHeader .mainDrawerButton')?.click();
        });
        return button;
    }
    function select(button, drawer) {
        const selected = global.location.hash === finRoute;
        const value = selected ? 'page' : 'false';
        if (button.getAttribute('aria-current') !== value) button.setAttribute('aria-current', value);
        if (drawer) button.classList.toggle('Mui-selected', selected);
        else {
            button.classList.toggle('MuiButton-textPrimary', selected);
            button.classList.toggle('MuiButton-textInherit', !selected);
        }
    }
    function sync() {
        if (stopped) return;
        const api = global.ApiClient, user = api?.getCurrentUserId?.() || null;
        if (!user) {
            if (identity) { ++ticket; identity = null; allowed = false; remove(); }
            return;
        }
        if (!identity || api !== identity.api || user !== identity.user) {
            ++ticket; identity = user && api?.getUrl && api?.getJSON ? { api, user } : null;
            allowed = false; remove();
            if (identity) {
                const current = identity, currentTicket = ticket;
                (async () => {
                    try {
                        const info = await api.getJSON(api.getUrl('System/Info/Public'));
                        if (!/^12\.(?:1|3)\.\d+(?:\.\d+)?$/.test(info?.Version || '')) return;
                        const mode = await api.getJSON(api.getUrl('Rowan/Home/Mode'));
                        if (currentTicket !== ticket || stopped || current !== identity ||
                            api !== global.ApiClient || api.getCurrentUserId?.() !== user) return;
                        allowed = mode?.DiscoveryEnabled === true || mode?.discoveryEnabled === true;
                        shellDirty = true;
                        schedule();
                    } catch (_) { /* No navigation on unavailable/unsupported server. */ }
                })();
            }
        }
        if (!allowed) { remove(); return; }
        // Only shell changes need fresh anchor measurements.
        if (!shellDirty && links.length && links.every(node => node.isConnected)) {
            for (const node of links) select(node.matches('.threepic-fin-global-nav-item')
                ? node.querySelector('.threepic-fin-global-nav') : node, node.matches('.threepic-fin-global-nav-item'));
            return;
        }
        shellDirty = false;
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
        if (drawer) targets.push({ anchor: drawer, drawer: true, favorite: favorites[0] });
        // TV is a legacy shell in 12.1/12.3, not a hidden MUI desktop toolbar.
        // Never insert a third managed tab: its controller owns the two indexes.
        const suppress = new Set(targets.map(target => target.favorite || target.anchor));
        const pages = [...document.querySelectorAll('#indexPage')].filter(page =>
            page.getClientRects().length > 0 && global.getComputedStyle(page).display !== 'none' &&
            global.getComputedStyle(page).visibility !== 'hidden');
        if (/^#\/home(?:\?(?:tab=[01]|fin=1))?$/.test(global.location.hash) && pages.length === 1 &&
            pages[0].querySelectorAll(':scope > #homeTab[data-index="0"] > .sections').length === 1 &&
            pages[0].querySelectorAll(':scope > #favoritesTab[data-index="1"]').length === 1) {
            const headers = [...document.querySelectorAll('.skinHeader .headerTabs')];
            if (headers.length === 1) {
                const header = headers[0], native = header.querySelector(':scope > [is="emby-tabs"]');
                const buttons = native?.querySelectorAll(':scope > .emby-tabs-slider > .emby-tab-button');
                if (buttons?.length === 2 && buttons[0].getAttribute('data-index') === '0' &&
                    buttons[1].getAttribute('data-index') === '1') {
                    suppress.add(buttons[1]);
                    if (header.getBoundingClientRect().width > 0 && global.getComputedStyle(header).visibility !== 'hidden')
                        targets.push({ anchor: native, favorite: buttons[0], legacy: 'tabs' });
                }
            }
        }
        const nativeHomes = [...document.querySelectorAll('.mainDrawer .mainDrawer-scrollContainer > a.navMenuOption[href="#/home"]')];
        if (nativeHomes.length === 1) targets.push({ anchor: nativeHomes[0], legacy: 'drawer' });
        // TV hides its drawer trigger outside Home and clears the Home tabs.
        // Use the native visible Home-button seam there, not an offscreen drawer.
        const headerHomes = [...document.querySelectorAll('.skinHeader > .headerTop > .headerLeft > .headerHomeButton')]
            .filter(node => node.getBoundingClientRect().width > 0 && global.getComputedStyle(node).visibility !== 'hidden');
        if (headerHomes.length === 1 && !targets.some(target => target.legacy === 'tabs'))
            targets.push({ anchor: headerHomes[0], legacy: 'header' });
        // Only validated navigation is hidden. Item favorite controls and routes survive.
        for (const node of hiddenFavorites) if (!suppress.has(node)) {
            node.classList.remove('threepic-fin-hidden-favorite'); hiddenFavorites.delete(node);
        }
        for (const node of suppress) if (!hiddenFavorites.has(node)) {
            node.classList.add('threepic-fin-hidden-favorite'); hiddenFavorites.add(node);
        }
        if (!targets.length) { remove(); return; }
        for (const old of links) if (!old.isConnected || !targets.some(t => old.previousElementSibling === t.anchor)) old.remove();
        links = targets.map(({ anchor, drawer: isDrawer, favorite, legacy }) => {
            let node = anchor.nextElementSibling;
            if (!links.includes(node) || !node?.classList.contains(isDrawer ? 'threepic-fin-global-nav-item' : 'threepic-fin-global-nav')) {
                // A framework clone can retain the class but lose its event listener.
                if (node?.classList.contains(isDrawer ? 'threepic-fin-global-nav-item' : 'threepic-fin-global-nav')) node.remove();
                const button = action(favorite || anchor, isDrawer, legacy);
                node = isDrawer ? anchor.cloneNode(false) : button;
                if (isDrawer) { node.classList.add('threepic-fin-global-nav-item'); node.appendChild(button); }
                anchor.insertAdjacentElement('afterend', node);
            }
            select(isDrawer ? node.querySelector('.threepic-fin-global-nav') : node, isDrawer);
            return node;
        });
    }
    function schedule() {
        if (scheduled || stopped) return;
        scheduled = true;
        // Yield to native rendering; never replenish the same microtask checkpoint.
        pending = global.setTimeout(() => { pending = null; scheduled = false; sync(); }, 80);
    }
    function owned(node) {
        const element = node.nodeType === 1 ? node : node.parentElement;
        return !!element?.closest('.threepic-fin-global-nav, .threepic-fin-global-nav-item');
    }
    const observer = new MutationObserver(records => {
        if (records.every(record => owned(record.target) || record.type === 'childList' &&
            [...record.addedNodes, ...record.removedNodes].every(owned))) return;
        shellDirty = true; schedule();
    });
    function poll() {
        if (stopped) return;
        // Rediscover replaced headers and portalled drawers without observing results.
        const next = [...document.querySelectorAll('.MuiToolbar-root, .MuiDrawer-root, .skinHeader, .mainDrawer')];
        if (next.length !== shells.length || next.some((node, i) => node !== shells[i])) {
            observer.disconnect(); shells = next; shellDirty = true;
            for (const node of shells) observer.observe(node, { childList: true, subtree: true,
                attributes: true, attributeFilter: ['href', 'class', 'style', 'hidden', 'aria-hidden'] });
        }
        const api = global.ApiClient, user = api?.getCurrentUserId?.() || null;
        if (shellDirty || (identity?.user || null) !== user || user && identity?.api !== api ||
            links.some(node => !node.isConnected)) schedule();
    }
    function resized() { shellDirty = true; schedule(); }
    global.addEventListener('hashchange', schedule);
    global.addEventListener('popstate', schedule);
    global.addEventListener('resize', resized);
    timer = global.setInterval(poll, 500);
    global.__threePicFinGlobalNav = { dispose() {
        stopped = true; ++ticket; observer.disconnect(); global.clearInterval(timer);
        if (pending !== null) global.clearTimeout(pending);
        global.removeEventListener('hashchange', schedule); global.removeEventListener('popstate', schedule);
        global.removeEventListener('resize', resized);
        shells = [];
        remove(); identity = null; allowed = false;
    } };
    poll();
})(typeof globalThis !== 'undefined' ? globalThis : this);
