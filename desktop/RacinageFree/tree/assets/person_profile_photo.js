/* Remove only the saved profile photo, without submitting other editor fields. */
(() => {
  if (window.racinagePersonProfilePhoto) return;
  const bindings = new WeakMap();
  const clear = form => form?.querySelectorAll('input[type="file"]').forEach(input => bindings.get(input)?.dispose());
  const bind = ({ form, input, preview, person, endpoint, csrf, translate, onRemoved, clientId = '' }) => {
    bindings.get(input)?.dispose();
    if (!input || !preview || !person?.long_id || !person.image_name) return;
    const t = translate || (key => key);
    const wrapper = document.createElement('span'); wrapper.className = 'person_profile_photo_wrap';
    preview.before(wrapper); wrapper.append(preview);
    const button = document.createElement('button'); button.type = 'button'; button.className = 'person_profile_photo_remove';
    button.title = button.ariaLabel = t('Remove profile image');
    button.innerHTML = window.racinageMenuIcon?.('delete') || '';
    const dropzone = input.closest('.tree_image_picker');
    (dropzone || wrapper).append(button);
    const state = { busy: false, locks: [] };
    const current = () => bindings.get(input) === state && form.isConnected;
    const unlock = () => {
      state.locks.forEach(([control, disabled]) => { control.disabled = disabled; });
      state.locks = []; delete form.dataset.personPhotoRemoving;
    };
    const change = () => { if (!state.busy) button.hidden = !person.image_name || Boolean(input.files?.length); };
    state.dispose = () => {
      input.removeEventListener('change', change);
      if (state.busy) preview.style.visibility = state.previousVisibility || '';
      if (bindings.get(input) === state) { unlock(); bindings.delete(input); }
      button.remove();
      if (wrapper.isConnected) { wrapper.before(preview); wrapper.remove(); }
    };
    bindings.set(input, state); input.addEventListener('change', change);
    button.addEventListener('click', async event => {
      event.preventDefault(); event.stopPropagation();
      if (state.busy || !current() || input.files?.length || form.dataset.personSaving === '1' || form.querySelector('[type="submit"]:disabled')) return;
      state.busy = true; form.dataset.personPhotoRemoving = '1';
      const previous = preview.innerHTML;
      const previousHidden = preview.hidden;
      const previousVisibility = preview.style.visibility;
      state.previousVisibility = previousVisibility;
      state.locks = [input, ...form.querySelectorAll('[type="submit"]')].map(control => [control, control.disabled]);
      state.locks.forEach(([control]) => { control.disabled = true; });
      button.disabled = true;
      preview.style.visibility = 'hidden';
      try {
        const response = await fetch(endpoint, {
          method: 'POST', credentials: 'same-origin', headers: { 'X-Racinage-CSRF': csrf, Accept: 'application/json' },
          body: new URLSearchParams({ action: 'remove_photo', csrf, person: person.long_id, expected_image: person.image_name, tree_sync_client_id: clientId })
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok || !payload.ok) throw new Error(payload.message || t('Could not remove the profile image.'));
        if (!current()) return;
        person.image_name = ''; person.image_url = person.image_sm_url = person.image_lg_url = payload.person.image_url;
        input.value = ''; input.dispatchEvent(new Event('change', { bubbles: true }));
        const image = document.createElement('img'); image.src = payload.person.image_url; image.alt = t('Profile image');
        preview.replaceChildren(image); preview.hidden = false; button.hidden = true;
        state.focusUpload = document.activeElement === button || document.activeElement === document.body;
        onRemoved?.(payload.person);
        window.racinageShowFeedback?.(payload.message || t('Profile image removed.'), 'success');
      } catch (error) {
        if (!current()) return;
        preview.innerHTML = previous; preview.hidden = previousHidden;
        window.racinageShowFeedback?.(error.message, 'fail');
      } finally {
        if (current()) {
          preview.style.visibility = previousVisibility; unlock(); state.busy = false; button.disabled = false;
          if (state.focusUpload) input.focus({ preventScroll: true });
        }
      }
    });
  };
  window.racinagePersonProfilePhoto = { bind, clear };
})();
