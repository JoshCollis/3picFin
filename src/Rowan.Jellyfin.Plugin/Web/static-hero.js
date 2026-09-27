/* Isolated Home hero: host owns mounting and teardown; never modifies HSS sections. */
var RowanStaticHero = (() => {
    const guid = /^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
    function mount(root, api, options = {}) {
        let disposed = false, slides = [], index = 0, imageGeneration = 0, imageUrl = null, imageRequest = null, elapsed = 0;
        const clock = () => typeof performance !== 'undefined' ? performance.now() : Date.now();
        let lastTick = clock(), hovering = false, focused = false;
        const panel = document.createElement('section');
        panel.className = 'rowan-static-hero';
        panel.setAttribute('aria-label', 'Featured media');
        panel.setAttribute('tabindex', '0');
        const image = document.createElement('img');
        image.alt = '';
        const title = document.createElement('h2');
        const description = document.createElement('p');
        const previous = document.createElement('button');
        const next = document.createElement('button');
        const counter = document.createElement('span');
        const pagination = document.createElement('nav');
        pagination.className = 'rowan-hero-pagination';
        pagination.setAttribute('aria-label', 'Featured slides');
        const progress = document.createElement('span');
        progress.className = 'rowan-hero-progress';
        progress.setAttribute('aria-hidden', 'true');
        const open = document.createElement('button');
        open.type = 'button'; open.className = 'rowan-hero-open'; open.textContent = 'Open';
        previous.type = next.type = 'button';
        previous.className = 'rowan-hero-previous'; next.className = 'rowan-hero-next';
        previous.textContent = 'Previous'; next.textContent = 'Next';
        counter.setAttribute('aria-live', 'polite');
        panel.append(image, title, description, previous, next, counter, pagination, progress, open);
        root.append(panel);
        function render() {
            if (!sameSession()) { cleanup(); return; }
            if (!slides.length) return;
            const slide = slides[index];
            title.textContent = slide.name || '';
            description.textContent = slide.overview || '';
            loadImage(slide);
            counter.textContent = `${index + 1} / ${slides.length}`;
            elapsed = 0;
            lastTick = clock();
            progress.style?.setProperty('--rowan-progress', '0%');
            if (pagination.children.length !== slides.length) {
                pagination.replaceChildren();
                slides.forEach((item, position) => {
                    const dot = document.createElement('button');
                    dot.type = 'button'; dot.className = 'rowan-hero-dot';
                    dot.setAttribute('aria-label', `Slide ${position + 1}: ${item.name || 'Featured title'}`);
                    dot.addEventListener('click', () => { if (sameSession() && index !== position) { index = position; render(); } });
                    pagination.append(dot);
                });
            }
            Array.from(pagination.children).forEach((dot, position) =>
                dot.setAttribute('aria-current', position === index ? 'true' : 'false'));
            const activeDot = pagination.children[index];
            if (activeDot && Number.isFinite(activeDot.offsetLeft) && pagination.clientWidth > 0) {
                pagination.scrollLeft = Math.max(0, activeDot.offsetLeft - pagination.offsetLeft
                    - (pagination.clientWidth - activeDot.clientWidth) / 2);
            }
            previous.disabled = next.disabled = slides.length < 2;
            for (const control of [previous, next, counter, pagination, progress]) control.hidden = slides.length < 2;
        }
        function clearImage() {
            imageRequest?.abort();
            imageRequest = null;
            image.removeAttribute('src');
            if (imageUrl) URL.revokeObjectURL(imageUrl);
            imageUrl = null;
        }
        async function loadImage(slide) {
            const generation = ++imageGeneration;
            clearImage();
            imageRequest = typeof AbortController === 'function' ? new AbortController() : null;
            try {
                const url = api.getUrl(`Rowan/Home/Hero/Image/${slide.id}`, { tag: slide.imageTag });
                const response = await api.fetch({ url, type: 'GET', headers: { accept: 'image/jpeg' }, ...(imageRequest ? { signal: imageRequest.signal } : {}) });
                if (!response.ok || response.headers?.get?.('content-type')?.split(';')[0] !== 'image/jpeg') return;
                const blob = await response.blob();
                if (!sameSession() || generation !== imageGeneration) return;
                imageUrl = URL.createObjectURL(blob);
                image.src = imageUrl;
            } catch { /* Leave background plain when image delivery fails. */ }
        }
        function back() { if (!sameSession() || slides.length < 2) return; index = (index + slides.length - 1) % slides.length; render(); }
        function forward() { if (!sameSession() || slides.length < 2) return; index = (index + 1) % slides.length; render(); }
        function onKey(event) {
            if (!sameSession() || slides.length < 2 || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
            const tag = event.target?.tagName?.toLowerCase();
            if (event.target !== panel && tag !== 'button') return;
            if (event.key === 'ArrowLeft') { event.preventDefault(); back(); }
            else if (event.key === 'ArrowRight') { event.preventDefault(); forward(); }
        }
        let touchStart = null;
        function onTouchStart(event) {
            touchStart = event.touches.length === 1
                ? { x: event.touches[0].clientX, y: event.touches[0].clientY } : null;
        }
        function onTouchEnd(event) {
            const start = touchStart;
            touchStart = null;
            if (!start || event.changedTouches.length !== 1 || event.touches?.length || !sameSession() || slides.length < 2) return;
            const dx = event.changedTouches[0].clientX - start.x;
            const dy = event.changedTouches[0].clientY - start.y;
            if (Math.abs(dx) < 60 || Math.abs(dx) < Math.abs(dy) * 1.5) return;
            if (dx < 0) forward(); else back();
        }
        previous.addEventListener('click', back);
        next.addEventListener('click', forward);
        const openItem = options.openItem ?? api.openItem;
        function onOpen() {
            if (sameSession() && slides[index] && typeof openItem === 'function') openItem(slides[index].id);
        }
        open.addEventListener('click', onOpen);
        open.hidden = typeof openItem !== 'function';
        panel.addEventListener('keydown', onKey);
        panel.addEventListener('touchstart', onTouchStart, { passive: true });
        panel.addEventListener('touchend', onTouchEnd, { passive: true });
        function onTouchCancel() { touchStart = null; }
        panel.addEventListener('touchcancel', onTouchCancel);
        const userId = api.getCurrentUserId?.();
        const token = api.accessToken?.();
        function sameSession() { return !disposed && !!userId && !!token && api.getCurrentUserId?.() === userId && api.accessToken?.() === token; }
        const monitor = setInterval(() => { if (!sameSession()) cleanup(); }, 500);
        const reduceMotion = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
        function isPaused() { return hovering || focused || document.hidden || reduceMotion; }
        function updateProgress() {
            const now = clock();
            if (!isPaused() && slides.length > 1) elapsed += Math.max(0, now - lastTick);
            lastTick = now;
            progress.style?.setProperty('--rowan-progress', `${Math.min(100, elapsed / 120)}%`);
            if (elapsed >= 12000 && !isPaused()) forward();
        }
        const advance = setInterval(() => {
            if (!sameSession()) { cleanup(); return; }
            updateProgress();
        }, 100);
        function onEnter() { updateProgress(); hovering = true; }
        function onLeave() { hovering = false; lastTick = clock(); }
        function onFocusIn() { updateProgress(); focused = true; }
        function onFocusOut(event) {
            if (event.relatedTarget && panel.contains(event.relatedTarget)) return;
            focused = false; lastTick = clock();
        }
        function onVisibility() { lastTick = clock(); }
        panel.addEventListener('mouseenter', onEnter);
        panel.addEventListener('mouseleave', onLeave);
        panel.addEventListener('focusin', onFocusIn);
        panel.addEventListener('focusout', onFocusOut);
        document.addEventListener?.('visibilitychange', onVisibility);
        function cleanup() {
            if (disposed) return;
            disposed = true;
            ++imageGeneration;
            clearImage();
            clearInterval(monitor);
            clearInterval(advance);
            panel.removeEventListener('mouseenter', onEnter);
            panel.removeEventListener('mouseleave', onLeave);
            panel.removeEventListener('focusin', onFocusIn);
            panel.removeEventListener('focusout', onFocusOut);
            document.removeEventListener?.('visibilitychange', onVisibility);
            previous.removeEventListener('click', back);
            next.removeEventListener('click', forward);
            open.removeEventListener('click', onOpen);
            panel.removeEventListener('keydown', onKey);
            panel.removeEventListener('touchstart', onTouchStart);
            panel.removeEventListener('touchend', onTouchEnd);
            panel.removeEventListener('touchcancel', onTouchCancel);
            touchStart = null;
            root.replaceChildren();
        }
        if (!sameSession()) { cleanup(); return cleanup; }
        const initial = Array.isArray(options.slides) ? Promise.resolve(options.slides) : api.getJSON(api.getUrl('Rowan/Home/Hero'));
        initial.then(items => {
            if (!sameSession()) { cleanup(); return; }
            slides = (Array.isArray(items) ? items : []).map(item => item && ({
                id: item.Id ?? item.id, name: item.Name ?? item.name,
                overview: item.Overview ?? item.overview,
                imageType: item.ImageType ?? item.imageType,
                imageIndex: item.ImageIndex ?? item.imageIndex,
                imageTag: item.ImageTag ?? item.imageTag
            })).filter(item => item && guid.test(item.id)
                && item.imageType === 'Backdrop' && item.imageIndex === 0
                && typeof item.imageTag === 'string' && /^[0-9a-f]{1,64}$/i.test(item.imageTag)).slice(0, 10);
            if (slides.length) render(); else root.replaceChildren();
        }).catch(cleanup);
        return cleanup;
    }
    return { mount };
})();
