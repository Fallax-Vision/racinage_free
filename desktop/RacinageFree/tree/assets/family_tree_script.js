
window.racinagePersonGallery = window.racinagePersonGallery || (() => {
    let activePerson = null;
    let activeItems = [];
    let activeCanManage = false;
    let activeIndex = 0;
    let returnFocus = null;
    let loadRevision = 0;

    const escapeHTML = (value) => String(value ?? '').replace(/[&<>"']/g, (char) => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;',
    })[char]);

    const shell = () => document.getElementById('personGalleryModal');
    const lightbox = () => document.getElementById('personGalleryLightbox');
    const dictionary = () => {
        try {
            return JSON.parse(shell()?.dataset.personGalleryI18n || '{}');
        } catch (error) {
            return {};
        }
    };
    const t = (key, fallback = key) => dictionary()[key] || fallback || key;
    const format = (key, value, fallback = key) => t(key, fallback).replace('%s', String(value));
    const personName = (person) => `${person?.first_name || ''} ${person?.last_name || ''}`.trim() || t('Person', 'Person');
    const summaryOf = (person) => ({
        count: Math.max(0, Number(person?.gallery_preview?.count || 0)),
        items: Array.isArray(person?.gallery_preview?.items) ? person.gallery_preview.items : [],
    });
    const personFromModal = (modal) => {
        try {
            return modal?.dataset.person ? JSON.parse(modal.dataset.person) : null;
        } catch (error) {
            return null;
        }
    };
    const mediaLabel = (item) => item.caption || item.original_name || t(item.type === 'video' ? 'Video' : 'Photo');
    const previewMedia = (item, name) => item.type === 'video'
        ? `<video src="${escapeHTML(item.thumb_url)}" preload="metadata" muted playsinline aria-label="${escapeHTML(mediaLabel(item))}"></video><span class="person_gallery_video_badge">${escapeHTML(t('Video'))}</span>`
        : `<img src="${escapeHTML(item.thumb_url)}" alt="${escapeHTML(item.caption || `${t('Photo')} - ${name}`)}" loading="lazy">`;

    const previewMarkup = ({ person } = {}) => {
        const localT = t;
        const summary = summaryOf(person);
        const name = personName(person);
        const canManage = person?.gallery_can_manage === true || person?.gallery_can_manage === 1;
        const items = summary.items.slice(0, 5);
        return `
            <section class="tree_person_gallery_section" data-person-gallery-preview>
                <div class="tree_person_gallery_heading">
                    <div>
                        <h4>${escapeHTML(localT('Private gallery', 'Private gallery'))}</h4>
                        <small>${escapeHTML(String(format('%s media files', summary.count, '%s media files')))}</small>
                    </div>
                    <div class="tree_person_gallery_actions">
                        ${canManage ? `<button type="button" class="tree_secondary_action" data-person-gallery-upload>${escapeHTML(localT('Add media', 'Add media'))}</button>` : ''}
                        ${summary.count > 0 ? `<button type="button" class="tree_primary_action" data-person-gallery-open>${escapeHTML(localT('View all', 'View all'))}</button>` : ''}
                    </div>
                </div>
                ${items.length ? `
                    <div class="tree_person_gallery_preview_grid">
                        ${items.map((item) => `
                            <button type="button" data-person-gallery-preview-media="${escapeHTML(item.id)}" aria-label="${escapeHTML(mediaLabel(item))}">
                                ${previewMedia(item, name)}
                            </button>
                        `).join('')}
                    </div>
                ` : `
                    <div class="tree_person_gallery_preview_empty">
                        <strong>${escapeHTML(localT('No media yet', 'No media yet'))}</strong>
                        <span>${escapeHTML(localT('Add private photos and videos for this person.', 'Add private photos and videos for this person.'))}</span>
                    </div>
                `}
            </section>
        `;
    };

    const request = async (url, options = {}) => {
        const response = await fetch(url, { credentials: 'same-origin', ...options });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok || !payload.ok) {
            throw new Error(payload.message || t('Unable to complete this action.'));
        }
        return payload;
    };

    const lockUnderlying = () => {
        const personModal = document.getElementById('treePersonModal');
        const galleryModal = shell();
        const lightboxOpen = lightbox()?.hidden === false;
        const locked = galleryModal?.classList.contains('open') || lightboxOpen;
        if (galleryModal) {
            galleryModal.inert = lightboxOpen;
            if (lightboxOpen) galleryModal.setAttribute('aria-hidden', 'true');
            else if (galleryModal.classList.contains('open')) galleryModal.setAttribute('aria-hidden', 'false');
        }
        if (!personModal) return;
        personModal.inert = Boolean(locked);
        if (locked) {
            personModal.setAttribute('aria-hidden', 'true');
        } else if (personModal.classList.contains('open')) {
            personModal.setAttribute('aria-hidden', 'false');
        }
    };

    const setFeedback = (message = '', error = false) => {
        const target = shell()?.querySelector('[data-person-gallery-feedback]');
        if (!target) return;
        target.textContent = message;
        target.classList.toggle('error', error);
    };

    const updateUnderlyingPreview = (summary) => {
        const personModal = document.getElementById('treePersonModal');
        if (!personModal || !activePerson) return;
        activePerson.gallery_preview = summary;
        activePerson.gallery_can_manage = activeCanManage;
        personModal.dataset.person = JSON.stringify(activePerson);
        const current = personModal.querySelector('[data-person-gallery-preview]');
        if (current) current.outerHTML = previewMarkup({ person: activePerson });
    };

    const renderGrid = () => {
        const modal = shell();
        const grid = modal?.querySelector('[data-person-gallery-grid]');
        const empty = modal?.querySelector('[data-person-gallery-empty]');
        const count = modal?.querySelector('[data-person-gallery-count]');
        const add = modal?.querySelector('[data-person-gallery-add]');
        if (!modal || !grid || !empty || !count) return;
        count.textContent = format('%s media files', activeItems.length, '%s media files');
        if (add) add.hidden = !activeCanManage;
        empty.hidden = activeItems.length > 0;
        grid.hidden = activeItems.length === 0;
        grid.innerHTML = activeItems.map((item, index) => `
            <article class="person_gallery_card">
                <button type="button" class="person_gallery_card_media" data-person-gallery-media-index="${index}" aria-label="${escapeHTML(mediaLabel(item))}">
                    ${previewMedia(item, personName(activePerson))}
                </button>
                <div class="person_gallery_card_meta">
                    <span title="${escapeHTML(mediaLabel(item))}">${escapeHTML(mediaLabel(item))}</span>
                    ${activeCanManage ? `<button type="button" data-person-gallery-delete="${escapeHTML(item.id)}">${escapeHTML(t('Remove media'))}</button>` : ''}
                </div>
            </article>
        `).join('');
    };

    const load = async () => {
        const modal = shell();
        if (!modal || !activePerson?.long_id) throw new Error(t('Unable to complete this action.'));
        const requestedPersonId = activePerson.long_id;
        const revision = ++loadRevision;
        setFeedback(t('Loading media...'));
        const query = new URLSearchParams({ action: 'list', person: requestedPersonId });
        const payload = await request(`${modal.dataset.personGalleryEndpoint}?${query}`);
        if (revision !== loadRevision || activePerson?.long_id !== requestedPersonId) return null;
        activeItems = Array.isArray(payload.items) ? payload.items : [];
        activeCanManage = payload.can_manage === true;
        setFeedback('');
        renderGrid();
        return activeItems;
    };

    const open = async (person, addAfterOpen = false, trigger = null) => {
        const modal = shell();
        if (!modal || !person?.long_id) return;
        activePerson = { ...person };
        activeItems = [];
        activeCanManage = person.gallery_can_manage === true || person.gallery_can_manage === 1;
        returnFocus = trigger || document.activeElement;
        modal.querySelector('#personGalleryTitle').textContent = format('Media for %s', personName(person), 'Media for %s');
        const add = modal.querySelector('[data-person-gallery-add]');
        if (add) add.hidden = !activeCanManage;
        renderGrid();
        modal.classList.add('open');
        modal.setAttribute('aria-hidden', 'false');
        lockUnderlying();
        modal.querySelector('[data-person-gallery-close]')?.focus();
        try {
            const items = await load();
            if (!items) return;
            if (addAfterOpen && activeCanManage) modal.querySelector('[data-person-gallery-input]')?.click();
        } catch (error) {
            setFeedback(error.message, true);
        }
    };

    const close = () => {
        const modal = shell();
        if (!modal) return;
        modal.classList.remove('open');
        modal.setAttribute('aria-hidden', 'true');
        loadRevision++;
        setFeedback('');
        lockUnderlying();
        if (returnFocus?.isConnected) returnFocus.focus();
    };

    const upload = async (files) => {
        const modal = shell();
        if (!modal || !activePerson?.long_id || !activeCanManage || !files?.length) return;
        if (files.length > 20) {
            setFeedback(t('Choose between 1 and 20 media files at a time.'), true);
            return;
        }
        const add = modal.querySelector('[data-person-gallery-add]');
        const requestedPersonId = activePerson.long_id;
        const body = new FormData();
        body.set('action', 'upload');
        body.set('person', requestedPersonId);
        body.set('csrf', modal.dataset.personGalleryCsrf || '');
        [...files].forEach((file) => body.append('media_files[]', file));
        if (add) add.disabled = true;
        setFeedback(t('Uploading media...'));
        try {
            const payload = await request(modal.dataset.personGalleryEndpoint, {
                method: 'POST',
                headers: { 'X-Racinage-CSRF': modal.dataset.personGalleryCsrf || '' },
                body,
            });
            if (activePerson?.long_id !== requestedPersonId) return;
            updateUnderlyingPreview(payload.summary || { count: activeItems.length, items: activeItems.slice(0, 5) });
            await load();
            setFeedback(payload.message || t('Media added to the private gallery.'));
        } catch (error) {
            setFeedback(error.message, true);
        } finally {
            if (add) add.disabled = false;
            const input = modal.querySelector('[data-person-gallery-input]');
            if (input) input.value = '';
        }
    };

    const remove = async (mediaId) => {
        const modal = shell();
        if (!modal || !activePerson?.long_id || !activeCanManage) return;
        if (!window.confirm(t('Remove this media file?'))) return;
        const requestedPersonId = activePerson.long_id;
        setFeedback('');
        try {
            const body = new URLSearchParams({
                action: 'delete',
                person: requestedPersonId,
                media: mediaId,
                csrf: modal.dataset.personGalleryCsrf || '',
            });
            const payload = await request(modal.dataset.personGalleryEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                    'X-Racinage-CSRF': modal.dataset.personGalleryCsrf || '',
                },
                body,
            });
            if (activePerson?.long_id !== requestedPersonId) return;
            activeItems = activeItems.filter((item) => item.id !== mediaId);
            setFeedback(payload.message || t('Media removed from the private gallery.'));
            updateUnderlyingPreview(payload.summary || { count: activeItems.length, items: activeItems.slice(0, 5) });
            renderGrid();
        } catch (error) {
            setFeedback(error.message, true);
        }
    };

    const showLightbox = (index) => {
        const overlay = lightbox();
        const stage = overlay?.querySelector('[data-person-gallery-lightbox-stage]');
        const caption = overlay?.querySelector('[data-person-gallery-lightbox-caption]');
        if (!overlay || !stage || !caption || !activeItems.length) return;
        activeIndex = ((Number(index) % activeItems.length) + activeItems.length) % activeItems.length;
        const item = activeItems[activeIndex];
        overlay.setAttribute('aria-label', mediaLabel(item));
        stage.innerHTML = item.type === 'video'
            ? `<video src="${escapeHTML(item.full_url)}" controls playsinline preload="metadata"></video>`
            : `<img src="${escapeHTML(item.full_url)}" alt="${escapeHTML(mediaLabel(item))}">`;
        caption.textContent = `${mediaLabel(item)} (${activeIndex + 1}/${activeItems.length})`;
        overlay.querySelectorAll('[data-person-gallery-lightbox-prev], [data-person-gallery-lightbox-next]').forEach((button) => {
            button.hidden = activeItems.length < 2;
        });
        overlay.hidden = false;
        lockUnderlying();
        overlay.querySelector('[data-person-gallery-lightbox-close]')?.focus();
    };

    const openPreviewMedia = async (person, mediaId, trigger = null) => {
        activePerson = { ...person };
        activeItems = [];
        activeCanManage = person.gallery_can_manage === true || person.gallery_can_manage === 1;
        returnFocus = trigger || document.activeElement;
        try {
            const items = await load();
            if (!items) return;
            const index = activeItems.findIndex((item) => item.id === mediaId);
            if (index >= 0) showLightbox(index);
        } catch (error) {
            window.alert(error.message);
        }
    };

    const closeLightbox = () => {
        const overlay = lightbox();
        if (!overlay) return;
        overlay.querySelector('video')?.pause();
        overlay.querySelector('[data-person-gallery-lightbox-stage]')?.replaceChildren();
        overlay.hidden = true;
        lockUnderlying();
        const mediaButton = shell()?.querySelector(`[data-person-gallery-media-index="${activeIndex}"]`);
        if (mediaButton?.isConnected && mediaButton.offsetParent !== null) mediaButton.focus();
        else if (returnFocus?.isConnected) returnFocus.focus();
    };

    const trapFocus = (container, event) => {
        if (event.key !== 'Tab') return;
        const focusable = [...container.querySelectorAll('button:not([hidden]):not([disabled]), input:not([hidden]):not([disabled]), [tabindex]:not([tabindex="-1"])')]
            .filter((element) => element.offsetParent !== null);
        if (!focusable.length) return;
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    };

    if (!window.racinagePersonGalleryBound) {
        window.racinagePersonGalleryBound = true;
        document.addEventListener('click', (event) => {
            const personModal = event.target.closest('#treePersonModal');
            const person = personFromModal(personModal);
            const profilePhoto = event.target.closest('[data-person-profile-photo]');
            if (profilePhoto && person) {
                ++loadRevision;
                activePerson = { ...person };
                activeCanManage = false;
                activeItems = [{ type: 'image', full_url: person.image_lg_url || person.image_url || person.image_sm_url,
                    caption: profilePhoto.getAttribute('aria-label') || personName(person) }];
                returnFocus = profilePhoto;
                showLightbox(0);
                return;
            }
            const openButton = event.target.closest('[data-person-gallery-open]');
            if (openButton && person) {
                open(person, false, openButton);
                return;
            }
            const uploadButton = event.target.closest('[data-person-gallery-upload]');
            if (uploadButton && person) {
                open(person, true, uploadButton);
                return;
            }
            const previewButton = event.target.closest('[data-person-gallery-preview-media]');
            if (previewButton && person) {
                openPreviewMedia(person, previewButton.dataset.personGalleryPreviewMedia, previewButton);
                return;
            }
            const modal = shell();
            if (event.target === modal || event.target.closest('[data-person-gallery-close]')) {
                close();
                return;
            }
            if (event.target.closest('[data-person-gallery-add]')) {
                if (activeCanManage) modal?.querySelector('[data-person-gallery-input]')?.click();
                return;
            }
            const mediaButton = event.target.closest('[data-person-gallery-media-index]');
            if (mediaButton) {
                returnFocus = mediaButton;
                showLightbox(Number(mediaButton.dataset.personGalleryMediaIndex));
                return;
            }
            const deleteButton = event.target.closest('[data-person-gallery-delete]');
            if (deleteButton) {
                remove(deleteButton.dataset.personGalleryDelete);
                return;
            }
            const overlay = lightbox();
            if (overlay?.contains(event.target) && !event.target.closest(
                '[data-person-gallery-lightbox-stage] img, [data-person-gallery-lightbox-stage] video, [data-person-gallery-lightbox-prev], [data-person-gallery-lightbox-next]'
            )) {
                closeLightbox();
                return;
            }
            if (event.target.closest('[data-person-gallery-lightbox-prev]')) showLightbox(activeIndex - 1);
            if (event.target.closest('[data-person-gallery-lightbox-next]')) showLightbox(activeIndex + 1);
        });
        document.addEventListener('change', (event) => {
            if (event.target.matches('[data-person-gallery-input]')) upload(event.target.files);
        });
        document.addEventListener('keydown', (event) => {
            const overlay = lightbox();
            if (overlay && !overlay.hidden) {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    closeLightbox();
                } else if (event.key === 'ArrowLeft') {
                    event.preventDefault();
                    showLightbox(activeIndex - 1);
                } else if (event.key === 'ArrowRight') {
                    event.preventDefault();
                    showLightbox(activeIndex + 1);
                } else {
                    trapFocus(overlay, event);
                }
                return;
            }
            const modal = shell();
            if (modal?.classList.contains('open')) {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    close();
                } else {
                    trapFocus(modal, event);
                }
            }
        });
    }

    return { previewMarkup, open, close };
})();

window.racinageTreePersonDetailsModal = window.racinageTreePersonDetailsModal || (() => {
    const escapeHTML = (value) => String(value ?? '').replace(/[&<>"']/g, (char) => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;',
    })[char]);

    const parseDate = (value) => {
        const match = String(value || '').match(/^(\d{4})-(\d{2})-(\d{2})/);
        if (!match) return null;
        const year = Number(match[1]), month = Number(match[2]), day = Number(match[3]);
        const date = new Date(year, month - 1, day);
        return (!Number.isNaN(date.getTime()) && date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day)
            ? date
            : null;
    };

    const yearsBetween = (startValue, endValue = new Date()) => {
        const start = parseDate(startValue);
        const end = endValue instanceof Date ? endValue : (parseDate(endValue) || new Date());
        if (!start || end < start) return '';
        let years = end.getFullYear() - start.getFullYear();
        if (end.getMonth() < start.getMonth() || (end.getMonth() === start.getMonth() && end.getDate() < start.getDate())) years--;
        return Math.max(0, years);
    };

    const dateDMY = (value) => {
        const date = parseDate(value);
        if (!date) return '';
        return [String(date.getDate()).padStart(2, '0'), String(date.getMonth() + 1).padStart(2, '0'), date.getFullYear()].join('/');
    };

    const returnFocusTargets = new WeakMap();
    const close = (modal = document.getElementById('treePersonModal')) => {
        if (!modal) return;
        const wasOpen = modal.classList.contains('open');
        modal.classList.remove('open');
        modal.setAttribute('aria-hidden', 'true');
        const trigger = returnFocusTargets.get(modal);
        if (wasOpen && trigger?.isConnected) {
            if (!trigger.hasAttribute('tabindex') && trigger.tabIndex < 0) trigger.tabIndex = -1;
            trigger.focus({ preventScroll: true });
        }
    };

    const bind = (modal) => {
        if (!modal || modal.dataset.personDetailsBound === '1') return;
        modal.dataset.personDetailsBound = '1';
        modal.querySelector('#treePersonClose')?.addEventListener('click', () => close(modal));
        modal.addEventListener('click', (event) => {
            if (event.target === modal) close(modal);
        });
        modal.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') close(modal);
            if (event.key === 'Tab') {
                const controls = [...modal.querySelectorAll('button, a[href], input, select, textarea, [tabindex]')]
                    .filter(control => !control.disabled && control.tabIndex >= 0 && control.getClientRects().length);
                const first = controls[0], last = controls[controls.length - 1];
                if (first && (!controls.includes(document.activeElement) || (event.shiftKey && document.activeElement === first) || (!event.shiftKey && document.activeElement === last))) {
                    event.preventDefault();
                    (event.shiftKey ? last : first).focus();
                }
            }
        });
    };

    const open = ({
        modal = document.getElementById('treePersonModal'),
        person,
        editing = false,
        generation = '',
        translate = (key) => key,
        returnFocus = document.activeElement,
    } = {}) => {
        if (!modal || !person) return false;
        const t = (key, fallback = key) => translate(key, fallback) || fallback || key;
        const unknownIdentity = !(person.last_name || '').trim() &&
            ['unknown person', 'unknown', 'personne inconnue', 'inconnu', 'inconnue'].includes((person.first_name || '').trim().toLowerCase());
        const fullName = unknownIdentity ? t('Unknown') : `${person.first_name || ''} ${person.last_name || ''}`.trim() || t('Person');
        const username = (person.username || '').trim();
        const imageUrl = person.image_lg_url || person.image_url || person.image_sm_url || '';
        const hasDeathDate = !!parseDate(person.death_date);
        const isDeceased = Number(person.is_deceased) === 1 || hasDeathDate;
        const hasLifeStatus = person.death_date !== undefined || person.is_deceased !== undefined;
        const ageYears = person.birth_date && hasLifeStatus && (!isDeceased || hasDeathDate) ? yearsBetween(person.birth_date, hasDeathDate ? person.death_date : new Date()) : '';
        const ageText = ageYears === '' ? '' : `${ageYears} ${t(ageYears === 1 ? 'year old' : 'years old')}`;
        const yearsSinceDeath = hasDeathDate ? yearsBetween(person.death_date) : '';
        const updatedMeta = person.updated_at ? `${t('Updated:')} ${dateDMY(person.updated_at)}` : '';
        const fieldUpdatedMeta = (value) => value && updatedMeta ? updatedMeta : '';
        const maritalMeta = person.has_deceased_partner
            ? t('Widowed')
            : (['widowed', 'divorced'].includes(person.marital_status) ? t(person.marital_status) : '');
        const genderText = person.gender === 'F' ? t('Female') : t('Male');
        const statusText = isDeceased ? t('Deceased') : (person.visibility === 'hidden' ? t('Hidden') : t('Visible'));
        const statusClass = isDeceased ? 'deceased' : (person.visibility === 'hidden' ? 'hidden' : 'visible');
        const identityMeta = [generation ? `${t('Generation')} ${generation}` : '', ageText].filter(Boolean);
        const siblingValue = person.sibling_value === 'no' ? t('No') : t('Yes');
        const siblingNextValue = person.sibling_value === 'no' ? 'yes' : 'no';
        const siblingMeta = (editing && person.sibling_editable)
            ? `<button type="button" class="tree_detail_inline_action" data-person-action="sibling_status" data-sibling-value="${siblingNextValue}">${escapeHTML(siblingNextValue === 'yes' ? t('Set Yes') : t('Set No'))}</button>`
            : (!person.sibling_editable ? t('Locked') : '');
        const identityFacts = [
            statusText,
            person.gender !== undefined ? genderText : '',
            person.marital_status ? t(person.marital_status) : '',
            updatedMeta,
        ].filter(Boolean).slice(0, 4);
        const detailRows = [
            person.gender !== undefined ? [t('Gender'), genderText, ''] : null,
            person.visibility !== undefined ? [t('Visibility'), person.visibility === 'hidden' ? t('Hidden') : t('Visible'), statusText] : null,
            person.sibling_value !== undefined ? [t('Sibling'), siblingValue, siblingMeta, editing && person.sibling_editable] : null,
            person.sibling_birth_order !== undefined ? [t('Birth position'), person.sibling_birth_order || t('Not set'), ''] : null,
            person.marital_status !== undefined ? [t('Marital status'), person.marital_status ? t(person.marital_status) : t('Not set'), maritalMeta] : null,
            person.birth_date !== undefined ? [t('Birth date'), person.birth_date ? dateDMY(person.birth_date) : t('Not set'), ageText] : null,
            person.death_date !== undefined ? [t('Death date'), hasDeathDate ? dateDMY(person.death_date) : t(isDeceased ? 'Deceased - date unknown' : 'Not set'), yearsSinceDeath === '' ? '' : `${yearsSinceDeath} ${t(yearsSinceDeath === 1 ? 'year ago' : 'years ago')}`] : null,
            person.phone1 !== undefined ? [t('Phone'), person.phone1 || t('Not set'), fieldUpdatedMeta(person.phone1)] : null,
            person.phone2 !== undefined ? [t('Second phone'), person.phone2 || t('Not set'), fieldUpdatedMeta(person.phone2)] : null,
            person.email1 !== undefined ? [t('Email'), person.email1 || t('Not set'), fieldUpdatedMeta(person.email1)] : null,
            person.email2 !== undefined ? [t('Second email'), person.email2 || t('Not set'), fieldUpdatedMeta(person.email2)] : null,
            person.address !== undefined ? [t('Location address'), person.address || t('Not set'), fieldUpdatedMeta(person.address)] : null,
            person.birth_place !== undefined ? [t('Birth place'), person.birth_place || t('Not set'), fieldUpdatedMeta(person.birth_place)] : null,
            person.nationality !== undefined ? [t('Nationality'), person.nationality || t('Not set'), ''] : null,
            person.blood_group !== undefined ? [t('Blood group'), person.blood_group || t('Not set'), ''] : null,
            person.education_level !== undefined ? [t('Education'), person.education_level || t('Not set'), fieldUpdatedMeta(person.education_level)] : null,
            person.religion !== undefined ? [t('Religion'), person.religion || t('Not set'), fieldUpdatedMeta(person.religion)] : null,
            person.website !== undefined ? [t('Website'), person.website || t('Not set'), fieldUpdatedMeta(person.website)] : null,
            person.creator_user_name ? [t('Added by'), person.creator_user_name, ''] : null,
            person.updater_user_name ? [t('Updated by'), person.updater_user_name, ''] : null,
        ].filter(Boolean);
        const relativePill = relative => `<button type="button" class="tree_person_relative_pill" data-open-person="${escapeHTML(relative.id)}"><img src="${escapeHTML(relative.image_url || '')}" alt=""><span>${escapeHTML(relative.name)}</span></button>`;
        const relativeRows = [
            person.spouses?.length ? [person.spouses.length > 1 ? t('Spouses') : t(person.spouses[0].gender === 'F' ? 'Spouse (wife)' : 'Spouse (husband)'), person.spouses] : null,
            person.partners?.length ? [t('Partners'), person.partners] : null,
        ].filter(Boolean);

        modal.dataset.personId = person.id;
        modal.dataset.personLongId = person.long_id || '';
        modal.dataset.person = JSON.stringify(person);
        modal.querySelector('.tree_person_modal')?.classList.toggle('editing', editing);
        modal.querySelector('#treePersonTitle').textContent = fullName;
        modal.querySelector('#treePersonTitle').classList.toggle('tree_identity_unknown', unknownIdentity);
        modal.querySelector('#treePersonSummary').innerHTML = `
            <div class="tree_person_profile">
                <div class="tree_person_large_photo">
                    <button type="button" class="tree_person_profile_photo" data-person-profile-photo aria-label="${escapeHTML(t('Photo') + ' - ' + fullName)}" ${imageUrl ? '' : 'disabled'}>
                        <img src="${escapeHTML(imageUrl)}" alt="${escapeHTML(fullName)}">
                    </button>
                    <span class="tree_person_listing_dot ${escapeHTML(statusClass)}" title="${escapeHTML(statusText)}" aria-label="${escapeHTML(statusText)}"></span>
                    <div class="tree_person_large_identity">
                        <strong class="${unknownIdentity ? 'tree_identity_unknown' : ''}">${escapeHTML(fullName)}</strong>
                        ${identityMeta.length ? `<small class="tree_person_identity_meta">${identityMeta.map(escapeHTML).join(' • ')}</small>` : ''}
                        ${username ? `<small>@${escapeHTML(username)}</small>` : ''}
                        ${identityFacts.length ? `<div class="tree_person_identity_facts">${identityFacts.map((item) => `<span>${escapeHTML(item)}</span>`).join('')}</div>` : ''}
                    </div>
                </div>
                <div class="tree_person_detail_grid">
                    ${relativeRows.map(([label, people]) => `
                        <div class="tree_person_detail_row tree_person_relatives_row">
                            <span>${escapeHTML(label)}</span>
                            <div class="tree_person_relative_pills">${people.map(relativePill).join('')}</div>
                        </div>
                    `).join('')}
                    ${detailRows.map(([label, value, meta, metaIsHtml]) => `
                        <div class="tree_person_detail_row">
                            <span>${escapeHTML(label)}</span>
                            <strong>${escapeHTML(value)}</strong>
                            <div>${metaIsHtml ? meta : escapeHTML(meta || '')}</div>
                        </div>
                    `).join('')}
                </div>
                ${window.racinagePersonGallery?.previewMarkup({ person }) || ''}
            </div>
        `;
        bind(modal);
        returnFocusTargets.set(modal, returnFocus);
        modal.classList.add('open');
        modal.setAttribute('aria-hidden', 'false');
        modal.querySelector('#treePersonClose')?.focus();
        return true;
    };

    return { open, close };
})();

function familyTreeInitializer() {
    const container = document.querySelector('.tree_container');
    const canvas    = document.getElementById('treeCanvas');
    if (!container || !canvas) return;
    if (container.dataset.treeDeferred === 'true') {
        window.racinageTreeLoading?.load(container, familyTreeInitializer);
        return;
    }
    if (container.dataset.treeInitialized === 'true') return;
    container.dataset.treeInitialized = 'true';
    const isPublicPreview = container.dataset.publicPreview === 'true';
    const generationIndicator = document.getElementById('treeGenerationIndicator');
    const generationRailEnabled = container.dataset.generationRail !== 'hide';
    const defaultTreeView = container.dataset.defaultView === 'vertical' ? 'vertical' : 'horizontal';
    const baseDefaultHorizontalScale = Math.max(0.5, Math.min(1.5, Number(container.dataset.defaultZoom || 0.8) || 0.8));
    const mobileDefaultHorizontalScale = Math.max(0.35, Math.min(1.5, Number(container.dataset.mobileDefaultZoom || baseDefaultHorizontalScale) || baseDefaultHorizontalScale));

    // Abort any listeners registered by a previous initialization
    // (happens when user navigates away then back via HTMX)
    if (window._ftAbort) window._ftAbort.abort();
    window._ftAbort = new AbortController();
    const sig = window._ftAbort.signal;

    // ── State ──────────────────────────────────────────────────────────────
    let state = { scale: 1, panning: false, pointX: 0, pointY: 0, startX: 0, startY: 0 };
    const ZOOM_SPEED = 0.1, MIN_SCALE = 0.1, MAX_SCALE = 5;
    const CONNECTOR_OVERLAP = 3;
    const EXTERNAL_PARTNER_GAP = 24;
    const EXTERNAL_PARTNER_SIBLING_GAP = 40;
    const TREE_VIEW_STORAGE_KEY = 'racinage_tree_view_type';
    const TREE_EDIT_STORAGE_KEY = 'racinage_tree_editing';
    const V_TREE_STATE_STORAGE_KEY = 'racinage_vertical_tree_sections';
    const treeSyncClientId = window._ftLiveClientId || (window._ftLiveClientId = `tree-${crypto.randomUUID ? crypto.randomUUID() : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`}`);
    const partialSessionKey='racinage.partial_tree.'+(container.dataset.treeId||'');
    const pendingPartialReanchor=sessionStorage.getItem('racinage.partial_tree.reanchor');
    if(pendingPartialReanchor)container.dataset.partialReanchorId=pendingPartialReanchor;
    if(container.dataset.partialTreeTemporary)sessionStorage.setItem(partialSessionKey,container.dataset.partialTreeTemporary);
    else if(!isPublicPreview&&!container.dataset.partialTreeId&&!pendingPartialReanchor&&!new URL(window.location.href).searchParams.has('tree')&&!new URL(window.location.href).searchParams.has('partial_temp')){
        const savedPartial = sessionStorage.getItem(partialSessionKey);
        if (savedPartial) {
            const target = new URL(window.location.href);
            target.searchParams.set('partial_temp', savedPartial);
            if (window.htmx) {
                window.htmx.ajax('GET', target.toString(), { target: '#main_container', swap: 'outerHTML' })
                    .then(() => window.history.replaceState(window.history.state, '', target.toString()))
                    .catch(() => { delete container.dataset.treeInitialized; familyTreeInitializer(); });
                sessionStorage.removeItem(partialSessionKey);
            } else window.location.replace(target.toString());
            return;
        }
    }
    let treeLiveSource = null;
    let treeLiveAfter = 0;
    let treeLiveReady = false;
    let treeLiveReconnect = 0;
    let treeLiveRefreshRunning = false;
    let treeLiveRefreshAgain = false;

    function t(key, fallback = key) {
        const dict = window.RACINAGE_TREE_I18N || {};
        return dict[key] || fallback || key;
    }

    async function copyText(value) {
        if (navigator.clipboard?.writeText) {
            try {
                await navigator.clipboard.writeText(value);
                return true;
            } catch (error) {}
        }
        const helper = document.createElement('textarea');
        helper.value = value;
        helper.setAttribute('readonly', '');
        helper.style.position = 'fixed';
        helper.style.opacity = '0';
        document.body.appendChild(helper);
        helper.select();
        const copied = document.execCommand('copy');
        helper.remove();
        return copied;
    }

    function setTransform() {
        canvas.style.transform = `translate(${state.pointX}px, ${state.pointY}px) scale(${state.scale})`;
        queueGenerationIndicatorUpdate();
    }

    container.addEventListener('racinage:minimap-pan', (event) => {
        stopPanMomentum();
        if (container.classList.contains('vertical')) return;
        const x = Number(event.detail?.x);
        const y = Number(event.detail?.y);
        if (!Number.isFinite(x) || !Number.isFinite(y)) return;
        state.pointX = container.clientWidth / 2 - canvas.offsetLeft - x * state.scale;
        state.pointY = container.clientHeight / 2 - canvas.offsetTop - y * state.scale;
        setTransform();
    }, { signal: sig });

    let generationIndicatorQueued = false;
    let generationRows = null;
    function generationDepth(anchor) {
        let depth = 0;
        let li = anchor.closest('li');
        while (li && canvas.contains(li)) {
            if (!li.classList.contains('external_partner')) depth++;
            li = li.parentElement?.closest('li');
        }
        return Math.max(1, depth);
    }

    function updateGenerationIndicator() {
        if (!generationIndicator) return;
        const hidden = !generationRailEnabled || container.classList.contains('vertical') || !canvas.querySelector('.tree > ul');
        generationIndicator.hidden = hidden;
        if (hidden) {
            if (generationIndicator.childElementCount) generationIndicator.replaceChildren();
            generationRows = null;
            return;
        }

        const containerRect = container.getBoundingClientRect();
        const canvasRect = canvas.getBoundingClientRect();
        const scale = state.scale || 1;
        // Layout changes invalidate this geometry. Panning/zooming only projects
        // the cached rows, rather than measuring every person on every frame.
        if (!generationRows) {
            const rows = new Map();
            canvas.querySelectorAll('.tree li > .couple, .tree li > .one_person').forEach(anchor => {
                const rect = anchor.getBoundingClientRect();
                if (!rect.height) return;
                const depth = generationDepth(anchor);
                const row = rows.get(depth) || { top: Infinity, bottom: -Infinity };
                row.top = Math.min(row.top, (rect.top - canvasRect.top) / scale);
                row.bottom = Math.max(row.bottom, (rect.bottom - canvasRect.top) / scale);
                rows.set(depth, row);
            });
            generationRows = [...rows.entries()].sort((a, b) => a[0] - b[0]).map(([depth, row]) => {
                const interval = document.createElement('div');
                interval.className = 'tree_generation_interval';
                interval.textContent = depth;
                interval.style.setProperty('--generation-color', `hsla(${(depth * 47) % 360}, 78%, 46%, 0.18)`);
                return { ...row, interval };
            });
            generationIndicator.replaceChildren(...generationRows.map(row => row.interval));
        }
        generationRows.forEach((row, index) => {
            const next = generationRows[index + 1];
            row.interval.style.top = `${Math.round(canvasRect.top - containerRect.top + row.top * scale)}px`;
            row.interval.style.height = `${Math.max(28, Math.round(((next?.top ?? row.bottom) - row.top) * scale - (next ? 5 : 0)))}px`;
        });
    }

    function queueGenerationIndicatorUpdate() {
        if (!generationIndicator || generationIndicatorQueued) return;
        generationIndicatorQueued = true;
        requestAnimationFrame(() => {
            generationIndicatorQueued = false;
            if (sig.aborted || !container.isConnected) return;
            updateGenerationIndicator();
        });
    }

    function connectorIdFor(li) {
        if (!li.dataset.connectorId) {
            li.dataset.connectorId = `tree-node-${Math.random().toString(36).slice(2)}`;
        }
        return li.dataset.connectorId;
    }

    function ensureHorizontalConnectorLayer() {
        let layer = canvas.querySelector(':scope > .tree_connector_layer');
        if (!layer) {
            layer = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            layer.classList.add('tree_connector_layer');
            layer.setAttribute('aria-hidden', 'true');
            canvas.prepend(layer);
        }
        return layer;
    }

    // Canvas-local geometry. Rect differences also cover absolutely positioned
    // table nodes, whose offsetParent chain can end before reaching the canvas.
    function canvasBox(el) {
        const scale = state.scale || 1;
        const canvasRect = canvas.getBoundingClientRect();
        const rect = el.getBoundingClientRect();
        const left = (rect.left - canvasRect.left) / scale;
        const top = (rect.top - canvasRect.top) / scale;
        const width = rect.width / scale;
        const height = rect.height / scale;
        return { left, top, width, height, right: left + width, bottom: top + height };
    }

    function roundedConnectorPath(parentPoint, childPoint, preferredMidY = null) {
        const midY = preferredMidY ?? Math.max(parentPoint.y + 18, childPoint.y - 20);
        const dx = childPoint.x - parentPoint.x;
        const direction = dx >= 0 ? 1 : -1;
        const radius = Math.min(14, Math.abs(dx) / 2, Math.abs(midY - parentPoint.y) / 2, Math.abs(childPoint.y - midY) / 2);

        if (radius < 1) {
            return `M ${parentPoint.x} ${parentPoint.y} V ${childPoint.y}`;
        }

        return [
            `M ${parentPoint.x} ${parentPoint.y}`,
            `V ${midY - radius}`,
            `Q ${parentPoint.x} ${midY} ${parentPoint.x + direction * radius} ${midY}`,
            `H ${childPoint.x - direction * radius}`,
            `Q ${childPoint.x} ${midY} ${childPoint.x} ${midY + radius}`,
            `V ${childPoint.y}`,
        ].join(' ');
    }

    function roundedSideConnectorPath(startPoint, endPoint) {
        return `M ${startPoint.x} ${startPoint.y} H ${endPoint.x}`;
    }

    function externalPartnerSource(partnerLi, parentAnchor) {
        if (!partnerLi || !parentAnchor) return null;
        const associatedParentId = partnerLi.getAttribute('data-associated-parent-id');
        const associatedPerson = associatedParentId ? findPersonById(parentAnchor, associatedParentId) : null;
        if (associatedPerson) return associatedPerson;
        if (!parentAnchor.classList.contains('couple')) return parentAnchor;

        const people = [...parentAnchor.querySelectorAll(':scope > .one_person')];
        if (!people.length) return parentAnchor;
        return partnerLi.classList.contains('husband') ? people[0] : people[people.length - 1];
    }

    // Keep each external partner beside their person and reserve the branch's
    // full width so the person's siblings stay beyond the partner's children.
    function alignHorizontalExternalPartners(resetSpacing = false) {
        if (resetSpacing) {
            canvas.querySelectorAll('.tree li[data-external-partner-space]').forEach(li => {
                li.style.marginLeft = '';
                li.style.marginRight = '';
                delete li.dataset.externalPartnerSpace;
            });
        }

        const groups = new Map();
        const parentReserves = new Map();
        canvas.querySelectorAll('.external_partner').forEach(partnerLi => {
            const ul = partnerLi.parentElement;
            if (!groups.has(ul)) groups.set(ul, []);
            groups.get(ul).push(partnerLi);
        });

        groups.forEach((partners, ul) => {
            const scale = state.scale || 1;
            const ulRect = ul.getBoundingClientRect();
            const parentLi = ul.closest('li');
            const parentAnchor = parentLi?.querySelector(':scope > .couple') || parentLi?.querySelector(':scope > .one_person');
            const parentRect = parentLi?.getBoundingClientRect();
            const sideEdges = new Map();

            partners.forEach(partnerLi => {
                const partner = partnerLi.querySelector(':scope > .one_person.partner');
                const source = externalPartnerSource(partnerLi, parentAnchor);
                if (!partner || !source || !parentRect) return;

                const sourceRect = source.getBoundingClientRect();
                const liRect = partnerLi.getBoundingClientRect();
                const firstUl = partnerLi.querySelector(':scope > .first_ul');
                const childRects = firstUl
                    ? [...firstUl.children]
                        .map(childLi => childLi.querySelector(':scope > .couple, :scope > .one_person'))
                        .filter(Boolean)
                        .map(child => child.getBoundingClientRect())
                    : [];
                const childrenCenterLocal = childRects.length
                    ? (Math.min(...childRects.map(rect => rect.left)) + Math.max(...childRects.map(rect => rect.right)) - 2 * liRect.left) / (2 * scale)
                    : partnerLi.offsetWidth / 2;
                const cardLeftLocal = childrenCenterLocal - partner.offsetWidth / 2;
                const visualLeftLocal = Math.min(0, cardLeftLocal);
                const visualRightLocal = Math.max(partnerLi.offsetWidth, cardLeftLocal + partner.offsetWidth);
                const isLeft = partnerLi.classList.contains('husband');
                const side = isLeft ? 'left' : 'right';
                const edge = sideEdges.get(side);
                const sourceLeft = (sourceRect.left - ulRect.left) / scale;
                const sourceRight = (sourceRect.right - ulRect.left) / scale;
                let cardCenter;

                if (isLeft) {
                    const cardRight = Number.isFinite(edge) ? edge : sourceLeft - EXTERNAL_PARTNER_GAP;
                    cardCenter = cardRight - partner.offsetWidth / 2;
                } else {
                    const cardLeft = Number.isFinite(edge) ? edge : sourceRight + EXTERNAL_PARTNER_GAP;
                    cardCenter = cardLeft + partner.offsetWidth / 2;
                }

                const liLeft = cardCenter - childrenCenterLocal;
                const branchLeft = liLeft + visualLeftLocal;
                const branchRight = liLeft + visualRightLocal;
                sideEdges.set(side, isLeft
                    ? branchLeft - EXTERNAL_PARTNER_GAP
                    : branchRight + EXTERNAL_PARTNER_GAP);
                partnerLi.style.setProperty('--external-partner-left', `${Math.round(liLeft)}px`);
                partnerLi.style.setProperty('--external-partner-card-left', `${Math.round(cardLeftLocal)}px`);

                const cardTopLocal = (sourceRect.top + sourceRect.height / 2 - liRect.top) / scale - partner.offsetHeight / 2;
                partnerLi.style.setProperty('--external-partner-card-top', `${Math.round(cardTopLocal)}px`);

                const reserve = parentReserves.get(parentLi) || { left: 0, right: 0 };
                reserve.left = Math.max(reserve.left, (parentRect.left - ulRect.left) / scale - branchLeft + EXTERNAL_PARTNER_SIBLING_GAP);
                reserve.right = Math.max(reserve.right, branchRight - (parentRect.right - ulRect.left) / scale + EXTERNAL_PARTNER_SIBLING_GAP);
                parentReserves.set(parentLi, reserve);
            });
        });

        parentReserves.forEach((reserve, parentLi) => {
            const currentLeft = Number.parseFloat(parentLi.style.marginLeft) || 0;
            const currentRight = Number.parseFloat(parentLi.style.marginRight) || 0;
            parentLi.style.marginLeft = reserve.left > 0 ? `${Math.ceil(Math.max(currentLeft, reserve.left))}px` : '';
            parentLi.style.marginRight = reserve.right > 0 ? `${Math.ceil(Math.max(currentRight, reserve.right))}px` : '';
            parentLi.dataset.externalPartnerSpace = 'true';
        });

        const snapPartnersToSources = () => {
            groups.forEach(partners => ['husband', 'wife'].forEach(side => {
                const sidePartners = partners.filter(li => li.classList.contains(side));
                const nearestLi = sidePartners[0];
                const nearest = nearestLi?.querySelector(':scope > .one_person.partner');
                const parentLi = nearestLi?.parentElement.closest('li');
                const parentAnchor = parentLi?.querySelector(':scope > .couple') || parentLi?.querySelector(':scope > .one_person');
                const source = externalPartnerSource(nearestLi, parentAnchor);
                if (!nearest || !source) return;
                const partnerRect = nearest.getBoundingClientRect();
                const sourceRect = source.getBoundingClientRect();
                const isLeft = side === 'husband';
                const desiredEdge = isLeft
                    ? sourceRect.left - EXTERNAL_PARTNER_GAP * (state.scale || 1)
                    : sourceRect.right + EXTERNAL_PARTNER_GAP * (state.scale || 1);
                const actualEdge = isLeft ? partnerRect.right : partnerRect.left;
                const delta = (desiredEdge - actualEdge) / (state.scale || 1);
                sidePartners.forEach(partnerLi => {
                    const currentLeft = Number.parseFloat(partnerLi.style.getPropertyValue('--external-partner-left')) || 0;
                    partnerLi.style.setProperty('--external-partner-left', `${Math.round(currentLeft + delta)}px`);
                });
            }));
        };

        snapPartnersToSources();

        // Nested unions are part of the occupied branch too. Resolve them from
        // the inside out before moving their enclosing branch as one unit.
        const partnerBranchBounds = partnerLi => {
            const rects = [...partnerLi.querySelectorAll('.one_person')]
                .map(card => card.getBoundingClientRect());
            return {
                left: Math.min(...rects.map(rect => rect.left)),
                right: Math.max(...rects.map(rect => rect.right)),
            };
        };
        const shiftPartnerBranch = (partnerLi, pixels) => {
            const currentLeft = Number.parseFloat(partnerLi.style.getPropertyValue('--external-partner-left')) || 0;
            partnerLi.style.setProperty('--external-partner-left', `${Math.round(currentLeft + pixels / (state.scale || 1))}px`);
        };

        [...groups].reverse().forEach(([ul, partners]) => {
            ['husband', 'wife'].forEach(side => {
                const sidePartners = partners.filter(li => li.classList.contains(side));
                let innerBounds = sidePartners[0] ? partnerBranchBounds(sidePartners[0]) : null;
                sidePartners.slice(1).forEach(partnerLi => {
                    let bounds = partnerBranchBounds(partnerLi);
                    const gap = side === 'husband' ? innerBounds.left - bounds.right : bounds.left - innerBounds.right;
                    const deficit = EXTERNAL_PARTNER_SIBLING_GAP * (state.scale || 1) - gap;
                    if (deficit > 0) {
                        shiftPartnerBranch(partnerLi, side === 'husband' ? -deficit : deficit);
                        bounds = partnerBranchBounds(partnerLi);
                    }
                    innerBounds = bounds;
                });
            });

            const normalChildren = [...ul.children].filter(li => !li.classList.contains('external_partner'));
            const normalCards = normalChildren.flatMap(li => [...li.querySelectorAll('.one_person')]);
            if (normalCards.length) {
                const normalRects = normalCards.map(card => card.getBoundingClientRect());
                ['husband', 'wife'].forEach(side => {
                    const sidePartners = partners.filter(li => li.classList.contains(side));
                    if (!sidePartners.length) return;
                    const externalBounds = sidePartners.map(partnerBranchBounds);
                    const isLeft = side === 'husband';
                    const gap = isLeft
                        ? Math.min(...normalRects.map(rect => rect.left)) - Math.max(...externalBounds.map(bounds => bounds.right))
                        : Math.min(...externalBounds.map(bounds => bounds.left)) - Math.max(...normalRects.map(rect => rect.right));
                    const deficit = EXTERNAL_PARTNER_SIBLING_GAP * (state.scale || 1) - gap;
                    if (deficit <= 0) return;
                    sidePartners.forEach(li => shiftPartnerBranch(li, isLeft ? -deficit : deficit));
                });
            }

            const leftPartners = partners.filter(li => li.classList.contains('husband'));
            const rightPartners = partners.filter(li => li.classList.contains('wife'));
            if (!leftPartners.length || !rightPartners.length) return;
            const leftRight = Math.max(...leftPartners.map(li => partnerBranchBounds(li).right));
            const rightLeft = Math.min(...rightPartners.map(li => partnerBranchBounds(li).left));
            const deficit = EXTERNAL_PARTNER_SIBLING_GAP * (state.scale || 1) - (rightLeft - leftRight);
            if (deficit <= 0) return;
            leftPartners.forEach(li => shiftPartnerBranch(li, -deficit / 2));
            rightPartners.forEach(li => shiftPartnerBranch(li, deficit / 2));
        });

        [...groups].reverse().forEach(([ul, partners]) => {
            const parentLi = ul.closest('li');
            const siblingUl = parentLi?.parentElement;
            if (!parentLi || siblingUl?.tagName !== 'UL') return;
            const siblings = [...siblingUl.children].filter(li => !li.classList.contains('external_partner'));
            const parentIndex = siblings.indexOf(parentLi);
            if (parentIndex < 0) return;

            const cardsFor = lis => lis.flatMap(li => [...li.querySelectorAll('.one_person')]);
            const verticallyAligned = (a, b) => Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top);
            const scale = state.scale || 1;

            [
                ['husband', siblings.slice(0, parentIndex), 'marginLeft'],
                ['wife', siblings.slice(parentIndex + 1), 'marginRight'],
            ].forEach(([side, siblingLis, marginProperty]) => {
                const externalCards = cardsFor(partners.filter(li => li.classList.contains(side)));
                const siblingCards = cardsFor(siblingLis);
                let deficit = 0;
                externalCards.forEach(externalCard => {
                    const externalRect = externalCard.getBoundingClientRect();
                    siblingCards.forEach(siblingCard => {
                        const siblingRect = siblingCard.getBoundingClientRect();
                        if (!verticallyAligned(externalRect, siblingRect)) return;
                        const gap = side === 'husband'
                            ? externalRect.left - siblingRect.right
                            : siblingRect.left - externalRect.right;
                        deficit = Math.max(deficit, EXTERNAL_PARTNER_SIBLING_GAP * scale - gap);
                    });
                });
                if (deficit <= 0) return;
                const currentMargin = Number.parseFloat(parentLi.style[marginProperty]) || 0;
                parentLi.style[marginProperty] = `${Math.ceil(currentMargin + deficit / scale)}px`;
                parentLi.dataset.externalPartnerSpace = 'true';
            });
        });
    }

    function drawHorizontalConnectorPaths() {
        const layer = ensureHorizontalConnectorLayer();
        layer.replaceChildren();
        if (container.classList.contains('vertical')) return;
        // Measure against a stable DOM, then attach all paths in one write.
        // Appending each path before measuring the next card forces layout again.
        const paths = document.createDocumentFragment();

        canvas.querySelectorAll('.tree li').forEach(parentLi => {
            const parentConnectorId = connectorIdFor(parentLi);
            const parentAnchor = parentLi.querySelector(':scope > .couple') || parentLi.querySelector(':scope > .one_person');
            const childUl = parentLi.querySelector(':scope > ul');
            if (!parentAnchor || !childUl) return;

            const pb = canvasBox(parentAnchor);
            const parentPoint = { x: pb.left + pb.width / 2, y: pb.bottom - CONNECTOR_OVERLAP };

            [...childUl.children].forEach(childLi => {
                const isExternal = childLi.classList.contains('external_partner');
                const childAnchor = childLi.querySelector(':scope > .couple') || childLi.querySelector(':scope > .one_person');
                if (!childAnchor) return;

                let startPoint = parentPoint;
                let endPoint;

                if (isExternal) {
                    // Side connector: from the couple member out to the partner
                    // card, tucked just inside both so it touches without overshoot.
                    const source = externalPartnerSource(childLi, parentAnchor) || parentAnchor;
                    const sb = canvasBox(source);
                    const cb = canvasBox(childAnchor);
                    const partnerIsLeft = cb.left < sb.left;
                    startPoint = { x: partnerIsLeft ? sb.left + CONNECTOR_OVERLAP : sb.right - CONNECTOR_OVERLAP, y: sb.top + sb.height / 2 };
                    endPoint = { x: partnerIsLeft ? cb.right - CONNECTOR_OVERLAP : cb.left + CONNECTOR_OVERLAP, y: cb.top + cb.height / 2 };
                } else {
                    const cb = canvasBox(childAnchor);
                    endPoint = { x: cb.left + cb.width / 2, y: cb.top + CONNECTOR_OVERLAP };
                }

                const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                // External-partner children sit a full generation below the
                // partner card (CSS aligns them to the main children row), so the
                // same curved couple→children connector applies to both.
                const pathData = isExternal
                    ? roundedSideConnectorPath(startPoint, endPoint)
                    : roundedConnectorPath(startPoint, endPoint);
                path.setAttribute('d', pathData);
                path.classList.add('tree_connector_path');
                path.dataset.parentConnectorId = parentConnectorId;
                paths.appendChild(path);
            });
        });
        layer.appendChild(paths);
    }

    // Cheap signature of where the external-partner cards landed, used to detect
    // when their (JS-set) positions have finished committing to layout.
    function externalPartnerLayoutSignature() {
        const tree = canvas.querySelector(':scope > .tree');
        let sig = `${tree?.offsetWidth || 0},${tree?.offsetHeight || 0}|`;
        canvas.querySelectorAll('.tree .one_person, .tree .couple').forEach(anchor => {
            sig += `${anchor.offsetLeft},${anchor.offsetTop},${anchor.offsetWidth},${anchor.offsetHeight};`;
        });
        canvas.querySelectorAll('.external_partner').forEach(partnerLi => {
            sig += `${partnerLi.offsetLeft},${partnerLi.offsetTop},${partnerLi.offsetWidth},${partnerLi.offsetHeight};`;
        });
        canvas.querySelectorAll('.external_partner > .one_person.partner').forEach(card => {
            const b = canvasBox(card);
            sig += `${b.left},${b.top},${b.width},${b.height};`;
        });
        return sig;
    }

    function settleFast(promise, ms) {
        return new Promise(resolve => {
            const timer = window.setTimeout(resolve, ms);
            Promise.resolve(promise).catch(() => {}).finally(() => {
                window.clearTimeout(timer);
                resolve();
            });
        });
    }

    function waitForHorizontalAssets() {
        // Card photos have fixed CSS dimensions. Decoding off-screen photos is
        // unnecessary for layout and would defeat their native lazy loading.
        return settleFast(document.fonts?.ready || Promise.resolve(), 180);
    }

    let _pathPollId = 0;
    function drawHorizontalChildConnectors(draw = true) {
        generationRows = null;
        return new Promise((resolve) => {
        if (container.classList.contains('vertical')) {
            ensureHorizontalConnectorLayer().replaceChildren();
            resolve();
            return;
        }
        // Settle every branch while the tree is hidden, then draw once against
        // the final geometry so users never see nodes or connectors drift.
        const myId = ++_pathPollId;
        let frames = 0, stableFrames = 0, lastSig = null;
        const pass = () => {
            if (myId !== _pathPollId || container.classList.contains('vertical')) {
                resolve();
                return;
            }
            alignHorizontalExternalPartners(frames === 0);
            const sig = externalPartnerLayoutSignature();
            if (sig === lastSig) { stableFrames++; } else { stableFrames = 0; lastSig = sig; }
            if (stableFrames < 2 && ++frames < 24) requestAnimationFrame(pass);
            else {
                if (draw) drawHorizontalConnectorPaths();
                generationRows = null;
                queueGenerationIndicatorUpdate();
                resolve();
            }
        };
        pass();
        });
    }

    function setConnectorHover(anchor, active) {
        const parentLi = anchor.closest('li');
        if (!parentLi?.dataset.connectorId) return;
        const selector = `.tree_connector_path[data-parent-connector-id="${parentLi.dataset.connectorId}"]`;
        canvas.querySelectorAll(selector).forEach(path => path.classList.toggle('hovered', active));
    }

    function getStoredTreeView() {
        try {
            window.localStorage.setItem(TREE_VIEW_STORAGE_KEY, defaultTreeView);
        } catch (e) {}
        return defaultTreeView;
    }

    function storeTreeView(view) {
        try {
            window.localStorage.setItem(TREE_VIEW_STORAGE_KEY, view);
        } catch (e) {}
    }

    function getStoredTreeEditing() {
        try {
            return window.localStorage.getItem(TREE_EDIT_STORAGE_KEY) === 'true';
        } catch (e) {
            return false;
        }
    }

    function storeTreeEditing(enabled) {
        try {
            window.localStorage.setItem(TREE_EDIT_STORAGE_KEY, enabled ? 'true' : 'false');
        } catch (e) {}
    }

    function getActiveTreeId() {
        return container.getAttribute('data-tree-id') || '';
    }

    function isCurrentTreeView() {
        return !sig.aborted && container.isConnected && document.querySelector('.tree_container') === container;
    }

    function rootUrl(path = 'trees') {
        const configuredRoot = window.ROOT || (typeof ROOT !== 'undefined' ? ROOT : '');
        const root = configuredRoot
            || document.querySelector('meta[name="app-root"]')?.content
            || window.location.pathname.split('/tree')[0]
            || '';
        return `${root.replace(/\/$/, '')}/${path.replace(/^\//, '')}`;
    }

    function treeViewUrl(treeId = getActiveTreeId(), focusPersonId = '', preservePartial = true) {
        const params = new URLSearchParams();
        const collectionSelection = document.getElementById('familyTreePickerMenu')?.dataset.collectionSelection;
        params.set('collection', collectionSelection || 'all');
        if (treeId) params.set('tree', treeId);
        if (focusPersonId) params.set('p', focusPersonId);
        if (preservePartial && treeId === getActiveTreeId()) {
            if (container.dataset.partialTreeId) params.set('partial', container.dataset.partialTreeId);
            else if (container.dataset.partialTreeTemporary) params.set('partial_temp', container.dataset.partialTreeTemporary);
        }
        return rootUrl(`trees${params.toString() ? `?${params.toString()}` : ''}`);
    }

    function reloadTree(treeId = getActiveTreeId(), focusPersonId = '', preservePartial = true) {
        if (!isCurrentTreeView()) return;
        const url = treeViewUrl(treeId, focusPersonId, preservePartial);
        if (window.htmx) {
            return window.htmx.ajax('GET', url, {
                target: '#main_container',
                swap: 'multi:#main_container:outerHTML',
            }).then(() => window.history.replaceState(window.history.state, '', url));
        } else {
            window.location.href = url;
        }
    }

    function getStoredVerticalSections() {
        try {
            return JSON.parse(window.localStorage.getItem(V_TREE_STATE_STORAGE_KEY) || '{}');
        } catch (e) {
            return {};
        }
    }

    function storeVerticalSection(key, expanded) {
        if (!key) return;
        try {
            const sections = getStoredVerticalSections();
            sections[key] = expanded ? 'expanded' : 'collapsed';
            window.localStorage.setItem(V_TREE_STATE_STORAGE_KEY, JSON.stringify(sections));
        } catch (e) {}
    }

    function revealTreeLayout() {
        if (!isCurrentTreeView()) return;
        const wasPending = container.hasAttribute('data-tree-layout-pending');
        container.querySelector('.tree_loading_state')?.remove();
        canvas.style.opacity = 1;
        container.removeAttribute('data-tree-layout-pending');
        container.removeAttribute('data-tree-layout-initial');
        window.racinageTreeLoading?.viewport(container);
        if (wasPending) requestAnimationFrame(() => {
            if (isCurrentTreeView() && !container.hasAttribute('data-tree-layout-pending')) {
                document.dispatchEvent(new CustomEvent('racinage:tree-ready', { detail: { container } }));
            }
        });
    }

    // ── Center tree on load ────────────────────────────────────────────────
    // Saved once after the initial layout; Reset View returns to this exactly.
    let _homeState = null;

    function homeTopOffset() {
        return window.innerWidth <= 520 ? 76 : (window.innerWidth <= 760 ? 56 : 50);
    }

    function homeAnchorTop() {
        return homeTopOffset() + 64;
    }

    function defaultHorizontalScale() {
        return window.matchMedia?.('(max-width: 760px)').matches ? mobileDefaultHorizontalScale : baseDefaultHorizontalScale;
    }

    function measureHomeState() {
        const containerRect = container.getBoundingClientRect();
        const scale = defaultHorizontalScale();
        const anchorTop = homeAnchorTop();

        const rootAnchor = canvas.querySelector('.tree > ul > li > .couple, .tree > ul > li > .one_person');
        if (rootAnchor) {
            const anchorBox = canvasBox(rootAnchor);
            return {
                x: (containerRect.width / 2) - ((anchorBox.left + anchorBox.width / 2) * scale),
                y: anchorTop - (anchorBox.top * scale),
                scale,
            };
        }

        const treeBox = canvasBox(canvas.firstElementChild);
        return { x: (containerRect.width - treeBox.width * scale) / 2, y: anchorTop, scale };
    }

    function centerTree(animate = false) {
        const emptyFirstButton = canvas.querySelector('.empty_tree_add_first');
        if (emptyFirstButton) {
            const containerRect = container.getBoundingClientRect();
            const buttonRect = emptyFirstButton.getBoundingClientRect();
            const canvasRect = canvas.getBoundingClientRect();
            const buttonRelX = buttonRect.left - canvasRect.left + buttonRect.width / 2;
            const buttonRelY = buttonRect.top - canvasRect.top;
            state.scale = 1;
            state.pointX = (containerRect.width / 2) - buttonRelX;
            state.pointY = homeAnchorTop() - buttonRelY;
            setTransform();
            return;
        }
        const targetState = measureHomeState();
        state.pointX = targetState.x;
        state.pointY = targetState.y;
        state.scale = targetState.scale;

        if (animate) {
            canvas.style.transition = 'transform 0.38s ease-in-out';
            canvas.getBoundingClientRect();
            setTransform();
            canvas.addEventListener('transitionend', () => { canvas.style.transition = ''; }, { once: true });
        } else {
            canvas.style.transition = 'none';
            setTransform();
            canvas.getBoundingClientRect();
            canvas.style.transition = '';
        }
    }

    async function centerHorizontalTree(animate = false) {
        await waitForHorizontalAssets();
        // Geometry reads below settle the mounted DOM. Extra frame delays only
        // prolong the empty canvas and do not make the measurements more stable.
        await drawHorizontalChildConnectors(false);
        centerTree(animate);
        _homeState = { x: state.pointX, y: state.pointY, scale: state.scale };
        drawHorizontalConnectorPaths();
    }

    // ── Zoom ───────────────────────────────────────────────────────────────
    function zoomTo(mx, my, newScale) {
        stopPanMomentum();
        const oldScale = state.scale;
        newScale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, newScale));
        state.pointX = mx - (mx - state.pointX) * (newScale / oldScale);
        state.pointY = my - (my - state.pointY) * (newScale / oldScale);
        state.scale = newScale;
        setTransform();
    }

    function zoom(mx, my, delta) {
        zoomTo(mx, my, state.scale - delta * ZOOM_SPEED * state.scale);
    }

    // ── Wheel zoom (disabled in vertical mode - browser handles scroll) ────
    container.addEventListener('wheel', (e) => {
        if (container.classList.contains('vertical')) return;
        // The viewport background and tree are zoomable; floating controls and
        // their scrollable menus keep native scrolling, including at the edges.
        if (e.target !== container && !canvas.contains(e.target)) return;
        if (e.target.closest('button, a, input, select, textarea, [role="dialog"], [role="menu"], [popover], .cta_panel, .tree_modal_backdrop')) return;
        if (!e.deltaY) return;
        e.preventDefault();
        const r = container.getBoundingClientRect();
        zoom(e.clientX - r.left, e.clientY - r.top, Math.sign(e.deltaY));
    }, { passive: false, signal: sig });

    // ── Pan (disabled in vertical mode) ───────────────────────────────────
    let momentumFrame = 0;
    let panSamples = [];
    function stopPanMomentum() {
        cancelAnimationFrame(momentumFrame);
        momentumFrame = 0;
    }
    function samplePan() {
        const now = performance.now();
        panSamples.push({ x: state.pointX, y: state.pointY, time: now });
        while (panSamples.length > 2 && panSamples[0].time < now - 90) panSamples.shift();
    }
    function glidePan() {
        stopPanMomentum();
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches || panSamples.length < 2) return;
        const first = panSamples[0], last = panSamples[panSamples.length - 1];
        if (performance.now() - last.time > 80) return;
        const elapsed = Math.max(16, last.time - first.time);
        let vx = Math.max(-2.5, Math.min(2.5, (last.x - first.x) / elapsed));
        let vy = Math.max(-2.5, Math.min(2.5, (last.y - first.y) / elapsed));
        let previous = performance.now();
        const step = now => {
            if (!isCurrentTreeView() || container.classList.contains('vertical') || state.panning) { stopPanMomentum(); return; }
            const dt = Math.min(32, now - previous);
            previous = now;
            const decay = Math.exp(-dt / 180);
            state.pointX += vx * 180 * (1 - decay);
            state.pointY += vy * 180 * (1 - decay);
            vx *= decay;
            vy *= decay;
            setTransform();
            if (Math.hypot(vx, vy) > 0.015) momentumFrame = requestAnimationFrame(step);
            else momentumFrame = 0;
        };
        canvas.style.transition = '';
        momentumFrame = requestAnimationFrame(step);
    }
    let mousePan = null;
    let suppressPanClick = false;
    function stopMousePan(event) {
        stopPanMomentum();
        if (!mousePan) return;
        const shouldGlide = event?.type === 'mouseup' && event.button === 0 && suppressPanClick;
        mousePan = null;
        state.panning = false;
        container.style.removeProperty('cursor');
        if (shouldGlide) glidePan();
    }

    container.addEventListener('mousedown', (e) => {
        stopPanMomentum();
        suppressPanClick = false;
        if (e.button !== 0) return;
        if (container.classList.contains('vertical')) return;
        if (e.target.closest('button, input, select, textarea, a, label, summary, [contenteditable]:not([contenteditable="false"]), [role="button"], .tree_modal_backdrop, .tree_minimap')) return;
        // Prevent text selection and native photo dragging from taking over the pan.
        e.preventDefault();
        mousePan = { x: e.clientX, y: e.clientY };
        canvas.style.transition = '';
        panSamples = [];
        samplePan();
        state.panning = true;
        state.startX  = e.clientX - state.pointX;
        state.startY  = e.clientY - state.pointY;
        container.style.cursor = 'grabbing';
    }, { signal: sig });

    window.addEventListener('mousemove', (e) => {
        if (!mousePan) return;
        // Mouseup can be lost outside the window or during a browser drag.
        if (!(e.buttons & 1) || container.classList.contains('vertical') || !isCurrentTreeView()) {
            stopMousePan();
            return;
        }
        if (!suppressPanClick && Math.hypot(e.clientX - mousePan.x, e.clientY - mousePan.y) < 4) return;
        suppressPanClick = true;
        e.preventDefault();
        state.pointX = e.clientX - state.startX;
        state.pointY = e.clientY - state.startY;
        samplePan();
        setTransform();
    }, { signal: sig });

    window.addEventListener('mouseup', stopMousePan, { capture: true, signal: sig });
    window.addEventListener('blur', stopMousePan, { signal: sig });
    window.addEventListener('pagehide', stopMousePan, { signal: sig });
    window.addEventListener('dragend', stopMousePan, { signal: sig });
    sig.addEventListener('abort', stopMousePan, { once: true });
    canvas.addEventListener('dragstart', (e) => {
        if (!container.classList.contains('vertical')) e.preventDefault();
    }, { signal: sig });
    container.addEventListener('click', (e) => {
        if (!suppressPanClick || e.detail === 0) return;
        suppressPanClick = false;
        e.preventDefault();
        e.stopPropagation();
    }, { capture: true, signal: sig });

    let touchZoom = null;
    function touchPoint(touch) {
        const r = container.getBoundingClientRect();
        return { x: touch.clientX - r.left, y: touch.clientY - r.top };
    }
    function touchDistance(touches) {
        const dx = touches[0].clientX - touches[1].clientX;
        const dy = touches[0].clientY - touches[1].clientY;
        return Math.hypot(dx, dy);
    }
    function touchCenter(touches) {
        return touchPoint({
            clientX: (touches[0].clientX + touches[1].clientX) / 2,
            clientY: (touches[0].clientY + touches[1].clientY) / 2,
        });
    }

    container.addEventListener('touchstart', (e) => {
        stopPanMomentum();
        panSamples = [];
        samplePan();
        if (container.classList.contains('vertical')) return;
        if (e.target.closest('.tree_modal_backdrop, .tree_existing_person_grid, .tree_minimap')) return;
        if (e.touches.length === 1) {
            const p = touchPoint(e.touches[0]);
            state.panning = true;
            state.startX = p.x - state.pointX;
            state.startY = p.y - state.pointY;
        } else if (e.touches.length >= 2) {
            state.panning = false;
            touchZoom = { distance: touchDistance(e.touches), scale: state.scale };
        }
    }, { passive: true, signal: sig });

    container.addEventListener('touchmove', (e) => {
        if (container.classList.contains('vertical')) return;
        if (e.touches.length === 1 && state.panning) {
            e.preventDefault();
            const p = touchPoint(e.touches[0]);
            state.pointX = p.x - state.startX;
            state.pointY = p.y - state.startY;
            samplePan();
            setTransform();
        } else if (e.touches.length >= 2 && touchZoom) {
            e.preventDefault();
            const center = touchCenter(e.touches);
            zoomTo(center.x, center.y, touchZoom.scale * (touchDistance(e.touches) / touchZoom.distance));
        }
    }, { passive: false, signal: sig });

    container.addEventListener('touchend', () => {
        const shouldGlide = state.panning && !touchZoom;
        state.panning = false;
        touchZoom = null;
        if (shouldGlide) glidePan();
    }, { signal: sig });
    container.addEventListener('touchcancel', () => {
        stopPanMomentum();
        state.panning = false;
        touchZoom = null;
    }, { signal: sig });

    // ── Animated transform helper ──────────────────────────────────────────
    function withAnimation(fn, transition = 'transform 0.32s cubic-bezier(0.4, 0, 0.2, 1)') {
        stopPanMomentum();
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            fn();
            return;
        }
        canvas.style.transition = transition;
        canvas.getBoundingClientRect();
        fn();
        canvas.addEventListener('transitionend', () => { canvas.style.transition = ''; }, { once: true });
    }

    // Empty-canvas multi-taps share the existing zoom bounds and focal point.
    let canvasTap = null, tapCount = 0, lastTap = null, tapTimer = 0;
    const emptyCanvasTarget = target => !container.classList.contains('vertical')
        && !target.closest('.one_person, .couple, .tree_modal_backdrop, .tree_minimap, .cta_panel, .tree_generation_indicator, .add_child, .add_parents, .add_spouse, .add_partner, .external_add_child, button, a, input, select, textarea, label, summary, [role="button"], [role="dialog"], [role="menu"], [popover], [contenteditable="true"]');
    const clearCanvasTaps = () => {
        clearTimeout(tapTimer);
        canvasTap = lastTap = null;
        tapCount = 0;
    };
    const zoomCanvasTap = delta => {
        if (!lastTap || !isCurrentTreeView() || container.classList.contains('vertical')) return;
        const { x, y } = lastTap;
        const rect = container.getBoundingClientRect();
        withAnimation(() => zoom(x - rect.left, y - rect.top, delta));
    };
    container.addEventListener('pointerdown', event => {
        if (!event.isPrimary || event.button !== 0 || !emptyCanvasTarget(event.target)) { clearCanvasTaps(); return; }
        canvasTap = { id: event.pointerId, x: event.clientX, y: event.clientY, time: performance.now() };
    }, { signal: sig });
    container.addEventListener('pointermove', event => {
        if (canvasTap?.id === event.pointerId && Math.hypot(event.clientX - canvasTap.x, event.clientY - canvasTap.y) > 4) clearCanvasTaps();
    }, { signal: sig });
    container.addEventListener('pointerup', event => {
        const tap = canvasTap;
        canvasTap = null;
        if (!tap || tap.id !== event.pointerId || performance.now() - tap.time > 500 || !emptyCanvasTarget(event.target)) { clearCanvasTaps(); return; }
        const now = performance.now();
        if (!lastTap || now - lastTap.time > 350 || Math.hypot(event.clientX - lastTap.x, event.clientY - lastTap.y) > 28) {
            clearCanvasTaps();
        }
        lastTap = { x: event.clientX, y: event.clientY, time: now };
        tapCount++;
        clearTimeout(tapTimer);
        if (tapCount === 3) {
            event.preventDefault();
            zoomCanvasTap(1);
            clearCanvasTaps();
        } else {
            tapTimer = setTimeout(() => {
                if (tapCount === 2) zoomCanvasTap(-1);
                clearCanvasTaps();
            }, 350);
        }
    }, { signal: sig });
    container.addEventListener('dblclick', event => { if (emptyCanvasTarget(event.target)) event.preventDefault(); }, { signal: sig });
    container.addEventListener('pointercancel', clearCanvasTaps, { signal: sig });
    window.addEventListener('blur', clearCanvasTaps, { signal: sig });
    sig.addEventListener('abort', clearCanvasTaps, { once: true });

    async function resetTreeView() {
        if (container.classList.contains('vertical')) {
            await applyLayout('horizontal', false, true);
            return;
        }
        const targetState = measureHomeState();
        withAnimation(() => {
            state.pointX = targetState.x;
            state.pointY = targetState.y;
            state.scale = targetState.scale;
            setTransform();
        }, 'transform 0.55s ease-in');
        _homeState = targetState;
    }

    // ── CTA panel buttons ──────────────────────────────────────────────────
    const zoomInBtn  = document.getElementById('zoomIn');
    const zoomOutBtn = document.getElementById('zoomOut');
    const resetBtn   = document.getElementById('resetView');
    const moveStep   = 80;

    if (zoomInBtn)  zoomInBtn .addEventListener('click', () => withAnimation(() => { const r = container.getBoundingClientRect(); zoom(r.width/2, r.height/2, -1); }), { signal: sig });
    if (zoomOutBtn) zoomOutBtn.addEventListener('click', () => withAnimation(() => { const r = container.getBoundingClientRect(); zoom(r.width/2, r.height/2,  1); }), { signal: sig });
    if (resetBtn)   resetBtn  .addEventListener('click', resetTreeView, { signal: sig });

    document.getElementById('moveUp')   ?.addEventListener('click', () => withAnimation(() => { state.pointY += moveStep; setTransform(); }), { signal: sig });
    document.getElementById('moveDown') ?.addEventListener('click', () => withAnimation(() => { state.pointY -= moveStep; setTransform(); }), { signal: sig });
    document.getElementById('moveLeft') ?.addEventListener('click', () => withAnimation(() => { state.pointX += moveStep; setTransform(); }), { signal: sig });
    document.getElementById('moveRight')?.addEventListener('click', () => withAnimation(() => { state.pointX -= moveStep; setTransform(); }), { signal: sig });

    // Keep the horizontal tree intact when the container resizes (window
    // resize, DevTools docking, responsive panes, etc.). The canvas pins the
    // tree to its origin (justify-content: flex-start) and positions it purely
    // through the transform, so a viewport change no longer slides the tree -
    // the pan/zoom is preserved and we only need to redraw the connectors so
    // they stay locked to the nodes. A ResizeObserver also catches size
    // changes that don't fire a window 'resize' event.
    let _treeResizeQueued = false;

    function handleTreeResize() {
        if (container.classList.contains('vertical')) {
            window.racinageTreeLoading?.viewport(container);
            return;
        }
        if (container.hasAttribute('data-tree-layout-pending')) return;
        container.setAttribute('data-tree-layout-pending', 'true');
        try {
            // A viewport/sidebar resize does not change relationships. Keep the
            // settled external-branch spacing instead of resetting it beneath
            // existing paths while an asynchronous layout pass is still running.
            drawHorizontalConnectorPaths();
            queueGenerationIndicatorUpdate();
        } finally {
            revealTreeLayout();
        }
    }

    const treeResizeObserver = new ResizeObserver(() => {
        if (_treeResizeQueued) return;
        _treeResizeQueued = true;
        requestAnimationFrame(() => {
            _treeResizeQueued = false;
            handleTreeResize();
        });
    });
    treeResizeObserver.observe(container);
    sig.addEventListener('abort', () => treeResizeObserver.disconnect());

    canvas.addEventListener('mouseover', (e) => {
        const anchor = e.target.closest('.couple, .one_person');
        if (!anchor || !canvas.contains(anchor)) return;
        setConnectorHover(anchor, true);
    }, { signal: sig });

    canvas.addEventListener('mouseout', (e) => {
        const anchor = e.target.closest('.couple, .one_person');
        if (!anchor || !canvas.contains(anchor)) return;
        const nextAnchor = e.relatedTarget?.closest?.('.couple, .one_person');
        if (nextAnchor && anchor.contains(nextAnchor)) return;
        setConnectorHover(anchor, false);
    }, { signal: sig });

    // ── Layout toggle & vertical menu ─────────────────────────────────────────
    const hBtn = document.getElementById('layoutHorizontal');
    const vBtn = document.getElementById('layoutVertical');
    const treeEditToggle = document.getElementById('verticalEditToggle');
    const layoutDropdown = document.getElementById('layoutDropdown');
    const layoutDropdownToggle = document.getElementById('layoutDropdownToggle');
    const layoutCurrentLabel = layoutDropdown?.querySelector('.layout_current_label');

    function layoutLabel(view) {
        const button = view === 'vertical' ? vBtn : hBtn;
        return button?.querySelector(':scope > span:last-child')?.textContent?.trim()
            || button?.getAttribute('title')
            || t(view === 'vertical' ? 'Vertical' : 'Horizontal');
    }

    function syncLayoutLabel() {
        if (!layoutCurrentLabel) return;
        layoutCurrentLabel.textContent = layoutLabel(container.classList.contains('vertical') ? 'vertical' : 'horizontal');
    }
    function closeLayoutDropdown() {
        layoutDropdown?.classList.remove('open');
        layoutDropdownToggle?.setAttribute('aria-expanded', 'false');
    }

    const V_CHEVRON = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="6 3 11 8 6 13"/></svg>';

    function vPersonData(el) {
        return {
            id: el.getAttribute('data-person-id') || '',
            gender: el.getAttribute('data-person-gender') || '',
            img:  el.querySelector('img')?.src || '',
            name: (el.getAttribute('title') || el.querySelector('span')?.textContent || '').trim(),
            birthdayIcon: el.querySelector(':scope > .person_birthday_icon')?.outerHTML || '',
            birthOrderBadge: el.querySelector(':scope > .tree_sibling_birth_order')?.outerHTML || '',
            dead: el.classList.contains('dead'),
            actualChild: el.classList.contains('sibling'),
            canAddParents: !!el.querySelector(':scope > .add_parents'),
            canAddSpouse: !!el.querySelector(':scope > .add_spouse'),
            canAddPartner: !!el.querySelector(':scope > .add_partner'),
        };
    }

    function escapeHTML(value) {
        return String(value).replace(/[&<>"']/g, (char) => ({
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#039;',
        })[char]);
    }

    function escapeSelector(value) {
        if (window.CSS?.escape) return CSS.escape(value);
        return String(value).replace(/["\\]/g, '\\$&');
    }

    function parseTreeDate(value) {
        const match = String(value || '').match(/^(\d{4})-(\d{2})-(\d{2})/);
        if (!match) return null;
        const year = Number(match[1]), month = Number(match[2]), day = Number(match[3]);
        const date = new Date(year, month - 1, day);
        return (!Number.isNaN(date.getTime()) && date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day) ? date : null;
    }

    function yearsBetween(startValue, endValue = new Date()) {
        const start = parseTreeDate(startValue);
        const end = endValue instanceof Date ? endValue : (parseTreeDate(endValue) || new Date());
        if (!start || end < start) return '';
        let years = end.getFullYear() - start.getFullYear();
        if (end.getMonth() < start.getMonth() || (end.getMonth() === start.getMonth() && end.getDate() < start.getDate())) years--;
        return Math.max(0, years);
    }

    function treeYearLabel(years, singular, plural) {
        return years === '' ? '' : `${years} ${t(years === 1 ? singular : plural)}`;
    }

    function treeDateDMY(value) {
        const date = parseTreeDate(value);
        if (!date) return '';
        return [String(date.getDate()).padStart(2, '0'), String(date.getMonth() + 1).padStart(2, '0'), date.getFullYear()].join('/');
    }

    function findPersonById(scope, personId) {
        return [...scope.querySelectorAll(':scope > .one_person')]
            .find(person => person.getAttribute('data-person-id') === String(personId));
    }

    function externalPartnerLabel(li) {
        const parentLi = li.parentElement?.parentElement;
        const couple = parentLi?.querySelector(':scope > .couple');
        if (!couple) return t('Partner');

        const people = [...couple.querySelectorAll(':scope > .one_person')];
        const associatedParentId = li.getAttribute('data-associated-parent-id');
        const associatedPerson = associatedParentId
            ? findPersonById(couple, associatedParentId)
            : (li.classList.contains('husband') ? people[0] : people[people.length - 1]);
        const name = associatedPerson ? vPersonData(associatedPerson).name : '';

        return name ? t('Partner of {name}').replace('{name}', name) : t('Partner');
    }

    function verticalNodeKey(isExternal, persons) {
        const ids = persons.map(p => p.id).filter(Boolean);
        if (ids.length) return `${isExternal ? 'external' : 'node'}:${ids.join('-')}`;
        return `${isExternal ? 'external' : 'node'}:${persons.map(p => p.name).join('-')}`;
    }

    function buildVTreeHTML(li) {
        const isExternal = li.classList.contains('external_partner');
        let personEls, childUl;
        const sourceAddChild = li.querySelector(':scope > .couple > .add_child');
        const canAddChild = !!sourceAddChild;

        if (isExternal) {
            const p = li.querySelector(':scope > .one_person');
            personEls = p ? [p] : [];
            childUl   = li.querySelector(':scope > .first_ul');
        } else {
            const couple = li.querySelector(':scope > .couple');
            personEls = couple
                ? [...couple.querySelectorAll(':scope > .one_person')]
                : [li.querySelector(':scope > .one_person')].filter(Boolean);
            childUl = li.querySelector(':scope > ul');
        }

        const persons   = personEls.map(vPersonData);
        const nodeKey   = verticalNodeKey(isExternal, persons);
        const childLis  = childUl ? [...childUl.querySelectorAll(':scope > li')] : [];
        const hasKids   = childLis.length > 0;
        const kidCount  = childLis.filter(l => !l.classList.contains('external_partner')).length;

        const photos = persons.map(p =>
            `<img loading="lazy" decoding="async" src="${escapeHTML(p.img)}" alt="" title="${escapeHTML(p.name)}" data-person-id="${escapeHTML(p.id)}" class="${p.actualChild ? 'v_actual_child_photo ' : ''}${p.dead ? 'v_dead_photo' : ''}">`
        ).join('');

        const plusIcon = '<span class="v_add_plus">+</span>';
        const personActions = persons.map(p => {
            const controls = [];
            const genderAttr = ` data-person-gender="${escapeHTML(p.gender)}"`;
            if (p.canAddParents) controls.push(`<button type="button" class="v_add_btn v_add_parents" data-person-id="${escapeHTML(p.id)}"${genderAttr} title="${escapeHTML(t('Add Parents'))}" aria-label="${escapeHTML(t('Add Parents'))}">${plusIcon}</button>`);
            if (p.canAddSpouse) controls.push(`<button type="button" class="v_add_btn v_add_spouse" data-person-id="${escapeHTML(p.id)}"${genderAttr} title="${escapeHTML(t('Add Spouse'))}" aria-label="${escapeHTML(t('Add Spouse'))}">${plusIcon}</button>`);
            if (p.canAddPartner) controls.push(`<button type="button" class="v_add_btn v_add_partner" data-person-id="${escapeHTML(p.id)}"${genderAttr} title="${escapeHTML(t('Add Partner'))}" aria-label="${escapeHTML(t('Add Partner'))}">${plusIcon}</button>`);
            return controls.length ? `<span class="v_person_actions">${controls.join('')}</span>` : '';
        }).join('');

        const names = persons.map((p, i) => {
            const sep = i > 0 ? '<span class="v_couple_sep">&amp;</span>' : '';
            return `${sep}<span class="v_person_name${p.actualChild ? ' v_actual_child_name' : ''}${p.dead ? ' v_dead_name' : ''}"><span>${escapeHTML(p.name)}</span>${p.birthdayIcon}${p.birthOrderBadge}</span>`;
        }).join('');

        const toggle   = hasKids ? `<button class="v_toggle" aria-label="${escapeHTML(t('Toggle'))}">${V_CHEVRON}</button>` : '<span class="v_leaf_dot"></span>';
        const badge    = isExternal ? `<span class="v_external_badge">${escapeHTML(externalPartnerLabel(li))}</span>` : '';
        const count    = (hasKids && kidCount > 0) ? `<span class="v_child_count">${kidCount}</span>` : '';
        const childAnchor = persons.find(p => p.actualChild) || persons[0] || {};
        const personIds = persons.map(p => p.id).join(',');
        const personNames = persons.map(p => p.name).join('|');
        const personGenders = persons.map(p => p.gender).join(',');
        const canAddParents = persons.some(p => p.canAddParents);
        const addChild = canAddChild ? `<button type="button" class="v_add_btn v_add_child" data-second-parent-id="${escapeHTML(sourceAddChild?.dataset.secondParentId || '')}" data-person-id="${escapeHTML(sourceAddChild?.dataset.personId || childAnchor.id || '')}" data-person-gender="${escapeHTML(childAnchor.gender || '')}" title="${escapeHTML(t('Add'))}" aria-label="${escapeHTML(t('Add'))}">${plusIcon}</button>` : '';
        const inner    = hasKids ? childLis.map(cl => buildVTreeHTML(cl)).join('') : '';
        const children = hasKids ? `<div class="v_children"><div class="v_children_inner">${inner}</div></div>` : '';

        return `<div class="v_node${hasKids ? ' has-children' : ''}${isExternal ? ' v_external' : ''}" data-v-node-key="${escapeHTML(nodeKey)}" data-v-person-ids="${escapeHTML(personIds)}" data-v-person-names="${escapeHTML(personNames)}" data-v-person-genders="${escapeHTML(personGenders)}" data-v-can-add-parents="${canAddParents ? 'true' : 'false'}"><div class="v_node_header">${toggle}<div class="v_photos">${photos}</div><div class="v_names">${names}${personActions}</div><div class="v_node_actions">${addChild}</div>${badge}${count}</div>${children}</div>`;
    }

    function applyStoredVerticalSections(vtree) {
        const storedSections = getStoredVerticalSections();
        const hasStoredSections = Object.keys(storedSections).length > 0;

        vtree.querySelectorAll('.v_node.has-children').forEach((node, index) => {
            const key = node.getAttribute('data-v-node-key');
            const storedState = key ? storedSections[key] : null;
            const expanded = storedState
                ? storedState === 'expanded'
                : (!hasStoredSections && index === 0);

            node.classList.toggle('expanded', expanded);
        });
    }

    let verticalClicksBound = false;
    function buildVerticalTree() {
        const vtContainer = container.querySelector('.v_tree_container');
        const vtree       = vtContainer && vtContainer.querySelector('.v_tree');
        if (!vtree || vtree.dataset.built) return;

        const rootLi = canvas.querySelector('.tree > ul > li');
        if (!rootLi) {
            vtree.innerHTML = container.querySelector('.empty_tree_canvas')?.outerHTML || '';
            vtree.dataset.built = '1';
            return;
        }

        vtree.innerHTML    = buildVTreeHTML(rootLi);
        vtree.dataset.built = '1';

        applyStoredVerticalSections(vtree);
        window.racinageTreeLoading?.viewport(container);

        // The same root survives live rebuilds. Bind once for this mounted tree.
        if (verticalClicksBound) return;
        verticalClicksBound = true;
        vtree.addEventListener('click', (e) => {
            if (e.target.closest('.v_add_btn')) return;
            if (e.target.closest('.v_photos')) return;
            const header = e.target.closest('.v_node_header');
            if (!header) return;
            const node = header.parentElement;
            if (!node.classList.contains('has-children')) return;
            node.classList.toggle('expanded');
            storeVerticalSection(node.getAttribute('data-v-node-key'), node.classList.contains('expanded'));
        }, { signal: sig });
    }

    function setTreeEditing(enabled, persist = true) {
        container.classList.toggle('tree_editing', enabled);
        if (container.classList.contains('vertical')) window.racinageTreeLoading?.viewport(container);
        if (treeEditToggle) {
            treeEditToggle.classList.toggle('on', enabled);
            treeEditToggle.setAttribute('aria-pressed', enabled ? 'true' : 'false');
            treeEditToggle.querySelector('.perm_toggle_btn')?.classList.toggle('on', enabled);
        }
        if (persist) storeTreeEditing(enabled);
    }

    function actionSupportsExistingPerson(action) {
        return ['add_first_member', 'add_parents', 'add_spouse', 'add_partner', 'add_child', 'replace_person'].includes(action);
    }

    let existingPeopleRequestSeq = 0;
    let existingPeopleSearchTimer = null;
    let existingPeopleState = { search: '', offset: 0, hasMore: true, loading: false };
    let existingPeopleGenderFilter = '';
    const EXISTING_PEOPLE_PAGE_SIZE = 24;

    function partnerGenderFor(action, anchorGender = '') {
        if (!['add_spouse', 'add_partner'].includes(action)) return '';
        return anchorGender === 'M' ? 'F' : (anchorGender === 'F' ? 'M' : '');
    }

    function setPersonGenderOptions(form, allowedGender = '') {
        const select = form?.querySelector('[name="person_gender"]');
        if (!select) return;
        [...select.options].forEach(option => {
            const hidden = allowedGender !== '' && option.value !== allowedGender;
            option.hidden = hidden;
            option.disabled = hidden;
        });
        if (allowedGender !== '') select.value = allowedGender;
    }

    async function loadAvailablePeople(search = '', append = false) {
        const grid = document.getElementById('treeExistingPersonGrid');
        if (!grid) return;
        if (existingPeopleState.loading) return;
        if (append && !existingPeopleState.hasMore) return;
        const requestSeq = ++existingPeopleRequestSeq;
        if (!append) {
            existingPeopleState = { search, offset: 0, hasMore: true, loading: false };
            grid.innerHTML = `<div class="tree_existing_person_empty">${escapeHTML(t('Loading people...'))}</div>`;
        }
        existingPeopleState.loading = true;
        let loader = null;
        if (append) {
            loader = document.createElement('div');
            loader.className = 'tree_existing_person_empty tree_existing_person_loader';
            loader.textContent = t('Loading more people...');
            grid.appendChild(loader);
        }
        const data = new FormData();
        data.set('action', 'list_available_persons');
        data.set('tree_long_id', getActiveTreeId());
        data.set('search', existingPeopleState.search);
        data.set('offset', String(existingPeopleState.offset));
        data.set('limit', String(EXISTING_PEOPLE_PAGE_SIZE));
        data.set('gender', existingPeopleGenderFilter);

        try {
            const payload = await postTreeAction(data);
            if (requestSeq !== existingPeopleRequestSeq) return;
            loader?.remove();
            const people = payload.people || [];
            existingPeopleState.offset = payload.next_offset || (existingPeopleState.offset + people.length);
            existingPeopleState.hasMore = payload.has_more === true;
            if (!people.length && !append) {
                grid.innerHTML = `<div class="tree_existing_person_empty">${escapeHTML(t('No available people found.'))}</div>`;
                return;
            }
            const html = people.map(person => {
                const gender = person.gender === 'F' ? t('Female') : t('Male');
                const hidden = person.visibility === 'hidden' ? ` (${t('hidden')})` : '';
                const name = person.name || `Person #${person.id}`;
                const deceased = person.is_deceased ? `<i class="tree_existing_person_deceased" title="${escapeHTML(t('Deceased'))}" aria-label="${escapeHTML(t('Deceased'))}">&#10013;</i>` : '';
                return `
                    <button type="button" class="tree_existing_person_card" data-person-id="${escapeHTML(person.id)}">
                        ${deceased}
                        <img src="${escapeHTML(person.image_sm_url || '')}" alt="${escapeHTML(name)}">
                        <span>${escapeHTML(name)}</span>
                        <small>${gender}${hidden}</small>
                    </button>
                `;
            }).join('');
            if (append) grid.insertAdjacentHTML('beforeend', html);
            else grid.innerHTML = html || `<div class="tree_existing_person_empty">${escapeHTML(t('No available people found.'))}</div>`;
        } catch (err) {
            if (requestSeq !== existingPeopleRequestSeq) return;
            loader?.remove();
            grid.innerHTML = `<div class="tree_existing_person_empty">${escapeHTML(t('Could not load people.'))}</div>`;
            document.getElementById('treeActionError').textContent = err.message;
        } finally {
            if (requestSeq === existingPeopleRequestSeq) existingPeopleState.loading = false;
        }
    }

    function setPersonSource(source) {
        const form = document.getElementById('treeActionForm');
        if (!form) return;
        const existingOnly = form.elements.namedItem('action')?.value === 'replace_person' && container.dataset.treeCanAddPersons !== 'true';
        const normalized = existingOnly ? 'existing' : (['existing', 'paste'].includes(source) ? source : 'new');
        document.getElementById('treePersonSource').value = normalized;
        document.getElementById('treeExistingPersonId').value = '';
        form.classList.toggle('tree_existing_source', normalized === 'existing');
        form.querySelectorAll('.tree_person_source_tab').forEach(tab => {
            tab.classList.toggle('active', tab.getAttribute('data-person-source') === normalized);
        });
        const existingSection = document.getElementById('treeExistingPersonSection');
        if (existingSection) existingSection.hidden = normalized !== 'existing';
        const pasteSection = document.getElementById('treePastePartialSection');
        if (pasteSection) pasteSection.hidden = normalized !== 'paste';
        form.querySelectorAll('[data-tree-section="first_member"], [data-tree-section="person"]').forEach(section => {
            if (!section.hidden) section.classList.toggle('tree_new_person_hidden', normalized !== 'new');
        });
        form.querySelectorAll('[data-tree-parent-new-fields]').forEach(section => {
            section.hidden = normalized !== 'new';
        });
        const galleryDraft = form?.querySelector('[data-person-gallery-draft]');
        // One new person only: updates use the profile gallery, parent pairs have no single target.
        const galleryAction = ['add_first_member', 'add_spouse', 'add_partner', 'add_child'].includes(document.getElementById('treeActionName')?.value);
        if (galleryDraft) galleryDraft.hidden = normalized !== 'new' || !galleryAction;
        if (normalized === 'paste') window.setTimeout(() => document.getElementById('treePastePartialInput')?.focus(), 0);
        const search = document.getElementById('treeExistingPersonSearch');
        if (normalized === 'existing') {
            if (search) search.value = '';
            loadAvailablePeople('');
            window.setTimeout(() => search?.focus(), 0);
        }
    }

    function resetTreeImagePreviews(form) {
        window.racinagePersonProfilePhoto?.clear(form);
        form.querySelectorAll('.tree_image_picker').forEach(picker => {
            picker.classList.remove('has_image');
            const preview = picker.querySelector('.tree_image_preview');
            if (preview) preview.innerHTML = '<span>+</span>';
        });
    }

    // Local previews only; files upload after the person is saved (see the submit handler).
    let galleryDraftUrls = [];
    function renderGalleryDraft(form) {
        const section = form?.querySelector('[data-person-gallery-draft]');
        if (!section) return;
        galleryDraftUrls.forEach(url => URL.revokeObjectURL(url));
        const files = [...(section.querySelector('[data-person-gallery-files]')?.files || [])];
        const shown = files.slice(0, 5);
        galleryDraftUrls = shown.map(file => URL.createObjectURL(file));
        const grid = section.querySelector('[data-person-gallery-draft-grid]');
        grid.innerHTML = shown.map((file, index) => `<span>${file.type.startsWith('video/')
            ? `<video src="${galleryDraftUrls[index]}" preload="metadata" muted playsinline aria-label="${escapeHTML(file.name)}"></video>`
            : `<img src="${galleryDraftUrls[index]}" alt="${escapeHTML(file.name)}">`}</span>`).join('');
        grid.hidden = !files.length;
        section.querySelector('[data-person-gallery-draft-empty]').hidden = files.length > 0;
        section.querySelector('[data-person-gallery-draft-count]').textContent = (section.dataset.countLabel || '%s media files').replace('%s', files.length);
    }
    document.getElementById('treeActionForm')?.addEventListener('click', event => {
        if (event.target.closest('[data-person-gallery-draft-add]')) event.currentTarget.querySelector('[data-person-gallery-files]')?.click();
    }, { signal: sig });
    document.getElementById('treeActionForm')?.addEventListener('change', event => {
        if (event.target.matches('[data-person-gallery-files]')) renderGalleryDraft(event.currentTarget);
    }, { signal: sig });

    let modalSelectionRevision = 0;
    const pendingGallerySaves = new WeakMap();
    // Missing/foreign tree -> show the family's first tree instead of a "Family tree not found." error.
    async function ensureActiveTree(isStale = () => false) {
        const data = new FormData();
        data.set('action', 'resolve_tree');
        data.set('tree_long_id', getActiveTreeId());
        const selection = await postTreeAction(data);
        if (!isCurrentTreeView() || isStale()) return false;
        if (selection.tree_long_id && selection.tree_long_id === getActiveTreeId()) return true;
        const url = new URL(treeViewUrl(selection.tree_long_id, '', false), location.href);
        url.searchParams.set('collection', 'all');
        if (window.htmx) {
            await window.htmx.ajax('GET', url.href, { target: '#main_container', swap: 'multi:#main_container:outerHTML' });
            window.history.replaceState(window.history.state, '', url.href);
        } else window.location.href = url.href;
        return false;
    }

    // Birth position squares. A number is held by at most one child; taken numbers show who
    // holds them and can be moved ("Change to"), swapping with whoever held the new number.
    async function mountBirthPosition(box, params, selected = null) {
        if (!box) return;
        const orderInput = box.querySelector('[data-birth-order]');
        const movesInput = box.querySelector('[data-birth-moves]');
        const revision = (box._birthRevision || 0) + 1;
        box._birthRevision = revision;
        box._birth = null;
        box.hidden = true;
        [orderInput, movesInput].forEach(input => { input.disabled = true; input.value = ''; });
        if (!params) return;
        const data = new FormData();
        data.set('action', 'list_sibling_orders');
        data.set('tree_long_id', getActiveTreeId());
        Object.entries(params).forEach(([key, value]) => data.set(key, value || ''));
        let payload;
        try { payload = await postTreeAction(data); } catch (error) { return; }
        if (revision !== box._birthRevision || !payload.has_parents) return;
        const siblings = payload.siblings.map(sibling => ({ ...sibling, order: sibling.order === null ? null : Number(sibling.order) }));
        siblings.forEach(sibling => { sibling.initial = sibling.order; });
        selected = Number(selected) || null;
        box._birth = { siblings, selected, max: Math.max(10, selected || 0, ...siblings.map(sibling => sibling.order || 0)) };
        [orderInput, movesInput].forEach(input => { input.disabled = false; });
        box.hidden = false;
        renderBirthPosition(box);
        if (box._birthBound) return;
        box._birthBound = true;
        box.addEventListener('click', event => onBirthPositionClick(box, event), { signal: sig });
        box.addEventListener('keydown', event => { if (event.key === 'Escape' && !box.querySelector('.tree_birth_change').hidden) { event.stopPropagation(); closeBirthChange(box); } }, { signal: sig });
        box.addEventListener('pointerover', event => showBirthHolder(box, event.target.closest('.tree_birth_square.taken')), { signal: sig });
        box.addEventListener('focusin', event => showBirthHolder(box, event.target.closest('.tree_birth_square.taken')), { signal: sig });
        box.addEventListener('pointerout', () => showBirthHolder(box, null), { signal: sig });
        document.addEventListener('pointerdown', event => { if (!box.contains(event.target)) closeBirthChange(box); }, { signal: sig });
    }
    function renderBirthPosition(box) {
        const state = box._birth;
        if (!state) return;
        const holder = number => state.siblings.find(sibling => sibling.order === number);
        let squares = '';
        for (let number = 1; number <= state.max; number++) {
            const taken = holder(number);
            const selected = state.selected === number;
            const label = taken ? t('Position {n} is taken by {name}').replace('{n}', number).replace('{name}', taken.name) : t('Position {n}').replace('{n}', number);
            squares += `<button type="button" class="tree_birth_square${selected ? ' selected' : ''}${taken ? ' taken' : ''}" data-birth-number="${number}" aria-pressed="${selected}" aria-label="${escapeHTML(label)}">${number}</button>`;
        }
        squares += `<button type="button" class="tree_birth_square add" data-birth-add aria-label="${escapeHTML(t('Add a position'))}" title="${escapeHTML(t('Add a position'))}">+</button>`;
        box.querySelector('.tree_birth_position_grid').innerHTML = squares;
        box.querySelector('[data-birth-order]').value = state.selected || '';
        const moves = Object.fromEntries(state.siblings.filter(sibling => sibling.order !== sibling.initial).map(sibling => [sibling.id, sibling.order ?? '']));
        box.querySelector('[data-birth-moves]').value = Object.keys(moves).length ? JSON.stringify(moves) : '';
    }
    function placeNear(box, panel, square) {
        panel.style.left = `${Math.max(0, Math.min(square.offsetLeft, box.clientWidth - panel.offsetWidth))}px`;
        panel.style.top = `${square.offsetTop + square.offsetHeight + 6}px`;
    }
    function showBirthHolder(box, square) {
        const tip = box.querySelector('.tree_birth_holder_tip');
        const holder = square && box._birth?.siblings.find(sibling => sibling.order === Number(square.dataset.birthNumber));
        if (!holder || !box.querySelector('.tree_birth_change').hidden) { tip.hidden = true; return; }
        tip.innerHTML = `<img src="${escapeHTML(holder.image_url || '')}" alt=""><span>${escapeHTML(holder.name)}</span>`;
        tip.hidden = false;
        placeNear(box, tip, square);
    }
    function closeBirthChange(box) {
        const panel = box.querySelector('.tree_birth_change');
        if (panel && !panel.hidden) { panel.hidden = true; panel.innerHTML = ''; }
    }
    function onBirthPositionClick(box, event) {
        const state = box._birth;
        if (!state) return;
        if (event.target.closest('[data-birth-add]')) { state.max++; renderBirthPosition(box); return; }
        const apply = event.target.closest('[data-birth-apply]');
        if (apply) {
            const panel = box.querySelector('.tree_birth_change');
            const holder = state.siblings.find(sibling => sibling.id === Number(panel.dataset.holder));
            const target = Number(panel.querySelector('input').value);
            const from = holder?.order;
            if (!holder || !Number.isInteger(target) || target < 1 || target > 65535) return;
            const other = state.siblings.find(sibling => sibling !== holder && sibling.order === target);
            if (other) other.order = from;
            else if (state.selected === target) state.selected = from;
            holder.order = target;
            state.max = Math.max(state.max, target);
            closeBirthChange(box);
            renderBirthPosition(box);
            box.querySelector(`[data-birth-number="${from}"]`)?.focus();
            return;
        }
        const square = event.target.closest('[data-birth-number]');
        if (!square) return;
        const number = Number(square.dataset.birthNumber);
        const holder = state.siblings.find(sibling => sibling.order === number);
        if (!holder) {
            closeBirthChange(box);
            state.selected = state.selected === number ? null : number;
            renderBirthPosition(box);
            box.querySelector(`[data-birth-number="${number}"]`)?.focus();
            return;
        }
        const used = new Set([...state.siblings.map(sibling => sibling.order), state.selected]);
        let suggestion = 1;
        while (used.has(suggestion)) suggestion++;
        const panel = box.querySelector('.tree_birth_change');
        panel.dataset.holder = holder.id;
        panel.innerHTML = `<strong>${escapeHTML(t('Change to'))}</strong><small>${escapeHTML(holder.name)}</small><div><input type="number" min="1" max="65535" value="${suggestion}" aria-label="${escapeHTML(t('Change to'))}"><button type="button" class="tree_secondary_action" data-birth-apply>${escapeHTML(t('Apply'))}</button></div>`;
        panel.hidden = false;
        box.querySelector('.tree_birth_holder_tip').hidden = true;
        placeNear(box, panel, square);
        panel.querySelector('input').focus();
        panel.querySelector('input').addEventListener('keydown', keyEvent => { if (keyEvent.key === 'Enter') { keyEvent.preventDefault(); panel.querySelector('[data-birth-apply]').click(); } });
    }

    let treeActionBackHandler = null;
    async function openTreeModal(action, options = {}) {
        const revision = ++modalSelectionRevision;
        try {
            if (!await ensureActiveTree(() => revision !== modalSelectionRevision)) return;
        } catch (error) {
            window.racinageShowFeedback?.(error.message, 'fail');
            return;
        }
        const modal = document.getElementById('treeActionModal');
        const form = document.getElementById('treeActionForm');
        if (!modal || !form) return;

        form.querySelectorAll('[data-gallery-save-lock]').forEach(control => { control.disabled = false; delete control.dataset.gallerySaveLock; });
        form.reset();
        renderGalleryDraft(form);
        pendingGallerySaves.delete(form);
        form.querySelector('[name="tree_long_id"]').value = getActiveTreeId();
        const deathDateInput = form.querySelector('[name="person_death_date"]');
        if (deathDateInput) deathDateInput.disabled = false;
        form.querySelectorAll('[data-unknown-identity]').forEach(input => {
            ['first_name', 'last_name'].forEach(field => { const name = form.elements.namedItem(input.dataset.unknownIdentity + field); if (name) name.readOnly = false; });
        });
        resetTreeImagePreviews(form);
        existingPeopleGenderFilter = partnerGenderFor(action, options.anchorGender || '');
        setPersonGenderOptions(form, existingPeopleGenderFilter);
        document.getElementById('treeActionName').value = action;
        form.querySelectorAll('.tree_unknown_identity').forEach(label => { label.hidden = !['add_first_member', 'add_parents', 'add_child', 'add_spouse', 'add_partner'].includes(action); });
        const maritalStatus = form.querySelector('[name="person_marital_status"]');
        if (maritalStatus) maritalStatus.value = ['add_spouse', 'add_partner'].includes(action) ? 'married' : 'single';
        document.getElementById('treeActionAnchorId').value = options.anchorId || '';
        document.getElementById('treeActionChildId').value = options.childId || '';
        const secondParentInput = document.getElementById('treeActionSecondParentId');
        if (secondParentInput) secondParentInput.value = options.secondParentId || '';
        document.getElementById('treeActionError').textContent = '';
        document.getElementById('treePersonSource').value = 'existing';
        document.getElementById('treeExistingPersonId').value = '';
        form.classList.remove('tree_existing_source');
        const titles = {
            add_parents: t('Add Parents'),
            add_first_member: t('Add First Member'),
            add_spouse: t('Add Spouse'),
            add_partner: t('Add Partner'),
            add_child: t('Add Child'),
            update_person: t('Update Person'),
            replace_person: t('Replace Person'),
        };
        document.getElementById('treeActionTitle').textContent = titles[action] || t('Add Person');
        treeActionBackHandler = typeof options.onBack === 'function' ? options.onBack : null;
        const backButton = document.getElementById('treeActionBack');
        if (backButton) backButton.hidden = !treeActionBackHandler;

        const supportsExisting = actionSupportsExistingPerson(action);
        const sourceTabs = document.getElementById('treePersonSourceTabs');
        const existingSection = document.getElementById('treeExistingPersonSection');
        if (sourceTabs) {
            const existingOnly = action === 'replace_person' && container.dataset.treeCanAddPersons !== 'true';
            sourceTabs.hidden = !supportsExisting || existingOnly;
            sourceTabs.querySelector('[data-person-source="new"]')?.toggleAttribute('disabled', existingOnly);
            const pasteTab = sourceTabs.querySelector('[data-person-source="paste"]');
            if (pasteTab) pasteTab.hidden = !['add_child', 'add_first_member'].includes(action) || container.dataset.treeCanAddPersons !== 'true';
        }
        resetPastedPartial();
        mountBirthPosition(form.querySelector('[data-tree-section="person"] [data-birth-position]'),
            action === 'add_child' && options.anchorId ? { anchor_person_id: options.anchorId, second_parent_id: options.secondParentId || '' }
                : (action === 'update_person' && options.person?.id ? { person_id: options.person.id } : null),
            action === 'update_person' ? options.person?.sibling_birth_order : null);
        if (existingSection) existingSection.hidden = true;

        form.querySelectorAll('.tree_form_section').forEach(section => {
            const wanted = action === 'add_parents' ? 'parents' : (action === 'add_first_member' ? 'first_member' : 'person');
            section.hidden = section.getAttribute('data-tree-section') !== wanted;
            section.classList.remove('tree_new_person_hidden');
        });

        if (options.person) {
            const photo = options.person.image_lg_url || options.person.image_url || options.person.image_sm_url;
            const picker = form.querySelector('[name="person_image"]')?.closest('.tree_image_picker');
            if (photo && picker) {
                picker.classList.add('has_image');
                picker.querySelector('.tree_image_preview').innerHTML = `<img src="${escapeHTML(photo)}" alt="${escapeHTML(t('Profile image'))}">`;
            }
            form.querySelector('[name="person_first_name"]').value = options.person.first_name || '';
            form.querySelector('[name="person_last_name"]').value = options.person.last_name || '';
            setPersonGenderOptions(form, '');
            form.querySelector('[name="person_gender"]').value = options.person.gender || 'M';
            form.querySelector('[name="person_birth_date"]').value = options.person.birth_date || '';
            form.querySelector('[name="person_death_date"]').value = options.person.death_date || '';
            const unknownDeath = form.querySelector('[data-deceased-unknown]');
            if (unknownDeath) {
                unknownDeath.checked = Number(options.person.is_deceased) === 1 && !options.person.death_date;
                deathDateInput.disabled = unknownDeath.checked;
            }
            form.querySelector('[name="person_marital_status"]').value = options.person.marital_status || 'single';
            form.querySelector('[name="person_phone1"]').value = options.person.phone1 || '';
            form.querySelector('[name="person_phone2"]').value = options.person.phone2 || '';
            form.querySelector('[name="person_email1"]').value = options.person.email1 || '';
            form.querySelector('[name="person_email2"]').value = options.person.email2 || '';
            form.querySelector('[name="person_address"]').value = options.person.address || '';
            form.querySelector('[name="person_birth_place"]').value = options.person.birth_place || '';
            form.querySelector('[name="person_nationality"]').value = options.person.nationality || '';
            form.querySelector('[name="person_blood_group"]').value = options.person.blood_group || '';
            form.querySelector('[name="person_education_level"]').value = options.person.education_level || '';
            form.querySelector('[name="person_religion"]').value = options.person.religion || '';
            form.querySelector('[name="person_website"]').value = options.person.website || '';
            let personIdInput = form.querySelector('[name="person_id"]');
            if (!personIdInput) {
                personIdInput = document.createElement('input');
                personIdInput.type = 'hidden';
                personIdInput.name = 'person_id';
                form.appendChild(personIdInput);
            }
            personIdInput.value = options.person.id || '';
            if (action === 'update_person') window.racinagePersonProfilePhoto?.bind({
                form, input: form.querySelector('[name="person_image"]'), preview: picker?.querySelector('.tree_image_preview'),
                person: options.person, endpoint: rootUrl('includes/persons.include.php'), csrf: container.dataset.treePreferenceCsrf || '', translate: t,
                clientId: treeSyncClientId,
                onRemoved: photo => {
                    if (!isCurrentTreeView()) return;
                    canvas.querySelectorAll(`.one_person[data-person-id="${escapeSelector(photo.id)}"] img`).forEach(image => { image.src = photo.image_url; });
                }
            });
        }

        modal.classList.add('open');
        modal.setAttribute('aria-hidden', 'false');
        if (supportsExisting && options.source !== 'new') setPersonSource('existing');
        else setPersonSource('new');
        form.querySelector('input:not([type="hidden"]), select')?.focus();
    }

    function closeTreeModal() {
        ++modalSelectionRevision;
        const modal = document.getElementById('treeActionModal');
        if (!modal) return;
        modal.classList.remove('open');
        modal.setAttribute('aria-hidden', 'true');
    }

    async function postTreeAction(formData, requestSignal = sig, renewed = false) {
        if (!isCurrentTreeView()) throw new DOMException('The tree view changed.', 'AbortError');
        if (!formData.has('tree_sync_client_id')) formData.set('tree_sync_client_id', treeSyncClientId);
        if (container.dataset.partialTreeId && !formData.has('partial_id')) formData.set('partial_id', container.dataset.partialTreeId);
        if (container.dataset.partialTreeTemporary && !formData.has('partial_temp')) formData.set('partial_temp', container.dataset.partialTreeTemporary);
        const response = await fetch(rootUrl('includes/tree_save.include.php'), {
            method: 'POST',
            body: formData,
            signal: requestSignal,
            credentials: 'same-origin',
            headers: {
                'Accept': 'application/json',
                'X-Racinage-CSRF': container?.dataset.treePreferenceCsrf || ''
            },
        });
        const payload = await response.json().catch(() => ({ ok: false, message: 'Unexpected server response.' }));
        if (!isCurrentTreeView()) throw new DOMException('The tree view changed.', 'AbortError');
        if (response.status === 401 || response.redirected) throw new Error(t('Your sign-in has expired. Your edits are still here. Sign in again in another tab, then save.'));
        if (response.status === 403 && payload.error_code === 'csrf_expired' && !renewed) {
            const renewal = new FormData();renewal.set('action','session_refresh');
            renewal.set('expected_user',container.dataset.treeSessionUser || '');renewal.set('expected_family',container.dataset.treeSessionFamily || '');
            const refreshed = await fetch(rootUrl('includes/tree_save.include.php'), {method:'POST',body:renewal,signal:requestSignal,credentials:'same-origin',cache:'no-store',headers:{'Accept':'application/json','X-Racinage-Session-Refresh':'1'}});
            const session = await refreshed.json().catch(()=>({ok:false}));
            if (!isCurrentTreeView()) throw new DOMException('The tree view changed.', 'AbortError');
            if(refreshed.status===401 || refreshed.redirected)throw new Error(t('Your sign-in has expired. Your edits are still here. Sign in again in another tab, then save.'));
            if(!refreshed.ok || !session.ok || !/^[a-f0-9]{32}$/.test(session.csrf || ''))throw new Error(session.message || t('Your account or family changed. Reopen this person before saving.'));
            container.dataset.treePreferenceCsrf=session.csrf;
            return postTreeAction(formData,requestSignal,true);
        }
        if (!response.ok || !payload.ok) {
            if(payload.error_code==='csrf_expired')throw new Error(t('Your sign-in has expired. Your edits are still here. Sign in again in another tab, then save.'));
            throw Object.assign(new Error(payload.message || 'Tree action failed.'), { code: payload.error_code || '' });
        }
        return payload;
    }

    let treeSelectionQueue = Promise.resolve();
    let treeSelectionRevision = 0;

    async function selectTree(treeId, focusPersonId = '') {
        const revision = ++treeSelectionRevision;
        treeSelectionQueue = treeSelectionQueue.catch(() => {}).then(async () => {
            if (!isCurrentTreeView() || revision !== treeSelectionRevision) return;
            sessionStorage.removeItem('racinage.partial_tree.'+getActiveTreeId());
            if (treeId && !isPublicPreview && container.dataset.treePreferenceCsrf) {
                const data = new FormData();
                data.set('action', 'select_tree');
                data.set('tree_long_id', treeId);
                data.set('tree_preference_csrf', container.dataset.treePreferenceCsrf);
                const selection = await postTreeAction(data);
                treeId = selection.tree_long_id || '';
            }
            if (isCurrentTreeView() && revision === treeSelectionRevision) await reloadTree(treeId, focusPersonId, false);
        });
        return treeSelectionQueue;
    }

    async function refreshTreeFromLiveEvent() {
        if (!isCurrentTreeView()) return;
        if (treeLiveRefreshRunning) {
            treeLiveRefreshAgain = true;
            return;
        }
        treeLiveRefreshRunning = true;
        try {
            do {
                treeLiveRefreshAgain = false;
                const data = new FormData();
                data.set('action', 'get_tree_state');
                data.set('tree_long_id', getActiveTreeId());
                const payload = await postTreeAction(data);
                await applyTreePayload(payload);
            } while (treeLiveRefreshAgain && !sig.aborted);
        } catch (error) {
            // The next SSE event or reconnect will retry without interrupting local editing.
        } finally {
            treeLiveRefreshRunning = false;
        }
    }

    function connectTreeLiveStream() {
        const baseUrl = container.dataset.treeLiveUrl || '';
        if (!baseUrl || !window.EventSource || !isCurrentTreeView()) return;
        const url = `${baseUrl}&after=${treeLiveAfter}&initial=${treeLiveReady ? '0' : '1'}`;
        treeLiveSource = new EventSource(url, { withCredentials: true });
        treeLiveSource.addEventListener('ready', event => {
            const update = JSON.parse(event.data || '{}');
            treeLiveAfter = Math.max(treeLiveAfter, Number(update.id || 0));
            treeLiveReady = true;
        });
        treeLiveSource.addEventListener('tree', event => {
            const update = JSON.parse(event.data || '{}');
            treeLiveAfter = Math.max(treeLiveAfter, Number(update.id || 0));
            if (update.clientId !== treeSyncClientId) refreshTreeFromLiveEvent();
        });
        treeLiveSource.onerror = () => {
            treeLiveSource?.close();
            if (!sig.aborted) {
                window.clearTimeout(treeLiveReconnect);
                treeLiveReconnect = window.setTimeout(connectTreeLiveStream, 1800);
            }
        };
    }

    function openTreeUtilityDialog(options = {}) {
        if (!isCurrentTreeView()) return Promise.resolve(null);
        const modal = document.getElementById('treeUtilityModal');
        const form = document.getElementById('treeUtilityForm');
        const input = document.getElementById('treeUtilityInput');
        const field = document.getElementById('treeUtilityField');
        const choices = document.getElementById('treeUtilityChoices');
        const error = document.getElementById('treeUtilityError');
        const confirmBtn = document.getElementById('treeUtilityConfirm');
        const cancelBtn = document.getElementById('treeUtilityCancel');
        if (!modal || !form || !input || !field || !choices || !confirmBtn || !cancelBtn) {
            return Promise.resolve(null);
        }

        return new Promise((resolve) => {
            let settled = false;
            const needsInput = options.type === 'prompt';
            const searchable = typeof options.loadChoices === 'function';
            const trigger = document.activeElement;
            let searchTimer = 0, searchRevision = 0, choosing = false;

            const finish = (value) => {
                if (settled) return;
                settled = true;
                modal.classList.remove('open');
                modal.setAttribute('aria-hidden', 'true');
                form.classList.remove('danger');
                form.classList.remove('tree_print_modal');
                options.content?.remove();
                cleanup();
                input.disabled = false;
                cancelBtn.disabled = false;
                const close = document.getElementById('treeUtilityClose'); if (close) close.disabled = false;
                if (trigger?.isConnected) trigger.focus({ preventScroll: true });
                resolve(value);
            };
            const cleanup = () => {
                window.clearTimeout(searchTimer);
                searchRevision++;
                input.removeEventListener('input', onSearch);
                modal.removeEventListener('keydown', onKey);
                form.removeEventListener('submit', onSubmit);
                modal.removeEventListener('click', onBackdrop);
                choices.removeEventListener('click', onChoice);
                document.getElementById('treeUtilityClose')?.removeEventListener('click', onCancel);
                document.getElementById('treeUtilityBack')?.removeEventListener('click', onBack);
                cancelBtn.removeEventListener('click', onCancel);
                sig.removeEventListener('abort', onAbort);
            };
            const onCancel = () => { if (!choosing) finish(null); };
            const onBack = () => { if (choosing) return; finish(null); options.onBack(); };
            const onAbort = () => finish(null);
            const onBackdrop = (e) => {
                if (e.target === modal && !choosing) finish(null);
            };
            const onSubmit = (e) => {
                e.preventDefault();
                error.textContent = '';
                if (searchable) return;
                if (!needsInput) {
                    options.onConfirm?.();
                    finish(true);
                    return;
                }
                const value = input.value.trim();
                if (options.required !== false && value === '') {
                    error.textContent = options.requiredMessage || 'This field is required.';
                    input.focus();
                    return;
                }
                finish(value);
            };
            const renderChoices = rows => {
                choices.innerHTML = rows.map(choice => `<button type="button" class="tree_utility_choice" data-choice-value="${escapeHTML(choice.value ?? choice.id)}"><strong>${escapeHTML(choice.label)}</strong>${choice.description ? `<span>${escapeHTML(choice.description)}</span>` : ''}</button>`).join('');
            };
            const loadChoices = async () => {
                const revision = ++searchRevision;
                choices.setAttribute('aria-busy', 'true');
                choices.innerHTML = `<p role="status">${escapeHTML(t('Loading people...'))}</p>`;
                try {
                    const rows = await options.loadChoices(input.value.trim());
                    if (settled || revision !== searchRevision) return;
                    renderChoices(rows);
                    error.textContent = rows.length ? '' : t('No couples found.');
                } catch (failure) { if (!settled && revision === searchRevision) error.textContent = failure.message; }
                finally { if (revision === searchRevision) choices.removeAttribute('aria-busy'); }
            };
            const onSearch = () => { searchRevision++; error.textContent = ''; choices.innerHTML = `<p role="status">${escapeHTML(t('Loading people...'))}</p>`; choices.setAttribute('aria-busy', 'true'); window.clearTimeout(searchTimer); searchTimer = window.setTimeout(loadChoices, 220); };
            const onKey = event => {
                if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onCancel(); }
                if (event.key !== 'Tab') return;
                const controls = [...modal.querySelectorAll('button,input,[tabindex="0"]')].filter(node => !node.disabled && node.getClientRects().length);
                const first = controls[0], last = controls.at(-1);
                if (!first) { event.preventDefault(); form.focus(); return; }
                if (!controls.includes(document.activeElement)) { event.preventDefault(); (event.shiftKey ? last : first).focus(); return; }
                if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
                else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
            };
            const onChoice = async (e) => {
                const option = e.target.closest('[data-choice-value]');
                if (!option || choosing) return;
                if (!options.onChoose) { finish(option.getAttribute('data-choice-value')); return; }
                choosing = true; input.disabled = true; cancelBtn.disabled = true;
                const close = document.getElementById('treeUtilityClose'); if (close) close.disabled = true;
                choices.querySelectorAll('button').forEach(button => button.disabled = true);
                form.tabIndex = -1;
                form.focus({ preventScroll: true });
                try { const result = await options.onChoose(option.getAttribute('data-choice-value')); finish(result); }
                catch (failure) { if (!settled) error.textContent = failure.message; }
                finally { choosing = false; if (!settled) { input.disabled = false; cancelBtn.disabled = false; if (close) close.disabled = false; choices.querySelectorAll('button').forEach(button => button.disabled = false); option.focus(); } }
            };

            document.getElementById('treeUtilityTitle').textContent = options.title || 'Confirm';
            document.getElementById('treeUtilityMessage').textContent = options.message || '';
            document.getElementById('treeUtilityInputLabel').textContent = options.inputLabel || 'Name';
            input.value = options.defaultValue || '';
            input.disabled = false;
            field.hidden = !needsInput && !searchable;
            if (searchable) choices.before(field);
            choices.hidden = options.type !== 'choice';
            choices.removeAttribute('aria-busy');
            choices.innerHTML = options.type === 'choice'
                ? (options.choices || []).map(choice => `
                    <button type="button" class="tree_utility_choice" data-choice-value="${escapeHTML(choice.value)}">
                        <strong>${escapeHTML(choice.label)}</strong>
                        ${choice.description ? `<span>${escapeHTML(choice.description)}</span>` : ''}
                    </button>
                  `).join('')
                : '';
            error.textContent = '';
            cancelBtn.hidden = options.type === 'alert';
            confirmBtn.hidden = options.type === 'choice';
            confirmBtn.textContent = options.confirmText || 'OK';
            cancelBtn.textContent = options.cancelText || 'Cancel';
            form.classList.toggle('danger', options.danger === true);
            form.classList.toggle('tree_print_modal', options.type === 'print');
            if (options.content) document.getElementById('treeUtilityMessage').after(options.content);
            confirmBtn.disabled = Boolean(options.ready);
            if (options.ready) Promise.resolve().then(options.ready).then(() => {
                if (!settled) confirmBtn.disabled = false;
            }).catch((errorValue) => {
                if (!settled) error.textContent = errorValue.message;
            });

            form.addEventListener('submit', onSubmit);
            modal.addEventListener('click', onBackdrop);
            choices.addEventListener('click', onChoice);
            document.getElementById('treeUtilityClose')?.addEventListener('click', onCancel);
            const backButton = document.getElementById('treeUtilityBack');
            if (backButton) {
                backButton.hidden = typeof options.onBack !== 'function';
                if (!backButton.hidden) backButton.addEventListener('click', onBack);
            }
            cancelBtn.addEventListener('click', onCancel);
            sig.addEventListener('abort', onAbort, { once: true });
            modal.addEventListener('keydown', onKey);
            if (searchable) { input.addEventListener('input', onSearch); loadChoices(); }

            modal.classList.add('open');
            modal.setAttribute('aria-hidden', 'false');
            window.setTimeout(() => {
                if (searchable) { input.focus(); } else if (options.type === 'choice') {
                    choices.querySelector('button')?.focus();
                } else if (needsInput) {
                    input.focus();
                    input.select();
                } else {
                    confirmBtn.focus();
                }
            }, 0);
        });
    }

    function treePrompt(message, defaultValue = '', title = t('Enter Details'), onBack = null) {
        return openTreeUtilityDialog({
            type: 'prompt',
            onBack,
            title,
            message,
            defaultValue,
            inputLabel: t('Name'),
            confirmText: t('Save'),
        });
    }

    function treeConfirm(message, title = t('Confirm'), danger = false, onBack = null) {
        return openTreeUtilityDialog({
            type: 'confirm',
            onBack,
            title,
            message,
            danger,
            confirmText: danger ? t('Delete') : 'OK',
        });
    }

    function treeAlert(message, title = t('Notice')) {
        return openTreeUtilityDialog({
            type: 'alert',
            title,
            message,
            confirmText: 'OK',
        });
    }

    function showTreeFeedback(message) {
        const toast = document.createElement('div');
        toast.className = 'tree_feedback_toast';
        toast.textContent = message;
        document.body.appendChild(toast);
        window.setTimeout(() => toast.classList.add('visible'), 10);
        window.setTimeout(() => {
            toast.classList.remove('visible');
            window.setTimeout(() => toast.remove(), 180);
        }, 1600);
    }

    function treeChoice(message, choices, title = t('Choose Action'), onBack = null) {
        return openTreeUtilityDialog({
            type: 'choice',
            title,
            message,
            choices,
            onBack,
        });
    }

    async function fetchPerson(personId) {
        if (container.dataset.publicToken) {
            const params = new URLSearchParams({
                action: 'get_public_tree_person',
                token: container.dataset.publicToken,
                person_id: personId,
            });
            if (container.dataset.publicFamilyToken) params.set('family_token', container.dataset.publicFamilyToken);
            const response = await fetch(rootUrl(`view?${params.toString()}`), {
                headers: { 'Accept': 'application/json' },
            });
            const payload = await response.json().catch(() => ({ ok: false, message: t('Unexpected server response.') }));
            if (!response.ok || !payload.ok) throw new Error(payload.message || t('Could Not Load Person'));
            return payload.person;
        }
        const data = new FormData();
        data.set('action', 'get_person');
        data.set('person_id', personId);
        data.set('tree_long_id', getActiveTreeId());
        return (await postTreeAction(data)).person;
    }

    function findVerticalPersonNode(personId = '') {
        const id = String(personId || '');
        if (!id) return null;
        return [...container.querySelectorAll('.v_node')].find(node =>
            (node.getAttribute('data-v-person-ids') || '').split(',').includes(id)
        ) || null;
    }

    function expandVerticalAncestors(node) {
        let parent = node?.parentElement?.closest('.v_node');
        while (parent) {
            if (parent.classList.contains('has-children')) {
                parent.classList.add('expanded');
                storeVerticalSection(parent.getAttribute('data-v-node-key'), true);
            }
            parent = parent.parentElement?.closest('.v_node');
        }
    }

    function centerFocusedElement(target) {
        if (!target) return;
        if (container.classList.contains('vertical')) {
            target.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
            return;
        }
        const containerRect = container.getBoundingClientRect();
        const targetRect = target.getBoundingClientRect();
        state.pointX += (containerRect.left + containerRect.width / 2) - (targetRect.left + targetRect.width / 2);
        state.pointY += (containerRect.top + containerRect.height / 2) - (targetRect.top + targetRect.height / 2);
        canvas.style.transition = 'transform 0.28s ease';
        setTransform();
        window.setTimeout(() => { canvas.style.transition = ''; }, 320);
    }

    function flashFocusedPerson(personId = '', recenter = true) {
        const params = new URLSearchParams(window.location.search);
        const focusPersonId = personId || params.get('person') || params.get('p') || params.get('focus_person_id');
        if (!focusPersonId) return;
        const escaped = escapeSelector(focusPersonId);
        const target = canvas.querySelector(`.one_person[data-person-long-id="${escaped}"]`)
            || canvas.querySelector(`.one_person[data-person-id="${escaped}"]`);
        if (!target) return;
        let flashTarget = target;
        if (container.classList.contains('vertical')) {
            buildVerticalTree();
            const verticalNode = findVerticalPersonNode(target.getAttribute('data-person-id') || focusPersonId);
            if (verticalNode) {
                expandVerticalAncestors(verticalNode);
                flashTarget = verticalNode.querySelector(':scope > .v_node_header') || verticalNode;
            }
        }
        if (!personId && params.get('focus_person_id') && !params.get('p')) {
            params.delete('focus_person_id');
            params.set('p', target.getAttribute('data-person-long-id') || focusPersonId);
            const query = params.toString();
            window.history.replaceState(window.history.state, '', `${window.location.pathname}${query ? `?${query}` : ''}${window.location.hash}`);
        }
        if (recenter) centerFocusedElement(flashTarget);
        flashTarget.classList.add('tree_person_focus_flash');
        window.setTimeout(() => flashTarget.classList.remove('tree_person_focus_flash'), 3800);
    }

    function highlightFocusedPerson(personId = '') {
        flashFocusedPerson(personId, true);
    }

    // Selecting a result already present in this tree only changes the focus.
    // Typing and result swaps never replace or recenter the mounted tree.
    document.addEventListener('click', (event) => {
        if (!isCurrentTreeView() || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const link = event.target.closest('.global_search_group_items a.global_search_result');
        if (!link) return;
        const url = new URL(link.href, window.location.href);
        if (url.origin !== window.location.origin || !/\/(?:trees|tree)(?:\.php)?\/?$/.test(url.pathname)) return;
        if (url.searchParams.has('tree') && url.searchParams.get('tree') !== getActiveTreeId()) return;
        const personId = url.searchParams.get('p') || url.searchParams.get('person') || url.searchParams.get('focus_person_id');
        if (!personId) return;
        const escaped = escapeSelector(personId);
        if (!canvas.querySelector(`.one_person[data-person-long-id="${escaped}"], .one_person[data-person-id="${escaped}"]`)) return;
        event.preventDefault();
        event.stopPropagation();
        const current = new URL(window.location.href);
        current.searchParams.set('p', personId);
        window.history.replaceState(window.history.state, '', current);
        highlightFocusedPerson(personId);
        window.racinageCloseSearch?.();
    }, { capture: true, signal: sig });

    const statsPublicPreview = isPublicPreview || !!container.dataset.publicToken;
    const statsModal = document.getElementById('treeStatsModal');
    const statsGrid = document.getElementById('treeStatsGrid');
    const statsSearch = document.getElementById('treeStatsSearch');
    const statsStatus = document.getElementById('treeStatsStatus');
    const statsRetry = document.getElementById('treeStatsRetry');
    let statsState = null;
    let statsRequest = null;
    let statsTimer = 0;
    let statsTrigger = null;

    function closeStatsModal() {
        window.clearTimeout(statsTimer);
        statsRequest?.abort();
        statsState = null;
        statsModal?.classList.remove('open');
        statsModal?.setAttribute('aria-hidden', 'true');
        if (statsTrigger?.isConnected) statsTrigger.focus({ preventScroll: true });
    }

    async function loadStatsPeople() {
        const state = statsState;
        if (!state || state.loading || !state.hasMore) return;
        state.loading = true;
        statsRequest = new AbortController();
        statsGrid.setAttribute('aria-busy', 'true');
        statsRetry.hidden = true;
        statsStatus.textContent = t(state.offset ? 'Loading more people...' : 'Loading people...');
        try {
            let payload;
            if (statsPublicPreview) {
                // Public previews reuse only information already disclosed in their canvas.
                const people = state.publicPeople.filter(person => person.name.toLocaleLowerCase().includes(state.search.toLocaleLowerCase()));
                await new Promise(resolve => window.requestAnimationFrame(resolve));
                payload = { people: people.slice(state.offset, state.offset + 12), next_offset: state.offset + 12, has_more: people.length > state.offset + 12 };
            } else {
                const data = new FormData();
                data.set('action', 'list_stat_persons');
                data.set('tree_long_id', state.treeId);
                data.set('category', state.category);
                data.set('search', state.search);
                data.set('offset', String(state.offset));
                Object.entries(state.filters).forEach(([key, value]) => data.set(key, value));
                payload = await postTreeAction(data, statsRequest.signal);
            }
            if (statsState !== state || state.treeId !== getActiveTreeId()) return;
            // Dropdowns for values present in this tree; keep the current choice.
            Object.entries(payload.filter_options || {}).forEach(([key, values]) => {
                const select = statsModal.querySelector(`[data-stat-options="${key}"]`);
                if (!select) return;
                const current = select.value;
                const first = select.options[0]?.outerHTML || '';
                select.innerHTML = first + values.map(value => typeof value === 'object'
                    ? `<option value="${escapeHTML(value.id)}">${escapeHTML(value.name)}</option>`
                    : `<option value="${escapeHTML(value)}">${escapeHTML(value)}</option>`).join('');
                select.value = [...select.options].some(option => option.value === current) ? current : '';
                select.closest('label').hidden = values.length === 0 && !current;
            });
            const html = (payload.people || []).map(person => `<button type="button" class="tree_existing_person_card" data-stat-person="${escapeHTML(person.id)}" title="${escapeHTML(person.name)}">
                <img loading="lazy" src="${escapeHTML(person.image_sm_url || '')}" alt="">
                <span>${escapeHTML(person.name || t('Person'))}</span>
                ${statsPublicPreview ? '' : `<small>${escapeHTML(person.gender === 'F' ? t('Female') : t('Male'))}${person.is_deceased ? ' · ' + escapeHTML(t('Deceased')) : ''}</small>`}
            </button>`).join('');
            statsGrid.insertAdjacentHTML('beforeend', html);
            state.offset = payload.next_offset;
            state.hasMore = payload.has_more === true;
            statsStatus.textContent = statsGrid.children.length ? '' : t('No people found.');
            state.failed = false;
        } catch (error) {
            if (statsState !== state || error.name === 'AbortError') return;
            state.failed = true;
            statsStatus.textContent = t('Could not load people.');
            statsRetry.textContent = t('Retry');
            statsRetry.hidden = false;
        } finally {
            if (statsState === state) {
                state.loading = false;
                statsGrid.setAttribute('aria-busy', 'false');
                if (!state.failed) requestAnimationFrame(fillStatsGrid);
            }
        }
    }

    function fillStatsGrid() {
        if (statsModal?.classList.contains('open') && statsGrid.scrollHeight - statsGrid.scrollTop - statsGrid.clientHeight < 160) loadStatsPeople();
    }

    function resetStatsSearch() {
        if (!statsState) return;
        statsRequest?.abort();
        statsState = { ...statsState, search: statsSearch.value.trim(), filters: Object.fromEntries([...statsModal.querySelectorAll('[data-stat-filter]')].map(el => [el.dataset.statFilter, el.value])), offset: 0, hasMore: true, loading: false };
        statsGrid.replaceChildren();
        statsGrid.scrollTop = 0;
        statsRetry.hidden = true;
        statsStatus.textContent = t('Loading people...');
    }

    document.addEventListener('click', event => {
        const trigger = event.target.closest('[data-tree-stat]');
        if (!trigger || !isCurrentTreeView() || !statsModal) return;
        closeStatsModal();
        statsTrigger = trigger;
        const category = trigger.dataset.treeStat;
        const seen = new Set();
        const publicPeople = statsPublicPreview ? [...canvas.querySelectorAll('.one_person[data-person-id]')].filter(card => {
            const id = card.dataset.personId;
            if (seen.has(id)) return false;
            seen.add(id);
            const gender = card.dataset.personGender;
            if (category === 'women' && gender !== 'F' || category === 'men' && gender !== 'M') return false;
            if (category === 'deceased' && !card.classList.contains('dead')) return false;
            if (category.endsWith('_in_law') && (!card.matches('.couple > .one_person:nth-child(2)') || gender !== (category === 'brothers_in_law' ? 'M' : 'F'))) return false;
            return true;
        }).map(card => ({ id: card.dataset.personId, name: card.querySelector(':scope > span')?.textContent.trim() || t('Person'), image_sm_url: card.querySelector('img')?.src || '' })) : [];
        statsState = { category, treeId: getActiveTreeId(), search: '', filters: {}, offset: 0, hasMore: true, loading: false, publicPeople };
        document.getElementById('treeStatsTitle').textContent = trigger.querySelector('.title').textContent.replace(/:\s*$/, '');
        statsSearch.value = '';
        statsModal.querySelector('[data-stats-filters]').hidden = statsPublicPreview;
        statsModal.querySelectorAll('[data-stat-filter]').forEach(el => el.value = '');
        statsGrid.replaceChildren();
        statsGrid.scrollTop = 0;
        statsModal.classList.add('open');
        statsModal.setAttribute('aria-hidden', 'false');
        statsSearch.focus({ preventScroll: true });
        loadStatsPeople();
    }, { signal: sig });
    statsModal?.querySelector('[data-stats-close]')?.addEventListener('click', closeStatsModal, { signal: sig });
    statsSearch?.addEventListener('input', () => {
        window.clearTimeout(statsTimer);
        resetStatsSearch();
        statsTimer = window.setTimeout(loadStatsPeople, 250);
    }, { signal: sig });
    statsModal?.querySelectorAll('[data-stat-filter]').forEach(el => el.addEventListener('change', () => {
        window.clearTimeout(statsTimer); resetStatsSearch(); loadStatsPeople();
    }, { signal: sig }));
    statsRetry?.addEventListener('click', loadStatsPeople, { signal: sig });
    statsModal?.querySelector('[data-stats-reset]')?.addEventListener('click', () => {
        statsModal.querySelectorAll('[data-stat-filter]').forEach(el => { el.value = ''; });
        window.clearTimeout(statsTimer); resetStatsSearch(); loadStatsPeople();
    }, { signal: sig });
    statsGrid?.addEventListener('scroll', () => {
        if (statsGrid.scrollHeight - statsGrid.scrollTop - statsGrid.clientHeight < 100) loadStatsPeople();
    }, { passive: true, signal: sig });
    statsGrid?.addEventListener('click', event => {
        const card = event.target.closest('[data-stat-person]');
        if (!card) return;
        if (statsPublicPreview) { closeStatsModal(); highlightFocusedPerson(card.dataset.statPerson); return; }
        statsModal.classList.remove('open');
        statsModal.setAttribute('aria-hidden', 'true');
        openPersonModal(card.dataset.statPerson, '', null, null, () => {
            statsModal.classList.add('open');
            statsModal.setAttribute('aria-hidden', 'false');
            card.isConnected ? card.focus({ preventScroll: true }) : statsSearch?.focus({ preventScroll: true });
        });
    }, { signal: sig });
    statsModal?.addEventListener('keydown', event => {
        if (event.key === 'Escape') { event.stopPropagation(); closeStatsModal(); }
        if (event.key !== 'Tab') return;
        const focusable = [...statsModal.querySelectorAll('button, input, select')].filter(el => !el.disabled && el.getClientRects().length);
        const first = focusable[0], last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    }, { signal: sig });
    sig.addEventListener('abort', () => { window.clearTimeout(statsTimer); statsRequest?.abort(); statsState = null; }, { once: true });

    function applyStatsDelta(delta = {}) {
        const deceasedDelta = Number(delta.deceased || 0);
        if (!deceasedDelta) return;
        const label = t('Deceased').replace(/:\s*$/, '').trim().toLowerCase();
        const stat = [...document.querySelectorAll('.wrapper_stats .one_value_stats')].find(item => {
            const title = item.querySelector('.title')?.textContent || '';
            return title.replace(/:\s*$/, '').trim().toLowerCase() === label;
        });
        const valueEl = stat?.querySelector('.value');
        if (!valueEl) return;
        const next = Math.max(0, (parseInt(valueEl.textContent, 10) || 0) + deceasedDelta);
        valueEl.textContent = String(next);
    }

    function applyPersonPatch(patch = {}) {
        const personId = patch.id ? String(patch.id) : '';
        if (!personId) return false;
        const selector = `.one_person[data-person-id="${escapeSelector(personId)}"]`;
        const people = [...canvas.querySelectorAll(selector)];
        if (!people.length) return false;
        people.forEach(person => {
            if (patch.visibility) {
                person.classList.toggle('tree_person_hidden', patch.visibility === 'hidden');
            }
            if (typeof patch.is_deceased !== 'undefined') {
                person.classList.toggle('dead', !!patch.is_deceased);
            }
            if (typeof patch.label === 'string') {
                const label = person.querySelector(':scope > span');
                if (label) label.textContent = patch.label;
            }
        });
        const vtree = container.querySelector('.v_tree');
        if (vtree) {
            vtree.innerHTML = '';
            delete vtree.dataset.built;
            if (container.classList.contains('vertical')) buildVerticalTree();
        }
        window.setTimeout(() => flashFocusedPerson(personId, false), 50);
        return true;
    }

    async function refreshHorizontalTreeConnectors() {
        await waitForHorizontalAssets();
        await drawHorizontalChildConnectors();
    }

    let treePayloadQueue = Promise.resolve();

    function treeFragmentIsUsable() {
        const treeEl = canvas.querySelector('.tree');
        if (!treeEl) return false;
        return !!treeEl.querySelector(':scope > ul > li, :scope > .empty_tree_canvas');
    }

    function applyProfileTreePayload(payload) {
        const current = canvas.querySelector(':scope > .tree');
        if (!payload.profile_update || !current || payload.tree_long_id !== getActiveTreeId()) return false;
        const incoming = document.createElement('div');
        incoming.innerHTML = payload.tree_html || '';
        const structure = (root) => {
            const branches = [...root.querySelectorAll('li')];
            const indexes = new Map(branches.map((branch, index) => [branch, index]));
            return JSON.stringify(branches.map(branch => [
                indexes.get(branch.parentElement?.closest('li')) ?? -1,
                branch.classList.contains('external_partner'),
                branch.querySelector(':scope > .couple')?.dataset.relationshipId || '',
                [...branch.querySelectorAll(':scope > .one_person, :scope > .couple > .one_person')]
                    .map(person => [person.dataset.personId, person.dataset.personGender]),
            ]));
        };
        if (structure(current) !== structure(incoming)) return false;
        const cards = [...current.querySelectorAll('.one_person')];
        const nextCards = [...incoming.querySelectorAll('.one_person')];
        if (!cards.length || cards.length !== nextCards.length || cards.some((card, i) => card.dataset.personId !== nextCards[i].dataset.personId)) return false;
        cards.forEach((card, index) => {
            const next = nextCards[index];
            // Keep the card and its couple mounted. Layout-owned inline styles
            // on external partners must survive a profile/image save too.
            for (const attribute of next.attributes) {
                if (attribute.name !== 'style') card.setAttribute(attribute.name, attribute.value);
            }
            if (card.innerHTML !== next.innerHTML) card.innerHTML = next.innerHTML;
        });
        const stats = document.querySelector('.wrapper_stats');
        if (stats && typeof payload.stats_html === 'string') stats.innerHTML = payload.stats_html;
        const vtree = container.querySelector('.v_tree');
        if (vtree) delete vtree.dataset.built;
        if (container.classList.contains('vertical')) buildVerticalTree();
        flashFocusedPerson(payload.focus_person_id || '', false);
        return true;
    }

    async function applyTreePayloadNow(payload) {
        if (applyProfileTreePayload(payload)) return;
        if (!isCurrentTreeView()) return;
        if (payload.person_patch && applyPersonPatch(payload.person_patch)) {
            applyStatsDelta(payload.stats_delta || {});
            return;
        }
        if (typeof payload.tree_html === 'string' || typeof payload.stats_html === 'string') {
            await applyTreeFragments(payload);
        } else {
            reloadTree(payload.tree_long_id || getActiveTreeId(), payload.focus_person_id || '');
        }
    }

    function applyTreePayload(payload) {
        treePayloadQueue = treePayloadQueue.catch(() => {}).then(async () => {
            if (!isCurrentTreeView()) return;
            const focusPersonId = String(payload?.focus_person_id || '');
            const canRecoverFromState = typeof payload?.tree_html === 'string';
            try {
                await applyTreePayloadNow(payload);
            } catch (error) {
                if (!isCurrentTreeView()) return;
                if (!canRecoverFromState) throw error;
                const recoveryData = new FormData();
                recoveryData.set('action', 'get_tree_state');
                recoveryData.set('tree_long_id', payload.tree_long_id || getActiveTreeId());
                await applyTreePayloadNow(await postTreeAction(recoveryData));
                return;
            }
            if (!isCurrentTreeView() || !canRecoverFromState) return;
            const focusIsPresent = !focusPersonId
                || !!canvas.querySelector(`.one_person[data-person-id="${escapeSelector(focusPersonId)}"]`);
            if (treeFragmentIsUsable() && focusIsPresent) return;

            const data = new FormData();
            data.set('action', 'get_tree_state');
            data.set('tree_long_id', payload.tree_long_id || getActiveTreeId());
            const canonical = await postTreeAction(data);
            await applyTreePayloadNow(canonical);
            if (!treeFragmentIsUsable()) reloadTree(canonical.tree_long_id || getActiveTreeId());
        });
        return treePayloadQueue;
    }

    async function applyTreeFragments(payload) {
        if (!isCurrentTreeView()) return;
        const treeEl = canvas.querySelector('.tree');
        const isHorizontal = !container.classList.contains('vertical');
        const savedView = isHorizontal
            ? { x: state.pointX, y: state.pointY, scale: state.scale }
            : null;
        const anchorEl = isHorizontal
            ? canvas.querySelector('.tree > ul > li > .couple > .one_person, .tree > ul > li > .one_person')
            : null;
        const anchorRect = anchorEl?.getBoundingClientRect();
        const screenAnchor = anchorEl && anchorRect ? {
            id: anchorEl.getAttribute('data-person-long-id') || anchorEl.getAttribute('data-person-id') || '',
            x: anchorRect.left + anchorRect.width / 2,
            y: anchorRect.top + anchorRect.height / 2,
        } : null;

        if (isHorizontal) {
            container.setAttribute('data-tree-layout-pending', 'true');
            ensureHorizontalConnectorLayer().replaceChildren();
        }

        try {
            if (treeEl && typeof payload.tree_html === 'string') {
                treeEl.innerHTML = payload.tree_html;
            }

            const statsEl = document.querySelector('.wrapper_stats');
            if (statsEl && typeof payload.stats_html === 'string') {
                statsEl.innerHTML = payload.stats_html;
            }

            const vtree = container.querySelector('.v_tree');
            if (vtree) {
                vtree.innerHTML = '';
                delete vtree.dataset.built;
            }

            if (payload.tree_long_id) {
                const url = treeViewUrl(payload.tree_long_id);
                container.setAttribute('data-tree-id', payload.tree_long_id);
                window.history.replaceState({}, '', url);
            }

            if (container.classList.contains('vertical')) {
                buildVerticalTree();
                await drawHorizontalChildConnectors();
            } else if (savedView) {
                state.pointX = Number.isFinite(savedView.x) ? savedView.x : 0;
                state.pointY = Number.isFinite(savedView.y) ? savedView.y : 0;
                state.scale = Number.isFinite(savedView.scale) && savedView.scale > 0
                    ? savedView.scale
                    : defaultHorizontalScale();
                canvas.style.transition = 'none';
                setTransform();
                canvas.getBoundingClientRect();
                canvas.style.transition = '';
                await refreshHorizontalTreeConnectors();

                let restoredAnchor = false;
                if (screenAnchor?.id) {
                    const escapedId = escapeSelector(screenAnchor.id);
                    const currentAnchor = canvas.querySelector(`.one_person[data-person-long-id="${escapedId}"]`)
                        || canvas.querySelector(`.one_person[data-person-id="${escapedId}"]`);
                    const currentRect = currentAnchor?.getBoundingClientRect();
                    if (currentRect) {
                        state.pointX += screenAnchor.x - (currentRect.left + currentRect.width / 2);
                        state.pointY += screenAnchor.y - (currentRect.top + currentRect.height / 2);
                        setTransform();
                        restoredAnchor = true;
                    }
                }
                if (!restoredAnchor) await centerHorizontalTree(false);
            }

            window.setTimeout(() => flashFocusedPerson(payload.focus_person_id || '', false), 250);
        } finally {
            revealTreeLayout();
        }
    }

    async function applyLayout(view, persist = false, animateCenter = false) {
        stopPanMomentum();
        if (persist) storeTreeView(view);
        if (view === 'horizontal') {
            container.setAttribute('data-tree-layout-pending', 'true');
        }
        if (view === 'vertical') {
            container.classList.add('vertical');
            vBtn?.classList.add('active');
            hBtn?.classList.remove('active');
            buildVerticalTree();
            drawHorizontalChildConnectors();
            queueGenerationIndicatorUpdate();
            syncLayoutLabel();
            revealTreeLayout();
        } else {
            container.classList.remove('vertical');
            hBtn?.classList.add('active');
            vBtn?.classList.remove('active');
            await centerHorizontalTree(animateCenter);
            syncLayoutLabel();
            revealTreeLayout();
        }

        container.dispatchEvent(new CustomEvent('racinage:tree-layout-changed', {
            bubbles: true,
            detail: { view }
        }));
    }

    if (treeEditToggle) {
        setTreeEditing(getStoredTreeEditing(), false);
    }

    async function applyInitialLayout() {
        const view = isPublicPreview ? 'horizontal' : getStoredTreeView();
        try {
            if (container.dataset.publicToken && view === 'horizontal' && document.readyState !== 'complete') {
                await new Promise(resolve => window.addEventListener('load', resolve, { once: true, signal: sig }));
            }
            await applyLayout(view, false);
            syncLayoutLabel();
        } finally {
            revealTreeLayout();
        }
    }

    applyInitialLayout();

    if (layoutDropdownToggle && layoutDropdown) {
        layoutDropdownToggle.addEventListener('click', (event) => {
            event.stopPropagation();
            const willOpen = !layoutDropdown.classList.contains('open');
            layoutDropdown.classList.toggle('open', willOpen);
            layoutDropdownToggle.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
        }, { signal: sig });
        document.addEventListener('click', (event) => {
            if (!layoutDropdown.contains(event.target)) closeLayoutDropdown();
        }, { signal: sig });
        document.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') closeLayoutDropdown();
        }, { signal: sig });
    }

    if (hBtn) {
        hBtn.addEventListener('click', async () => {
            closeLayoutDropdown();
            await applyLayout('horizontal', true, false);
            syncLayoutLabel();
        }, { signal: sig });
    }

    if (vBtn) {
        vBtn.addEventListener('click', () => {
            closeLayoutDropdown();
            if (container.classList.contains('vertical')) return;
            applyLayout('vertical', true);
            syncLayoutLabel();
        }, { signal: sig });
    }

    if (treeEditToggle) {
        treeEditToggle.addEventListener('click', () => {
            setTreeEditing(!container.classList.contains('tree_editing'), true);
        }, { signal: sig });
    }

    const pickerToggle = document.getElementById('familyTreePickerToggle');
    const pickerMenu = document.getElementById('familyTreePickerMenu');
    const treeManageMenu = document.getElementById('treeManageMenu');
    let activeManageTree = null;

    function syncTreePickerPublicState(resourceType, resourceId, publicUrl = '', isPublic = true) {
        if (!resourceId) return;
        const isPartial = resourceType === 'tree_partial';
        const row = isPartial
            ? container.querySelector(`[data-partial-tree-row][data-partial-id="${escapeSelector(resourceId)}"]`)
            : container.querySelector(`.family_tree_picker_row[data-tree-long-id="${escapeSelector(resourceId)}"]`);
        if (row) {
            if (isPartial) {
                row.dataset.partialPublic = isPublic ? 'true' : 'false';
                row.dataset.partialPublicUrl = isPublic ? (publicUrl || row.dataset.partialPublicUrl || '') : '';
            } else {
                row.dataset.treePublic = isPublic ? 'true' : 'false';
                row.dataset.treePublicUrl = isPublic ? (publicUrl || row.dataset.treePublicUrl || '') : '';
            }
            const item = row.querySelector(isPartial ? '.family_tree_partial_item' : '.family_tree_picker_item');
            let badge = item?.querySelector('.public_badge');
            if (isPublic && item && !badge) {
                badge = document.createElement('span');
                badge.className = 'public_badge';
                badge.textContent = t('Public');
                item.appendChild(badge);
            } else if (!isPublic && badge) {
                badge.remove();
            }
        }
        const isActiveResource = isPartial
            ? container.dataset.partialTreeId === resourceId
            : !container.dataset.partialTreeId && !container.dataset.partialTreeTemporary && resourceId === getActiveTreeId();
        if (isActiveResource) {
            container.dataset.treePublic = isPublic ? 'true' : 'false';
            let currentBadge = pickerToggle?.querySelector('.tree_current_public_badge');
            if (isPublic && pickerToggle && !currentBadge) {
                currentBadge = document.createElement('span');
                currentBadge.className = 'public_badge tree_current_public_badge';
                currentBadge.textContent = t('Public');
                pickerToggle.querySelector('svg')?.before(currentBadge);
            } else if (!isPublic && currentBadge) {
                currentBadge.remove();
            }
        }
    }

    function closeTreeManageMenu() {
        if (!treeManageMenu) return;
        treeManageMenu.classList.remove('open');
        treeManageMenu.setAttribute('aria-hidden', 'true');
        activeManageTree = null;
    }

    function openTreeManageMenu(row, anchor) {
        if (!treeManageMenu || !row || !anchor) return;
        activeManageTree = {
            trigger: anchor,
            id: row.getAttribute('data-tree-long-id') || '',
            name: row.getAttribute('data-tree-name') || '',
            resourceType: 'tree',
            isPublic: row.getAttribute('data-tree-public') === 'true',
            publicUrl: row.getAttribute('data-tree-public-url') || '',
        };
        const collectionAction = treeManageMenu.querySelector('[data-collection-assign]');
        if (collectionAction) {
            collectionAction.dataset.resourceKey = activeManageTree.id;
            collectionAction.dataset.resourceTitle = activeManageTree.name;
            collectionAction.dataset.collectionId = row.closest('[data-tree-collection]')?.dataset.treeCollection || '';
            if (collectionAction.dataset.collectionId === 'none') collectionAction.dataset.collectionId = '';
            collectionAction.querySelector('span').textContent = t(collectionAction.dataset.collectionId ? 'Edit Collection' : 'Add to Collection');
        }
        const publicAction = treeManageMenu.querySelector('[data-tree-menu-action="public_share"]');
        if (publicAction) {
            const label = activeManageTree.isPublic
                ? publicAction.dataset.publicUpdateLabel
                : publicAction.dataset.publicLabel;
            const labelNode = publicAction.querySelector('span');
            if (labelNode) labelNode.textContent = label;
            else publicAction.textContent = label;
        }
        const copyPublicAction = treeManageMenu.querySelector('[data-tree-copy-public]');
        if (copyPublicAction) copyPublicAction.hidden = !activeManageTree.publicUrl;
        const exportAction = treeManageMenu.querySelector('[data-racinage-transfer-export]');
        if (exportAction) {
            exportAction.setAttribute('data-transfer-record-id', activeManageTree.id);
            exportAction.setAttribute('data-transfer-record-title', activeManageTree.name);
        }
        const rect = anchor.getBoundingClientRect();
        treeManageMenu.style.left = `${Math.max(8, Math.min(rect.right - 170, window.innerWidth - 188))}px`;
        treeManageMenu.style.top = `${rect.bottom + 6}px`;
        treeManageMenu.classList.add('open');
        treeManageMenu.setAttribute('aria-hidden', 'false');
    }

    async function runTreeManageAction(action) {
        if (!activeManageTree?.id) return;

        if (action === 'stats') { await showTreeInsights(activeManageTree.id, '', '', activeManageTree.trigger); return; }
        if (action === 'important_dates') { showTreeImportantDates(activeManageTree.id, '', '', activeManageTree.trigger); return; }

        if (action === 'print') {
            const treeId = activeManageTree.id;
            closeTreeManageMenu();
            pickerMenu?.classList.remove('open');
            if (treeId !== getActiveTreeId()) {
                window.racinagePendingTreePrint = treeId;
                try { await selectTree(treeId); }
                catch (error) { window.racinagePendingTreePrint = ''; throw error; }
                return;
            }
            await window.racinagePrintTree?.();
            return;
        }

        if (action === 'public_share') {
            openTreePublicShare(activeManageTree);
            return;
        }
        if (action === 'copy_public_link') {
            if (activeManageTree.publicUrl) {
                if (await copyText(activeManageTree.publicUrl)) {
                    showTreeFeedback(t('Public link copied.'));
                }
            }
            return;
        }

        const data = new FormData();
        data.set('tree_long_id', activeManageTree.id);

        if (action === 'rename') {
            const name = await treePrompt(t('Rename family tree:'), activeManageTree.name, t('Rename Tree'));
            if (name === null) return;
            data.set('action', 'rename_tree');
            data.set('name', name.trim());
        } else if (action === 'duplicate') {
            const name = await treePrompt(t('Name the duplicated tree:'), `${activeManageTree.name} ${t('Copy')}`, t('Duplicate Tree'));
            if (name === null) return;
            data.set('action', 'duplicate_tree');
            data.set('name', name.trim());
        } else if (action === 'archive') {
            const confirmed = await treeConfirm(t('Archive tree confirm').replace('{name}', activeManageTree.name), t('Archive Tree'));
            if (!confirmed) return;
            data.set('action', 'archive_tree');
        } else if (action === 'delete') {
            const confirmed = await treeConfirm(t('Delete tree confirm').replace('{name}', activeManageTree.name), t('Delete Tree'), true);
            if (!confirmed) return;
            data.set('action', 'delete_tree');
        } else {
            return;
        }

        try {
            const payload = await postTreeAction(data);
            if (['duplicate', 'archive', 'delete'].includes(action)) {
                await selectTree(payload.tree_long_id || getActiveTreeId());
            } else {
                reloadTree(payload.tree_long_id || getActiveTreeId());
            }
        } catch (err) {
            await treeAlert(err.message, t('Tree Action Failed'));
        }
    }

    const publicShareModal = document.getElementById('treePublicShareModal');
    const publicShareForm = document.getElementById('treePublicShareForm');
    let publicShareTree = null;

    function closeTreePublicShare() {
        publicShareModal?.classList.remove('open');
        publicShareModal?.setAttribute('aria-hidden', 'true');
        publicShareTree = null;
    }

    async function publicShareRequest(action, settings = null, extra = {}) {
        if (!publicShareTree?.id) throw new Error(t('Family tree not found.'));
        const data = new FormData();
        data.set('action', action);
        data.set('resource_type', publicShareTree.resourceType || 'tree');
        data.set('resource_key', publicShareTree.id);
        if (settings) data.set('settings', JSON.stringify(settings));
        Object.entries(extra).forEach(([key, value]) => data.set(key, value));
        const response = await fetch(`${window.ROOT || ''}/includes/public_share_save.include.php`, {
            method: 'POST',
            body: data,
            credentials: 'same-origin',
            headers: {
                'Accept': 'application/json',
                'X-Racinage-CSRF': document.querySelector('[data-tree-preference-csrf]')?.dataset.treePreferenceCsrf || '',
            },
        });
        const payload = await response.json().catch(() => ({ ok: false, message: t('Unexpected server response.') }));
        if (!response.ok || !payload.ok) throw new Error(payload.message || t('Tree Action Failed'));
        return payload;
    }

    function renderPublicRecommendations(items = []) {
        const section = publicShareForm?.querySelector('[data-public-recommendations]');
        const list = section?.querySelector('div');
        if (!section || !list) return;
        section.hidden = items.length === 0;
        list.innerHTML = items.map(item => `
            <article class="public_recommendation_item ${escapeHTML(item.status || '')}">
                <p>${escapeHTML(item.recommendation_text || '')}</p>
                <small>${escapeHTML(item.submitter_name || t('Anonymous'))} · ${escapeHTML(item.created_at || '')}</small>
                ${item.status === 'pending' ? `
                    <span>
                        <button type="button" data-review-recommendation="${Number(item.id)}" data-decision="approved">${escapeHTML(t('Approve'))}</button>
                        <button type="button" class="danger" data-review-recommendation="${Number(item.id)}" data-decision="rejected">${escapeHTML(t('Reject'))}</button>
                    </span>
                ` : `<em>${escapeHTML(t(item.status === 'approved' ? 'Approved' : 'Rejected'))}</em>`}
            </article>
        `).join('');
    }

    function renderPublicShareInstances(items = [], activeId = 0) {
        const list = publicShareForm?.querySelector('[data-public-share-instances]');
        if (!list) return;
        list.innerHTML = items.map(item => `
            <button type="button" class="${Number(item.id) === Number(activeId) ? 'active' : ''}" data-public-share-instance="${Number(item.id)}">
                <strong>${escapeHTML(item.name || t('Public link'))}</strong>
                <small>${escapeHTML(item.updated_at || '')}</small>
            </button>
        `).join('');
    }

    function resetPublicShareForm() {
        publicShareForm.reset();
        publicShareForm.elements.namedItem('share_id').value = '';
        publicShareForm.elements.namedItem('public_token').value = '';
        publicShareForm.elements.namedItem('share_name').value = '';
        publicShareForm.elements.namedItem('hide_living_dates').checked = true;
        [
            'show_gender', 'show_marital_status', 'show_birth_date', 'show_death_date',
            'show_birth_place', 'show_nationality', 'show_education', 'show_website',
            'show_share_button',
        ].forEach(name => {
            const field = publicShareForm.elements.namedItem(name);
            if (field) field.checked = true;
        });
        publicShareForm.querySelector('[data-make-private]').hidden = true;
        window.racinagePublicDiscovery?.sync(publicShareForm);
        renderPublicRecommendations([]);
        publicShareForm.querySelectorAll('[data-public-share-instance]').forEach(item => item.classList.remove('active'));
    }

    async function generatePublicShareLink() {
        const payload = await publicShareRequest('generate_public_share_link');
        const linkWrap = publicShareForm.querySelector('[data-public-share-link]');
        publicShareForm.elements.namedItem('public_token').value = payload.public_token || '';
        linkWrap.hidden = false;
        linkWrap.querySelector('input').value = payload.public_url || '';
        return payload;
    }

    function setPublicShareStatus(message = '', state = '') {
        const status = publicShareForm?.querySelector('[data-public-share-status]');
        if (!status) return;
        status.classList.add('racinage_share_status');
        status.textContent = message ? `${state === 'success' ? '✅ ' : state === 'error' ? '❌ ' : ''}${message}` : '';
        status.classList.toggle('success', state === 'success');
        status.classList.toggle('error', state === 'error');
    }

    function applyPublicSharePayload(payload) {
        Object.entries(payload.settings || {}).forEach(([key, value]) => {
            const field = publicShareForm.elements.namedItem(key);
            if (!field) return;
            if (field.type === 'checkbox') field.checked = Boolean(value);
            else field.value = value;
        });
        publicShareForm.elements.namedItem('share_id').value = payload.share_id || '';
        publicShareForm.elements.namedItem('public_token').value = '';
        publicShareForm.elements.namedItem('share_name').value = payload.share_name || '';
        const selected = Boolean(payload.share_id);
        const linkWrap = publicShareForm.querySelector('[data-public-share-link]');
        linkWrap.hidden = !payload.public_url;
        if (payload.public_url) linkWrap.querySelector('input').value = payload.public_url;
        publicShareForm.querySelector('[data-make-private]').hidden = !selected;
        renderPublicShareInstances(payload.shares || [], payload.share_id || 0);
        renderPublicRecommendations(payload.recommendations || []);
        window.racinagePublicDiscovery?.apply(publicShareForm, payload.settings || {});
        if (publicShareTree?.id && Object.prototype.hasOwnProperty.call(payload, 'is_public')) {
            syncTreePickerPublicState(publicShareTree.resourceType || 'tree', publicShareTree.id, payload.public_url || publicShareTree.publicUrl || '', Boolean(payload.is_public));
        }
    }

    async function openTreePublicShare(tree) {
        if (!publicShareModal || !publicShareForm) return;
        publicShareTree = tree;
        const status = publicShareForm.querySelector('[data-public-share-status]');
        const linkWrap = publicShareForm.querySelector('[data-public-share-link]');
        const makePrivate = publicShareForm.querySelector('[data-make-private]');
        setPublicShareStatus();
        publicShareModal.classList.add('open');
        publicShareModal.setAttribute('aria-hidden', 'false');
        try {
            const payload = await publicShareRequest('get_public_share');
            applyPublicSharePayload(payload);
            if (!payload.share_id) await generatePublicShareLink();
        } catch (error) {
            setPublicShareStatus(error.message, 'error');
        }
    }

    publicShareModal?.addEventListener('click', (event) => {
        if (event.target === publicShareModal || event.target.closest('[data-public-share-close]')) {
            closeTreePublicShare();
        }
    }, { signal: sig });

    publicShareForm?.addEventListener('submit', async (event) => {
        event.preventDefault();
        const existingShareId = publicShareForm.elements.namedItem('share_id').value;
        setPublicShareStatus();
        const settings = {};
        [
            'hide_profile_images', 'hide_external_partner_identity', 'hide_external_partner_branches',
            'hide_living_dates', 'show_share_button', 'allow_recommendations',
            'show_gender', 'show_marital_status', 'show_birth_date', 'show_death_date',
            'show_phone1', 'show_phone2', 'show_email1', 'show_email2', 'show_address',
            'show_birth_place', 'show_nationality', 'show_blood_group', 'show_education',
            'show_religion', 'show_website',
        ].forEach((name) => {
            settings[name] = publicShareForm.elements.namedItem(name).checked;
        });
        settings.name_display = publicShareForm.elements.namedItem('name_display').value;
        window.racinagePublicDiscovery?.read(publicShareForm, settings);
        try {
            const payload = await publicShareRequest('save_public_share', settings, {
                share_id: publicShareForm.elements.namedItem('share_id').value,
                share_name: publicShareForm.elements.namedItem('share_name').value,
                public_token: publicShareForm.elements.namedItem('public_token').value,
            });
            const linkWrap = publicShareForm.querySelector('[data-public-share-link]');
            linkWrap.hidden = false;
            linkWrap.querySelector('input').value = payload.public_url || '';
            publicShareForm.querySelector('[data-make-private]').hidden = false;
            publicShareForm.elements.namedItem('share_id').value = payload.share_id || '';
            setPublicShareStatus(t(existingShareId ? 'Public link updated' : 'Tree is now public.'), 'success');
            publicShareTree.isPublic = true;
            publicShareTree.publicUrl = payload.public_url || '';
            syncTreePickerPublicState(publicShareTree.resourceType || 'tree', publicShareTree.id, payload.public_url || '', true);
            applyPublicSharePayload(await publicShareRequest('get_public_share', null, { share_id: payload.share_id }));
        } catch (error) {
            setPublicShareStatus(error.message, 'error');
        }
    }, { signal: sig });

    publicShareForm?.querySelector('[data-copy-public-share]')?.addEventListener('click', async () => {
        const input = publicShareForm.querySelector('[data-public-share-link] input');
        if (!input?.value) return;
        if (await copyText(input.value)) {
            setPublicShareStatus(t('Public link copied.'), 'success');
        }
    }, { signal: sig });

    publicShareForm?.querySelector('[data-make-private]')?.addEventListener('click', async () => {
        try {
            await publicShareRequest('delete_public_share', null, {
                share_id: publicShareForm.elements.namedItem('share_id').value,
            });
            applyPublicSharePayload(await publicShareRequest('get_public_share'));
        } catch (error) {
            setPublicShareStatus(error.message, 'error');
        }
    }, { signal: sig });

    publicShareForm?.querySelector('[data-public-recommendations]')?.addEventListener('click', async (event) => {
        const button = event.target.closest('[data-review-recommendation]');
        if (!button) return;
        try {
            await publicShareRequest('review_public_recommendation', null, {
                share_id: publicShareForm.elements.namedItem('share_id').value,
                recommendation_id: button.dataset.reviewRecommendation,
                decision: button.dataset.decision,
            });
            const payload = await publicShareRequest('get_public_share', null, {
                share_id: publicShareForm.elements.namedItem('share_id').value,
            });
            renderPublicRecommendations(payload.recommendations || []);
        } catch (error) {
            setPublicShareStatus(error.message, 'error');
        }
    }, { signal: sig });

    // Detail presets: Basic shows non-contact details, All / None toggle every detail.
    publicShareForm?.querySelector('.public_share_presets')?.addEventListener('click', (event) => {
        const preset = event.target.closest('[data-public-share-preset]')?.dataset.publicSharePreset;
        if (!preset) return;
        publicShareForm.querySelectorAll('.public_share_chip input').forEach((input) => {
            input.checked = preset === 'all' || (preset === 'basic' && !input.closest('.is_sensitive') && !['show_blood_group', 'show_religion'].includes(input.name));
        });
    }, { signal: sig });

    publicShareForm?.querySelector('[data-new-public-share]')?.addEventListener('click', async () => {
        resetPublicShareForm();
        setPublicShareStatus();
        try {
            await generatePublicShareLink();
        } catch (error) {
            setPublicShareStatus(error.message, 'error');
        }
    }, { signal: sig });

    publicShareForm?.querySelector('[data-public-share-instances]')?.addEventListener('click', async (event) => {
        const button = event.target.closest('[data-public-share-instance]');
        if (!button) return;
        try {
            applyPublicSharePayload(await publicShareRequest('get_public_share', null, {
                share_id: button.dataset.publicShareInstance,
            }));
        } catch (error) {
            setPublicShareStatus(error.message, 'error');
        }
    }, { signal: sig });

    pickerToggle?.addEventListener('click', (e) => {
        e.stopPropagation();
        closeTreeManageMenu();
        pickerMenu?.classList.toggle('open');
        pickerToggle.setAttribute('aria-expanded', pickerMenu?.classList.contains('open') ? 'true' : 'false');
    }, { signal: sig });
    document.addEventListener('click', () => {
        pickerMenu?.classList.remove('open');
        pickerToggle?.setAttribute('aria-expanded', 'false');
        closeTreeManageMenu();
        closePartialMenu();
    }, { signal: sig });

    async function refreshPartialTreePicker(removeCurrent = false, treeId = '', partialId = '') {
        if (!isCurrentTreeView()) return;
        const target = new URL(window.location.href);
        if (treeId) target.searchParams.set('tree', treeId);
        if (removeCurrent) {
            sessionStorage.removeItem(partialSessionKey);
            target.searchParams.delete('partial');
            target.searchParams.delete('partial_temp');
        }
        if (partialId) {
            target.searchParams.set('partial', partialId);
            target.searchParams.delete('partial_temp');
        }
        if (window.htmx) {
            await window.htmx.ajax('GET', target.toString(), { target: '#main_container', swap: 'outerHTML' });
            window.history.replaceState(window.history.state, '', target.toString());
            return;
        }
        window.location.assign(target.toString());
    }

    let insightsRequest = 0;
    async function showTreeInsights(treeId, partialId = '', temporary = '', trigger = pickerToggle) {
        const request = ++insightsRequest;
        const data = new FormData(); data.set('action', 'tree_insights'); data.set('tree_long_id', treeId);
        data.set('partial_id', partialId); data.set('partial_temp', temporary);
        try {
            const result = await postTreeAction(data);
            if (isCurrentTreeView() && request === insightsRequest) {
                const dialog = window.racinageContentDialog?.(t('Tree Stats') + ': ' + result.title, result.html);
                if (dialog && result.traffic_available) window.racinageTreeStatsTabs?.(dialog, (values, signal) => {
                    const query = new FormData(); query.set('action','tree_insights'); query.set('tree_long_id',treeId); query.set('stats_tab','visits');
                    query.set('partial_id',partialId); query.set('partial_temp',temporary);
                    Object.entries(values).forEach(([key,value])=>query.set(key,value));
                    return postTreeAction(query, signal);
                }, result.visits_html);
                dialog?.addEventListener('close', () => {
                    const target = trigger?.isConnected && trigger.getClientRects().length ? trigger : pickerToggle;
                    if (target?.isConnected) target.focus({preventScroll:true});
                }, {once:true});
            }
        } catch (error) { await treeAlert(error.message, t('Tree Action Failed')); }
    }

    function showTreeImportantDates(treeId, partialId = '', temporary = '', trigger = pickerToggle) {
        insightsRequest++;
        const e = escapeHTML;
        const locale = document.documentElement.lang.startsWith('fr') ? 'fr-FR' : 'en-GB';
        const months = Array.from({length:12}, (_, month) => `<option value="${month + 1}">${e(new Intl.DateTimeFormat(locale, {month:'long', timeZone:'UTC'}).format(new Date(Date.UTC(2024, month, 1))))}</option>`).join('');
        const dialog = window.racinageContentDialog?.(t('Important Dates'), `<section class="tree_important_dates">
            <form class="tree_dates_filters">
                <label class="tree_dates_search">${e(t('Search by name'))}<input type="search" name="search" autocomplete="off"></label>
                <label>${e(t('Category'))}<select name="category"><option value="">${e(t('All dates'))}</option><option value="birthday">${e(t('Birthdays'))}</option><option value="death">${e(t('Death anniversaries'))}</option></select></label>
                <label>${e(t('Month'))}<select name="month"><option value="">${e(t('All months'))}</option>${months}</select></label>
                <label>${e(t('Sort'))}<select name="sort"><option value="upcoming">${e(t('Upcoming first'))}</option><option value="date">${e(t('Calendar order'))}</option><option value="name">${e(t('Name'))}</option></select></label>
                <button type="reset" class="button1 secondary">${e(t('Reset filters'))}</button>
                <output data-dates-status aria-live="polite"></output>
            </form><div data-dates-results></div><p data-dates-error role="alert" hidden></p><button type="button" class="button1 secondary" data-dates-retry hidden>${e(t('Retry'))}</button>
        </section>`);
        if (!dialog) return;
        dialog.classList.add('tree_important_dates_dialog');
        const form = dialog.querySelector('form');
        const results = dialog.querySelector('[data-dates-results]');
        const status = dialog.querySelector('[data-dates-status]');
        const errorBox = dialog.querySelector('[data-dates-error]');
        const retry = dialog.querySelector('[data-dates-retry]');
        let request = null, version = 0, timer = null, page = 1;
        const invalidate = () => {
            clearTimeout(timer); request?.abort(); version++;
            status.textContent = t('Loading dates...'); results.setAttribute('aria-busy', 'true'); results.inert = true;
        };
        const load = async () => {
            invalidate();
            const revision = version;
            request = new AbortController();
            const data = new FormData(form);
            data.set('action', 'tree_important_dates'); data.set('tree_long_id', treeId);
            data.set('partial_id', partialId); data.set('partial_temp', temporary); data.set('page', page);
            status.textContent = t('Loading dates...'); results.setAttribute('aria-busy', 'true'); results.inert = true;
            errorBox.hidden = retry.hidden = true;
            try {
                const result = await postTreeAction(data, request.signal);
                if (revision !== version || !dialog.open || !isCurrentTreeView()) return;
                dialog.querySelector('h2').textContent = t('Important Dates') + ': ' + result.title;
                results.innerHTML = result.html; page = result.page;
                status.textContent = t('%s important dates').replace('%s', String(result.total));
            } catch (error) {
                if (error.name === 'AbortError' || revision !== version || !dialog.open) return;
                status.textContent = '';
                errorBox.textContent = error.message || t('Could not load important dates.');
                errorBox.hidden = retry.hidden = false;
            } finally {
                if (revision === version) { results.removeAttribute('aria-busy'); results.inert = false; }
            }
        };
        form.addEventListener('submit', event => { event.preventDefault(); page = 1; load(); });
        form.addEventListener('input', event => {
            if (event.target.name !== 'search') return;
            invalidate(); page = 1; timer = setTimeout(load, 220);
        });
        form.addEventListener('change', event => { if (event.target.name !== 'search') { page = 1; load(); } });
        form.addEventListener('reset', () => { invalidate(); page = 1; timer = setTimeout(load, 0); });
        retry.addEventListener('click', load);
        results.addEventListener('click', event => {
            const button = event.target.closest('[data-dates-page]');
            if (!button) return;
            page = Number(button.dataset.datesPage); load(); form.querySelector('input').focus({preventScroll:true});
            dialog.scrollTo({top:0, behavior:'instant'});
        });
        const closeOnNavigation = () => dialog.close();
        sig.addEventListener('abort', closeOnNavigation, {once:true});
        dialog.addEventListener('close', () => {
            invalidate(); sig.removeEventListener('abort', closeOnNavigation);
            const target = trigger?.isConnected && trigger.getClientRects().length ? trigger : pickerToggle;
            if (target?.isConnected) target.focus({preventScroll:true});
        }, {once:true});
        load();
    }

    // Partial Tree actions open as a dropdown under the row's button, styled like the tree manage menu.
    let partialMenu = null;
    function closePartialMenu() {
        if (!partialMenu) return;
        const { el, resolve } = partialMenu;
        partialMenu = null;
        el.remove();
        resolve(null);
    }
    function partialMenuChoice(anchor, choices) {
        const sameAnchor = partialMenu?.anchor === anchor;
        closePartialMenu();
        if (sameAnchor || !anchor) return Promise.resolve(null);
        return new Promise(resolve => {
            const el = document.createElement('div');
            el.className = 'tree_manage_menu open';
            el.setAttribute('role', 'menu');
            // Reuse the tree manage menu's icons for the matching actions.
            const iconOf = { visibility: 'public_share', reanchor: 'duplicate', restore: 'archive' };
            const icon = value => treeManageMenu?.querySelector(`[data-tree-menu-action="${iconOf[value] || value}"] svg`)?.outerHTML || '';
            el.innerHTML = choices.map(choice => `<button type="button" role="menuitem" data-menu-action="${escapeHTML(choice.value)}"${choice.value === 'delete' ? ' class="danger"' : ''}>${icon(choice.value)}<span>${escapeHTML(choice.label)}</span></button>`).join('');
            (treeManageMenu?.parentNode || document.body).appendChild(el);
            window.racinageEnhanceActionMenus?.(el);
            const rect = anchor.getBoundingClientRect();
            Object.assign(el.style, { position: 'fixed', zIndex: '10030' });
            el.style.left = `${Math.max(8, Math.min(rect.right - el.offsetWidth, window.innerWidth - el.offsetWidth - 8))}px`;
            el.style.top = `${Math.max(8, rect.bottom + 6 + el.offsetHeight > window.innerHeight - 8 ? rect.top - el.offsetHeight - 6 : rect.bottom + 6)}px`;
            partialMenu = { el, anchor, resolve };
            el.addEventListener('click', event => {
                event.stopPropagation();
                const item = event.target.closest('[data-menu-action]');
                if (!item) return;
                partialMenu = null;
                el.remove();
                resolve(item.dataset.menuAction);
            });
        });
    }

    async function managePartialTree(row) {
        const menuAnchor = row?.querySelector('[data-partial-tree-manage]');
        if (row?.dataset.partialTemp) {
            const choice = await partialMenuChoice(menuAnchor, [{value:'stats',label:t('Tree Stats')},{value:'important_dates',label:t('Important Dates')}]);
            if (choice === 'stats') await showTreeInsights(row.dataset.treeLongId, '', row.dataset.partialTemp, row.querySelector('[data-partial-tree-manage]'));
            if (choice === 'important_dates') showTreeImportantDates(row.dataset.treeLongId, '', row.dataset.partialTemp, row.querySelector('[data-partial-tree-manage]'));
            return;
        }
        const partialId = row?.dataset.partialId || '';
        const status = row?.dataset.partialStatus || 'active';
        const canManage = row?.dataset.canManagePartial === 'true';
        if (!partialId) return;
        const choices = ['active','archived'].includes(status) ? [{ value: 'stats', label: t('Tree Stats') }] : [];
        if (['active','archived'].includes(status)) choices.push({value:'important_dates',label:t('Important Dates')});
        if (status === 'archived' && canManage) choices.push({ value: 'restore', label: t('Restore Partial Tree') });
        else if (status !== 'archived') {
            if (status === 'active' && row.dataset.canPublic === 'true') {
                choices.push({ value: 'public_share', label: row.dataset.partialPublic === 'true' ? t('Update Public Settings') : t('Make Public') });
                if (row.dataset.partialPublicUrl) choices.push({ value: 'copy_public_link', label: t('Copy Link') });
            }
            if (canManage) {
                choices.push({ value: 'rename', label: t('Rename Partial Tree') });
                choices.push({ value: 'visibility', label: t('Change visibility') });
                choices.push({ value: 'reanchor', label: t('Re-anchor') });
                if (status === 'active') choices.push({ value: 'archive', label: t('Archive Partial Tree') });
            }
        }
        if (canManage) choices.push({ value: 'delete', label: t('Delete Partial Tree') });
        if (!choices.length) return;
        const selected = await partialMenuChoice(menuAnchor, choices);
        if (!selected) return;
        if (selected === 'stats') { await showTreeInsights(row.dataset.treeLongId, partialId, '', row.querySelector('[data-partial-tree-manage]')); return; }
        if (selected === 'important_dates') { showTreeImportantDates(row.dataset.treeLongId, partialId, '', row.querySelector('[data-partial-tree-manage]')); return; }
        if (selected === 'public_share') {
            openTreePublicShare({
                id: partialId,
                name: row.dataset.partialName || t('Permanent Partial Tree'),
                resourceType: 'tree_partial',
                isPublic: row.dataset.partialPublic === 'true',
                publicUrl: row.dataset.partialPublicUrl || '',
            });
            return;
        }
        if (selected === 'copy_public_link') {
            if (await copyText(row.dataset.partialPublicUrl || '')) showTreeFeedback(t('Public link copied.'));
            return;
        }
        if (selected === 'reanchor') {
            sessionStorage.setItem('racinage.partial_tree.reanchor', partialId);
            pickerMenu?.classList.remove('open');
            pickerToggle?.setAttribute('aria-expanded', 'false');
            await treeAlert(t('Select a new parent couple for this Partial Tree.'), t('Re-anchor'));
            await refreshPartialTreePicker(true, row.dataset.treeLongId || getActiveTreeId());
            return;
        }
        const data = new FormData();
        data.set('action', 'update_partial_tree');
        data.set('partial_id', partialId);
        data.set('tree_preference_csrf', container.dataset.treePreferenceCsrf || '');
        if (selected === 'rename') {
            const name = await treePrompt(t('Name this Partial Tree:'), row.dataset.partialName || '', t('Rename Partial Tree'), () => managePartialTree(row));
            if (name === null || !name.trim()) return;
            data.set('name', name.trim());
        } else if (selected === 'visibility') {
            const visibility = await treeChoice(t('Who can see this Partial Tree?'), [{ value: 'private', label: t('Private') }, { value: 'family', label: t('Shared') }], t('Partial Trees'), () => managePartialTree(row));
            if (!visibility) return;
            data.set('visibility', visibility);
        } else if (selected === 'delete') {
            if (!await treeConfirm(t('Delete this Partial Tree?'), t('Delete Partial Tree'), false, () => managePartialTree(row))) return;
            data.set('status', 'deleted');
        } else data.set('status', selected === 'restore' ? 'active' : 'archived');
        try {
            await postTreeAction(data);
            const removingCurrent = (selected === 'delete' || selected === 'archive') && container.dataset.partialTreeId === partialId;
            await refreshPartialTreePicker(removingCurrent);
        } catch (error) {
            await treeAlert(error.message, t('Tree Action Failed'));
        }
    }

    const pickerSearch = pickerMenu?.querySelector('[data-partial-tree-search]');
    const pickerResults = pickerMenu?.querySelector('[data-tree-picker-results]');
    const pickerCollapseKey = `racinage.tree-collections.${pickerMenu?.dataset.treePickerStorage || ''}`;
    let pickerSearchTimer = 0;
    let pickerRequest = null;
    let pickerRequestRevision = 0;
    pickerMenu?.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || pickerMenu.querySelector(':popover-open')) return;
        event.preventDefault();
        pickerMenu.classList.remove('open');
        pickerToggle?.setAttribute('aria-expanded', 'false');
        pickerToggle?.focus();
    }, {signal: sig});
    function pickerCollapsedSections() {
        try { return JSON.parse(localStorage.getItem(pickerCollapseKey) || '{}'); } catch (_) { return {}; }
    }
    function restorePickerSections() {
        const searching = Boolean(pickerResults?.querySelector('[data-tree-picker-query]')?.dataset.treePickerQuery);
        const collapsed = pickerCollapsedSections();
        pickerResults?.querySelectorAll('[data-tree-collection]').forEach(section => {
            const expanded = searching || !collapsed[section.dataset.treeCollection];
            section.querySelector('[data-tree-collection-toggle]')?.setAttribute('aria-expanded', String(expanded));
            section.querySelector('.tree_collection_items').hidden = !expanded;
        });
    }
    async function loadPickerPage(page = 1, focusPagination = false) {
        if (!pickerResults || !isCurrentTreeView()) return;
        const revision = ++pickerRequestRevision;
        pickerRequest?.abort();
        pickerRequest = new AbortController();
        const url = new URL(window.location.href);
        url.searchParams.set('tree_picker', '1');
        url.searchParams.set('tree_picker_query', pickerSearch?.value.trim() || '');
        url.searchParams.set('tree_picker_page', String(page));
        url.searchParams.set('collection', pickerMenu.dataset.collectionSelection || 'all');
        pickerResults.setAttribute('aria-busy', 'true');
        try {
            const response = await fetch(url, {credentials: 'same-origin', headers: {'HX-Request': 'true'}, signal: pickerRequest.signal});
            if (!response.ok) throw new Error(t('Could not load family trees.'));
            const html = await response.text();
            if (sig.aborted || revision !== pickerRequestRevision || !pickerResults.isConnected) return;
            const parsed = new DOMParser().parseFromString(html, 'text/html');
            const results = parsed.querySelector('.tree_collection_results');
            if (!results) throw new Error(t('Could not load family trees.'));
            pickerResults.replaceChildren(document.importNode(results, true));
            window.htmx?.process(pickerResults);
            restorePickerSections();
            if (focusPagination) pickerResults.querySelector('.tree_collection_pagination button:not(:disabled)')?.focus();
        } catch (error) {
            if (error.name === 'AbortError' || sig.aborted || revision !== pickerRequestRevision) return;
            pickerResults.querySelector('[data-tree-picker-error]')?.remove();
            const message = document.createElement('p');
            message.dataset.treePickerError = '';
            message.setAttribute('role', 'alert');
            message.textContent = t('Could not load family trees.');
            const retry = document.createElement('button');
            retry.type = 'button';
            retry.dataset.treePickerPageButton = String(page);
            retry.textContent = t('Retry');
            message.append(' ', retry);
            pickerResults.prepend(message);
        } finally {
            if (revision === pickerRequestRevision) pickerResults.removeAttribute('aria-busy');
        }
    }
    restorePickerSections();
    pickerSearch?.addEventListener('input', () => {
        clearTimeout(pickerSearchTimer);
        // Invalidate immediately so an old response cannot replace the newer query during debounce.
        pickerRequestRevision++;
        pickerRequest?.abort();
        pickerSearchTimer = setTimeout(() => loadPickerPage(1), 220);
    }, {signal: sig});
    document.addEventListener('racinage:collections-changed', () => {
        if (pickerMenu?.dataset.collectionSelection) reloadTree();
        else loadPickerPage(1);
    }, {signal: sig});
    sig.addEventListener('abort', () => { clearTimeout(pickerSearchTimer); pickerRequest?.abort(); }, {once: true});

    pickerMenu?.addEventListener('submit', async (e) => {
        const form = e.target.closest('#newFamilyTreeForm');
        if (!form) return;
        e.preventDefault();
        const submit = form.querySelector('[type="submit"]');
        const error = form.querySelector('[data-new-tree-error]');
        const data = new FormData();
        data.set('action', 'create_tree');
        data.set('name', form.elements.name.value.trim() || t('New Family Tree'));
        data.set('description', form.elements.description.value.trim());
        submit.disabled = true;
        error.textContent = '';
        try {
            const payload = await postTreeAction(data);
            form.reset();
            form.hidden = true;
            document.getElementById('newFamilyTreeBtn')?.setAttribute('aria-expanded', 'false');
            pickerMenu?.classList.remove('open');
            pickerToggle?.setAttribute('aria-expanded', 'false');
            await selectTree(payload.tree_long_id, payload.focus_person_id || '');
        } catch (err) {
            error.textContent = err.message;
        } finally {
            submit.disabled = false;
        }
    }, { signal: sig });

    pickerMenu?.addEventListener('click', async (e) => {
        // Import and export are opened by a document-level listener, so let those clicks through.
        if (e.target.closest('[data-racinage-transfer-import], [data-racinage-transfer-export]')) return;
        e.stopPropagation();

        const pageButton = e.target.closest('[data-tree-picker-page-button]');
        if (pageButton) { await loadPickerPage(Number(pageButton.dataset.treePickerPageButton), true); return; }
        const collectionToggle = e.target.closest('[data-tree-collection-toggle]');
        if (collectionToggle) {
            const section = collectionToggle.closest('[data-tree-collection]');
            const expanded = collectionToggle.getAttribute('aria-expanded') !== 'true';
            collectionToggle.setAttribute('aria-expanded', String(expanded));
            section.querySelector('.tree_collection_items').hidden = !expanded;
            if (!pickerSearch?.value.trim()) {
                const collapsed = pickerCollapsedSections();
                if (expanded) delete collapsed[section.dataset.treeCollection];
                else collapsed[section.dataset.treeCollection] = true;
                try { localStorage.setItem(pickerCollapseKey, JSON.stringify(collapsed)); } catch (_) {}
            }
            return;
        }

        const partialManage = e.target.closest('[data-partial-tree-manage]');
        if (partialManage) {
            closeTreeManageMenu();
            await managePartialTree(partialManage.closest('[data-partial-tree-row]'));
            return;
        }

        const moreBtn = e.target.closest('.family_tree_more_btn');
        if (moreBtn) {
            closePartialMenu();
            // A second click on the same button closes its menu.
            if (treeManageMenu?.classList.contains('open') && activeManageTree?.trigger === moreBtn) closeTreeManageMenu();
            else openTreeManageMenu(moreBtn.closest('.family_tree_picker_row'), moreBtn);
            return;
        }

        const treeRow = e.target.closest('.family_tree_picker_row');
        if (treeRow && e.target.closest('.family_tree_picker_item')) {
            try {
                await selectTree(treeRow.getAttribute('data-tree-long-id'));
            } catch (err) {
                await treeAlert(err.message, t('Tree Action Failed'));
            }
            return;
        }

        // New Family Tree opens a small form right under the button, like New Family on Manage.
        const newTreeBtn = e.target.closest('#newFamilyTreeBtn');
        if (newTreeBtn) {
            const form = document.getElementById('newFamilyTreeForm');
            if (!form) return;
            form.hidden = !form.hidden;
            newTreeBtn.setAttribute('aria-expanded', String(!form.hidden));
            if (!form.hidden) form.elements.name.focus();
        }
    }, { signal: sig });

    // Export, Copy and Paste share one preview built from the tree's own markup, in the current view.
    function renderPartialPreview(target, preview) {
        const holder = document.createElement('div');
        holder.innerHTML = preview.html || '';
        const list = holder.querySelector(':scope > ul');
        const vertical = container.classList.contains('vertical');
        const icon = path => `<svg viewBox="0 0 24 24"><path d="${path}"/></svg>`;
        // Same buttons and icons as the main canvas cta_panel, without its page-level ids.
        const panel = [
            ['reset', 'Reset View', 'M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z'],
            ['in', 'Zoom In', 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z'],
            ['out', 'Zoom Out', 'M19 13H5v-2h14v2z'],
            ['up', 'Move Up', 'M7.41 15.41L12 10.83l4.59 4.58L18 14l-6-6-6 6z'],
            ['down', 'Move Down', 'M7.41 8.59L12 13.17l4.59-4.58L18 10l-6 6-6-6 1.41-1.41z'],
            ['left', 'Move Left', 'M15.41 16.59L10.83 12l4.58-4.59L14 6l-6 6 6 6 1.41-1.41z'],
            ['right', 'Move Right', 'M8.59 16.59L13.17 12 8.59 7.41 10 6l6 6-6 6-1.41-1.41z'],
        ].map(([action, label, path]) => `<button type="button" class="one_btn_cta_panel" data-partial-view="${action}" title="${escapeHTML(t(label))}" aria-label="${escapeHTML(t(label))}">${icon(path)}</button>`).join('');
        target.innerHTML = `<div class="tree_partial_preview_toolbar"><span>${escapeHTML(t('{count} people').replace('{count}', preview.count || 0))}</span></div><div class="tree_partial_preview_viewport"><div class="tree_partial_preview_stage"></div><div class="cta_panel tree_partial_cta">${panel}</div></div>`;
        const viewport = target.querySelector('.tree_partial_preview_viewport');
        const stage = target.querySelector('.tree_partial_preview_stage');
        stage.classList.add(vertical ? 'vertical' : 'horizontal');
        if (vertical) {
            stage.innerHTML = `<div class="v_tree">${[...(list?.children || [])].map(buildVTreeHTML).join('')}</div>`;
            stage.querySelectorAll('.v_node.has-children').forEach(node => node.classList.add('expanded'));
        } else if (list) stage.append(list);
        const view = { x: 0, y: 0, scale: 1 };
        const apply = () => { stage.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.scale})`; };
        const zoomAt = (factor, px = viewport.clientWidth / 2, py = viewport.clientHeight / 2) => {
            const scale = Math.min(2.5, Math.max(0.15, view.scale * factor));
            view.x = px - (px - view.x) * (scale / view.scale);
            view.y = py - (py - view.y) * (scale / view.scale);
            view.scale = scale;
            apply();
        };
        const reset = () => {
            const width = stage.offsetWidth, height = stage.offsetHeight;
            view.scale = Math.min(1, viewport.clientWidth / Math.max(1, width), vertical ? 1 : viewport.clientHeight / Math.max(1, height));
            view.x = (viewport.clientWidth - width * view.scale) / 2;
            view.y = vertical ? 0 : Math.max(0, (viewport.clientHeight - height * view.scale) / 2);
            apply();
        };
        requestAnimationFrame(reset);
        let dragged = false;
        target.onclick = e => {
            const button = e.target.closest('[data-partial-view]');
            if (button) {
                const step = 80, action = button.dataset.partialView;
                if (action === 'reset') reset();
                else if (action === 'in' || action === 'out') zoomAt(action === 'in' ? 1.2 : 1 / 1.2);
                else { view.x += action === 'left' ? step : action === 'right' ? -step : 0; view.y += action === 'up' ? step : action === 'down' ? -step : 0; apply(); }
                return;
            }
            if (dragged) return;
            const header = e.target.closest('.v_node_header');
            if (header?.parentElement.classList.contains('has-children')) header.parentElement.classList.toggle('expanded');
        };
        viewport.onwheel = e => {
            e.preventDefault();
            e.stopPropagation();
            const box = viewport.getBoundingClientRect();
            zoomAt(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX - box.left, e.clientY - box.top);
        };
        viewport.onpointerdown = e => {
            e.stopPropagation();
            if (e.button !== 0 || e.target.closest('.cta_panel')) return;
            const start = [e.clientX, e.clientY, view.x, view.y];
            dragged = false;
            viewport.setPointerCapture(e.pointerId);
            viewport.onpointermove = move => {
                const dx = move.clientX - start[0], dy = move.clientY - start[1];
                if (!dragged && Math.hypot(dx, dy) < 4) return;
                dragged = true;
                view.x = start[2] + dx;
                view.y = start[3] + dy;
                apply();
            };
            viewport.onpointerup = viewport.onpointercancel = () => { viewport.onpointermove = null; };
        };
    }

    async function openPartialTransfer(relationshipId, kind, personId = '') {
        const content = document.createElement('div');
        content.className = 'tree_partial_transfer';
        content.innerHTML = `<div class="tree_partial_preview" aria-live="polite"><p role="status">${escapeHTML(t('Preparing preview...'))}</p></div><div class="tree_partial_transfer_options">${[
            ['exclude_parents', 'Exclude Main Parents', false], ['photos', 'Export Profile Photos', true], ['gallery', 'Export Gallery Media', true],
        ].map(([name, label, checked]) => `<label><input type="checkbox" name="${name}"${checked ? ' checked' : ''}><span>${escapeHTML(t(label))}</span></label>`).join('')}</div>`;
        let current = null, revision = 0;
        const load = async () => {
            const confirm = document.getElementById('treeUtilityConfirm');
            const mine = ++revision;
            current = null;
            if (confirm) confirm.disabled = true;
            const data = new FormData();
            data.set('action', 'partial_package');
            data.set('tree_long_id', getActiveTreeId());
            data.set('relationship_id', relationshipId);
            content.querySelectorAll('input[type="checkbox"]').forEach(box => data.set(box.name, box.checked ? '1' : '0'));
            const payload = await postTreeAction(data);
            if (mine !== revision || !content.isConnected) return;
            current = payload;
            renderPartialPreview(content.querySelector('.tree_partial_preview'), payload);
            if (confirm) confirm.disabled = false;
        };
        content.addEventListener('change', () => {
            const error = document.getElementById('treeUtilityError');
            if (error) error.textContent = '';
            load().catch(failure => { if (error && content.isConnected) error.textContent = failure.message; });
        });
        await openTreeUtilityDialog({
            type: 'print',
            title: kind === 'export' ? t('Export Partial') : t('Copy Partial'),
            content,
            confirmText: kind === 'export' ? t('Export') : t('Copy Partial'),
            cancelText: t('Cancel'),
            onBack: () => openPartialTreeChooser(relationshipId, personId),
            ready: load,
            onConfirm: () => {
                if (!current) return;
                if (kind === 'copy') {
                    copyText(current.package).then(copied => showTreeFeedback(copied ? t('Partial copied. Use Paste from Add Child in any tree.') : t('Could not copy the Partial.')));
                    return;
                }
                const link = document.createElement('a');
                link.href = URL.createObjectURL(new Blob([current.package], { type: 'application/json' }));
                link.download = `${(current.name || 'partial').replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '').toLowerCase() || 'partial'}.racinage-partial.json`;
                link.click();
                window.setTimeout(() => URL.revokeObjectURL(link.href), 1000);
            },
        });
    }

    // Paste tab: the server verifies the pasted package before anything is previewed or saved.
    let pastedPartial = '', pastePartialRevision = 0, pastePartialTimer = 0;
    // Same-name people already in this family: keep both, keep the existing person, or keep the pasted one in its place.
    function renderPasteConflicts(conflicts = []) {
        const box = document.getElementById('treePastePartialConflicts');
        if (!box) return;
        box.hidden = !conflicts.length;
        if (!conflicts.length) { box.innerHTML = ''; return; }
        const life = item => [item.birth_date ? t('Born {date}').replace('{date}', treeDateDMY(item.birth_date)) : '', item.death_date ? t('Died {date}').replace('{date}', treeDateDMY(item.death_date)) : ''].filter(Boolean).join(' · ');
        const label = (key, item) => [t(key).replace('{name}', item.name), life(item), item.in_tree ? t('in this tree') : ''].filter(Boolean).join(' · ');
        box.innerHTML = `<div class="tree_paste_conflicts_head"><div><h4>${escapeHTML(t('Possible duplicates'))} (${conflicts.length})</h4><p>${escapeHTML(t('People with the same name already exist in this family. Choose what to do with each one.'))}</p></div><div class="tree_paste_conflicts_bulk"><button type="button" class="tree_secondary_action" data-paste-bulk="use">${escapeHTML(t('Keep existing for all'))}</button><button type="button" class="tree_secondary_action" data-paste-bulk="new">${escapeHTML(t('Keep all separate'))}</button></div></div>`
            + conflicts.map(conflict => `<div class="tree_paste_conflict"><div class="tree_paste_conflict_person"><span class="tree_paste_conflict_photos">${conflict.candidates.slice(0, 3).map(item => `<img src="${escapeHTML(item.image_url)}" alt="" title="${escapeHTML(item.name)}">`).join('')}</span><span><strong>${escapeHTML(conflict.name)}</strong><small>${escapeHTML(life(conflict))}</small></span></div><select data-paste-resolution="${escapeHTML(conflict.ref)}" data-suggested="${escapeHTML(conflict.suggested)}" aria-label="${escapeHTML(t('What to do with {name}').replace('{name}', conflict.name))}"><option value="new">${escapeHTML(t('Keep both as separate people'))}</option>${conflict.candidates.map(item => `<option value="use:${item.id}">${escapeHTML(label('Keep existing: {name}', item))}</option><option value="overwrite:${item.id}">${escapeHTML(label('Keep pasted, replace existing: {name}', item))}</option>`).join('')}</select></div>`).join('');
        box.querySelectorAll('select[data-paste-resolution]').forEach(select => { select.value = select.dataset.suggested; });
    }
    function renderPasteSibling(couple = []) {
        const box = document.getElementById('treePasteSibling');
        if (!box) return;
        const action = document.getElementById('treeActionName')?.value;
        const show = couple.length === 2 && action === 'add_child';
        box.hidden = !show;
        const options = box.querySelector('[data-paste-sibling-options]');
        options.innerHTML = show ? couple.map(person => `<button type="button" class="tree_person_source_tab${person.sibling ? ' active' : ''}" data-paste-sibling-ref="${escapeHTML(person.ref)}" aria-pressed="${person.sibling}">${escapeHTML(person.name)}</button>`).join('') : '';
        mountBirthPosition(box.querySelector('[data-birth-position]'), show ? { anchor_person_id: document.getElementById('treeActionAnchorId').value, second_parent_id: document.getElementById('treeActionSecondParentId')?.value || '' } : null);
    }
    function resetPastedPartial() {
        renderPasteSibling();
        renderPasteConflicts();
        pastedPartial = '';
        pastePartialRevision++;
        const input = document.getElementById('treePastePartialInput');
        const preview = document.getElementById('treePastePartialPreview');
        if (input) { input.value = ''; input.hidden = false; }
        if (preview) { preview.hidden = true; preview.innerHTML = ''; }
        const clear = document.querySelector('[data-paste-partial-clear]');
        if (clear) clear.hidden = true;
    }
    async function previewPastedPartial(text) {
        const preview = document.getElementById('treePastePartialPreview');
        const input = document.getElementById('treePastePartialInput');
        const error = document.getElementById('treeActionError');
        const mine = ++pastePartialRevision;
        pastedPartial = '';
        error.textContent = '';
        if (!text.trim() || !preview || !input) return;
        const data = new FormData();
        data.set('action', 'paste_partial_preview');
        data.set('package', text.trim());
        data.set('tree_long_id', getActiveTreeId());
        try {
            const payload = await postTreeAction(data);
            if (mine !== pastePartialRevision) return;
            pastedPartial = text.trim();
            input.hidden = true;
            preview.hidden = false;
            renderPartialPreview(preview, payload);
            renderPasteConflicts(payload.conflicts || []);
            renderPasteSibling(payload.couple || []);
            const clear = document.querySelector('[data-paste-partial-clear]');
            if (clear) clear.hidden = false;
        } catch (failure) {
            if (mine === pastePartialRevision) error.textContent = failure.message;
        }
    }

    // Back from visibility re-asks the name; back from the name returns to the Partial Trees choice.
    async function askPermanentPartial(relationshipId, personId, name = t('Permanent Partial Tree')) {
        let back = null;
        const named = await treePrompt(t('Name this Partial Tree:'), name, t('Permanent Partial Tree'), () => { back = 'chooser'; });
        if (back === 'chooser') { openPartialTreeChooser(relationshipId, personId); return null; }
        if (named === null) return null;
        const visibility = await treeChoice(t('Who can see this Partial Tree?'), [{value:'private',label:t('Private')},{value:'family',label:t('Shared')}], t('Partial Trees'), () => { back = 'name'; });
        if (back === 'name') return askPermanentPartial(relationshipId, personId, named);
        return visibility ? { name: named.trim(), visibility } : null;
    }

    async function openPartialTreeChooser(relationshipId, personId = '') {
        relationshipId = String(relationshipId || '');
        if (!/^\d+$/.test(relationshipId)) return;
        if(container.dataset.partialReanchorId){
            const data=new FormData();data.set('action','update_partial_tree');data.set('partial_id',container.dataset.partialReanchorId);data.set('anchor_relationship_id',relationshipId);data.set('tree_preference_csrf',container.dataset.treePreferenceCsrf||'');
            try{await postTreeAction(data);sessionStorage.removeItem('racinage.partial_tree.reanchor');const partialId=container.dataset.partialReanchorId;delete container.dataset.partialReanchorId;await refreshPartialTreePicker(false,getActiveTreeId(),partialId);}catch(error){await treeAlert(error.message,t('Tree Action Failed'));}
            return;
        }
        const kind=await openTreeUtilityDialog({type:'choice',title:t('Partial Trees'),message:t('Choose Partial Tree type'),onBack:personId?()=>openPersonModal(personId,relationshipId):null,choices:[
            {value:'temporary',label:t('Temporary'),description:t('This view lasts for the current tab session.')},
            {value:'permanent',label:t('Permanent'),description:t('Save this view in the tree picker.')},
            {value:'export',label:t('Export'),description:t('Download this Partial with parents, children and their descendants.')},
            {value:'copy',label:t('Copy Partial'),description:t('Copy this Partial, then paste it from Add Child in any tree.')}
        ]});if(!kind)return;
        if(kind==='export'||kind==='copy')return openPartialTransfer(relationshipId,kind,personId);
        const data=new FormData();data.set('action','create_partial_tree');data.set('tree_long_id',getActiveTreeId());data.set('relationship_id',relationshipId);data.set('partial_kind',kind);data.set('tree_preference_csrf',container.dataset.treePreferenceCsrf||'');
        if(kind==='permanent'){
            const details=await askPermanentPartial(relationshipId,personId);if(!details)return;
            data.set('name',details.name);data.set('visibility',details.visibility);
        }
        try {
            const payload = await postTreeAction(data);
            if (payload.temporary && payload.partial_token) sessionStorage.setItem(partialSessionKey, payload.partial_token);
            if (payload.url && window.htmx) {
                await window.htmx.ajax('GET', payload.url, { target: '#main_container', swap: 'outerHTML' });
                window.history.pushState(window.history.state, '', payload.url);
            } else if (payload.url) window.location.assign(payload.url);
        } catch(error) { await treeAlert(error.message,t('Tree Action Failed')); }
    }

    treeManageMenu?.addEventListener('click', async (e) => {
        if (e.target.closest('[data-racinage-transfer-export]')) { closeTreeManageMenu(); return; }
        e.stopPropagation();
        const collectionAction = e.target.closest('[data-collection-assign]');
        if (collectionAction) {
            closeTreeManageMenu();
            window.RacinageCollections?.openSelector(collectionAction);
            return;
        }
        const actionBtn = e.target.closest('[data-tree-menu-action]');
        if (!actionBtn) return;
        const action = actionBtn.getAttribute('data-tree-menu-action');
        await runTreeManageAction(action);
        closeTreeManageMenu();
    }, { signal: sig });

    async function openVerticalAddChoice(addBtn) {
        const node = addBtn.closest('.v_node');
        if (!node) return;
        const ids = (node.getAttribute('data-v-person-ids') || '').split(',').filter(Boolean);
        const names = (node.getAttribute('data-v-person-names') || '').split('|').filter(Boolean);
        const genders = (node.getAttribute('data-v-person-genders') || '').split(',');
        const primaryId = addBtn.getAttribute('data-person-id') || ids[0] || '';
        const primaryGender = addBtn.getAttribute('data-person-gender') || genders[ids.indexOf(primaryId)] || genders[0] || '';
        const choices = [];

        if (node.getAttribute('data-v-can-add-parents') === 'true' && primaryId) {
            choices.push({ value: `add_parents:${primaryId}`, label: t('Add Parents'), description: t('Create or attach parents above this first member.') });
        }
        if (ids.length === 1 && primaryId) {
            choices.push({ value: `add_spouse:${primaryId}:${primaryGender}`, label: t('Add Spouse to {name}').replace('{name}', names[0] || t('this person')), description: t('Connect a spouse in this tree.') });
        }
        if (primaryId) {
            choices.push({ value: `add_child:${primaryId}`, label: t('Add Child'), description: t('Add a child under this family branch.') });
        }

        if (!choices.length) return;
        const selected = await treeChoice(t('What would you like to add here?'), choices, t('Add To This Branch'));
        if (!selected) return;
        const [action, id, gender] = selected.split(':');
        const onBack = () => openVerticalAddChoice(addBtn);
        if (action === 'add_parents') openTreeModal(action, { childId: id, onBack });
        else openTreeModal(action, { anchorId: id, anchorGender: gender || primaryGender, secondParentId: addBtn.dataset.secondParentId || '', onBack });
    }

    async function runBranchAction(button) {
        if (button.disabled || !container.classList.contains('tree_editing') || container.dataset.treeCanRearrange !== 'true') return;
        const treeId = getActiveTreeId();
        const base = () => {
            const data = new FormData();
            data.set('tree_long_id', treeId);
            data.set('source_relationship_id', button.dataset.sourceRelationshipId || '');
            data.set('source_person_ids', button.dataset.sourcePersonIds || '');
            data.set('person_id', button.dataset.personId || '');
            data.set('expected_parent_ids', button.dataset.expectedParentIds || '');
            return data;
        };
        button.disabled = true;
        try {
            let payload;
            if (button.dataset.treeBranchAction === 'move_child') {
                const coupleSnapshots = new Map();
                payload = await openTreeUtilityDialog({
                    type: 'choice', title: t('Move Child'), inputLabel: t('Search couples'), cancelText: t('Cancel'), onBack: () => {},
                    message: t('Choose the couple that should be this child’s parents.'),
                    loadChoices: async search => { const data = base(); data.set('action', 'list_tree_couples'); data.set('search', search); const rows = (await postTreeAction(data)).couples; rows.forEach(row => coupleSnapshots.set(String(row.id), row.person_ids)); return rows; },
                    onChoose: async id => { const data = base(); data.set('action', 'move_child'); data.set('relationship_id', id); data.set('target_person_ids', coupleSnapshots.get(String(id)) || ''); return postTreeAction(data); },
                });
            } else {
                const data = base(); data.set('action', button.dataset.treeBranchAction === 'change_partner' ? 'change_partner' : 'promote_partner');
                data.set('anchor_person_id', button.dataset.anchorPersonId || '');
                data.set('relationship_id', button.dataset.relationshipId || '');
                data.set('target_person_ids', button.dataset.targetPersonIds || '');
                payload = await postTreeAction(data);
            }
            if (payload && isCurrentTreeView() && treeId === getActiveTreeId()) {
                closePersonModal();
                await applyTreePayload(payload);
                showTreeFeedback(payload.message);
            }
        } catch (error) { if (isCurrentTreeView()) await treeAlert(error.message, t('Tree Action Failed')); }
        finally {
            if (button.isConnected) {
                button.disabled = false;
                if (button.closest('#treePersonModal.open')) button.focus({ preventScroll: true });
            }
        }
    }

    container.addEventListener('click', (e) => {
        const addBtn = e.target.closest('.add_parents, .v_add_parents, .add_spouse, .v_add_spouse, .add_partner, .v_add_partner, .add_child, .v_add_child');
        if (addBtn) {
            e.preventDefault();
            e.stopPropagation();
            if (addBtn.matches('.v_add_child')) {
                openVerticalAddChoice(addBtn);
                return;
            }
            const person = addBtn.closest('.one_person');
            const couple = addBtn.closest('.couple');
            const anchorPerson = person || couple?.querySelector(':scope > .one_person.sibling') || couple?.querySelector(':scope > .one_person');
            const anchorId = addBtn.getAttribute('data-person-id') || anchorPerson?.getAttribute('data-person-id');
            const anchorGender = addBtn.getAttribute('data-person-gender') || anchorPerson?.getAttribute('data-person-gender') || '';
            if (addBtn.matches('.add_parents, .v_add_parents')) {
                if (anchorId) openTreeModal('add_parents', { childId: anchorId });
            } else if (addBtn.matches('.add_spouse, .v_add_spouse')) {
                if (anchorId) openTreeModal('add_spouse', { anchorId, anchorGender });
            } else if (addBtn.matches('.add_partner, .v_add_partner')) {
                if (anchorId) openTreeModal('add_partner', { anchorId, anchorGender });
            } else if (addBtn.matches('.add_child, .v_add_child')) {
                if (anchorId) openTreeModal('add_child', { anchorId, secondParentId: addBtn.getAttribute('data-second-parent-id') || '' });
            }
            return;
        }

        const clickedPerson = e.target.closest('.one_person');
        if (clickedPerson) {
            if (isPublicPreview) return;
            e.preventDefault();
            e.stopPropagation();
            openPersonModal(
                clickedPerson.getAttribute('data-person-id'),
                clickedPerson.closest('.couple')?.getAttribute('data-relationship-id') || '',
                clickedPerson
            );
            return;
        }
        const verticalPhoto = e.target.closest('.v_photos img[data-person-id]');
        if (verticalPhoto) {
            e.preventDefault();
            e.stopPropagation();
            openPersonModal(verticalPhoto.getAttribute('data-person-id'), '', null, verticalPhoto);
            return;
        }

        if (e.target.closest('#addFirstMemberBtn, .empty_tree_add_first')) {
            e.preventDefault();
            e.stopPropagation();
            // Starting a tree means adding someone new, so open on the New Person tab.
            openTreeModal('add_first_member', { source: 'new' });
        }
    }, { signal: sig });

    document.getElementById('treeActionClose')?.addEventListener('click', closeTreeModal, { signal: sig });
    // Back to the popup this form was opened from (unsaved edits are discarded, like Close).
    document.getElementById('treeActionBack')?.addEventListener('click', event => {
        const onBack = treeActionBackHandler;
        event.currentTarget.blur(); // Return focus to the tree card, not this hidden button.
        closeTreeModal();
        onBack?.();
    }, { signal: sig });
    // Back from a person popup opened from another popup (e.g. Stats).
    document.getElementById('treePersonBack')?.addEventListener('click', event => {
        const onBack = personBackHandler;
        event.currentTarget.blur();
        closePersonModal();
        onBack?.();
    }, { signal: sig });
    document.getElementById('treeActionCancel')?.addEventListener('click', closeTreeModal, { signal: sig });
    document.getElementById('treeActionModal')?.addEventListener('click', (e) => {
        if (e.target.id === 'treeActionModal') closeTreeModal();
    }, { signal: sig });
    document.getElementById('treePersonSourceTabs')?.addEventListener('click', (e) => {
        const tab = e.target.closest('[data-person-source]');
        if (!tab) return;
        setPersonSource(tab.getAttribute('data-person-source'));
    }, { signal: sig });
    document.getElementById('treePastePartialInput')?.addEventListener('input', (e) => {
        window.clearTimeout(pastePartialTimer);
        const text = e.currentTarget.value;
        pastePartialTimer = window.setTimeout(() => previewPastedPartial(text), 250);
    }, { signal: sig });
    document.getElementById('treePastePartialSection')?.addEventListener('click', (e) => {
        if (e.target.closest('[data-paste-partial-clear]')) { resetPastedPartial(); document.getElementById('treePastePartialInput')?.focus(); }
        if (e.target.closest('[data-paste-partial-choose]')) document.getElementById('treePastePartialFile')?.click();
        const siblingChoice = e.target.closest('[data-paste-sibling-ref]');
        if (siblingChoice) siblingChoice.parentElement.querySelectorAll('[data-paste-sibling-ref]').forEach(button => {
            button.classList.toggle('active', button === siblingChoice);
            button.setAttribute('aria-pressed', String(button === siblingChoice));
        });
        const bulk = e.target.closest('[data-paste-bulk]');
        if (bulk) e.currentTarget.querySelectorAll('select[data-paste-resolution]').forEach(select => {
            const suggested = select.dataset.suggested.startsWith('use:') ? select.dataset.suggested : select.querySelector('option[value^="use:"]')?.value;
            select.value = bulk.dataset.pasteBulk === 'use' && suggested ? suggested : 'new';
        });
    }, { signal: sig });
    document.getElementById('treePastePartialFile')?.addEventListener('change', async (e) => {
        const file = e.currentTarget.files?.[0];
        e.currentTarget.value = '';
        if (!file) return;
        const text = await file.text();
        const input = document.getElementById('treePastePartialInput');
        if (input) input.value = text;
        previewPastedPartial(text);
    }, { signal: sig });
    document.getElementById('treeExistingPersonSearch')?.addEventListener('input', (e) => {
        window.clearTimeout(existingPeopleSearchTimer);
        existingPeopleSearchTimer = window.setTimeout(() => {
            loadAvailablePeople(e.currentTarget.value.trim());
        }, 220);
    }, { signal: sig });
    document.getElementById('treeExistingPersonGrid')?.addEventListener('wheel', (e) => {
        e.stopPropagation();
    }, { passive: true, signal: sig });
    document.getElementById('treeExistingPersonGrid')?.addEventListener('scroll', (e) => {
        const grid = e.currentTarget;
        if (grid.scrollTop + grid.clientHeight >= grid.scrollHeight - 80) {
            loadAvailablePeople(existingPeopleState.search, true);
        }
    }, { signal: sig });
    document.getElementById('treeExistingPersonGrid')?.addEventListener('click', async (e) => {
        const card = e.target.closest('.tree_existing_person_card');
        if (!card || card.classList.contains('loading')) return;
        const form = document.getElementById('treeActionForm');
        const error = document.getElementById('treeActionError');
        if (!form || !error) return;
        form.querySelectorAll('.tree_existing_person_card').forEach(btn => btn.disabled = true);
        card.classList.add('loading');
        document.getElementById('treePersonSource').value = 'existing';
        document.getElementById('treeExistingPersonId').value = card.getAttribute('data-person-id') || '';
        error.textContent = '';
        try {
            const payload = await postTreeAction(new FormData(form));
            closeTreeModal();
            await applyTreePayload(payload);
        } catch (err) {
            error.textContent = err.message;
            card.classList.remove('loading');
            form.querySelectorAll('.tree_existing_person_card').forEach(btn => btn.disabled = false);
        }
    }, { signal: sig });
    document.getElementById('treeActionForm')?.addEventListener('change', (e) => {
        const unknownDeath = e.target.closest('[data-deceased-unknown]');
        if (unknownDeath) {
            const date = unknownDeath.closest('.person_death_field').querySelector('[type="date"]');
            if (unknownDeath.checked) date.value = '';
            date.disabled = unknownDeath.checked;
            return;
        }
        const unknown = e.target.closest('[data-unknown-identity]');
        if (!unknown) return;
        const prefix = unknown.dataset.unknownIdentity;
        ['first_name', 'last_name'].forEach(field => {
            const input = e.currentTarget.elements.namedItem(prefix + field);
            if (!input) return;
            if (unknown.checked) { input.dataset.knownValue = input.value; input.value = field === 'first_name' ? unknown.dataset.unknownLabel : ''; }
            else input.value = input.dataset.knownValue || '';
            input.readOnly = unknown.checked;
        });
    }, { signal: sig });
    document.getElementById('treeActionForm')?.addEventListener('change', (e) => {
        const input = e.target.closest('.tree_image_picker input[type="file"]');
        if (!input) return;
        const picker = input.closest('.tree_image_picker');
        const preview = picker?.querySelector('.tree_image_preview');
        const file = input.files?.[0];
        if (!picker || !preview || !file) return;
        if (!file.type.startsWith('image/')) {
            input.value = '';
            picker.classList.remove('has_image');
            preview.innerHTML = '<span>+</span>';
            document.getElementById('treeActionError').textContent = 'Choose a valid image file.';
            return;
        }
        const reader = new FileReader();
        reader.onload = () => {
            if (!input.isConnected || input.files?.[0] !== file) return;
            picker.classList.add('has_image');
            preview.innerHTML = `<img src="${escapeHTML(reader.result)}" alt="Selected profile image">`;
        };
        reader.readAsDataURL(file);
    }, { signal: sig });
    document.getElementById('treeActionForm')?.addEventListener('submit', async (e) => {
        e.preventDefault();
        const form = e.currentTarget;
        if (form.dataset.personPhotoRemoving === '1' || form.dataset.personSaving === '1') return;
        const error = document.getElementById('treeActionError');
        error.textContent = '';
        if (document.getElementById('treePersonSource')?.value === 'existing') return;
        if (document.getElementById('treePersonSource')?.value === 'paste') {
            if (!pastedPartial) { error.textContent = t('Paste a copied Partial first.'); return; }
            form.dataset.personSaving = '1';
            try {
                const data = new FormData();
                data.set('action', 'paste_partial');
                data.set('package', pastedPartial);
                data.set('paste_mode', document.getElementById('treeActionName').value);
                data.set('tree_long_id', getActiveTreeId());
                data.set('anchor_person_id', document.getElementById('treeActionAnchorId').value);
                data.set('second_parent_id', document.getElementById('treeActionSecondParentId')?.value || '');
                const resolutions = {};
                form.querySelectorAll('select[data-paste-resolution]').forEach(select => { resolutions[select.dataset.pasteResolution] = select.value; });
                data.set('resolutions', JSON.stringify(resolutions));
                const sibling = document.getElementById('treePasteSibling');
                if (sibling && !sibling.hidden) {
                    data.set('paste_sibling_ref', sibling.querySelector('[data-paste-sibling-ref].active')?.dataset.pasteSiblingRef || '');
                    const order = sibling.querySelector('[data-birth-order]');
                    if (order && !order.disabled) {
                        data.set('sibling_birth_order', order.value);
                        data.set('sibling_birth_moves', sibling.querySelector('[data-birth-moves]').value);
                    }
                }
                const payload = await postTreeAction(data);
                if (!isCurrentTreeView()) return;
                closeTreeModal();
                await applyTreePayload(payload);
                showTreeFeedback(payload.message);
            } catch (err) {
                error.textContent = err.message;
            } finally {
                delete form.dataset.personSaving;
            }
            return;
        }
        form.dataset.personSaving = '1';
        try {
            let pending = pendingGallerySaves.get(form);
            if (!pending) {
                const media = [...(form.querySelector('[data-person-gallery-files]')?.files || [])];
                if (media.length > 20) throw new Error(t('Choose between 1 and 20 media files at a time.'));
                const payload = await postTreeAction(new FormData(form));
                pending = { payload, media };
                pendingGallerySaves.set(form, pending);
                if (pending.media.length) form.querySelectorAll('input:not(:disabled), select:not(:disabled), textarea:not(:disabled)').forEach(control => {
                    control.disabled = true;
                    control.dataset.gallerySaveLock = '';
                });
            }
            const payload = pending.payload;
            if (pending.media.length) {
                const person = await fetchPerson(payload.focus_person_id);
                const data = new FormData();
                data.set('action', 'upload');
                data.set('person', person.long_id);
                data.set('csrf', container.dataset.treePreferenceCsrf || '');
                pending.media.forEach(file => data.append('media_files[]', file));
                const response = await fetch(rootUrl('includes/person_gallery.include.php'), { method: 'POST', credentials: 'same-origin', body: data });
                const result = await response.json();
                if (!response.ok || !result.ok) throw new Error(t('Person saved. Retry to finish uploading the gallery.') + ' ' + (result.message || ''));
                pending.media = [];
            }
            if (!isCurrentTreeView() || !form.isConnected) return;
            pendingGallerySaves.delete(form);
            closeTreeModal();
            await applyTreePayload(payload);
        } catch (err) {
            if (err.code === 'tree_not_found') {
                closeTreeModal();
                await ensureActiveTree().catch(fallback => window.racinageShowFeedback?.(fallback.message, 'fail'));
                return;
            }
            error.textContent = err.message;
        } finally {
            delete form.dataset.personSaving;
        }
    }, { signal: sig });

    let personOpenRequest = 0, personBackHandler = null;
    async function openPersonModal(personId, coupleRelationshipId = '', sourceNode = null, focusTrigger = sourceNode, onBack = null) {
        const modal = document.getElementById('treePersonModal');
        if (!modal || !personId) return;
        const request = ++personOpenRequest;
        const treeId = getActiveTreeId();
        const opener = focusTrigger || document.activeElement;
        try {
            const person = await fetchPerson(personId);
            if (request !== personOpenRequest || !isCurrentTreeView() || treeId !== getActiveTreeId() || !modal.isConnected) return;
            const isEditing = container.classList.contains('tree_editing');
            const node = sourceNode?.isConnected ? sourceNode : canvas.querySelector(`.one_person[data-person-id="${escapeSelector(person.id)}"]`);
            coupleRelationshipId ||= node?.closest('.couple')?.dataset.relationshipId || '';
            const generation = node ? generationDepth(node) : '';
            window.racinageTreePersonDetailsModal.open({
                modal,
                person,
                editing: isEditing,
                generation,
                translate: t,
                returnFocus: opener && opener !== document.body ? opener : node,
            });
            personBackHandler = onBack;
            const personBack = document.getElementById('treePersonBack');
            if (personBack) personBack.hidden = typeof onBack !== 'function';
            const lifeBtn = document.getElementById('treePersonLifeStatusBtn');
            if (lifeBtn) lifeBtn.textContent = Number(person.is_deceased) === 1 || person.death_date ? t('Mark Alive') : t('Mark Dead');
            const hideBtn = modal.querySelector('[data-person-action="hide"]');
            if (hideBtn) hideBtn.textContent = person.visibility === 'hidden' ? t('Show') : t('Hide');
            modal.querySelector('[data-person-action="main_relationship"]')?.remove();
            modal.querySelector('[data-person-action="partial_tree"]')?.remove();
            modal.querySelectorAll('[data-tree-branch-action]').forEach(button => button.remove());
            const actions = modal.querySelector('.tree_person_actions');
            if (isEditing && actions && container.dataset.treeCanRearrange === 'true') {
                const selector = `:scope > template[data-tree-person-actions][data-person-id="${escapeSelector(person.id)}"]`;
                const template = node?.querySelector(selector) || node?.closest('li')?.querySelector(selector);
                if (template) {
                    const buttons = template.content.cloneNode(true);
                    const remove = actions.querySelector('[data-person-action="remove"]');
                    actions.insertBefore(buttons, remove);
                }
            }
            if (isEditing && person.main_relationship_eligible && !person.main_relationship_selected) {
                const mainCoupleBtn = document.createElement('button');
                mainCoupleBtn.type = 'button';
                mainCoupleBtn.className = 'button1 secondary';
                mainCoupleBtn.setAttribute('data-person-action', 'main_relationship');
                mainCoupleBtn.setAttribute('data-relationship-id', person.main_relationship_id || '');
                mainCoupleBtn.textContent = t('Use as main couple');
                const updateBtn = modal.querySelector('[data-person-action="update"]') || modal.querySelector('[data-person-action="replace"]');
                updateBtn?.insertAdjacentElement('afterend', mainCoupleBtn);
            }
            if (isEditing && /^\d+$/.test(String(coupleRelationshipId || ''))) {
                const partialBtn = document.createElement('button');
                partialBtn.type = 'button';
                partialBtn.className = 'button1 secondary';
                partialBtn.setAttribute('data-person-action', 'partial_tree');
                partialBtn.setAttribute('data-relationship-id', String(coupleRelationshipId));
                partialBtn.textContent = t('Partial');
                const insertAfter = modal.querySelector('[data-person-action="main_relationship"]')
                    || modal.querySelector('[data-person-action="update"]') || modal.querySelector('[data-person-action="replace"]');
                insertAfter?.insertAdjacentElement('afterend', partialBtn);
            }
            const actionHelp = {
                replace: t('Replace this person with another person in this tree.'),
                update: t('Edit this person details.'),
                hide: person.visibility === 'hidden' ? t('Show this person details in the tree.') : t('Hide this person details in the tree.'),
                main_relationship: t('Use as main couple'),
                partial_tree: t('Create Partial Tree'),
                life_status: Number(person.is_deceased) === 1 || person.death_date ? t('Mark this person as alive.') : t('Mark this person as deceased.'),
                remove: t('Remove this person from this tree.'),
            };
            modal.querySelectorAll('.tree_person_actions [data-person-action]').forEach(btn => {
                btn.setAttribute('data-action-help', actionHelp[btn.getAttribute('data-person-action')] || btn.textContent.trim());
            });
            if (actions) actions.style.setProperty('--person-action-columns', Math.ceil(actions.querySelectorAll('button:not([hidden])').length / 2));
        } catch (err) {
            if (request === personOpenRequest && isCurrentTreeView() && treeId === getActiveTreeId()) await treeAlert(err.message, t('Could Not Load Person'));
        }
    }

    let personActionTipTimer = 0;
    let personActionTipTarget = null;

    function personActionTip() {
        let tip = document.querySelector('.tree_person_action_tip');
        if (!tip) {
            tip = document.createElement('div');
            tip.className = 'tree_person_action_tip';
            tip.id = 'treePersonActionTip';
            tip.setAttribute('role', 'tooltip');
            tip.setAttribute('aria-hidden', 'true');
            document.body.appendChild(tip);
        }
        return tip;
    }

    function positionPersonActionTip(btn, tip) {
        const rect = btn.getBoundingClientRect();
        const tipWidth = tip.offsetWidth || 220;
        const tipHeight = tip.offsetHeight || 40;
        const center = rect.left + rect.width / 2;
        const left = Math.max(8, Math.min(window.innerWidth - tipWidth - 8, center - tipWidth / 2));
        const top = Math.max(8, rect.top - tipHeight - 10);
        const arrowLeft = Math.max(10, Math.min(tipWidth - 22, center - left - 6));
        tip.style.left = `${Math.round(left)}px`;
        tip.style.top = `${Math.round(top)}px`;
        tip.style.setProperty('--action-tip-arrow-left', `${Math.round(arrowLeft)}px`);
    }

    function showPersonActionTip(btn) {
        const text = btn?.getAttribute('data-action-help') || '';
        if (!text) return;
        personActionTipTarget = btn;
        window.clearTimeout(personActionTipTimer);
        personActionTipTimer = window.setTimeout(() => {
            if (personActionTipTarget !== btn || !btn.isConnected || !document.getElementById('treePersonModal')?.classList.contains('open')) return;
            const tip = personActionTip();
            tip.textContent = text;
            tip.classList.add('visible');
            tip.setAttribute('aria-hidden', 'false');
            positionPersonActionTip(btn, tip);
        }, 800);
    }

    function hidePersonActionTip() {
        personActionTipTarget = null;
        window.clearTimeout(personActionTipTimer);
        const tip = document.querySelector('.tree_person_action_tip');
        if (!tip) return;
        tip.classList.remove('visible');
        tip.setAttribute('aria-hidden', 'true');
    }

    function closePersonModal() {
        personOpenRequest++;
        const modal = document.getElementById('treePersonModal');
        if (!modal) return;
        hidePersonActionTip();
        window.racinageTreePersonDetailsModal.close(modal);
    }

    document.getElementById('treePersonClose')?.addEventListener('click', closePersonModal, { signal: sig });
    window.addEventListener('resize', hidePersonActionTip, { signal: sig });
    document.getElementById('treePersonModal')?.addEventListener('scroll', () => {
        const tip = document.querySelector('.tree_person_action_tip.visible');
        if (tip && personActionTipTarget) positionPersonActionTip(personActionTipTarget, tip);
    }, { capture: true, passive: true, signal: sig });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            hidePersonActionTip();
            const modal = document.getElementById('treePersonModal');
            if (modal?.classList.contains('open') && !modal.inert && !document.getElementById('treeUtilityModal')?.classList.contains('open')) closePersonModal();
            else personOpenRequest++;
        }
    }, { signal: sig });
    sig.addEventListener('abort', () => {
        hidePersonActionTip();
        document.querySelector('.tree_person_action_tip')?.remove();
    }, { once: true });
    document.getElementById('treePersonModal')?.addEventListener('mouseover', (e) => {
        const btn = e.target.closest('.tree_person_actions [data-action-help]');
        if (btn) showPersonActionTip(btn);
    }, { signal: sig });
    document.getElementById('treePersonModal')?.addEventListener('mouseout', (e) => {
        const btn = e.target.closest('.tree_person_actions [data-action-help]');
        if (btn && !btn.contains(e.relatedTarget)) hidePersonActionTip();
    }, { signal: sig });
    document.getElementById('treePersonModal')?.addEventListener('focusin', (e) => {
        const btn = e.target.closest('.tree_person_actions [data-action-help]');
        if (btn) showPersonActionTip(btn);
    }, { signal: sig });
    document.getElementById('treePersonModal')?.addEventListener('focusout', hidePersonActionTip, { signal: sig });
    document.getElementById('treePersonModal')?.addEventListener('click', async (e) => {
        if (e.target.id === 'treePersonModal') {
            closePersonModal();
            return;
        }
        const relative = e.target.closest('[data-open-person]');
        if (relative) {
            const fromId = document.getElementById('treePersonModal')?.dataset.personId || '';
            closePersonModal();
            openPersonModal(relative.dataset.openPerson, '', null, null, fromId ? () => openPersonModal(fromId) : null);
            return;
        }
        const actionBtn = e.target.closest('[data-person-action]');
        if (!actionBtn) return;
        hidePersonActionTip();
        if (actionBtn.dataset.treeBranchAction) {
            e.preventDefault();
            e.stopPropagation();
            await runBranchAction(actionBtn);
            return;
        }
        const modal = document.getElementById('treePersonModal');
        const personId = modal?.dataset.personId;
        const person = modal?.dataset.person ? JSON.parse(modal.dataset.person) : null;
        const action = actionBtn.getAttribute('data-person-action');

        if (action === 'update') {
            closePersonModal();
            openTreeModal('update_person', { person, personId, onBack: () => openPersonModal(personId) });
            return;
        }
        if (action === 'replace') {
            closePersonModal();
            openTreeModal('replace_person', { person: { id: personId, first_name: '', last_name: '', gender: person?.gender || 'M' }, onBack: () => openPersonModal(personId) });
            return;
        }
        if (action === 'partial_tree') {
            const relationshipId = actionBtn.getAttribute('data-relationship-id') || '';
            closePersonModal();
            await openPartialTreeChooser(relationshipId, personId);
            return;
        }

        const data = new FormData();
        data.set('tree_long_id', getActiveTreeId());
        data.set('person_id', personId);
        if (action === 'hide') {
            data.set('action', 'hide_person');
            data.set('visibility', person?.visibility === 'hidden' ? 'visible' : 'hidden');
        } else if (action === 'sibling_status') {
            data.set('action', 'set_sibling_status');
            data.set('sibling_value', actionBtn.getAttribute('data-sibling-value') === 'no' ? 'no' : 'yes');
        } else if (action === 'main_relationship') {
            data.set('action', 'set_primary_relationship');
            data.set('relationship_id', actionBtn.getAttribute('data-relationship-id') || '');
        } else if (action === 'life_status') {
            const isDeceased = Number(person?.is_deceased) === 1 || !!person?.death_date;
            const confirmed = await treeConfirm(isDeceased ? t('Mark this person as alive?') : t('Mark this person as deceased with an unknown death date?'), isDeceased ? t('Mark Alive') : t('Mark Dead'), false, () => {});
            if (!confirmed) return;
            data.set('action', 'toggle_life_status');
        } else if (action === 'remove') {
            if (person?.has_spouse || person?.has_children) {
                const selected = await treeChoice(t('Remove person from tree confirm'), [
                    {
                        value: 'keep',
                        label: t('Remove person only but keep other relationships'),
                        description: t('Leave a hidden placeholder in this tree branch.'),
                    },
                    {
                        value: 'relationships',
                        label: t('Remove person and their relationships'),
                        description: t('Remove this person from the tree branch with their spouse or child links.'),
                    },
                ], t('Remove From Tree'), () => {});
                if (!selected) return;
                data.set('remove_mode', selected);
            } else {
                const confirmed = await treeConfirm(t('Remove person from tree confirm'), t('Remove From Tree'), true, () => {});
                if (!confirmed) return;
                data.set('remove_mode', 'relationships');
            }
            data.set('action', 'remove_person_from_tree');
        } else {
            return;
        }
        try {
            const payload = await postTreeAction(data);
            closePersonModal();
            await applyTreePayload(payload);
        } catch (err) {
            await treeAlert(err.message, t('Tree Action Failed'));
        }
    }, { signal: sig });

    connectTreeLiveStream();
    document.addEventListener('htmx:beforeCleanupElement', event => {
        if (event.detail?.elt === container) window._ftAbort?.abort();
    }, { signal: sig });
    sig.addEventListener('abort', () => {
        treeLiveSource?.close();
        window.clearTimeout(treeLiveReconnect);
    }, { once: true });
    window.setTimeout(highlightFocusedPerson, 300);
    window.applyTreePayload = applyTreePayload;
    window.racinagePrintTree = async () => {
        if (!isCurrentTreeView() || !container.dataset.treePrintUrl) return;
        const { printTree } = await import(container.dataset.treePrintUrl);
        if (!isCurrentTreeView()) return;
        await printTree({ t, signal: sig, dialog: openTreeUtilityDialog, snapshot: async () => {
            const vertical = container.classList.contains('vertical');
            if (vertical) await applyLayout('horizontal', false);
            await waitForHorizontalAssets();
            const cards = [...canvas.querySelectorAll('.tree .one_person')].map(card => ({
                ...canvasBox(card), name: card.title || card.querySelector(':scope > span.limit_lines')?.textContent || '',
                image: card.querySelector(':scope > img')?.src || '',
                dead: card.classList.contains('dead'),
            }));
            const paths = [...ensureHorizontalConnectorLayer().querySelectorAll('path')].map(path => path.getAttribute('d'));
            if (vertical) await applyLayout('vertical', false);
            return { cards, paths, title: pickerToggle?.textContent.trim() || t('Family Tree') };
        } });
    };
    if (window.racinagePendingTreePrint === getActiveTreeId()) {
        window.racinagePendingTreePrint = '';
        window.setTimeout(() => { if (isCurrentTreeView()) window.racinagePrintTree(); }, 0);
    }
    // Stats bar extras: live zoom label, click to reset the view, and focus a person by id.
    const updateZoomLabel = () => {
        const label = document.querySelector('[data-tree-zoom-label]');
        const treeCanvas = document.getElementById('treeCanvas');
        if (!label || !treeCanvas) return;
        // Read the target scale from the inline transform; the computed one lags during the zoom transition.
        const inline = /scale\(([\d.]+)\)/.exec(treeCanvas.style.transform || '');
        const scale = inline ? Number(inline[1]) : (new DOMMatrixReadOnly(getComputedStyle(treeCanvas).transform).a || 1);
        label.textContent = `${Math.round(scale * 100)}%`;
    };
    // The canvas can be replaced when a tree loads, so watch the container for any canvas style change.
    const zoomHost = document.getElementById('tree_container');
    if (zoomHost) {
        const zoomObserver = new MutationObserver((mutations) => {
            if (mutations.some((mutation) => mutation.target.id === 'treeCanvas' || mutation.type === 'childList')) updateZoomLabel();
        });
        zoomObserver.observe(zoomHost, { subtree: true, attributes: true, attributeFilter: ['style'], childList: true });
        sig.addEventListener('abort', () => zoomObserver.disconnect(), { once: true });
    }
    const statsBar = document.querySelector('.wrapper_stats');
    if (statsBar) {
        new MutationObserver(updateZoomLabel).observe(statsBar, { childList: true });
        updateZoomLabel();
    }
    document.addEventListener('click', (event) => {
        if (event.target.closest('[data-tree-zoom-reset]')) { window.racinageResetTreeView?.(); return; }
        const focus = event.target.closest('[data-tree-focus-person]');
        if (focus && isCurrentTreeView()) highlightFocusedPerson(focus.dataset.treeFocusPerson);
    }, { signal: sig });

    window.racinageResetTreeView = async function () {
        if (!document.querySelector('.tree_container')) return;
        await resetTreeView();
    };
    window.racinageReflowTree = async function () {
        if (!document.querySelector('.tree_container')) return;
        if (container.classList.contains('vertical')) {
            buildVerticalTree();
            await drawHorizontalChildConnectors();
        } else {
            await refreshHorizontalTreeConnectors();
        }
        queueGenerationIndicatorUpdate();
    };
}

// The script lives in <head> (loaded once). Re-initialize whenever the tree
// page is swapped in via HTMX, and also on the first full-page load.
if (!window.racinageFamilyTreeInitializerBound) {
    window.racinageFamilyTreeInitializerBound = true;
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', familyTreeInitializer);
    else familyTreeInitializer();
    document.addEventListener('htmx:afterSettle', () => {
        if (document.querySelector('.tree_container')) familyTreeInitializer();
    });
}
