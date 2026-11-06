// Private snapshots are never displayed until the server validates access and revision.
window.racinageTreeLoading = window.racinageTreeLoading || (() => {
    const database = 'racinage-tree-snapshots';
    const maximumBytes = 12 * 1024 * 1024;
    const maximumEntryBytes = 4 * 1024 * 1024;
    const maximumAge = 86400000;
    const mounted = new Map();
    const encoder = new TextEncoder();
    const popups = new Map();
    let popupFrame = 0;
    const popupBlocked = key => {
        const tree = document.querySelector('.tree_container');
        return Boolean(tree && (tree.dataset.treeDeferred === 'true' || tree.hasAttribute('data-tree-layout-pending')))
            || (!key.startsWith('onboarding') && Boolean(window.RacinageOnboarding?.isPending?.()));
    };
    function deferPopup(key, show) {
        if (!popupBlocked(key)) return false;
        // One callback per producer; never retain an obsolete tree page.
        popups.set(key, { show, tree: document.querySelector('.tree_container') });
        return true;
    }
    function flushPopups() {
        cancelAnimationFrame(popupFrame);
        popupFrame = requestAnimationFrame(() => {
            for (const [key, pending] of [...popups].sort(([a], [b]) => Number(b.startsWith('onboarding')) - Number(a.startsWith('onboarding')))) {
                if (pending.tree && pending.tree !== document.querySelector('.tree_container')) { popups.delete(key); continue; }
                if (popupBlocked(key)) continue;
                popups.delete(key);
                pending.show();
            }
        });
    }
    document.addEventListener('racinage:tree-ready', flushPopups);
    document.addEventListener('racinage:popups-ready', flushPopups);
    window.addEventListener('racinage:onboarding-closed', flushPopups);
    const digest = async (html, stats) => Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(html + '\n' + stats))), b => b.toString(16).padStart(2, '0')).join('');

    async function storage(namespace, entry = null, key = '') {
        if (!namespace || !window.indexedDB || !window.crypto?.subtle) return null;
        return new Promise(resolve => {
            let settled = false;
            const finish = value => { if (!settled) { settled = true; clearTimeout(timer); resolve(value); } };
            const timer = setTimeout(() => finish(null), 1000);
            let request;
            try { request = indexedDB.open(database, 1); } catch (_) { finish(null); return; }
            request.onupgradeneeded = () => request.result.createObjectStore('snapshots', { keyPath: 'key' });
            request.onerror = request.onblocked = () => finish(null);
            request.onsuccess = () => {
                const db = request.result;
                if (settled) { db.close(); return; }
                db.onversionchange = () => db.close();
                const tx = db.transaction('snapshots', 'readwrite');
                const store = tx.objectStore('snapshots');
                let result = null;
                tx.oncomplete = () => { db.close(); finish(result); };
                tx.onabort = tx.onerror = () => { db.close(); finish(null); };
                const all = store.getAll();
                all.onsuccess = () => {
                    // The allowance is shared across all trees and sessions on this origin.
                    const rows = all.result.filter(row => {
                        const valid = row.namespace === namespace && row.savedAt > Date.now() - maximumAge
                            && Number.isFinite(row.bytes) && row.bytes <= maximumEntryBytes;
                        if (!valid) store.delete(row.key);
                        return valid;
                    });
                    if (entry && entry.bytes <= maximumEntryBytes) {
                        const old = rows.findIndex(row => row.key === entry.key);
                        if (old >= 0) rows.splice(old, 1);
                        rows.push(entry);
                        store.put(entry);
                    }
                    rows.sort((a, b) => b.savedAt - a.savedAt);
                    let bytes = 0;
                    rows.forEach((row, index) => {
                        bytes += row.bytes;
                        if (index >= 3 || bytes > maximumBytes) store.delete(row.key);
                        else if (row.key === key) result = row;
                    });
                };
            };
        });
    }

    async function load(container, initialize) {
        if (mounted.get(container)?.loading) return;
        const controller = new AbortController();
        const state = { controller, loading: true };
        mounted.set(container, state);
        const current = () => container.isConnected && mounted.get(container) === state && !controller.signal.aborted;
        const retry = container.querySelector('[data-tree-load-retry]');
        if (retry) { retry.hidden = true; retry.onclick = () => load(container, initialize); }
        container.setAttribute('aria-busy', 'true');
        const label = container.querySelector('.tree_loading_state span');
        if (label) label.textContent = container.dataset.treeLoadingLabel;
        const namespace = container.dataset.treeCacheNamespace;
        const key = [namespace, container.dataset.treeId, container.dataset.partialTreeId, container.dataset.partialTreeTemporary].join(':');
        try {
            let cached = await storage(namespace, null, key);
            if (!current()) return;
            if (cached && (typeof cached.html !== 'string' || typeof cached.stats !== 'string' || await digest(cached.html, cached.stats) !== cached.digest)) cached = null;
            const body = new FormData();
            body.set('action', 'get_tree_state');
            body.set('tree_long_id', container.dataset.treeId);
            body.set('partial_id', container.dataset.partialTreeId || '');
            body.set('partial_temp', container.dataset.partialTreeTemporary || '');
            body.set('tree_preference_csrf', container.dataset.treePreferenceCsrf || '');
            body.set('snapshot_cache', '1');
            if (cached) { body.set('cache_token', cached.token); body.set('cache_digest', cached.digest); }
            const response = await fetch(container.dataset.treeSnapshotUrl, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            const payload = await response.json();
            if (!current()) return;
            if (!response.ok || !payload.ok || (payload.not_modified && !cached)) throw new Error('load');
            const html = payload.not_modified ? cached.html : payload.tree_html;
            const stats = payload.not_modified ? cached.stats : payload.stats_html;
            if (typeof html !== 'string' || typeof stats !== 'string') throw new Error('load');
            container.querySelector('.tree').innerHTML = html;
            const statsBox = document.querySelector('.wrapper_stats');
            if (statsBox) statsBox.innerHTML = stats;
            delete container.dataset.treeDeferred;
            container.removeAttribute('aria-busy');
            state.loading = false;
            initialize();
            document.dispatchEvent(new CustomEvent('racinage:tree-loaded', { detail: { container } }));
            if (!payload.not_modified && payload.cache_token) {
                const bytes = encoder.encode(html + stats + payload.cache_token).byteLength;
                if (bytes <= maximumEntryBytes) void storage(namespace, { key, namespace, html, stats, bytes, token: payload.cache_token, digest: payload.cache_digest, savedAt: Date.now() });
            }
        } catch (_) {
            if (!current()) return;
            if (label) label.textContent = container.dataset.treeLoadingError;
            if (retry) retry.hidden = false;
            container.setAttribute('aria-busy', 'false');
        } finally { state.loading = false; }
    }

    function viewport(container) {
        let state = mounted.get(container);
        if (!state) { state = {}; mounted.set(container, state); }
        state.observer?.disconnect();
        // Responsive row transitions must finish before their retained size is read.
        container.classList.add('tree_viewport_measuring');
        container.querySelectorAll('.tree_card_offscreen').forEach(card => card.classList.remove('tree_card_offscreen'));
        const finishMeasuring = () => container.classList.remove('tree_viewport_measuring');
        const vertical = container.classList.contains('vertical');
        if (!window.IntersectionObserver || (vertical && !CSS.supports('content-visibility', 'hidden'))) { finishMeasuring(); return; }
        const cards = [...container.querySelectorAll(vertical ? '.v_node_header' : '.tree .one_person')];
        if ((vertical ? container.querySelectorAll('.tree .one_person').length : cards.length) < 150) { finishMeasuring(); return; }
        // Read all dimensions before writing. Preserve card boxes, branches and connectors.
        const dimensions = vertical ? cards.map(card => [card.offsetWidth, card.offsetHeight]) : [];
        finishMeasuring();
        state.observer = new IntersectionObserver(entries => {
            entries.forEach(entry => {
                entry.target.classList.toggle('tree_card_offscreen', !entry.isIntersecting && !entry.target.contains(document.activeElement));
                if (entry.isIntersecting) entry.target.querySelectorAll('img[loading="lazy"]').forEach(img => {
                    // Fetch nearby card media after stable layout, leaving distant
                    // images lazy without removing any relationship geometry.
                    img.loading = 'eager';
                });
            });
        }, { root: vertical ? container.querySelector('.v_tree_container') : container, rootMargin: '600px' });
        cards.forEach((card, index) => {
            if (vertical) card.style.setProperty('--tree-card-height', dimensions[index][1] + 'px');
            state.observer.observe(card);
        });
    }
    function dispose(container) {
        const state = mounted.get(container);
        state?.controller?.abort();
        state?.observer?.disconnect();
        mounted.delete(container);
    }
    document.addEventListener('htmx:beforeCleanupElement', event => {
        for (const container of mounted.keys()) if (event.detail.elt === container || event.detail.elt?.contains(container)) dispose(container);
        for (const [key, pending] of popups) if (pending.tree && (event.detail.elt === pending.tree || event.detail.elt?.contains(pending.tree))) popups.delete(key);
    });
    window.addEventListener('pagehide', () => { for (const container of mounted.keys()) dispose(container); });
    window.addEventListener('beforeprint', () => {
        for (const [container, state] of mounted) {
            state.observer?.disconnect();
            container.querySelectorAll('.tree_card_offscreen').forEach(card => card.classList.remove('tree_card_offscreen'));
        }
    });
    window.addEventListener('afterprint', () => { for (const container of mounted.keys()) viewport(container); });
    document.addEventListener('click', event => {
        const link = event.target.closest?.('a[href]');
        if (link && new URL(link.href, location.href).pathname.replace(/\/$/, '').endsWith('/logout')) {
            try { indexedDB.deleteDatabase(database); } catch (_) { /* Storage may be disabled. */ }
        }
    }, true);
    return { load, viewport, deferPopup };
})();
