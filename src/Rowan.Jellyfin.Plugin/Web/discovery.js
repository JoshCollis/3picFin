/* Host loads this public asset once and calls mount(fragmentRoot, authenticatedApiClient). */
(function (global) {
    'use strict';
    const instances = new WeakMap();
    const field = (item, name) => item?.[name] ?? item?.[name[0].toLowerCase() + name.slice(1)];
    const list = value => Array.isArray(value) ? value : [];
    const validPoster = value => typeof value === 'string' && /^\/[a-zA-Z0-9_/-]+\.(?:jpg|jpeg|png|webp)$/.test(value) && !value.includes('..') && !value.startsWith('//');
    const label = type => type === 'tv' ? 'TV' : type === 'movie' ? 'Movie' : 'Media';
    // Seerr v3.4.1 server/constants/media.ts, MediaRequestStatus (not MediaStatus).
    const requestStatus = status => ({ 1: 'Pending', 2: 'Approved', 3: 'Declined', 4: 'Failed', 5: 'Completed' })[status] ?? 'Unknown';
    function text(parent, tag, value, className) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        node.textContent = String(value);
        parent.appendChild(node);
        return node;
    }
    function message(container, value) { container.replaceChildren(); text(container, 'p', value, 'threepic-fin-discovery__message'); }
    function card(item, request, openRequest, openDetails) {
        const article = document.createElement('article');
        article.className = 'threepic-fin-discovery__card';
        const type = field(item, request ? 'MediaType' : 'MediaType') || (request ? field(item, 'Type') : null);
        const title = field(item, 'Title');
        const tmdbId = field(item, 'TmdbId');
        const poster = field(item, 'PosterPath');
        const canInspect = !request && (type === 'movie' || type === 'tv') && Number.isInteger(tmdbId) && tmdbId > 0;
        const posterHost = canInspect ? document.createElement('button') : document.createElement('div');
        posterHost.className = canInspect ? 'threepic-fin-discovery__poster-button' : 'threepic-fin-discovery__poster-frame';
        if (canInspect) { posterHost.type = 'button'; posterHost.setAttribute('aria-label', `Details for ${typeof title === 'string' && title.trim() ? title : label(type)}`); posterHost.addEventListener('click', () => openDetails(item, posterHost)); }
        article.appendChild(posterHost);
        if (validPoster(poster)) {
            const image = document.createElement('img');
            image.src = `https://image.tmdb.org/t/p/w342${poster}`;
            image.alt = '';
            image.loading = 'lazy';
            image.addEventListener('error', () => {
                posterHost.replaceChildren();
                text(posterHost, 'div', 'Artwork unavailable', 'threepic-fin-discovery__poster-fallback');
            });
            posterHost.appendChild(image);
        } else text(posterHost, 'div', 'Artwork unavailable', 'threepic-fin-discovery__poster-fallback');
        const info = document.createElement('div');
        info.className = 'threepic-fin-discovery__card-info';
        const displayTitle = typeof title === 'string' && title.trim() ? title : `${label(type)}${Number.isInteger(tmdbId) && tmdbId > 0 ? ` · TMDb #${tmdbId}` : ' · details unavailable'}`;
        if (canInspect) { const heading = document.createElement('h4'); const link = text(heading, 'button', displayTitle, 'threepic-fin-discovery__title-button'); link.type = 'button'; link.addEventListener('click', () => openDetails(item, link)); info.appendChild(heading); }
        else text(info, 'h4', displayTitle);
        if (title) text(info, 'p', label(type));
        if (request) text(info, 'p', `Request status: ${requestStatus(field(item, 'Status'))}`);
        else if (field(item, 'Date')) text(info, 'p', field(item, 'Date'));
        // Inspect first: eligibility and request state belong in the verified details dialog.
        article.appendChild(info);
        return article;
    }
    function render(container, source, empty, request = false, openRequest = null, openDetails = null) {
        container.replaceChildren();
        if (!source || field(source, 'Error') || !Array.isArray(field(source, 'Items'))) {
            message(container, `${empty} unavailable right now.`); return;
        }
        const items = list(field(source, 'Items'));
        if (!items.length) { message(container, `No ${empty} found.`); return; }
        for (const item of items) container.appendChild(card(item, request, openRequest, openDetails));
    }
    // A nested Arr title is not proof of display-safe metadata: refuse release/file-shaped values.
    function safeDownloadTitle(value) {
        if (typeof value !== 'string' || !value.trim() || value.length > 200 || /[\\/\\\\]/.test(value)) return false;
        const title = value.trim();
        // Dotted release basenames are ambiguous even without known tags/extensions. Keep
        // ordinary abbreviations (Dr. Strangelove, S.W.A.T.) but fall back for joined words.
        if (/(?:^|[^a-z0-9])(?:[a-z0-9]{2,}\.[a-z0-9]+)(?:[^a-z0-9]|$)/i.test(title)) return false;
        // Reject filename extensions and release markers as whole tokens regardless of
        // trailing punctuation/copy suffixes; never let an unrecognized suffix disclose them.
        if (/(?:^|[^a-z0-9])(?:mkv|mp4|m4v|avi|mov|wmv|ts|iso|torrent|nzb|rar|zip|7z|part|remux|hevc|480p|720p|1080p|2160p|4k|web[-.]?dl|web[-.]?rip|bluray|brrip|hdtv|s\d{1,2}e\d{1,3}|x26[45]|h26[45])(?=$|[^a-z0-9])/i.test(title)) return false;
        return true;
    }
    function renderDownloads(container, source, kind) {
        container.replaceChildren();
        if (!source || field(source, 'Error') || !Array.isArray(field(source, 'Items'))) {
            message(container, `${kind} downloads unavailable right now.`); return;
        }
        const items = field(source, 'Items');
        if (!items.length) message(container, `No ${kind} downloads right now.`);
        for (const item of items) {
            const row = document.createElement('article');
            row.className = 'threepic-fin-discovery__download';
            const title = field(item, 'Title');
            const safeTitle = safeDownloadTitle(title);
            const id = field(item, 'TitleId');
            text(row, 'h4', safeTitle ? title.trim() : `${kind === 'Movie' ? 'Movie' : 'TV'}${Number.isInteger(id) && id > 0 ? ` · ${kind === 'Movie' ? 'TMDb' : 'TVDb'} #${id}` : ' · title unavailable'}`);
            const state = field(item, 'State');
            const progress = field(item, 'Progress');
            const detail = ['Downloading', 'Paused', 'Queued', 'Completed', 'Unknown'].includes(state) ? state : 'Unknown';
            text(row, 'p', `${detail}${typeof progress === 'number' && Number.isFinite(progress) && progress >= 0 && progress <= 1 ? ` · ${Math.round(progress * 100)}%` : ''} · title-wide`);
            container.appendChild(row);
        }
        if (field(source, 'Partial') === true) text(container, 'p', 'Partial results — more titles may be downloading.', 'threepic-fin-discovery__message');
    }
    function renderCalendar(container, source, kind) {
        container.replaceChildren();
        if (!source || field(source, 'Error') || !Array.isArray(field(source, 'Items'))) {
            message(container, `${kind} calendar unavailable right now.`); return;
        }
        const items = field(source, 'Items');
        if (!items.length) message(container, `No ${kind} calendar events in this window.`);
        for (const item of items.slice(0, 100)) {
            const row = document.createElement('article');
            row.className = 'threepic-fin-discovery__download';
            const id = field(item, 'TitleId'), title = field(item, 'Title');
            const identity = `${kind}${Number.isInteger(id) && id > 0 ? ` · ${kind === 'Movie' ? 'TMDb' : 'TVDb'} #${id}` : ' · title unavailable'}`;
            text(row, 'h4', safeDownloadTitle(title) ? title.trim() : identity);
            const event = field(item, 'EventType'), date = field(item, 'Date');
            const validDate = typeof date === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|\+00:00)$/.test(date) && !Number.isNaN(Date.parse(date));
            const eventLabel = kind === 'Movie' && ['Cinema', 'Digital', 'Physical'].includes(event) ? event : kind === 'TV' && event === 'Episode' ? 'Episode' : 'Event';
            const season = field(item, 'SeasonNumber'), episode = field(item, 'EpisodeNumber');
            const number = kind === 'TV' && Number.isInteger(season) && season >= 0 && season <= 999 && Number.isInteger(episode) && episode >= 0 && episode <= 999 ? ` · S${String(season).padStart(2, '0')}E${String(episode).padStart(2, '0')}` : '';
            const episodeTitle = field(item, 'EpisodeTitle');
            const detail = kind === 'TV' && eventLabel === 'Episode' ? ` · ${safeDownloadTitle(episodeTitle) ? episodeTitle.trim() : 'episode title unavailable'}` : '';
            text(row, 'p', `${eventLabel}${number}${detail} · ${validDate ? date.slice(0, 10) + ' UTC' : 'date unavailable'} · title-wide`);
            container.appendChild(row);
        }
        if (field(source, 'Partial') === true || items.length > 100) text(container, 'p', 'Partial results — more events may exist.', 'threepic-fin-discovery__message');
    }
    function mount(root, ApiClient, host = {}) {
        const { deferInitialLoad = false } = host;
        instances.get(root)?.();
        const el = name => root.querySelector(`#threepic-fin-${name}`);
        const controls = [], railControls = [];
        let disposed = false, discoveryGeneration = 0, searchGeneration = 0, sharedGeneration = 0, downloadsGeneration = 0, downloadsAbort = null;
        let calendarGeneration = 0, calendarAbort = null, calendarOffset = 0, calendarLoading = false;
        const calendarEpoch = Date.parse(new Date().toISOString().slice(0, 10) + 'T00:00:00Z');
        let sharedPage = 1, sharedMax = 1;
        let requestListGeneration = 0, sharedListGeneration = 0;
        const metadata = new Map(), metadataAbort = new AbortController();
        // Request rows contain media identity, not TMDb presentation metadata. Only
        // this signed-in, mapped route may resolve titles; never use a public TMDb key.
        async function requestCards(container, source, empty, shared = false) {
            const generation = shared ? ++sharedListGeneration : ++requestListGeneration;
            if (!source || field(source, 'Error') || !Array.isArray(field(source, 'Items'))) {
                render(container, source, empty, true); return;
            }
            const original = field(source, 'Items');
            const rows = original.map(item => ({ ...item }));
            const paint = () => {
                if (disposed || generation !== (shared ? sharedListGeneration : requestListGeneration)) return;
                render(container, { Items: rows }, empty, true);
            };
            for (const row of rows) {
                const type = field(row, 'MediaType') || field(row, 'Type'), id = field(row, 'TmdbId');
                if (type === 'movie' || type === 'tv') row.Title = Number.isInteger(id) && id > 0 ? 'Loading title…' : 'Title unavailable';
            }
            paint();
            // Bound fan-out per mounted user. A title may appear in both personal
            // and opt-in household lists; share only within this mount, never across users.
            const pending = rows.filter(row => ['movie', 'tv'].includes(field(row, 'MediaType') || field(row, 'Type')) && Number.isInteger(field(row, 'TmdbId')) && field(row, 'TmdbId') > 0);
            let next = 0;
            async function worker() {
                while (!disposed && next < pending.length) {
                    const row = pending[next++], type = field(row, 'MediaType') || field(row, 'Type'), id = field(row, 'TmdbId');
                    const key = `${type}:${id}`;
                    if (!metadata.has(key)) metadata.set(key, ApiClient.getJSON(ApiClient.getUrl('3picFin/TitleDetails', { mediaType: type, mediaId: id }), { signal: metadataAbort.signal }).catch(() => null));
                    const detail = await metadata.get(key);
                    if (disposed || generation !== (shared ? sharedListGeneration : requestListGeneration)) return;
                    row.Title = field(detail, 'MediaType') === type && field(detail, 'TmdbId') === id && typeof field(detail, 'Title') === 'string' && field(detail, 'Title').trim() ? field(detail, 'Title') : 'Title unavailable';
                    row.PosterPath = row.Title !== 'Title unavailable' ? field(detail, 'PosterPath') : null;
                    paint();
                }
            }
            await Promise.all(Array.from({ length: Math.min(3, pending.length) }, worker));
        }
        const pages = { movies: 1, tv: 1, requests: 1 };
        const maxima = { movies: 1, tv: 1, requests: 1 };
        let searchPage = 1, searchQuery = '', searchMax = 1;
        let requestGeneration = 0, selection = null, options = null, submitting = false, locked = false, personal = null, trigger = null;
        const dialog = el('request-dialog');
        const detailsDialog = el('details-dialog');
        let detailsGeneration = 0, detailsAbort = null, detailsItem = null, detailsTrigger = null, detailsBusy = false;
        function invalidateDetails() { ++detailsGeneration; detailsAbort?.abort(); detailsAbort = null; detailsBusy = false; detailsItem = null; }
        function closeDetails(restore = true) {
            invalidateDetails();
            if (detailsDialog.open) detailsDialog.close();
            if (restore && !disposed) detailsTrigger?.focus();
            detailsTrigger = null;
        }
        async function openDetails(item, button) {
            if (disposed || detailsBusy) return;
            const mediaType = field(item, 'MediaType'), mediaId = field(item, 'TmdbId');
            if (!['movie', 'tv'].includes(mediaType) || !Number.isInteger(mediaId) || mediaId <= 0) return;
            invalidateDetails();
            const generation = detailsGeneration;
            detailsAbort = new AbortController(); detailsBusy = true; detailsTrigger = button;
            el('details-body').replaceChildren(); el('details-seasons').replaceChildren();
            el('details-title').textContent = field(item, 'Title') || label(mediaType);
            el('details-meta').textContent = ''; el('details-overview').textContent = '';
            el('details-status').textContent = 'Loading details…';
            el('details-open').hidden = true; el('details-request').hidden = true; el('details-request').disabled = true;
            if (!detailsDialog.open) detailsDialog.showModal();
            try {
                const result = await ApiClient.getJSON(ApiClient.getUrl('3picFin/TitleDetails', { mediaType, mediaId }), { signal: detailsAbort.signal });
                if (disposed || generation !== detailsGeneration || !detailsDialog.open || host.isCurrent && !host.isCurrent()) return;
                if (field(result, 'MediaType') !== mediaType || field(result, 'TmdbId') !== mediaId) throw Error('Invalid details');
                detailsItem = item;
                const title = field(result, 'Title'), overview = field(result, 'Overview'), poster = field(result, 'PosterPath');
                el('details-title').textContent = typeof title === 'string' && title.trim() ? title : field(item, 'Title') || label(mediaType);
                el('details-overview').textContent = typeof overview === 'string' && overview.trim() ? overview : 'No overview available.';
                if (validPoster(poster)) {
                    const image = document.createElement('img'); image.src = `https://image.tmdb.org/t/p/w500${poster}`; image.alt = '';
                    image.addEventListener('error', () => image.remove());
                    el('details-body').appendChild(image);
                }
                const date = field(result, 'Date') || field(result, 'ReleaseDate') || field(result, 'FirstAirDate');
                el('details-meta').textContent = `${label(mediaType)}${typeof date === 'string' && /^\d{4}-\d\d-\d\d$/.test(date) ? ` · ${date}` : ''}`;
                const libraryId = field(result, 'LibraryItemId');
                const available = typeof libraryId === 'string' && /^(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12})$/.test(libraryId);
                const state = field(result, 'MediaStatus');
                el('details-status').textContent = available ? typeof host.openItem === 'function' ? 'Available in your library' : 'Available in your library · opening unavailable here' : ({ 2: 'Pending', 3: 'Requested', 4: 'Partially available', 5: 'Reported available · not in your library', 6: 'Blocklisted' })[state] || 'Not requested';
                const seasons = field(result, 'Seasons');
                if (mediaType === 'tv' && Array.isArray(seasons) && seasons.length <= 100) {
                    for (const n of seasons) if (Number.isInteger(n) && n > 0 && n <= 1000) text(el('details-seasons'), 'span', `Season ${n}`, 'threepic-fin-discovery__season');
                }
                // Only this authenticated, user-visible server match can be opened. Never infer it from the catalog.
                el('details-open').hidden = !available || typeof host.openItem !== 'function';
                el('details-open').onclick = available && typeof host.openItem === 'function' ? async () => {
                    if (disposed || detailsItem !== item || detailsBusy || !detailsDialog.open) return;
                    const ticket = detailsGeneration;
                    detailsBusy = true; el('details-open').disabled = true;
                    try {
                        const opened = await host.openItem({ mediaType, mediaId, libraryItemId: libraryId });
                        if (disposed || ticket !== detailsGeneration || !detailsDialog.open) return;
                        if (opened === true) closeDetails(false);
                        else el('details-status').textContent = 'Library item changed or unavailable. Reopen details to check again.';
                    } catch (_) {
                        if (!disposed && ticket === detailsGeneration && detailsDialog.open) el('details-status').textContent = 'Unable to open library item right now.';
                    } finally {
                        if (ticket === detailsGeneration) { detailsBusy = false; el('details-open').disabled = false; }
                    }
                } : null;
                const canNormal = field(result, 'CanRequest') === true && state !== 5 && !(mediaType === 'movie' && available);
                const can4k = field(result, 'CanRequest4k') === true;
                const hasSeasons = mediaType !== 'tv' || Array.isArray(seasons) && seasons.some(n => Number.isInteger(n) && n > 0 && n <= 1000);
                const requestable = state !== 6 && hasSeasons && (canNormal || can4k);
                const action = el('details-request');
                action.hidden = state === 6 || !hasSeasons && state !== 5;
                action.disabled = !requestable;
                action.textContent = !requestable && state === 5 || !requestable && mediaType === 'movie' && available ? 'Available' :
                    !requestable ? 'Request unavailable' : mediaType === 'tv' && state === 4 ? 'Request more' :
                    mediaType === 'tv' ? 'Request seasons' : canNormal ? 'Request' : 'Request 4K';
            } catch (_) {
                if (disposed || generation !== detailsGeneration || !detailsDialog.open || host.isCurrent && !host.isCurrent()) return;
                el('details-status').textContent = 'Details unavailable right now.';
                el('details-open').hidden = true; el('details-request').hidden = true;
            } finally { if (generation === detailsGeneration) { detailsBusy = false; detailsAbort = null; } }
        }
        const status = value => { el('request-status').textContent = value; };
        function updateRequestStatus() {
            const mediaStatus = field(options, el('request-4k').checked ? 'MediaStatus4k' : 'MediaStatus');
            const unavailable = field(options, 'MediaStatus') === 6 || mediaStatus === 5 ||
                !(el('request-4k').checked ? field(options, 'CanRequest4k') : field(options, 'CanRequest'));
            el('request-submit').disabled = unavailable;
            status(unavailable ? mediaStatus === 5 ? 'This version is reported available. No request can be submitted.' : 'This request option is unavailable.' :
                `Select ${selection.mediaType === 'tv' ? 'seasons and ' : 'and '}confirm request.`);
        }
        // Declined/completed requests do not reserve seasons in Seerr v3.4.1; movies may be resubmitted.
        const matching = (item, chosen) => ![3, 5].includes(field(item, 'Status')) && field(item, 'TmdbId') === chosen.mediaId &&
            (field(item, 'MediaType') || field(item, 'Type')) === chosen.mediaType && field(item, 'Is4k') === chosen.is4k &&
            (chosen.mediaType === 'movie' || chosen.seasons.every(n => list(field(item, 'Seasons')).includes(n)));
        const existing = (source, chosen) => {
            if (!source || field(source, 'Error') || !Array.isArray(field(source, 'Items'))) return null;
            return list(field(source, 'Items')).find(item => matching(item, chosen)) || null;
        };
        function closeRequest() {
            if (submitting) return;
            ++requestGeneration;
            if (dialog.open) dialog.close();
            trigger?.focus(); trigger = null;
            selection = null; options = null;
        }
        async function openRequest(item, button) {
            if (disposed || submitting || host.isCurrent && !host.isCurrent() || host.userId && ApiClient.getCurrentUserId?.() !== host.userId) return;
            const generation = ++requestGeneration;
            trigger = button;
            const mediaType = field(item, 'MediaType'), mediaId = field(item, 'TmdbId');
            selection = { mediaType, mediaId };
            options = null; locked = false;
            el('request-title').textContent = `Request ${field(item, 'Title') || label(mediaType)}`;
            el('request-submit').disabled = true;
            el('request-seasons').replaceChildren(); el('request-seasons').hidden = true;
            text(el('request-seasons'), 'legend', 'Select seasons to request');
            el('request-4k').checked = false; el('request-4k-wrap').hidden = true;
            status('Checking Seerr permissions and availability…');
            if (!dialog.open) dialog.showModal();
            try {
                const result = await ApiClient.getJSON(ApiClient.getUrl('3picFin/RequestOptions', { mediaType, mediaId }));
                if (disposed || generation !== requestGeneration || !dialog.open || host.isCurrent && !host.isCurrent() || host.userId && ApiClient.getCurrentUserId?.() !== host.userId) return;
                if (typeof field(result, 'CanRequest') !== 'boolean' || typeof field(result, 'CanRequest4k') !== 'boolean' ||
                    !Array.isArray(field(result, 'Seasons'))) throw Error('Invalid options');
                options = result;
                const canRequest = field(result, 'CanRequest'), can4k = field(result, 'CanRequest4k');
                el('request-4k-wrap').hidden = !can4k;
                el('request-4k').checked = !canRequest && can4k;
                const seasons = field(result, 'Seasons');
                if (mediaType === 'tv') {
                    const valid = seasons.length <= 100 && seasons.every(n => Number.isInteger(n) && n > 0 && n <= 1000) && new Set(seasons).size === seasons.length;
                    if (!valid) throw Error('Invalid seasons');
                    el('request-seasons').hidden = false;
                    for (const n of seasons) {
                        const labelNode = document.createElement('label'), checkbox = document.createElement('input');
                        checkbox.type = 'checkbox'; checkbox.value = String(n);
                        labelNode.appendChild(checkbox); text(labelNode, 'span', `Season ${n}`);
                        el('request-seasons').appendChild(labelNode);
                    }
                }
                if (field(result, 'MediaStatus') === 6) {
                    status('This title is blocklisted. No request can be submitted.'); return;
                }
                if (!canRequest && !can4k || mediaType === 'tv' && !seasons.length) {
                    status('Request unavailable for this title or account.'); return;
                }
                updateRequestStatus();
            } catch (_) {
                if (disposed || generation !== requestGeneration || !dialog.open || host.isCurrent && !host.isCurrent() || host.userId && ApiClient.getCurrentUserId?.() !== host.userId) return;
                status('Request details unavailable. No request was sent.');
                el('request-submit').disabled = true;
            }
        }
        async function submitRequest(event) {
            event.preventDefault();
            if (disposed || host.isCurrent && !host.isCurrent() || host.userId && ApiClient.getCurrentUserId?.() !== host.userId || !dialog.open || !selection || !options || submitting || locked || el('request-submit').disabled) return;
            const is4k = !el('request-4k-wrap').hidden && el('request-4k').checked;
            if (field(options, 'MediaStatus') === 6 || field(options, is4k ? 'MediaStatus4k' : 'MediaStatus') === 5) {
                status('This version is unavailable for requests.'); el('request-submit').disabled = true; return;
            }
            if (!(is4k ? field(options, 'CanRequest4k') : field(options, 'CanRequest'))) {
                status('This request option is not permitted.'); return;
            }
            const chosen = { ...selection, is4k };
            if (chosen.mediaType === 'tv') {
                chosen.seasons = Array.from(el('request-seasons').children).filter(node => node.tagName === 'LABEL' && node.children[0].checked).map(node => Number(node.children[0].value));
                if (!chosen.seasons.length) { status('Choose at least one season.'); return; }
            }
            if (existing(personal, chosen)) { status('Already requested. Check My Requests.'); el('request-submit').disabled = true; return; }
            const generation = requestGeneration;
            submitting = true; locked = true; el('request-submit').disabled = true; status('Submitting request…');
            let created = null, failure = null;
            try {
                created = await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('3picFin/Requests'), data: JSON.stringify(chosen), contentType: 'application/json', dataType: 'json' });
            } catch (error) { failure = error; }
            try {
                const fresh = await ApiClient.getJSON(ApiClient.getUrl('3picFin/Discovery', { moviePage: 1, tvPage: 1, requestsPage: 1 }));
                if (disposed || generation !== requestGeneration) return;
                personal = field(fresh, 'Requests');
                const items = !field(personal, 'Error') && Array.isArray(field(personal, 'Items')) ? field(personal, 'Items') : [];
                // Seerr may remove already-requested/available TV seasons; the created ID is authoritative.
                const createdId = field(created, 'Id');
                const verified = Number.isInteger(createdId) && createdId > 0 && items.find(item => field(item, 'Id') === createdId);
                const found = existing(personal, chosen);
                if (verified) {
                    status(`Request ${requestStatus(field(verified, 'Status'))}. Check My Requests for updates.`);
                    requestCards(el('requests'), personal, 'Requests');
                } else if (found && !created) {
                    status('A matching request appears in My Requests, but this submission could not be verified. Do not retry without checking it.');
                    requestCards(el('requests'), personal, 'Requests');
                } else if (failure?.status === 409) status('Already requested. Check My Requests; the matching request was not verified here.');
                else status('Outcome unknown: could not verify the request. Check My Requests before trying again.');
            } catch (_) {
                if (!disposed && generation === requestGeneration) status('Outcome unknown: could not verify the request. Check My Requests before trying again.');
            } finally { submitting = false; }
        }
        const on = (element, event, handler) => { element.addEventListener(event, handler); controls.push(() => element.removeEventListener(event, handler)); };
        // Keep a page of cards in one Home-style row. Rail arrows move within
        // that page; the existing pager fetches the next bounded server page.
        for (const name of ['requests', 'shared-requests', 'recommendations', 'search-results', 'movies', 'tv']) {
            const rail = el(name), section = rail.closest('section'), heading = section.querySelector('h3');
            const header = document.createElement('div');
            header.className = 'threepic-fin-discovery__row-heading';
            heading.before(header); header.appendChild(heading);
            const navigation = document.createElement('div');
            navigation.className = 'threepic-fin-discovery__rail-nav';
            for (const [direction, caption] of [[-1, 'left'], [1, 'right']]) {
                const button = document.createElement('button');
                button.type = 'button'; button.textContent = direction < 0 ? '‹' : '›';
                button.setAttribute('aria-label', `Scroll ${name === 'search-results' ? 'search results' : name === 'shared-requests' ? 'household requests' : name} ${caption}`);
                navigation.appendChild(button);
                on(button, 'click', () => rail.scrollBy({ left: direction * rail.clientWidth * .8, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' }));
            }
            header.appendChild(navigation);
            const update = () => {
                const overflow = rail.scrollWidth > rail.clientWidth + 2;
                navigation.hidden = !overflow;
                navigation.children[0].disabled = !overflow || rail.scrollLeft <= 1;
                navigation.children[1].disabled = !overflow || rail.scrollLeft + rail.clientWidth >= rail.scrollWidth - 2;
            };
            on(rail, 'scroll', update);
            const observer = new MutationObserver(() => { rail.scrollLeft = 0; requestAnimationFrame(update); });
            observer.observe(rail, { childList: true });
            controls.push(() => observer.disconnect());
            railControls.push({ header, heading, update });
            requestAnimationFrame(update);
        }
        function pager(prefix, page, max) {
            el(`${prefix}-prev`).disabled = page <= 1;
            el(`${prefix}-next`).disabled = page >= max;
            el(`${prefix}-page`).textContent = `Page ${page} of ${max}`;
        }
        function maxPages(source) {
            const n = field(source, 'TotalPages');
            return Number.isInteger(n) && n > 0 ? Math.min(n, 100) : 1;
        }
        async function sharedRequests(page) {
            const generation = ++sharedGeneration;
            sharedPage = page;
            message(el('shared-requests'), 'Loading household requests…');
            pager('shared-requests', page, 1);
            try {
                const result = await ApiClient.getJSON(ApiClient.getUrl('3picFin/SharedRequests', { page }));
                if (disposed || generation !== sharedGeneration) return;
                requestCards(el('shared-requests'), result, 'Household requests', true);
                sharedMax = field(result, 'Error') || !Array.isArray(field(result, 'Items')) ? 1 : maxPages(result);
                pager('shared-requests', page, sharedMax);
            } catch (_) {
                if (disposed || generation !== sharedGeneration) return;
                message(el('shared-requests'), 'Household requests unavailable or not enabled.');
                sharedMax = 1;
                pager('shared-requests', page, 1);
            }
        }
        async function discovery() {
            const generation = ++discoveryGeneration;
            for (const name of ['movies', 'tv', 'requests', 'recommendations']) message(el(name), 'Loading…');
            for (const name of Object.keys(pages)) pager(name, pages[name], 1);
            try {
                const result = await ApiClient.getJSON(ApiClient.getUrl('3picFin/Discovery', { moviePage: pages.movies, tvPage: pages.tv, requestsPage: pages.requests }));
                if (disposed || generation !== discoveryGeneration) return;
                const movies = field(result, 'Movies'), tv = field(result, 'Tv'), requests = field(result, 'Requests');
                personal = requests;
                render(el('movies'), movies, 'Movies', false, openRequest, openDetails);
                render(el('tv'), tv, 'TV', false, openRequest, openDetails);
                requestCards(el('requests'), requests, 'Requests');
                // A small sample from the current discovery pages, not personalized recommendations.
                const suggestions = [...list(field(movies, 'Items')).slice(0, 2), ...list(field(tv, 'Items')).slice(0, 2)];
                render(el('recommendations'), { Items: suggestions, Error: field(movies, 'Error') && field(tv, 'Error') }, 'titles', false, openRequest, openDetails);
                for (const [name, source] of Object.entries({ movies, tv, requests })) {
                    maxima[name] = field(source, 'Error') || !Array.isArray(field(source, 'Items')) ? 1 : maxPages(source);
                    pager(name, pages[name], maxima[name]);
                }
            } catch (_) {
                if (disposed || generation !== discoveryGeneration) return;
                for (const name of ['movies', 'tv', 'requests', 'recommendations']) message(el(name), 'Content unavailable right now.');
                for (const name of Object.keys(pages)) pager(name, pages[name], 1);
            }
        }
        async function search(page) {
            const generation = ++searchGeneration;
            searchPage = page;
            message(el('search-results'), 'Loading search results…');
            pager('search', page, 1);
            try {
                const result = await ApiClient.getJSON(ApiClient.getUrl('3picFin/Search', { query: searchQuery, page }));
                if (disposed || generation !== searchGeneration) return;
                render(el('search-results'), result, 'Search results', false, openRequest, openDetails);
                searchMax = maxPages(result);
                pager('search', page, searchMax);
            } catch (_) {
                if (disposed || generation !== searchGeneration) return;
                message(el('search-results'), 'Search results unavailable right now.');
                pager('search', page, 1);
            }
        }
        function stopDownloads() { ++downloadsGeneration; downloadsAbort?.abort(); downloadsAbort = null; }
        function stopCalendar() {
            ++calendarGeneration;
            calendarAbort?.abort(); calendarAbort = null;
            calendarLoading = false;
            calendarWindow();
        }
        function selectView(view) {
            if (el(`${view}-tab`).getAttribute('aria-selected') === 'true' && view !== 'discover') return;
            if (view !== 'downloads') stopDownloads();
            if (view !== 'calendar') stopCalendar();
            for (const name of ['discover', 'downloads', 'calendar']) {
                el(`${name}-panel`).hidden = name !== view;
                el(`${name}-tab`).setAttribute('aria-selected', String(name === view));
                el(`${name}-tab`).tabIndex = name === view ? 0 : -1;
            }
            if (view === 'downloads') loadDownloads();
            if (view === 'calendar') loadCalendar();
        }
        function calendarWindow() {
            const start = new Date(calendarEpoch + calendarOffset * 31 * 86400000).toISOString().slice(0, 10);
            const end = new Date(calendarEpoch + (calendarOffset + 1) * 31 * 86400000).toISOString().slice(0, 10);
            el('calendar-window').textContent = `${start} to ${end} (end exclusive, UTC)`;
            el('calendar-prev').disabled = calendarLoading || calendarOffset <= -12;
            el('calendar-next').disabled = calendarLoading || calendarOffset >= 12;
            return { start, end };
        }
        async function loadCalendar() {
            if (disposed || calendarLoading || el('calendar-panel').hidden) return;
            calendarLoading = true;
            const generation = ++calendarGeneration;
            calendarAbort = new AbortController();
            const window = calendarWindow();
            for (const name of ['radarr', 'sonarr']) message(el(`calendar-${name}`), 'Loading calendar…');
            try {
                const response = await ApiClient.getJSON(ApiClient.getUrl('3picFin/Calendar', window), { signal: calendarAbort.signal });
                if (disposed || generation !== calendarGeneration) return;
                if (field(field(response, 'Radarr'), 'Error') === 'Disabled' && field(field(response, 'Sonarr'), 'Error') === 'Disabled') {
                    el('calendar-tab').hidden = true;
                    selectView('discover'); el('discover-tab').focus(); return;
                }
                renderCalendar(el('calendar-radarr'), field(response, 'Radarr'), 'Movie');
                renderCalendar(el('calendar-sonarr'), field(response, 'Sonarr'), 'TV');
            } catch (error) {
                if (disposed || generation !== calendarGeneration) return;
                if (error?.status === 404 || error?.statusCode === 404) {
                    el('calendar-tab').hidden = true;
                    selectView('discover'); el('discover-tab').focus(); return;
                }
                for (const name of ['radarr', 'sonarr']) message(el(`calendar-${name}`), 'Calendar unavailable right now. Return to Discover and try again.');
            } finally {
                if (generation === calendarGeneration) {
                    calendarAbort = null;
                    calendarLoading = false;
                    calendarWindow();
                }
            }
        }
        async function loadDownloads() {
            stopDownloads();
            const generation = downloadsGeneration;
            downloadsAbort = new AbortController();
            for (const name of ['radarr', 'sonarr']) message(el(`downloads-${name}`), 'Loading downloads…');
            try {
                const response = await ApiClient.getJSON(ApiClient.getUrl('3picFin/Downloads'), { signal: downloadsAbort.signal });
                if (disposed || generation !== downloadsGeneration) return;
                if (field(field(response, 'Radarr'), 'Error') === 'Disabled' && field(field(response, 'Sonarr'), 'Error') === 'Disabled') {
                    el('downloads-tab').hidden = true;
                    selectView('discover');
                    el('discover-tab').focus();
                    return;
                }
                renderDownloads(el('downloads-radarr'), field(response, 'Radarr'), 'Movie');
                renderDownloads(el('downloads-sonarr'), field(response, 'Sonarr'), 'TV');
            } catch (error) {
                if (disposed || generation !== downloadsGeneration) return;
                if (error?.status === 404 || error?.statusCode === 404) {
                    el('downloads-tab').hidden = true;
                    selectView('discover');
                    el('discover-tab').focus();
                    return;
                }
                for (const name of ['radarr', 'sonarr']) message(el(`downloads-${name}`), 'Downloads unavailable right now. Return to Discover and try again.');
            } finally { if (generation === downloadsGeneration) downloadsAbort = null; }
        }
        on(el('discover-tab'), 'click', () => selectView('discover'));
        on(el('downloads-tab'), 'click', () => { if (!el('downloads-tab').hidden) selectView('downloads'); });
        on(el('calendar-tab'), 'click', () => { if (!el('calendar-tab').hidden) selectView('calendar'); });
        on(root.querySelector('.threepic-fin-discovery__tabs'), 'keydown', event => {
            if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
            const tabs = ['discover', 'downloads', 'calendar'].filter(name => !el(`${name}-tab`).hidden);
            const index = tabs.findIndex(name => el(`${name}-tab`) === document.activeElement);
            if (index < 0) return;
            event.preventDefault();
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
            el(`${tabs[next]}-tab`).focus(); selectView(tabs[next]);
        });
        on(el('calendar-prev'), 'click', () => { if (!calendarLoading && calendarOffset > -12 && !el('calendar-panel').hidden) { --calendarOffset; loadCalendar(); } });
        on(el('calendar-next'), 'click', () => { if (!calendarLoading && calendarOffset < 12 && !el('calendar-panel').hidden) { ++calendarOffset; loadCalendar(); } });
        on(el('request-form'), 'submit', submitRequest);
        on(el('details-close'), 'click', () => closeDetails());
        on(detailsDialog, 'cancel', () => { invalidateDetails(); detailsTrigger?.focus(); detailsTrigger = null; });
        on(detailsDialog, 'close', () => { invalidateDetails(); detailsTrigger?.focus(); detailsTrigger = null; });
        on(el('details-request'), 'click', () => { if (!detailsItem || detailsBusy || el('details-request').hidden || el('details-request').disabled) return; const item = detailsItem, button = detailsTrigger; closeDetails(false); openRequest(item, button); });
        on(el('request-4k'), 'change', () => { if (options && selection && !submitting && !locked) updateRequestStatus(); });
        on(el('request-cancel'), 'click', closeRequest);
        on(el('shared-requests-load'), 'click', () => sharedRequests(1));
        on(el('shared-requests-prev'), 'click', () => { if (sharedPage > 1) sharedRequests(sharedPage - 1); });
        on(el('shared-requests-next'), 'click', () => { if (sharedPage < sharedMax) sharedRequests(sharedPage + 1); });
        on(dialog, 'cancel', event => { if (submitting) event.preventDefault(); else { ++requestGeneration; trigger?.focus(); selection = null; options = null; } });
        on(dialog, 'close', () => { if (!submitting) { ++requestGeneration; trigger?.focus(); trigger = null; selection = null; options = null; } });
        on(el('search-form'), 'submit', e => {
            e.preventDefault();
            const query = el('search').value.trim();
            if (!query || query.length > 200) return;
            searchQuery = query; search(1);
        });
        on(el('search-prev'), 'click', () => { if (searchPage > 1) search(searchPage - 1); });
        on(el('search-next'), 'click', () => { if (searchPage < searchMax) search(searchPage + 1); });
        for (const name of Object.keys(pages)) {
            on(el(`${name}-prev`), 'click', () => { if (pages[name] > 1) { --pages[name]; discovery(); } });
            on(el(`${name}-next`), 'click', () => { if (pages[name] < maxima[name]) { ++pages[name]; discovery(); } });
        }
        pager('search', 1, 1);
        pager('shared-requests', 1, 1);
        let activated = false;
        const activate = () => {
            if (disposed || activated) return;
            activated = true;
            discovery();
        };
        if (!deferInitialLoad) activate();
        const cleanup = () => {
            if (disposed) return;
            disposed = true; metadataAbort.abort(); metadata.clear(); ++requestListGeneration; ++sharedListGeneration; closeDetails(false); stopDownloads(); stopCalendar(); ++searchGeneration; ++discoveryGeneration; ++requestGeneration; ++sharedGeneration;
            for (const name of ['movies', 'tv', 'requests', 'recommendations', 'shared-requests', 'search-results', 'downloads-radarr', 'downloads-sonarr', 'calendar-radarr', 'calendar-sonarr']) el(name).replaceChildren();
            el('calendar-panel').hidden = true;
            if (dialog.open) dialog.close();
            controls.forEach(remove => remove());
            for (const { header, heading } of railControls) { header.before(heading); header.remove(); }
            instances.delete(root);
        };
        cleanup.activate = activate;
        // Search reuses this exact dialog/options/POST/read-back controller; the
        // caller owns the fragment and must dispose it on route/user teardown.
        cleanup.openRequest = (item, button) => {
            if (!disposed && ApiClient.getCurrentUserId?.() === host.userId) openRequest(item, button);
        };
        cleanup.openDetails = (item, button) => {
            if (!disposed && ApiClient.getCurrentUserId?.() === host.userId &&
                (!host.isCurrent || host.isCurrent())) openDetails(item, button);
        };
        instances.set(root, cleanup);
        return cleanup;
    }
    global.ThreePicFinDiscovery = { mount, cleanup(root) { instances.get(root)?.(); } };
})(typeof window === 'undefined' ? globalThis : window);
