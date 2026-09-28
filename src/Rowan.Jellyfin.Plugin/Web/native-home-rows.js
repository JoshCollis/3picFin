/* Supplementary user-scoped rows. Never relocates Jellyfin's .sections. */
(function (global) {
    'use strict';
    const allowed = ['ContinueWatching', 'NextUp', 'LatestMovies', 'LatestShows', 'MyMedia',
        'ContinueWatchingNextUp', 'Collections', 'BecauseYouWatched', 'MyRequests'];
    const field = (item, key) => item?.[key] ?? item?.[key[0].toLowerCase() + key.slice(1)];
    const guid = id => typeof id === 'string' && /^(?:[a-f\d]{32}|[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12})$/i.test(id);
    const title = name => ({ ContinueWatchingNextUp: 'Continue Watching / Next Up',
        BecauseYouWatched: 'Because You Watched' })[name] || name.replace(/([a-z])([A-Z])/g, '$1 $2');
    function createRows({ document, IntersectionObserver, enabledRows = [], openItem } = {}) {
        const kinds = [...new Set(enabledRows)].filter(name => allowed.includes(name)).slice(0, 9);
        let current = null, generation = 0;
        function dispose() {
            ++generation;
            if (!current) return;
            current.observer.disconnect();
            current.cleanup();
            current.root.replaceChildren();
            current = null;
        }
        function mount(root, apiClient, userId) {
            dispose();
            if (!root || !userId || !kinds.length || typeof apiClient?.getUrl !== 'function' ||
                typeof apiClient?.getJSON !== 'function' || !IntersectionObserver) return false;
            const ticket = generation;
            const active = () => ticket === generation && current?.userId === userId &&
                (!apiClient.getCurrentUserId || apiClient.getCurrentUserId() === userId);
            const makeCard = (item, landscape = false) => {
                const id = field(item, 'Id'), type = field(item, 'Type');
                if (!guid(id) || !['Movie', 'Series', 'Episode', 'BoxSet', 'Folder', 'CollectionFolder'].includes(type)) return null;
                const playable = ['Movie', 'Series', 'Episode'].includes(type);
                const card = document.createElement('div');
                card.className = 'rowan-native-row__card card card-hoverable show-animation ' +
                    (landscape ? 'rowan-native-row__card--landscape overflowBackdropCard' :
                        'rowan-native-row__card--portrait overflowPortraitCard');
                card.setAttribute('data-id', id);
                card.setAttribute('data-type', type);
                const serverId = apiClient.serverId?.();
                if (typeof serverId === 'string' && serverId) card.setAttribute('data-serverid', serverId);
                card.setAttribute('data-isfolder', String(['BoxSet', 'Folder', 'CollectionFolder'].includes(type)));
                if (playable) card.setAttribute('data-mediatype', 'Video');
                if (typeof openItem === 'function' && ['Movie', 'Series', 'Episode', 'BoxSet'].includes(type)) {
                    card.tabIndex = 0;
                    card.setAttribute('role', 'button');
                    const open = event => {
                        if (!active() || event?.target?.closest?.('.cardOverlayContainer, .cardOverlayButton')) return;
                        openItem({ Id: id, Type: type });
                    };
                    card.addEventListener('click', open);
                    card.addEventListener('keydown', event => {
                        if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(event); }
                    });
                }
                // Mirror the native card hierarchy; the scoped CSS only supplies image sizing.
                const box = document.createElement('div'); box.className = 'cardBox';
                const scalable = document.createElement('div'); scalable.className = 'cardScalable';
                const padder = document.createElement('div');
                padder.className = 'cardPadder ' + (landscape ? 'cardPadder-overflowBackdrop' : 'cardPadder-overflowPortrait');
                const content = document.createElement('div'); content.className = 'cardContent cardImageContainer';
                scalable.append(padder, content);
                box.appendChild(scalable); card.appendChild(box);
                if (serverId) {
                    const overlay = document.createElement('div');
                    overlay.className = 'cardOverlayContainer itemAction';
                    overlay.setAttribute('data-action', 'link');
                    if (playable) {
                        const play = document.createElement('button'); play.type = 'button';
                        play.className = 'cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light cardOverlayFab-primary';
                        play.setAttribute('data-action', 'resume'); play.setAttribute('title', 'Play');
                        const icon = document.createElement('span'); icon.className = 'material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover play_arrow';
                        icon.setAttribute('aria-hidden', 'true'); play.appendChild(icon); overlay.appendChild(play);
                    }
                    const corner = document.createElement('div'); corner.className = 'cardOverlayButton-br flex';
                    const menu = document.createElement('button'); menu.type = 'button';
                    menu.className = 'cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light';
                    menu.setAttribute('data-action', 'menu'); menu.setAttribute('title', 'More');
                    const icon = document.createElement('span'); icon.className = 'material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover more_vert';
                    icon.setAttribute('aria-hidden', 'true'); menu.appendChild(icon); corner.appendChild(menu);
                    overlay.appendChild(corner); scalable.appendChild(overlay);
                }
                const tags = field(item, 'ImageTags');
                const backdrop = field(item, 'BackdropImageTags');
                const validTag = tag => typeof tag === 'string' && /^[0-9a-f]{1,64}$/i.test(tag);
                const art = landscape ? [
                    [id, 'Thumb', field(tags, 'Thumb')],
                    [field(item, 'SeriesId'), 'Thumb', field(item, 'SeriesThumbImageTag')],
                    [field(item, 'ParentThumbItemId'), 'Thumb', field(item, 'ParentThumbImageTag')],
                    [field(item, 'SeriesId'), 'Backdrop/0', field(item, 'SeriesBackdropImageTag')],
                    [id, 'Backdrop/0', Array.isArray(backdrop) ? backdrop[0] : null],
                    [field(item, 'ParentBackdropItemId'), 'Backdrop/0', field(item, 'ParentBackdropImageTag')],
                    [id, 'Primary', field(tags, 'Primary')]
                ] : [[id, 'Primary', field(tags, 'Primary')]];
                // Only use IDs and tags supplied by the scoped DTO; never construct URLs from names.
                const candidates = art.filter(([imageId, , tag]) => guid(imageId) && validTag(tag));
                if (candidates.length) {
                    const image = document.createElement('img');
                    let index = 0;
                    const next = () => {
                        if (!active() || index >= candidates.length) {
                            image.remove(); card.classList.add('rowan-native-row__card--no-art'); return;
                        }
                        const [imageId, imageType, tag] = candidates[index++];
                        image.src = apiClient.getUrl(`Items/${imageId}/Images/${imageType}`, { tag, maxWidth: landscape ? 480 : 240 });
                    };
                    image.alt = ''; image.loading = 'lazy';
                    image.addEventListener('error', next);
                    content.appendChild(image);
                    next();
                } else card.classList.add('rowan-native-row__card--no-art');
                const label = document.createElement('div'); label.className = 'cardText cardTextCentered';
                label.textContent = String(field(item, 'Name') || 'Untitled').slice(0, 180);
                box.appendChild(label); return card;
            };
            const carousels = [];
            const addControls = (section, track, label) => {
                track.tabIndex = 0;
                track.setAttribute?.('role', 'region');
                track.setAttribute?.('aria-label', label);
                const controls = document.createElement('div'); controls.className = 'rowan-native-row__controls';
                const buttons = [-1, 1].map(direction => {
                    const button = document.createElement('button'); button.type = 'button';
                    button.className = 'rowan-native-row__arrow';
                    button.textContent = direction < 0 ? '‹' : '›';
                    button.setAttribute?.('aria-label', `${direction < 0 ? 'Previous' : 'Next'} ${label}`);
                    button.addEventListener('click', () => { if (active()) move(direction); });
                    controls.appendChild(button); return button;
                });
                const update = () => {
                    controls.hidden = track.scrollWidth <= track.clientWidth + 1;
                    buttons[0].disabled = track.scrollLeft <= 1;
                    buttons[1].disabled = track.scrollLeft + track.clientWidth >= track.scrollWidth - 1;
                };
                const move = direction => track.scrollBy({ left: direction * track.clientWidth, behavior: 'smooth' });
                track.addEventListener('scroll', update);
                track.addEventListener('keydown', event => {
                    if (!active() || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
                    event.preventDefault(); move(event.key === 'ArrowRight' ? 1 : -1);
                });
                section.appendChild(controls);
                carousels.push(update);
                update();
            };
            const bodies = [];
            const sections = kinds.map(kind => {
                const section = document.createElement('section'); section.className = 'rowan-native-row';
                const body = document.createElement('div', 'emby-itemscontainer');
                body.className = kind === 'BecauseYouWatched' ? 'rowan-native-row__seeds' : 'rowan-native-row__items itemsContainer';
                if (kind !== 'BecauseYouWatched') {
                    body.setAttribute('is', 'emby-itemscontainer');
                    body.setAttribute('data-multiselect', 'false');
                }
                if (kind !== 'BecauseYouWatched') {
                    const heading = document.createElement('h2'); heading.textContent = title(kind);
                    section.appendChild(heading);
                }
                section.appendChild(body);
                if (kind !== 'BecauseYouWatched') addControls(section, body, title(kind));
                bodies.push(body);
                root.appendChild(section); return section;
            });
            const resize = () => carousels.forEach(update => update());
            document.defaultView?.addEventListener('resize', resize);
            const requested = new Set(), attempts = new Map();
            const observer = new IntersectionObserver(entries => {
                for (const entry of entries) {
                    const index = sections.indexOf(entry.target);
                    if (!entry.isIntersecting || index < 0 || requested.has(index) || !active() ||
                        (attempts.get(index) || 0) >= 2) continue;
                    requested.add(index);
                    attempts.set(index, (attempts.get(index) || 0) + 1);
                    const section = sections[index], body = bodies[index], kind = kinds[index];
                    body.textContent = 'Loading…';
                    const path = kind === 'BecauseYouWatched' ? 'Rowan/Home/BecauseYouWatched' : `Rowan/Home/Rows/${kind}`;
                    Promise.resolve().then(() => active() ? apiClient.getJSON(apiClient.getUrl(path)) : null)
                        .then(row => {
                            if (!active()) return;
                            if (kind === 'BecauseYouWatched') {
                                if (!Array.isArray(row) || row.length > 5) throw Error('Invalid row');
                                const groups = row.map(seed => {
                                    const items = field(seed, 'Items'), headingText = field(seed, 'Heading');
                                    if (!Array.isArray(items) || items.length > 16 || typeof headingText !== 'string' ||
                                        !headingText.startsWith('Because You Watched ')) throw Error('Invalid seed');
                                    const group = document.createElement('section'); group.className = 'rowan-native-row__seed';
                                    const heading = document.createElement('h3'); heading.textContent = headingText.slice(0, 180);
                                    const cards = document.createElement('div', 'emby-itemscontainer');
                                    cards.className = 'rowan-native-row__items itemsContainer';
                                    cards.setAttribute('is', 'emby-itemscontainer');
                                    cards.setAttribute('data-multiselect', 'false');
                                    for (const item of items) { const card = makeCard(item, true); if (card) cards.appendChild(card); }
                                    group.append(heading, cards); addControls(group, cards, heading.textContent); return group;
                                });
                                body.replaceChildren(...groups);
                                resize();
                                section.hidden = !groups.length;
                                return;
                            }
                            const items = field(row, 'Items');
                            if (field(row, 'Error') || !Array.isArray(items) || items.length > 64 || field(row, 'Kind') !== kind)
                                throw Error('Invalid row');
                            const cards = items.map(item => makeCard(item, ['ContinueWatching', 'NextUp', 'ContinueWatchingNextUp'].includes(kind))).filter(Boolean);
                            body.replaceChildren(...cards);
                            resize();
                            if (!cards.length) {
                                section.hidden = true;
                                body.replaceChildren(); body.textContent = '';
                                return;
                            }
                            section.hidden = false;
                        }).catch(() => {
                            if (active()) { requested.delete(index); body.textContent = 'Row unavailable'; }
                        });
                }
            });
            current = { observer, root, userId, cleanup: () => document.defaultView?.removeEventListener('resize', resize) };
            sections.forEach(section => observer.observe(section));
            return true;
        }
        return { mount, dispose };
    }
    global.RowanNativeHomeRows = { createRows };
    if (typeof module !== 'undefined' && module.exports) module.exports = { createRows };
})(typeof globalThis !== 'undefined' ? globalThis : this);
