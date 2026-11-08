/* Bounded transport + horizontal windowing shared by plugin-owned catalog rails. */
(function (global) {
    'use strict';
    const field = (item, name) => item?.[name] ?? item?.[name[0].toLowerCase() + name.slice(1)];
    function create({ rail, status, title, renderItem, loadPage, isCurrent = () => true,
        key = item => `${field(item, 'MediaType') || field(item, 'Type')}:${field(item, 'TmdbId')}`,
        onItems = () => {}, onRender = () => {}, onDenied = () => {} }) {
        let items = [], seen = new Set(), nodes = new Map(), page = 0, max = 1;
        let generation = 0, abort = null, busy = false, failed = false, stopped = false;
        let stride = 160, gap = 12, frame = null, emptyRuns = 0, pendingFocus = null, enabled = false;
        const listeners = [], dirty = new Set();
        const listen = (node, type, handler) => { node.addEventListener(type, handler); listeners.push(() => node.removeEventListener(type, handler)); };
        rail.classList.add('fin-continuous'); rail.tabIndex = 0;
        rail.setAttribute('role', 'list'); rail.setAttribute('aria-label', title);
        rail.setAttribute('aria-live', 'off');
        status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
        const caption = document.createElement('span'), retry = document.createElement('button');
        retry.type = 'button'; retry.textContent = 'Retry'; retry.hidden = true;
        retry.setAttribute('aria-label', `Retry ${title}`); status.replaceChildren(caption, retry);
        const valid = ticket => !stopped && ticket === generation && isCurrent();
        const more = () => page < max;
        function announce() {
            rail.setAttribute('aria-busy', String(busy)); retry.hidden = !failed;
            caption.textContent = busy ? `Loading ${title}…` : failed ? `${title} unavailable right now. ` :
                !page ? '' : emptyRuns >= 3 ? 'No further matching results in this range. Refine your search for more.' :
                    !items.length ? `No ${title} found.` : more() ? `${items.length} loaded` :
                    max === 100 ? 'Browsing limit reached. Refine your search for more.' : `End of ${title}`;
        }
        function spacer(count) {
            const node = document.createElement('div'); node.setAttribute('aria-hidden', 'true');
            node.className = 'fin-continuous__space';
            node.style.cssText = `flex:0 0 ${Math.max(0, count * stride - gap)}px;pointer-events:none;`;
            return node;
        }
        function paint() {
            if (!valid(generation)) return;
            for (const item of dirty) refreshItem(item);
            // A focused card (including a modal's return target) is never recycled.
            const active = document.activeElement;
            const pinned = [...nodes].filter(([, node]) => node.contains(active) || node.getAttribute('data-fin-pinned') === 'true').map(([index]) => index);
            const start = Math.max(0, Math.floor(rail.scrollLeft / stride) - 6);
            const end = Math.min(items.length, Math.ceil((rail.scrollLeft + rail.clientWidth) / stride) + 6);
            const indices = [...new Set([...Array.from({ length: Math.max(0, end - start) }, (_, n) => start + n), ...pinned])].sort((a, b) => a - b);
            const keep = new Set(indices);
            for (const [index, node] of nodes) if (!keep.has(index)) { node.remove(); nodes.delete(index); }
            for (const node of [...rail.children]) if (!node.hasAttribute('data-fin-index')) node.remove();
            // Reconcile in place: moving/replacing a focused button would blur it.
            let cursor = rail.firstElementChild, previous = -1;
            for (const index of indices) {
                let node = nodes.get(index);
                if (!node) {
                    node = renderItem(items[index]);
                    node.setAttribute('data-fin-index', String(index)); node.setAttribute('role', 'listitem');
                    node.setAttribute('aria-posinset', String(index + 1)); nodes.set(index, node);
                }
                node.setAttribute('aria-setsize', more() ? '-1' : String(items.length));
                if (index > previous + 1) rail.insertBefore(spacer(index - previous - 1), cursor);
                if (node !== cursor) rail.insertBefore(node, cursor);
                cursor = node.nextElementSibling; previous = index;
            }
            if (previous < items.length - 1) rail.appendChild(spacer(items.length - previous - 1));
            if (!items.length) {
                const message = document.createElement('p'); message.className = 'threepic-fin-discovery__message';
                message.textContent = busy ? 'Loading…' : failed ? `${title} unavailable right now.` : page ? `No ${title} found.` : '';
                rail.appendChild(message);
            }
            const sample = nodes.values().next().value;
            if (sample?.getBoundingClientRect) {
                const width = sample.getBoundingClientRect().width;
                gap = parseFloat(global.getComputedStyle(rail).columnGap) || 0;
                if (width > 0 && Math.abs(stride - width - gap) > .5) {
                    const anchor = rail.scrollLeft / stride; stride = width + gap;
                    rail.scrollLeft = anchor * stride; schedule();
                }
            }
            onRender();
        }
        function nearEnd() {
            if (!rail.getClientRects().length || rail.closest('[hidden]')) return false;
            const rect = rail.getBoundingClientRect();
            return rect.bottom > 0 && rect.top < global.innerHeight + 300 &&
                rail.scrollLeft + rail.clientWidth + Math.max(rail.clientWidth, stride * 4) >= items.length * stride;
        }
        function schedule() {
            if (stopped || frame !== null) return;
            frame = global.requestAnimationFrame(() => { frame = null; paint(); if (enabled && nearEnd() && !failed) load(); });
        }
        function accept(source, requestedPage) {
            const rows = field(source, 'Items');
            if (field(source, 'Error') || !Array.isArray(rows) || rows.length > 20) throw Error('Invalid feed');
            const responsePage = field(source, 'Page');
            if (responsePage != null && responsePage !== requestedPage) throw Error('Unexpected feed page');
            page = requestedPage;
            const total = field(source, 'TotalPages'); max = Number.isInteger(total) ? Math.max(page, Math.min(100, total)) : page;
            const added = [];
            for (const original of rows) {
                const identity = key(original);
                if (seen.has(identity)) continue;
                seen.add(identity); const item = { ...original }; items.push(item); added.push(item);
            }
            emptyRuns = added.length ? 0 : emptyRuns + 1;
            // Filtered/duplicate-only pages must not cause an eager catalog crawl.
            if (emptyRuns >= 3) max = page;
            const ticket = generation;
            onItems(added, () => valid(ticket), refreshItem);
            announce(); paint(); schedule();
            if (pendingFocus !== null && pendingFocus < items.length) { const index = pendingFocus; pendingFocus = null; focus(index); }
        }
        async function load() {
            if (!valid(generation) || busy || !more()) return;
            enabled = true;
            busy = true; failed = false; abort = new AbortController();
            const ticket = generation, next = page + 1;
            announce(); if (!items.length) paint();
            try {
                const source = await loadPage(next, abort.signal);
                if (!valid(ticket)) return;
                busy = false; accept(source, next);
            } catch (error) {
                if (!valid(ticket)) return;
                busy = false; failed = true; pendingFocus = null;
                if ([403, 404].includes(error?.status ?? error?.statusCode)) onDenied();
                announce(); paint();
            } finally { if (ticket === generation) abort = null; }
        }
        function reset() {
            ++generation; abort?.abort(); abort = null; busy = failed = false;
            enabled = false;
            items = []; seen.clear(); nodes.clear(); dirty.clear(); page = 0; max = 1; emptyRuns = 0; pendingFocus = null;
            rail.replaceChildren(); rail.scrollLeft = 0; announce();
        }
        function refreshItem(item) {
            const index = items.indexOf(item), node = nodes.get(index);
            if (!node) { dirty.delete(item); return; }
            if (node.contains(document.activeElement) || node.getAttribute('data-fin-pinned') === 'true') { dirty.add(item); return; }
            dirty.delete(item);
            const replacement = renderItem(item);
            for (const name of ['data-fin-index', 'role', 'aria-posinset', 'aria-setsize']) replacement.setAttribute(name, node.getAttribute(name));
            node.replaceWith(replacement); nodes.set(index, replacement);
        }
        function focus(index) {
            if (index < 0) return;
            if (index >= items.length) { if (more()) { pendingFocus = index; load(); } return; }
            rail.scrollLeft = index * stride; paint();
            const node = nodes.get(index), target = node.querySelector('button, [tabindex]') || node;
            if (target === node && !node.hasAttribute('tabindex')) node.tabIndex = 0;
            target.focus({ preventScroll: true }); schedule();
        }
        listen(retry, 'click', () => { load(); });
        listen(rail, 'scroll', schedule); listen(rail, 'focusin', schedule); listen(rail, 'focusout', schedule);
        listen(rail, 'keydown', event => {
            const node = event.target.closest('[data-fin-index]');
            const index = node ? Number(node.getAttribute('data-fin-index')) : 0;
            if (['ArrowRight', 'ArrowLeft', 'Home', 'End'].includes(event.key)) {
                event.preventDefault(); focus(event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : index + (event.key === 'ArrowRight' ? 1 : -1));
            } else if (event.key === 'Tab' && node) {
                const stops = [...node.querySelectorAll('button, [tabindex]')].filter(el => el.tabIndex >= 0 && !el.disabled);
                if (event.shiftKey && (event.target === stops[0] || event.target === node) && index > 0) {
                    event.preventDefault(); focus(index - 1);
                } else if (!event.shiftKey && (event.target === stops.at(-1) || event.target === node) && index < items.length - 1) {
                    event.preventDefault(); focus(index + 1);
                }
            }
        });
        // Capture handles Jellyfin's nested page scroller as well as document scrolling.
        global.addEventListener('scroll', schedule, true); global.addEventListener('resize', schedule);
        const resize = global.ResizeObserver ? new global.ResizeObserver(schedule) : null; resize?.observe(rail);
        return {
            load, reset, refreshItem, update: schedule,
            snapshot: () => ({ items: items.map(item => ({ ...item })), page, max, anchor: rail.scrollLeft / stride, failed, enabled }),
            restore(saved) {
                reset(); items = saved.items.map(item => ({ ...item })); seen = new Set(items.map(key));
                page = saved.page; max = saved.max; failed = saved.failed; enabled = saved.enabled;
                const ticket = generation;
                onItems(items.filter(item => !field(item, 'Title') || field(item, 'Title') === 'Loading title…'), () => valid(ticket), refreshItem);
                // Layout must exist before the browser can accept a nonzero scroll offset.
                paint(); rail.scrollLeft = saved.anchor * stride; announce(); paint(); schedule();
                if (enabled && !page && !failed) load();
            },
            seed(source) { reset(); enabled = true; try { accept(source, 1); } catch (_) { failed = true; announce(); paint(); } },
            dispose() { reset(); stopped = true; if (frame !== null) global.cancelAnimationFrame(frame); listeners.forEach(remove => remove()); resize?.disconnect(); global.removeEventListener('scroll', schedule, true); global.removeEventListener('resize', schedule); status.replaceChildren(); }
        };
    }
    global.ThreePicFinContinuousRail = { create };
    if (typeof module !== 'undefined' && module.exports) module.exports = { create };
})(typeof window === 'undefined' ? globalThis : window);
