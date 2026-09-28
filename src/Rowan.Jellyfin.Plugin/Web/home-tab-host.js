/* Home host; loaded by the version/structure-gated adapter, never mutates native tabs. */
(function (global) {
    'use strict';
    const SUPPORTED_WEB_CONTRACT = '12.1';
    const field = (item, name) => item?.[name] ?? item?.[name[0].toLowerCase() + name.slice(1)];
    const guid = id => typeof id === 'string' && /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i.test(id);
    const defaultFragment = async url => {
        const response = await fetch(url, { credentials: 'same-origin' });
        if (!response.ok) throw new Error('Discovery fragment unavailable');
        return response.text();
    };
    const defaultScript = (url, document) => new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = url;
        script.onload = () => { script.remove(); resolve(url.includes('static-hero.js') ? global.RowanStaticHero :
            url.includes('native-home-rows.js') ? global.RowanNativeHomeRows : global.ThreePicFinDiscovery); };
        script.onerror = () => { script.remove(); reject(new Error('Discovery script unavailable')); };
        document.head.appendChild(script);
    });
    function createHost({ document,
        loadFragment = defaultFragment, loadScript = url => defaultScript(url, document) } = {}) {
        let current = null, generation = 0;
        function clearFinUrl() {
            if (global.location?.hash !== '#/home?fin=1') return;
            try {
                if (!global.history?.replaceState) throw new Error('No replaceState');
                global.history.replaceState(global.history.state, '',
                    `${global.location.pathname}${global.location.search}#/home`);
            } catch (_) { global.location.hash = '#/home'; }
        }
        const validSlide = slide => slide && guid(field(slide, 'Id')) &&
            field(slide, 'ImageType') === 'Backdrop' && field(slide, 'ImageIndex') === 0 &&
            typeof field(slide, 'ImageTag') === 'string' && /^[0-9a-f]{1,64}$/i.test(field(slide, 'ImageTag'));
        function clearHero(state) {
            ++state.heroTicket;
            try { state.heroCleanup?.(); } catch (_) { /* Never block teardown. */ }
            state.heroCleanup = null;
            state.heroRoot?.remove(); state.heroRoot = null;
            state.heroStyles?.remove(); state.heroStyles = null;
        }
        function reserveHero(state) {
            if (!state.heroEnabled || state.heroRoot) return;
            const root = document.createElement('div');
            root.className = 'threepic-fin-host__hero';
            // Reserve before the asynchronous slide and script fetches. Inline geometry
            // applies even while the stylesheet is still loading.
            const height = global.matchMedia?.('(max-width: 600px)')?.matches ? '65dvh' : 'min(78dvh, 800px)';
            root.style.minHeight = height;
            root.style.height = height;
            const style = document.createElement('link');
            style.rel = 'stylesheet'; style.href = state.apiClient.getUrl('3picFin/Web/static-hero.css');
            state.pane.insertBefore(root, state.sections);
            document.head.appendChild(style);
            state.heroRoot = root; state.heroStyles = style;
        }
        function startHero(state) {
            if (!state.heroEnabled || state.heroStarted ||
                state.pane.getAttribute('data-threepic-fin-view') !== 'home') return;
            reserveHero(state);
            state.heroStarted = true;
            const ticket = ++state.heroTicket, identity = state.apiClient.getCurrentUserId?.();
            const token = state.apiClient.accessToken?.();
            const active = () => current === state && state.heroTicket === ticket &&
                state.pane.getAttribute('data-threepic-fin-view') === 'home' &&
                document.documentElement?.contains(state.pane) === true &&
                global.location?.hash?.split('?')[0] === '#/home' &&
                identity === state.userId && !!token && state.apiClient.accessToken?.() === token &&
                state.apiClient.getCurrentUserId?.() === identity;
            (async () => {
                try {
                    if (!active() || typeof state.apiClient.fetch !== 'function') { clearHero(state); return; }
                    const slides = await state.apiClient.getJSON(state.apiClient.getUrl('Rowan/Home/Hero'));
                    if (!active()) return;
                    if (!Array.isArray(slides)) { clearHero(state); return; }
                    const safe = slides.filter(validSlide).slice(0, 10);
                    if (!safe.length) { clearHero(state); return; }
                    const hero = await loadScript(state.apiClient.getUrl('3picFin/Web/static-hero.js'));
                    if (!active()) return;
                    if (typeof hero?.mount !== 'function') { clearHero(state); return; }
                    state.heroCleanup = hero.mount(state.heroRoot, state.apiClient, { slides: safe, openItem: async id => {
                        if (!active() || !guid(id) || !safe.some(slide =>
                            field(slide, 'Id').replaceAll('-', '').toLowerCase() === id.replaceAll('-', '').toLowerCase()) ||
                            typeof global.Emby?.Page?.showItem !== 'function') return false;
                        const route = global.location.hash, navigationTicket = generation;
                        let fresh;
                        try { fresh = await state.apiClient.getJSON(state.apiClient.getUrl(`Users/${identity}/Items/${id}`)); }
                        catch (_) { return false; }
                        const freshId = field(fresh, 'Id'), type = field(fresh, 'Type');
                        if (navigationTicket !== generation || !active() || global.location.hash !== route ||
                            !guid(freshId) || freshId.replaceAll('-', '').toLowerCase() !== id.replaceAll('-', '').toLowerCase() ||
                            !['Movie', 'Series'].includes(type)) return false;
                        const serverId = state.apiClient.serverId?.();
                        if (typeof serverId !== 'string' || !serverId) return false;
                        global.Emby.Page.showItem({ Id: freshId, Type: type, ServerId: serverId });
                        return true;
                    } });
                } catch (_) { if (current === state && state.heroTicket === ticket) clearHero(state); }
            })();
        }
        function clearRows(state) {
            ++state.rowsTicket;
            state.rowsLoading = false;
            try { state.rowsCleanup?.(); } catch (_) { /* Still remove owned DOM. */ }
            state.rowsCleanup = null;
            state.rowsRoot?.remove(); state.rowsRoot = null;
            state.rowsStyles?.remove(); state.rowsStyles = null;
        }
        function startRows(state) {
            if (!state.rowsEnabled || state.rowsRoot || state.rowsLoading ||
                state.pane.getAttribute('data-threepic-fin-view') !== 'home') return;
            const ticket = ++state.rowsTicket, token = state.apiClient.accessToken?.();
            state.rowsLoading = true;
            const active = () => current === state && state.rowsTicket === ticket &&
                state.pane.getAttribute('data-threepic-fin-view') === 'home' &&
                document.documentElement?.contains(state.pane) === true &&
                global.location?.hash?.split('?')[0] === '#/home' &&
                state.apiClient.getCurrentUserId?.() === state.userId && !!token &&
                state.apiClient.accessToken?.() === token;
            (async () => {
                try {
                    const renderer = await loadScript(state.apiClient.getUrl('3picFin/Web/native-home-rows.js'));
                    if (!active() || typeof renderer?.createRows !== 'function') return;
                    const rows = renderer.createRows({ document, IntersectionObserver: global.IntersectionObserver,
                        enabledRows: state.rowKinds, openItem: async item => {
                            const id = field(item, 'Id'), type = field(item, 'Type');
                            if (!active() || !guid(id) || !['Movie', 'Series', 'Episode', 'BoxSet'].includes(type) ||
                                typeof global.Emby?.Page?.showItem !== 'function') return false;
                            const route = global.location.hash, navigationTicket = generation;
                            let fresh;
                            try { fresh = await state.apiClient.getJSON(state.apiClient.getUrl(`Users/${state.userId}/Items/${id}`)); }
                            catch (_) { return false; }
                            const freshId = field(fresh, 'Id');
                            if (navigationTicket !== generation || !active() || route !== global.location.hash ||
                                !guid(freshId) || freshId.replaceAll('-', '').toLowerCase() !== id.replaceAll('-', '').toLowerCase() ||
                                field(fresh, 'Type') !== type) return false;
                            const serverId = state.apiClient.serverId?.();
                            if (typeof serverId !== 'string' || !serverId) return false;
                            global.Emby.Page.showItem({ Id: freshId, Type: type, ServerId: serverId }); return true;
                        } });
                    const root = document.createElement('div'); root.className = 'rowan-native-rows';
                    const style = document.createElement('link'); style.rel = 'stylesheet';
                    style.href = state.apiClient.getUrl('3picFin/Web/native-home-rows.css');
                    state.sections.insertAdjacentElement('afterend', root);
                    document.head.appendChild(style);
                    state.rowsRoot = root; state.rowsStyles = style;
                    if (!rows.mount(root, state.apiClient, state.userId)) { clearRows(state); return; }
                    state.rowsCleanup = () => rows.dispose();
                } catch (_) { if (current === state && state.rowsTicket === ticket) clearRows(state); }
                finally { if (state.rowsTicket === ticket) state.rowsLoading = false; }
            })();
        }
        function dispose() {
            ++generation;
            if (!current) return;
            const { pane, panel, stylesheet, hostStyles, cleanup } = current;
            clearHero(current);
            clearRows(current);
            current.removeNav?.();
            current = null;
            try { cleanup?.(); } catch (_) { /* Discovery cleanup must not prevent host teardown. */ }
            panel?.remove(); stylesheet?.remove(); hostStyles?.remove();
            pane.removeAttribute('data-threepic-fin-view');
        }
        async function mount({ pane, favorites, apiClient, fingerprint, userId, enabled = false, mode }) {
            // The adapter checks server version, script timing, route and native DOM.
            // Never accept arbitrary fingerprints from a caller.
            if (!enabled || fingerprint !== SUPPORTED_WEB_CONTRACT ||
                !pane || !favorites || pane === favorites || !userId ||
                !apiClient?.getUrl || !apiClient?.getJSON) { dispose(); return false; }
            const sections = Array.from(pane.children).find(child =>
                child.classList?.contains('sections') || child.className?.split(/\s+/).includes('sections'));
            if (!sections) { dispose(); return false; }
            if (current?.pane === pane && current?.userId === userId && current?.apiClient === apiClient) {
                current.syncNav?.(); return true;
            }
            dispose();
            const ticket = generation;
            const url = path => apiClient.getUrl(`3picFin/Web/${path}`);
            try {
                mode ??= await apiClient.getJSON(apiClient.getUrl('Rowan/Home/Mode'));
            } catch (_) { if (ticket === generation) clearFinUrl(); return false; }
            if (ticket !== generation || (apiClient.getCurrentUserId && apiClient.getCurrentUserId() !== userId) ||
                !mode || (mode.DiscoveryEnabled !== true && mode.discoveryEnabled !== true &&
                    mode.HeroEnabled !== true && mode.heroEnabled !== true &&
                    mode.RowsEnabled !== true && mode.rowsEnabled !== true)) {
                if (ticket === generation && (!apiClient.getCurrentUserId || apiClient.getCurrentUserId() === userId))
                    clearFinUrl();
                return false;
            }
            const discoveryEnabled = mode.DiscoveryEnabled === true || mode.discoveryEnabled === true;
            const heroEnabled = mode.HeroEnabled === true || mode.heroEnabled === true;
            const rowKinds = mode.Rows ?? mode.rows;
            const rowsEnabled = (mode.RowsEnabled === true || mode.rowsEnabled === true) &&
                Array.isArray(rowKinds) && rowKinds.length > 0 && rowKinds.length <= 9;
            if (!discoveryEnabled) {
                // Hero-only mode does not create inner tabs, panels, or Discovery requests.
                clearFinUrl();
                current = { pane, userId, apiClient, sections, heroEnabled, heroStarted: false, heroTicket: 0,
                    rowsEnabled, rowKinds, rowsTicket: 0 };
                pane.setAttribute('data-threepic-fin-view', 'home');
                startHero(current);
                startRows(current);
                return true;
            }
            let html, discovery;
            try {
                html = await loadFragment(url('discovery.html'));
                if (ticket !== generation) return false;
                discovery = await loadScript(url('discovery.js'));
                if (ticket !== generation) return false;
                if (typeof discovery?.mount !== 'function') { clearFinUrl(); return false; }
            } catch (_) { if (ticket === generation) clearFinUrl(); return false; }
            const stylesheet = document.createElement('link');
            stylesheet.rel = 'stylesheet'; stylesheet.href = url('discovery.css');
            const hostStyles = document.createElement('link');
            hostStyles.rel = 'stylesheet'; hostStyles.href = url('home-tab-host.css');
            const panel = document.createElement('div'); panel.className = 'threepic-fin-host__panel';
            panel.setAttribute('role', 'region'); panel.setAttribute('aria-label', '3pic Fin');
            panel.innerHTML = html; panel.hidden = true;
            let activated = false;
            function select(view) {
                if (view === 'discovery' && current?.panel === panel) {
                    clearHero(current);
                    clearRows(current);
                    current.heroStarted = false;
                }
                panel.hidden = view !== 'discovery';
                if (view === 'discovery' && !activated && current?.panel === panel) {
                    activated = true;
                    current.cleanup?.activate?.();
                }
                pane.setAttribute('data-threepic-fin-view', view);
                if (view === 'home' && current?.panel === panel) { startHero(current); startRows(current); }
                current?.nav?.setAttribute('aria-pressed', String(view === 'discovery'));
            }
            // Keep native tabs/indexing untouched; place our control in the visible
            // 12.1 toolbar (or its mobile drawer), never inside the tabs slider.
            function syncNav() {
                if (current?.panel !== panel) return;
                const route = global.location?.hash;
                if (route === '#/home?fin=1' || route === '#/home' || route === '#/home?tab=0')
                    select(route === '#/home?fin=1' ? 'discovery' : 'home');
                // Index-level navigation survives disposal of this Home-owned host.
                if (global.__threePicFinGlobalNav) { current.removeNav?.(); return; }
                const headers = document.querySelectorAll('.skinHeader .headerTabs');
                const failClosed = () => {
                    current.removeNav?.(); select('home'); clearFinUrl();
                };
                if (headers.length !== 1) { failClosed(); return; }
                const header = headers[0];
                const native = header.querySelector(':scope > [is="emby-tabs"]');
                const buttons = native?.querySelectorAll(':scope > .emby-tabs-slider > .emby-tab-button');
                if (!buttons || buttons.length !== 2 || buttons[0].getAttribute('data-index') !== '0' ||
                    buttons[1].getAttribute('data-index') !== '1' ||
                    !buttons[0].textContent.trim() || !buttons[1].textContent.trim() ||
                    !buttons[0].classList.contains('emby-tab-button-active')) {
                    failClosed(); return;
                }
                const toolbars = [...document.querySelectorAll('.MuiToolbar-root')].filter(toolbar =>
                    toolbar.getBoundingClientRect().width > 0 &&
                    toolbar.querySelector(':scope > .MuiStack-root > a[href="#/home?tab=1"]'));
                if (toolbars.length > 1) { failClosed(); return; }
                const stack = toolbars.length === 1 ? toolbars[0].querySelector(':scope > .MuiStack-root') : null;
                let favoritesLink = stack?.querySelector(':scope > a[href="#/home?tab=1"]');
                let homeLink = stack?.querySelector(':scope > a[href="#/"]');
                let container = stack, drawer = false, stock = false;
                if (!stack) {
                    const links = [...document.querySelectorAll('.MuiListItem-root > a[href="#/home?tab=1"]')];
                    const favoriteItem = links.length === 1 ? links[0].parentElement : null;
                    const list = favoriteItem?.parentElement;
                    const homes = list ? [...list.querySelectorAll(':scope > .MuiListItem-root > a[href="#/"]')] : [];
                    if (homes.length === 1) {
                        favoritesLink = links[0]; homeLink = homes[0]; container = list; drawer = true;
                    }
                }
                // Stock 12.1 need not have the distribution's MUI toolbar/drawer.
                // Add a sibling to the validated native tabs, never a slider child
                // (the tab manager owns its two indexes and click handlers).
                if (!container && toolbars.length === 0 && header.getBoundingClientRect().width > 0 &&
                    header.parentElement?.getBoundingClientRect().width > 0 &&
                    document.querySelectorAll('.MuiListItem-root > a[href="#/home?tab=1"]').length === 0) {
                    container = header; homeLink = buttons[0]; stock = true;
                }
                if (!container || (!stock && !favoritesLink) || !homeLink ||
                    (stack && (stack.querySelectorAll(':scope > a[href="#/home?tab=1"]').length !== 1 ||
                        stack.querySelectorAll(':scope > a[href="#/"]').length !== 1))) {
                    failClosed(); return;
                }
                if (current.navContainer?.parentNode === container && current.nativeHome === homeLink) return;
                current.removeNav?.();
                const nav = document.createElement('button');
                nav.type = 'button'; nav.className = stock ? 'threepic-fin-host__nav threepic-fin-host__nav--stock' : drawer
                    ? 'threepic-fin-host__nav MuiButtonBase-root MuiListItemButton-root'
                    : 'threepic-fin-host__nav MuiButtonBase-root MuiButton-root MuiButton-text MuiButton-textInherit MuiButton-sizeMedium';
                nav.textContent = '3pic Fin';
                nav.setAttribute('aria-pressed', String(pane.getAttribute('data-threepic-fin-view') === 'discovery'));
                nav.addEventListener('click', () => {
                    const fin = pane.getAttribute('data-threepic-fin-view') !== 'discovery';
                    global.location.hash = fin ? '#/home?fin=1' : '#/home';
                    select(fin ? 'discovery' : 'home');
                    if (drawer) document.querySelector('.MuiToolbar-root button[aria-label="Open Menu"]')?.click();
                });
                const onHome = () => select('home');
                homeLink.addEventListener('click', onHome);
                const wrapper = drawer ? document.createElement('li') : nav;
                if (drawer) { wrapper.className = 'threepic-fin-host__nav-item MuiListItem-root'; wrapper.appendChild(nav); }
                (stock ? native : drawer ? favoritesLink.parentElement : favoritesLink).insertAdjacentElement('afterend', wrapper);
                current.nav = nav; current.navContainer = wrapper; current.nativeHome = homeLink;
                current.removeNav = () => {
                    homeLink.removeEventListener('click', onHome);
                    wrapper.remove(); current.nav = null; current.navContainer = null; current.nativeHome = null;
                };
            }
            // .sections remains in place for HSS: no reparent, clone, or render.
            pane.appendChild(panel);
            document.head.appendChild(stylesheet);
            document.head.appendChild(hostStyles);
            pane.setAttribute('data-threepic-fin-view', 'home');
            try {
                const active = () => current?.panel === panel && current.userId === userId &&
                    apiClient.getCurrentUserId?.() === userId &&
                    document.documentElement?.contains(pane) === true &&
                    global.location?.hash?.split('?')[0] === '#/home' &&
                    pane.getAttribute('data-threepic-fin-view') === 'discovery';
                // Jellyfin-web 12.1 appRouter.js exposes Emby.Page.showItem(item).
                // Its object branch routes using Id, Type and ServerId without a second async item lookup.
                const openItem = async ({ mediaType, mediaId, libraryItemId } = {}) => {
                    if (!active() || !['movie', 'tv'].includes(mediaType) || !Number.isInteger(mediaId) || mediaId <= 0 ||
                        !guid(libraryItemId) || typeof global.Emby?.Page?.showItem !== 'function') return false;
                    const identity = userId, route = global.location.hash, ticket = generation;
                    let fresh;
                    try { fresh = await apiClient.getJSON(apiClient.getUrl('3picFin/TitleDetails', { mediaType, mediaId })); }
                    catch (_) { return false; }
                    const id = field(fresh, 'LibraryItemId');
                    if (ticket !== generation || !active() || identity !== apiClient.getCurrentUserId() ||
                        route !== global.location.hash || field(fresh, 'MediaType') !== mediaType ||
                        field(fresh, 'TmdbId') !== mediaId || !guid(id) ||
                        id.replaceAll('-', '').toLowerCase() !== libraryItemId.replaceAll('-', '').toLowerCase()) return false;
                    const serverId = apiClient.serverId?.();
                    if (typeof serverId !== 'string' || !serverId) return false;
                    global.Emby.Page.showItem({ Id: id, Type: mediaType === 'tv' ? 'Series' : 'Movie', ServerId: serverId });
                    return true;
                };
                current = { pane, userId, apiClient, panel, stylesheet, hostStyles, sections, syncNav,
                    heroEnabled, heroStarted: false, heroTicket: 0, rowsEnabled, rowKinds, rowsTicket: 0 };
                current.cleanup = discovery.mount(panel, apiClient, { deferInitialLoad: true, openItem });
                if (global.location?.hash === '#/home?fin=1') select('discovery');
                syncNav();
                if (!current.nav && !global.__threePicFinGlobalNav) { dispose(); return false; }
                startHero(current);
                startRows(current);
            } catch (_) {
                current = { pane, panel, stylesheet, hostStyles, sections, rowsTicket: 0 };
                dispose(); return false;
            }
            return true;
        }
        return { mount, dispose, sync: () => current?.syncNav?.() };
    }
    global.ThreePicFinHomeHost = { createHost };
    if (typeof module !== 'undefined' && module.exports) module.exports = { createHost };
})(typeof globalThis !== 'undefined' ? globalThis : this);
