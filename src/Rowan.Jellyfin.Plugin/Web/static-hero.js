/* Isolated Home hero: host owns mounting and teardown; never modifies HSS sections. */
var RowanStaticHero = (() => {
    const guid = /^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
    function mount(root, api) {
        let disposed = false, slides = [], index = 0, imageGeneration = 0, imageUrl = null, imageRequest = null;
        const panel = document.createElement('section');
        panel.className = 'rowan-static-hero';
        panel.setAttribute('aria-label', 'Featured media');
        const image = document.createElement('img');
        image.alt = '';
        const title = document.createElement('h2');
        const description = document.createElement('p');
        const previous = document.createElement('button');
        const next = document.createElement('button');
        const counter = document.createElement('span');
        previous.type = next.type = 'button';
        previous.textContent = 'Previous'; next.textContent = 'Next';
        counter.setAttribute('aria-live', 'polite');
        panel.append(image, title, description, previous, next, counter);
        root.append(panel);
        function render() {
            if (!sameSession()) { cleanup(); return; }
            if (!slides.length) return;
            const slide = slides[index];
            title.textContent = slide.name || '';
            description.textContent = slide.overview || '';
            loadImage(slide);
            counter.textContent = `${index + 1} / ${slides.length}`;
            previous.disabled = next.disabled = slides.length < 2;
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
        function back() { index = (index + slides.length - 1) % slides.length; render(); }
        function forward() { index = (index + 1) % slides.length; render(); }
        previous.addEventListener('click', back);
        next.addEventListener('click', forward);
        const userId = api.getCurrentUserId?.();
        const token = api.accessToken?.();
        function sameSession() { return !disposed && !!userId && !!token && api.getCurrentUserId?.() === userId && api.accessToken?.() === token; }
        const monitor = setInterval(() => { if (!sameSession()) cleanup(); }, 500);
        function cleanup() {
            if (disposed) return;
            disposed = true;
            ++imageGeneration;
            clearImage();
            clearInterval(monitor);
            previous.removeEventListener('click', back);
            next.removeEventListener('click', forward);
            root.replaceChildren();
        }
        if (!sameSession()) { cleanup(); return cleanup; }
        const url = api.getUrl('Rowan/Home/Hero');
        api.getJSON(url).then(items => {
            if (!sameSession()) { cleanup(); return; }
            slides = (Array.isArray(items) ? items : []).map(item => item && ({
                id: item.Id ?? item.id, name: item.Name ?? item.name,
                overview: item.Overview ?? item.overview,
                imageType: item.ImageType ?? item.imageType,
                imageIndex: item.ImageIndex ?? item.imageIndex,
                imageTag: item.ImageTag ?? item.imageTag
            })).filter(item => item && guid.test(item.id)
                && item.imageType === 'Backdrop' && item.imageIndex === 0
                && typeof item.imageTag === 'string' && /^[0-9a-f]{1,64}$/i.test(item.imageTag)).slice(0, 12);
            if (slides.length) render(); else root.replaceChildren();
        }).catch(cleanup);
        return cleanup;
    }
    return { mount };
})();
