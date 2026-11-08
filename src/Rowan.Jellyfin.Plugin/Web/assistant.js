/* Account-private concierge. Assistant Markdown is sanitized; user messages remain plain text. */
(function (global) {
    'use strict';
    const guid = value => typeof value === 'string' && /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i.test(value) && !/^0+$/.test(value.replaceAll('-', ''));
    const sameGuid = (a, b) => guid(a) && guid(b) && a.replaceAll('-', '').toLowerCase() === b.replaceAll('-', '').toLowerCase();
    const poster = value => typeof value === 'string' && /^\/[a-zA-Z0-9_/-]+\.(?:jpg|jpeg|png|webp)$/.test(value) && !value.includes('..');
    const normalize = value => Array.isArray(value) ? value.map(normalize) : value && typeof value === 'object'
        ? Object.fromEntries(Object.entries(value).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), normalize(v)])) : value;
    const hiddenRoute = hash => /(?:^|\/)(?:video(?:player)?|audio(?:player)?|playback|login|selectserver|dashboard[^/?]*|(?:web)?configuration(?:page)?|plugins)(?:[/?&]|$)/i.test(hash || '') || /[?&]context=dashboard/i.test(hash || '');

    function createAssistant(world) {
        const document = world.document;
        let stopped = false, session = null, root = null, panel, launcher, transcript, input, send, status, historyPanel, memoryPanel;
        let state = null, thread = null, busy = false, opened = false, tab = 'chat', viewEpoch = 0;
        let pendingMessage = null;
        let flow = null, flowPromise = null, originalLogout = null, logoutWrapper = null;
        let nextCheck = 0, checking = false, loggingOut = false, interval;
        let settingsEpoch = 0;
        let markdownReady = null, markdownParser = null;
        const styles = [];
        function current(s = session) {
            return !stopped && !!s && session === s && world.ApiClient === s.api &&
                s.api.getCurrentUserId?.() === s.user && s.api.accessToken?.() === s.token;
        }
        function node(tag, text, className, parent) {
            const element = document.createElement(tag);
            if (text !== undefined) element.textContent = text;
            if (className) element.className = className;
            parent?.appendChild(element); return element;
        }
        function button(text, parent, handler, className = '') {
            const owner = session;
            const b = node('button', text, className, parent); b.type = 'button';
            b.addEventListener('click', event => { if (current(owner)) handler(event, b); }); return b;
        }
        function icon(name, parent) {
            const paths = {
                chat: 'M20 11.5a8 8 0 0 1-8 8H5l-3 3V11.5a9 9 0 0 1 18 0Z M7 10h8 M7 14h5',
                close: 'm6 6 12 12 M6 18 18 6',
                plus: 'M12 5v14 M5 12h14',
                history: 'M3 11a9 9 0 1 1 2.6 7 M3 4v7h7 M12 7v5l3 2',
                settings: 'M4 7h16 M4 17h16 M8 4v6 M16 14v6',
                send: 'M12 19V5 M6 11l6-6 6 6',
                arrow: 'M5 12h14 m-6-6 6 6-6 6',
                back: 'M19 12H5 m6-6-6 6 6 6',
                trash: 'M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13 M10 11v5 M14 11v5',
                check: 'm5 12 4 4L19 6',
                film: 'M4 3h16v18H4Z M4 8h16 M4 16h16 M8 3v18 M16 3v18',
                heart: 'M12 20 4 12a5 5 0 0 1 8-6 5 5 0 0 1 8 6Z',
                tv: 'M4 7h16v13H4Z m4-4 4 4 4-4'
            };
            const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            for (const [key, value] of Object.entries({ viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.7', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true', focusable: 'false' })) svg.setAttribute(key, value);
            const path = document.createElementNS('http://www.w3.org/2000/svg', 'path'); path.setAttribute('d', paths[name]);
            svg.appendChild(path); parent.appendChild(svg); return svg;
        }
        function iconButton(label, name, parent, handler, className = '') {
            const b = button('', parent, handler, 'fin-ai-icon-button ' + className);
            b.setAttribute('aria-label', label); b.title = label; icon(name, b); return b;
        }
        function actionButton(label, name, parent, handler, className = '') {
            const b = button('', parent, handler, className); icon(name, b); node('span', label, '', b); return b;
        }
        function style(path) {
            const link = node('link'); link.rel = 'stylesheet'; link.href = session.api.getUrl('3picFin/Web/' + path);
            document.head.appendChild(link); styles.push(link);
        }
        async function loadMarkdown(s) {
            if (!markdownReady) markdownReady = (async () => {
                for (const [name, file] of [['marked', 'assistant-marked.js'], ['DOMPurify', 'assistant-purify.js']]) {
                    if (world[name]) continue;
                    await new Promise((resolve, reject) => {
                        const script = node('script'); script.src = s.api.getUrl('3picFin/Web/' + file);
                        script.onload = () => { script.remove(); resolve(); };
                        script.onerror = () => { script.remove(); reject(Error('Markdown renderer unavailable.')); };
                        document.head.appendChild(script);
                    });
                }
                const escape = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
                markdownParser = new world.marked.Marked({ gfm: true, breaks: false, renderer: {
                    html: token => escape(token.text),
                    // Images remain explicit links, avoiding arbitrary model-controlled network fetches.
                    image: token => '<a href="' + escape(token.href) + '">Image: ' + escape(token.text) + '</a>'
                } });
            })().catch(error => { markdownReady = null; throw error; });
            return markdownReady;
        }
        function renderMarkdown(text, parent) {
            if (!markdownParser || !world.DOMPurify) { parent.textContent = text; return; }
            try {
                const fragment = world.DOMPurify.sanitize(markdownParser.parse(text), {
                    RETURN_DOM_FRAGMENT: true,
                    ALLOWED_TAGS: ['p','br','strong','em','del','h1','h2','h3','h4','h5','h6','ul','ol','li','blockquote','pre','code','hr','a','table','thead','tbody','tr','th','td','input'],
                    ALLOWED_ATTR: ['href','title','start','align','type','checked','disabled'], ALLOW_DATA_ATTR: false, ALLOW_ARIA_ATTR: false
                });
                for (const a of fragment.querySelectorAll('a')) {
                    try {
                        const url = new URL(a.getAttribute('href'));
                        if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) throw Error();
                        a.target = '_blank'; a.rel = 'noopener noreferrer'; a.referrerPolicy = 'no-referrer';
                    } catch (_) { a.replaceWith(...a.childNodes); }
                }
                for (const box of fragment.querySelectorAll('input')) { box.type = 'checkbox'; box.disabled = true; }
                for (const table of [...fragment.querySelectorAll('table')]) {
                    const wrapper = node('div', undefined, 'fin-ai-table'); wrapper.tabIndex = 0;
                    wrapper.setAttribute('role', 'region'); wrapper.setAttribute('aria-label', 'Markdown table');
                    table.replaceWith(wrapper); wrapper.appendChild(table);
                }
                parent.replaceChildren(fragment);
            } catch (_) { parent.textContent = text; }
        }
        async function request(path, method = 'GET', body, s = session) {
            if (!current(s)) throw Error('Session changed.');
            const response = await world.fetch(s.api.getUrl('3picFin/' + path), {
                method, signal: s.abort.signal, credentials: 'same-origin', cache: 'no-store',
                headers: { 'Authorization': 'MediaBrowser Token="' + s.token + '"', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
                ...(body === undefined ? {} : { body: JSON.stringify(body) })
            });
            if (!current(s)) throw Error('Session changed.');
            const data = normalize(await response.json().catch(() => null));
            if (!current(s)) throw Error('Session changed.');
            if (!response.ok) { const error = Error(data?.error || 'Assistant unavailable. Try again shortly.'); error.status = response.status; throw error; }
            return data;
        }
        function close() { opened = false; if (panel) panel.hidden = true; root?.classList.remove('fin-ai--open'); launcher?.setAttribute('aria-expanded', 'false'); }
        function destroy() {
            ++viewEpoch; close();
            flow?.cleanup?.(); flow?.root.remove(); flow = flowPromise = null;
            root?.remove(); root = null; styles.splice(0).forEach(s => s.remove());
            state = thread = pendingMessage = null; busy = false; opened = false; tab = 'chat';
        }
        function endSession() {
            const old = session;
            session = null; old?.abort.abort(); destroy();
            if (old?.api.logout === logoutWrapper) old.api.logout = originalLogout;
            originalLogout = logoutWrapper = null;
        }
        function setBusy(value) {
            busy = value;
            if (!root) return;
            root.querySelectorAll('.fin-ai-action').forEach(el => { el.disabled = value; });
            updateSend();
            root.setAttribute('aria-busy', String(value));
        }
        async function perform(action) {
            if (busy || !current()) return;
            const s = session;
            setBusy(true); status.textContent = '';
            try { await action(s); }
            catch (error) { if (current(s)) status.textContent = error.message; }
            finally { if (current(s) && root) setBusy(false); }
        }
        function renderState() {
            historyPanel.replaceChildren();
            node('h3', 'Conversations', '', historyPanel);
            if (!state.threads?.length) node('p', 'Your conversations will appear here.', 'fin-ai-muted', historyPanel);
            for (const item of state.threads || []) {
                const row = node('div', undefined, 'fin-ai-history-row', historyPanel);
                const open = actionButton(item.title, 'chat', row, () => perform(async s => {
                    ++viewEpoch; thread = await request('Ai/Threads/' + item.id, 'GET', undefined, s);
                    if (current(s)) { switchTab('chat'); renderThread(); renderState(); }
                }), 'fin-ai-history-title fin-ai-action');
                if (item.id === thread?.id) open.setAttribute('aria-current', 'true');
                iconButton('Delete conversation: ' + item.title, 'trash', row, () => perform(async s => {
                    if (!world.confirm('Delete this conversation? Saved preferences are separate.')) return;
                    await request('Ai/Threads/' + item.id, 'DELETE', undefined, s);
                    if (thread?.id === item.id) { thread = null; renderThread(); }
                    await refreshState(s);
                }), 'fin-ai-action');
            }
            renderMemory();
        }
        async function refreshState(s = session) {
            const epoch = settingsEpoch;
            const result = await request('Ai/State', 'GET', undefined, s);
            if (!current(s) || !root || epoch !== settingsEpoch) return;
            state = result; renderState();
        }
        function navigate(card) {
            if (!current() || !guid(card.libraryItemId)) return false;
            const server = session.api.serverId?.();
            if (typeof server !== 'string' || !server) return false;
            close();
            if (typeof world.Emby?.Page?.showItem === 'function') world.Emby.Page.showItem({
                Id: card.libraryItemId, Type: card.mediaType === 'tv' ? 'Series' : 'Movie', ServerId: server
            });
            else world.location.hash = '#/details?id=' + encodeURIComponent(card.libraryItemId) + '&serverId=' + encodeURIComponent(server);
            return true;
        }
        async function loadFlow(s) {
            const epoch = viewEpoch;
            if (flow) return flow;
            if (!flowPromise) flowPromise = (async () => {
                if (!world.ThreePicFinDiscovery) await new Promise((resolve, reject) => {
                    const script = node('script'); script.src = s.api.getUrl('3picFin/Web/discovery.js');
                    script.onload = resolve; script.onerror = () => reject(Error('Title details unavailable.')); document.head.appendChild(script);
                });
                const response = await world.fetch(s.api.getUrl('3picFin/Web/discovery.html'), { signal: s.abort.signal });
                if (!response.ok) throw Error('Title details unavailable.');
                const html = await response.text();
                if (!current(s) || !root || epoch !== viewEpoch) return null;
                style('discovery.css');
                const fragment = node('div', undefined, 'fin-ai-flow');
                fragment.innerHTML = html; // Trusted, embedded plugin fragment only; never model/user content.
                document.body.appendChild(fragment);
                const cleanup = world.ThreePicFinDiscovery.mount(fragment, s.api, {
                    deferInitialLoad: true, userId: s.user, isCurrent: () => current(s),
                    openItem: async ({ mediaType, mediaId, libraryItemId }) => {
                        const fresh = await request('TitleDetails?mediaType=' + mediaType + '&mediaId=' + mediaId, 'GET', undefined, s);
                        return current(s) && sameGuid(fresh.libraryItemId, libraryItemId) && fresh.tmdbId === mediaId && fresh.mediaType === mediaType && navigate(fresh);
                    }
                });
                flow = { root: fragment, cleanup }; return flow;
            })().finally(() => { if (current(s)) flowPromise = null; });
            return flowPromise;
        }
        async function openCard(card, trigger) {
            await perform(async s => {
                if (guid(card.libraryItemId)) {
                    const fresh = await request('Ai/Library/' + card.libraryItemId, 'GET', undefined, s);
                    if (!sameGuid(fresh.libraryItemId, card.libraryItemId) || !navigate(fresh)) throw Error('This library item is no longer available.');
                } else if (['movie', 'tv'].includes(card.mediaType) && Number.isInteger(card.tmdbId) && card.tmdbId > 0) {
                    const fresh = await request('TitleDetails?mediaType=' + card.mediaType + '&mediaId=' + card.tmdbId, 'GET', undefined, s);
                    if (fresh.mediaType !== card.mediaType || fresh.tmdbId !== card.tmdbId) throw Error('Title identity changed.');
                    if (guid(fresh.libraryItemId)) { if (!navigate(fresh)) throw Error('Cannot open this library item.'); }
                    else {
                        const controller = await loadFlow(s);
                        if (current(s)) controller?.cleanup.openDetails(card, trigger);
                    }
                }
            });
        }
        function renderCard(card, parent, proposal) {
            const article = node('article', undefined, 'fin-ai-card' + (proposal ? ' fin-ai-card--request' : ''), parent);
            const artwork = node('div', undefined, 'fin-ai-poster', article);
            icon(card.mediaType === 'tv' ? 'tv' : 'film', artwork);
            const art = poster(card.posterPath) ? 'https://image.tmdb.org/t/p/w185' + card.posterPath
                : guid(card.libraryItemId) ? session.api.getUrl('Items/' + card.libraryItemId + '/Images/Primary', { maxWidth: 160 }) : null;
            if (art) {
                const image = node('img', undefined, '', artwork); image.alt = ''; image.loading = 'lazy'; image.src = art;
                image.addEventListener('error', () => image.remove());
            }
            const info = node('div', undefined, 'fin-ai-card-info', article);
            button(card.title, info, (_, b) => openCard(card, b), 'fin-ai-title fin-ai-action');
            const meta = node('div', undefined, 'fin-ai-meta', info);
            node('span', card.mediaType === 'tv' ? 'TV series' : 'Movie', '', meta);
            if (card.date) node('span', card.date.slice(0, 4), '', meta);
            if (!proposal) node('span', guid(card.libraryItemId) ? 'In your library' : 'Discover', 'fin-ai-availability', meta);
            if (card.overview) node('p', card.overview, 'fin-ai-overview', info);
            if (!proposal) actionButton(guid(card.libraryItemId) ? 'Open in Jellyfin' : 'Details & request', 'arrow', info, (_, b) => openCard(card, b), 'fin-ai-link fin-ai-action');
            else {
                const choice = proposal.selection;
                const selection = node('div', undefined, 'fin-ai-selection', article);
                node('span', choice.is4k ? '4K' : 'Standard', '', selection);
                if (choice.mediaType === 'tv') node('span', 'Seasons ' + choice.seasons.join(', '), '', selection);
                const expired = Date.parse(proposal.expiresAt) <= Date.now();
                if (proposal.state === 'pending' && !expired) {
                    const actions = node('div', undefined, 'fin-ai-card-actions', article);
                    actionButton('Confirm request', 'check', actions, () => confirmRequest(proposal, true), 'fin-ai-action fin-ai-primary');
                    button('Cancel', actions, () => confirmRequest(proposal, false), 'fin-ai-action fin-ai-secondary');
                } else node('p', proposal.result || (expired ? 'Confirmation expired. Ask for a fresh card.' : 'Outcome unknown. Check My Requests before trying again.'), 'fin-ai-result', article);
            }
        }
        function renderMessage(role, text) {
            const entry = node('section', undefined, 'fin-ai-message fin-ai-message--' + role, transcript);
            entry.setAttribute('aria-label', role === 'user' ? 'You' : '3pic Fin');
            if (text) {
                const bubble = node('div', undefined, 'fin-ai-text' + (role === 'assistant' ? ' fin-ai-markdown' : ''), entry);
                if (role === 'assistant') renderMarkdown(text, bubble); else bubble.textContent = text;
            }
            return entry;
        }
        function renderThread() {
            transcript.replaceChildren();
            if (!thread?.messages?.length && !pendingMessage) {
                const welcome = node('div', undefined, 'fin-ai-welcome', transcript);
                icon('chat', node('div', undefined, 'fin-ai-welcome-icon', welcome));
                node('h3', 'What are we watching?', '', welcome);
                node('p', 'A mood, a favourite, or something new.\nLet’s find your next watch.', 'fin-ai-muted', welcome);
                const prompts = node('div', undefined, 'fin-ai-prompts', welcome);
                for (const [text, name] of [['Something light under two hours', 'film'], ['A show like my favourites', 'heart'], ['A new series to get into', 'tv']])
                    actionButton(text, name, prompts, () => { input.value = text; resizeInput(); input.focus(); }, 'fin-ai-prompt fin-ai-action');
            }
            for (const message of thread?.messages || []) {
                const entry = renderMessage(message.role === 'user' ? 'user' : 'assistant', message.text);
                // A confirmation already contains its title; don't show the same poster twice.
                const proposedKeys = new Set((message.proposals || []).map(p => p.card.key));
                for (const card of message.cards || []) if (!proposedKeys.has(card.key)) renderCard(card, entry);
                for (const proposal of message.proposals || []) renderCard(proposal.card, entry, proposal);
            }
            if (pendingMessage) {
                renderMessage('user', pendingMessage);
                const typing = node('div', undefined, 'fin-ai-typing', transcript);
                typing.setAttribute('role', 'status'); typing.setAttribute('aria-label', 'Finding your next watch');
                for (let i = 0; i < 3; i++) node('span', undefined, '', typing).setAttribute('aria-hidden', 'true');
            }
            transcript.scrollTop = transcript.scrollHeight;
            setBusy(busy);
        }
        async function confirmRequest(proposal, confirm) {
            await perform(async s => {
                const id = thread.id;
                try { thread = await request('Ai/Confirm', 'POST', { threadId: id, proposalId: proposal.id, confirm }, s); }
                catch (error) {
                    // A GET reconciliation is safe; NEVER retry the confirmation POST automatically.
                    try { thread = await request('Ai/Threads/' + id, 'GET', undefined, s); } catch (_) { }
                    throw error;
                } finally { if (current(s)) renderThread(); }
                if (current(s)) await refreshState(s);
            });
        }
        function renderMemory() {
            const owner = session;
            memoryPanel.replaceChildren();
            node('h3', 'Your preferences', '', memoryPanel);
            node('p', 'Fine-tune your recommendations and what I remember.', 'fin-ai-muted', memoryPanel);
            for (const [key, label] of [['personalization', 'Use my watched titles, favourites and progress'], ['learnMemory', 'Learn my entertainment tastes between chats']]) {
                const row = node('label', undefined, 'fin-ai-check', memoryPanel);
                const box = node('input', undefined, 'fin-ai-action', row); box.type = 'checkbox'; box.checked = state[key];
                node('span', label, '', row);
                box.addEventListener('change', () => { if (!current(owner)) return; perform(async s => {
                    ++settingsEpoch; // Ignore state reads begun before this edit.
                    try {
                        const saved = await request('Ai/Settings', 'POST', { [key]: box.checked }, s);
                        if (current(s)) { Object.assign(state, saved); renderMemory(); }
                    } catch (error) {
                        if (current(s)) {
                            // A failed response may still have saved. Reconcile if possible;
                            // otherwise restore the last confirmed values, never the optimistic checkbox.
                            try { await refreshState(s); } catch (_) { }
                            if (current(s)) renderMemory();
                        }
                        throw Error('Privacy change could not be confirmed. Check your settings before chatting. ' + error.message);
                    } finally {
                        ++settingsEpoch; // Also invalidate reads started during the save.
                    }
                }); });
            }
            for (const item of state.memory || []) {
                const row = node('div', undefined, 'fin-ai-memory-row', memoryPanel);
                const text = node('textarea', undefined, 'fin-ai-action', row); text.maxLength = 160; text.value = item.text; text.setAttribute('aria-label', 'Remembered preference');
                const actions = node('div', undefined, 'fin-ai-card-actions', row);
                actionButton('Save preference', 'check', actions, () => perform(async s => { await request('Ai/Memory', 'POST', { id: item.id, text: text.value }, s); await refreshState(s); }), 'fin-ai-action fin-ai-secondary');
                actionButton('Forget', 'trash', actions, () => perform(async s => { await request('Ai/Memory/' + item.id, 'DELETE', undefined, s); await refreshState(s); }), 'fin-ai-action fin-ai-link');
            }
            if (!state.memory?.length) node('p', 'No saved preferences yet.', 'fin-ai-meta', memoryPanel);
            setBusy(busy);
        }
        function switchTab(next) {
            tab = next; memoryPanel.hidden = next !== 'memory'; transcript.hidden = next !== 'chat'; historyPanel.hidden = next !== 'history';
            root.querySelector('.fin-ai-compose').hidden = next !== 'chat';
            for (const [selector, active, label, name] of [['.fin-ai-memory-toggle', next === 'memory', 'Preferences', 'settings'], ['.fin-ai-history-toggle', next === 'history', 'Conversations', 'history']]) {
                const b = root.querySelector(selector); b.replaceChildren(); icon(active ? 'back' : name, b);
                b.setAttribute('aria-label', active ? 'Back to chat' : label); b.title = active ? 'Back to chat' : label;
                b.setAttribute('aria-pressed', String(active));
            }
            status.textContent = '';
        }
        function updateSend() { if (send) send.disabled = busy || !input?.value.trim(); }
        function resizeInput() {
            input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 120) + 'px'; updateSend();
        }
        function resizeViewport() {
            if (!root || !world.visualViewport) return;
            root.style.setProperty('--ai-viewport-height', world.visualViewport.height + 'px');
            root.style.setProperty('--ai-viewport-top', world.visualViewport.offsetTop + 'px');
        }
        function build() {
            const owner = session;
            style('assistant.css'); root = node('div', undefined, 'fin-ai', document.body);
            launcher = button('', root, () => {
                opened = !opened; panel.hidden = !opened; launcher.setAttribute('aria-expanded', String(opened));
                root.classList.toggle('fin-ai--open', opened);
                if (opened) { switchTab(tab); refreshState().catch(() => {}); }
            }, 'fin-ai-launcher');
            icon('chat', launcher); launcher.title = 'Find something to watch';
            launcher.setAttribute('aria-label', 'Open 3pic Fin assistant'); launcher.setAttribute('aria-expanded', 'false'); launcher.setAttribute('aria-controls', 'fin-ai-panel');
            panel = node('section', undefined, 'fin-ai-panel', root); panel.id = 'fin-ai-panel'; panel.hidden = true;
            panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-label', '3pic Fin assistant');
            const header = node('header', undefined, 'fin-ai-header', panel);
            const heading = node('div', undefined, 'fin-ai-heading', header); node('h2', '3pic Fin', '', heading); node('span', 'Movie & TV chat', '', heading);
            const tools = node('nav', undefined, 'fin-ai-tools', header); tools.setAttribute('aria-label', 'Chat controls');
            iconButton('Conversations', 'history', tools, () => switchTab(tab === 'history' ? 'chat' : 'history'), 'fin-ai-history-toggle');
            iconButton('New conversation', 'plus', tools, () => {
                if (busy) return;
                ++viewEpoch; thread = null; input.value = ''; resizeInput(); switchTab('chat'); renderThread(); renderState(); input.focus();
            }, 'fin-ai-action');
            iconButton('Preferences', 'settings', tools, () => switchTab(tab === 'memory' ? 'chat' : 'memory'), 'fin-ai-memory-toggle');
            iconButton('Close', 'close', tools, () => { close(); launcher.focus(); });
            historyPanel = node('div', undefined, 'fin-ai-history', panel); historyPanel.hidden = true;
            transcript = node('div', undefined, 'fin-ai-transcript', panel); transcript.setAttribute('role', 'log'); transcript.setAttribute('aria-label', 'Conversation'); transcript.setAttribute('aria-live', 'polite'); transcript.setAttribute('aria-relevant', 'additions');
            memoryPanel = node('div', undefined, 'fin-ai-memory', panel); memoryPanel.hidden = true;
            status = node('p', '', 'fin-ai-status', panel); status.setAttribute('role', 'status');
            const compose = node('form', undefined, 'fin-ai-compose', panel);
            const composer = node('div', undefined, 'fin-ai-composer', compose);
            input = node('textarea', undefined, 'fin-ai-action', composer); input.maxLength = 2000; input.rows = 1; input.placeholder = 'What are you in the mood for?'; input.setAttribute('aria-label', 'Message the assistant');
            input.addEventListener('input', resizeInput);
            send = iconButton('Send', 'send', composer, () => {}, 'fin-ai-primary fin-ai-action'); send.type = 'submit';
            const sendMessage = async () => { if (!current(owner) || busy) return; await perform(async s => {
                const text = input.value.trim(); if (!text) return;
                pendingMessage = text; input.value = ''; resizeInput(); renderThread();
                try {
                    const result = await request('Ai/Message', 'POST', { threadId: thread?.id || null, text }, s);
                    if (!current(s)) return;
                    thread = result;
                } catch (error) {
                    if (current(s)) { input.value = text; resizeInput(); }
                    throw error;
                } finally { if (current(s)) { pendingMessage = null; renderThread(); } }
                await refreshState(s);
            }); if (current(owner) && opened && tab === 'chat') input.focus({ preventScroll: true }); };
            compose.addEventListener('submit', event => { event.preventDefault(); sendMessage(); });
            input.addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); sendMessage(); } });
            panel.addEventListener('keydown', event => { if (current(owner) && event.key === 'Escape') {
                event.stopPropagation();
                if (tab !== 'chat') switchTab('chat'); else { close(); launcher.focus(); }
            } });
            renderThread(); renderState(); updateSend(); resizeViewport();
        }
        async function sync() {
            if (stopped || loggingOut) return;
            const api = world.ApiClient, user = api?.getCurrentUserId?.(), token = api?.accessToken?.();
            if (!user || !token || !api?.getUrl) { if (session) endSession(); return; }
            if (!session || session.api !== api || session.user !== user || session.token !== token) {
                endSession(); session = { api, user, token, abort: new world.AbortController() }; nextCheck = 0;
                if (typeof api.logout === 'function') {
                    originalLogout = api.logout;
                    const original = originalLogout;
                    logoutWrapper = function (...args) {
                        loggingOut = true; endSession();
                        let result;
                        try { result = original.apply(this, args); }
                        catch (error) { loggingOut = false; throw error; }
                        Promise.resolve(result).finally(() => { loggingOut = false; }).catch(() => {});
                        return result;
                    };
                    api.logout = logoutWrapper;
                }
            }
            if (root) root.hidden = hiddenRoute(world.location.hash) || !!document.fullscreenElement;
            if (checking || Date.now() < nextCheck || busy) return;
            const s = session, epoch = settingsEpoch; checking = true; nextCheck = Date.now() + 30_000;
            try {
                const info = await api.getJSON(api.getUrl('System/Info/Public'));
                if (!current(s) || !/^12\.3\.\d+(?:\.\d+)?$/.test(info?.Version || info?.version || '')) return;
                const data = await request('Ai/State', 'GET', undefined, s);
                if (!current(s) || epoch !== settingsEpoch) return;
                state = data;
                try { await loadMarkdown(s); } catch (_) { /* Plain text remains usable if a dependency fails. */ }
                if (!current(s)) return;
                if (!root) build();
                root.hidden = hiddenRoute(world.location.hash) || !!document.fullscreenElement;
            } catch (error) { if (current(s) && [401, 403, 404].includes(error.status)) destroy(); }
            finally { checking = false; }
        }
        world.addEventListener('hashchange', sync); world.addEventListener('popstate', sync);
        document.addEventListener('fullscreenchange', sync);
        world.visualViewport?.addEventListener('resize', resizeViewport);
        world.visualViewport?.addEventListener('scroll', resizeViewport);
        interval = world.setInterval(sync, 500); sync();
        return { dispose() { stopped = true; endSession(); world.clearInterval(interval); world.removeEventListener('hashchange', sync); world.removeEventListener('popstate', sync); document.removeEventListener('fullscreenchange', sync); world.visualViewport?.removeEventListener('resize', resizeViewport); world.visualViewport?.removeEventListener('scroll', resizeViewport); } };
    }
    if (typeof module !== 'undefined' && module.exports) module.exports = { createAssistant, guid, sameGuid, poster, normalize, hiddenRoute };
    if (global.document) { global.__threePicFinAssistant?.dispose(); global.__threePicFinAssistant = createAssistant(global); }
})(typeof window !== 'undefined' ? window : globalThis);
