/* Home host; loaded by the separately pinned adapter, never mutates native tabs. */
(function (global) {
    'use strict';
    // Intentionally unpinned in the public build: no production web distribution approved yet.
    // Never enable by server version, DOM shape alone, or a caller-provided arbitrary hash.
    const VERIFIED_WEB_BUNDLE_SHA256 = null;
    const field = (item, name) => item?.[name] ?? item?.[name[0].toLowerCase() + name.slice(1)];
    const guid = id => typeof id === 'string' && /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i.test(id);
    let nextInstanceId = 0;
    const defaultFragment = async url => {
        const response = await fetch(url, { credentials: 'same-origin' });
        if (!response.ok) throw new Error('Discovery fragment unavailable');
        return response.text();
    };
    const defaultScript = (url, document) => new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = url;
        script.onload = () => { script.remove(); resolve(url.includes('static-hero.js') ? global.RowanStaticHero : global.ThreePicFinDiscovery); };
        script.onerror = () => { script.remove(); reject(new Error('Discovery script unavailable')); };
        document.head.appendChild(script);
    });
    function createHost({ document,
        loadFragment = defaultFragment, loadScript = url => defaultScript(url, document) } = {}) {
        const pinnedFingerprint = VERIFIED_WEB_BUNDLE_SHA256;
        let current = null, generation = 0;
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
        function startHero(state) {
            if (!state.heroEnabled || state.heroStarted || state.heroRoot ||
                state.pane.getAttribute('data-threepic-fin-view') !== 'home') return;
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
                    if (!active() || typeof state.apiClient.fetch !== 'function') return;
                    const slides = await state.apiClient.getJSON(state.apiClient.getUrl('Rowan/Home/Hero'));
                    if (!active() || !Array.isArray(slides)) return;
                    const safe = slides.filter(validSlide).slice(0, 10);
                    if (!safe.length) return; // Trust gate off: endpoint returns []; never show an empty placeholder.
                    const hero = await loadScript(state.apiClient.getUrl('3picFin/Web/static-hero.js'));
                    if (!active() || typeof hero?.mount !== 'function') return;
                    const root = document.createElement('div'); root.className = 'threepic-fin-host__hero';
                    const style = document.createElement('link');
                    style.rel = 'stylesheet'; style.href = state.apiClient.getUrl('3picFin/Web/static-hero.css');
                    state.pane.insertBefore(root, state.sections);
                    document.head.appendChild(style);
                    state.heroRoot = root; state.heroStyles = style;
                    state.heroCleanup = hero.mount(root, state.apiClient, { slides: safe, openItem: async id => {
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
        function dispose() {
            ++generation;
            if (!current) return;
            const { pane, tabs, panel, stylesheet, hostStyles, cleanup, buttons, sections, originalAttributes } = current;
            clearHero(current);
            current = null;
            (buttons || []).forEach(({ button, handler, keyHandler }) => {
                button.removeEventListener('click', handler);
                button.removeEventListener('keydown', keyHandler);
            });
            try { cleanup?.(); } catch (_) { /* Discovery cleanup must not prevent host teardown. */ }
            tabs?.remove(); panel?.remove(); stylesheet?.remove(); hostStyles?.remove();
            for (const [name, value] of Object.entries(originalAttributes || {})) {
                if (value === null) sections.removeAttribute(name);
                else sections.setAttribute(name, value);
            }
            pane.removeAttribute('data-threepic-fin-view');
        }
        async function mount({ pane, favorites, apiClient, fingerprint, userId, enabled = false, mode }) {
            // A fingerprint must be computed from the exact web bundle bytes by a future
            // adapter; this host does not trust Jellyfin server version or native tab indexes.
            if (!enabled || !pinnedFingerprint || fingerprint !== pinnedFingerprint ||
                !pane || !favorites || pane === favorites || !userId ||
                !apiClient?.getUrl || !apiClient?.getJSON) { dispose(); return false; }
            const sections = Array.from(pane.children).find(child =>
                child.classList?.contains('sections') || child.className?.split(/\s+/).includes('sections'));
            if (!sections) { dispose(); return false; }
            if (current?.pane === pane && current?.userId === userId && current?.apiClient === apiClient) return true;
            dispose();
            const ticket = generation;
            const url = path => apiClient.getUrl(`3picFin/Web/${path}`);
            try {
                mode ??= await apiClient.getJSON(apiClient.getUrl('Rowan/Home/Mode'));
            } catch (_) { return false; }
            if (ticket !== generation || (apiClient.getCurrentUserId && apiClient.getCurrentUserId() !== userId) ||
                !mode || (mode.DiscoveryEnabled !== true && mode.discoveryEnabled !== true &&
                    mode.HeroEnabled !== true && mode.heroEnabled !== true)) return false;
            const discoveryEnabled = mode.DiscoveryEnabled === true || mode.discoveryEnabled === true;
            const heroEnabled = mode.HeroEnabled === true || mode.heroEnabled === true;
            if (!discoveryEnabled) {
                // Hero-only mode does not create inner tabs, panels, or Discovery requests.
                current = { pane, userId, apiClient, sections, heroEnabled, heroStarted: false, heroTicket: 0 };
                pane.setAttribute('data-threepic-fin-view', 'home');
                startHero(current);
                return true;
            }
            let html, discovery;
            try {
                html = await loadFragment(url('discovery.html'));
                if (ticket !== generation) return false;
                discovery = await loadScript(url('discovery.js'));
                if (ticket !== generation) return false;
                if (typeof discovery?.mount !== 'function') return false;
            } catch (_) { return false; }
            const stylesheet = document.createElement('link');
            stylesheet.rel = 'stylesheet'; stylesheet.href = url('discovery.css');
            const hostStyles = document.createElement('link');
            hostStyles.rel = 'stylesheet'; hostStyles.href = url('home-tab-host.css');
            const tabs = document.createElement('div'); tabs.className = 'threepic-fin-host__tabs';
            tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', 'Home views');
            const panel = document.createElement('div'); panel.className = 'threepic-fin-host__panel';
            panel.setAttribute('role', 'tabpanel'); panel.innerHTML = html; panel.hidden = true;
            const prefix = `threepic-fin-host-${++nextInstanceId}`;
            const originalAttributes = Object.fromEntries(['id', 'role', 'aria-labelledby'].map(name =>
                [name, sections.getAttribute(name) ?? null]));
            sections.setAttribute('id', originalAttributes.id || `${prefix}-home-panel`);
            sections.setAttribute('role', 'tabpanel');
            panel.setAttribute('id', `${prefix}-fin-panel`);
            const buttons = [];
            let activated = false;
            const addButton = (name, view) => {
                const button = document.createElement('button'); button.type = 'button';
                button.className = 'threepic-fin-host__tab'; button.textContent = name;
                button.setAttribute('role', 'tab');
                button.setAttribute('id', `${prefix}-${view}-tab`);
                button.setAttribute('aria-controls', view === 'home' ? sections.getAttribute('id') : panel.getAttribute('id'));
                (view === 'home' ? sections : panel).setAttribute('aria-labelledby', button.getAttribute('id'));
                const handler = () => select(view);
                const keyHandler = event => {
                    const target = { ArrowRight: view === 'home' ? 'discovery' : 'home',
                        ArrowLeft: view === 'home' ? 'discovery' : 'home', Home: 'home', End: 'discovery' }[event.key];
                    if (!target) return;
                    event.preventDefault(); select(target);
                };
                button.addEventListener('click', handler);
                button.addEventListener('keydown', keyHandler);
                buttons.push({ button, handler, keyHandler }); tabs.appendChild(button);
            };
            function select(view, focus = true) {
                if (view === 'discovery' && current?.panel === panel) {
                    clearHero(current);
                    current.heroStarted = false;
                }
                panel.hidden = view !== 'discovery';
                if (view === 'discovery' && !activated && current?.panel === panel) {
                    activated = true;
                    current.cleanup?.activate?.();
                }
                pane.setAttribute('data-threepic-fin-view', view);
                if (view === 'home' && current?.panel === panel) startHero(current);
                buttons.forEach(({ button }, index) => {
                    const selected = (index === 0) === (view === 'home');
                    button.setAttribute('aria-selected', String(selected));
                    button.setAttribute('tabindex', selected ? '0' : '-1');
                });
                if (focus) buttons[view === 'home' ? 0 : 1].button.focus();
            }
            addButton('Home', 'home'); addButton('3pic Fin', 'discovery');
            // .sections remains in place for HSS: no reparent, clone, or render.
            pane.insertBefore(tabs, pane.children[0] || null);
            pane.appendChild(panel);
            document.head.appendChild(stylesheet);
            document.head.appendChild(hostStyles);
            select('home', false);
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
                current = { pane, userId, apiClient, tabs, panel, stylesheet, hostStyles, buttons, sections, originalAttributes,
                    heroEnabled, heroStarted: false, heroTicket: 0 };
                current.cleanup = discovery.mount(panel, apiClient, { deferInitialLoad: true, openItem });
                startHero(current);
            } catch (_) {
                current = { pane, tabs, panel, stylesheet, hostStyles, buttons, sections, originalAttributes };
                dispose(); return false;
            }
            return true;
        }
        return { mount, dispose };
    }
    global.ThreePicFinHomeHost = { createHost };
    if (typeof module !== 'undefined' && module.exports) module.exports = { createHost };
})(typeof globalThis !== 'undefined' ? globalThis : this);
