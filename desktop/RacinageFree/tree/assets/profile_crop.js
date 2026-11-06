/* Confirm a square crop locally before handing the file to existing save flows. */
(() => {
  'use strict';
  if (window.racinageProfileCrop) return;
  window.racinageProfileCrop = true;
  const selector = 'input[type="file"][data-profile-crop]';
  const accepted = new WeakMap();
  const confirmed = new WeakSet();
  let active = null;
  const label = key => {
    try { return JSON.parse(document.getElementById('profile-crop-labels')?.textContent || '{}')[key] || key; }
    catch (_) { return key; }
  };
  const files = file => {
    const transfer = new DataTransfer();
    if (file) transfer.items.add(file);
    return transfer.files;
  };
  const preview = (input, file) => {
    let target = input.dataset.profileCropPreview ? document.querySelector(input.dataset.profileCropPreview) : null;
    target ||= input.closest('label')?.querySelector('[data-profile-crop-preview]');
    if (!target) return; // Existing change handlers own the other previews.
    const image = target.tagName === 'IMG' ? target : document.createElement('img');
    image.alt = label('Image preview');
    const url = URL.createObjectURL(file);
    image.onload = image.onerror = () => URL.revokeObjectURL(url);
    image.src = url;
    if (image !== target) target.replaceChildren(image);
    target.hidden = false;
  };
  function close() {
    if (!active) return;
    const state = active;
    active = null;
    state.observer.disconnect();
    URL.revokeObjectURL(state.url);
    state.dialog.close();
    state.dialog.remove();
    if (state.trigger?.isConnected) state.trigger.focus();
  }
  function open(input, file) {
    close();
    const dialog = document.createElement('dialog');
    dialog.className = 'profile_crop_dialog';
    dialog.setAttribute('aria-labelledby', 'profile-crop-title');
    const create = (tag, text, parent = dialog) => {
      const element = document.createElement(tag);
      if (text) element.textContent = label(text);
      parent.appendChild(element);
      return element;
    };
    const title = create('h2', 'Crop image'); title.id = 'profile-crop-title';
    const hint = create('p', 'Drag to reposition. Use arrow keys for precise adjustments.'); hint.id = 'profile-crop-hint';
    const canvas = create('canvas'); canvas.width = canvas.height = 640; canvas.tabIndex = 0;
    canvas.setAttribute('aria-label', label('Image preview'));
    canvas.setAttribute('aria-describedby', hint.id);
    const zoomLabel = create('label', 'Zoom'); zoomLabel.className = 'profile_crop_zoom';
    const zoom = create('input', '', zoomLabel); zoom.type = 'range'; zoom.min = '1'; zoom.max = '4'; zoom.step = '.01'; zoom.value = '1';
    zoom.setAttribute('aria-label', label('Zoom'));
    const zoomValue = create('output', '', zoomLabel); zoomValue.setAttribute('aria-hidden', 'true');
    const lightLabel = create('label', 'Light'); lightLabel.className = 'profile_crop_zoom';
    const light = create('input', '', lightLabel); light.type = 'range'; light.min = '-50'; light.max = '50'; light.step = '1'; light.value = '0';
    light.setAttribute('aria-label', label('Light'));
    const lightValue = create('output', '', lightLabel); lightValue.setAttribute('aria-hidden', 'true');
    const error = create('p'); error.className = 'profile_crop_error'; error.setAttribute('role', 'alert');
    const actions = create('div'); actions.className = 'profile_crop_actions';
    const reset = create('button', 'Reset', actions);
    const cancel = create('button', 'Cancel', actions);
    const use = create('button', 'Use crop', actions); use.className = 'button1';
    [reset, cancel, use].forEach(button => { button.type = 'button'; });
    use.disabled = reset.disabled = zoom.disabled = light.disabled = true;
    const image = new Image();
    const state = { input, dialog, trigger: document.activeElement, url: URL.createObjectURL(file), observer: null };
    active = state;
    let scale = 1, base = 1, x = 0, y = 0, drag = null;
    const current = () => active === state && input.isConnected;
    const draw = () => {
      x = Math.min(0, Math.max(640 - image.naturalWidth * scale, x));
      y = Math.min(0, Math.max(640 - image.naturalHeight * scale, y));
      const ctx = canvas.getContext('2d');
      ctx.clearRect(0, 0, 640, 640);
      ctx.drawImage(image, x, y, image.naturalWidth * scale, image.naturalHeight * scale);
      // Adjust the exported pixels as well as the preview, without accumulating edits.
      const brightness = 1 + Number(light.value) / 100;
      if (brightness !== 1) {
        const pixels = ctx.getImageData(0, 0, 640, 640);
        for (let i = 0; i < pixels.data.length; i += 4) {
          pixels.data[i] *= brightness;
          pixels.data[i + 1] *= brightness;
          pixels.data[i + 2] *= brightness;
        }
        ctx.putImageData(pixels, 0, 0);
      }
      zoomValue.value = Math.round(Number(zoom.value) * 100) + '%';
      lightValue.value = (Number(light.value) > 0 ? '+' : '') + light.value + '%';
      zoom.setAttribute('aria-valuetext', zoomValue.value);
      light.setAttribute('aria-valuetext', lightValue.value);
    };
    const resetCrop = () => {
      scale = base; zoom.value = '1'; light.value = '0';
      x = (640 - image.naturalWidth * scale) / 2; y = (640 - image.naturalHeight * scale) / 2;
      draw();
    };
    const fail = message => { error.textContent = label(message); use.disabled = reset.disabled = zoom.disabled = light.disabled = true; };
    cancel.addEventListener('click', close);
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
    // Only an actual backdrop click dismisses; interior padding is part of the dialog.
    dialog.addEventListener('click', event => {
      const r = dialog.getBoundingClientRect();
      if (event.target === dialog && (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom)) close();
    });
    reset.addEventListener('click', resetCrop);
    zoom.addEventListener('input', () => {
      const next = base * Number(zoom.value), ratio = next / scale;
      x = 320 - (320 - x) * ratio; y = 320 - (320 - y) * ratio; scale = next; draw();
    });
    light.addEventListener('input', draw);
    canvas.addEventListener('pointerdown', event => {
      if (use.disabled) return;
      drag = { id: event.pointerId, x: event.clientX, y: event.clientY };
      canvas.setPointerCapture(event.pointerId); canvas.focus();
    });
    canvas.addEventListener('pointermove', event => {
      if (!drag || event.pointerId !== drag.id) return;
      const ratio = 640 / canvas.getBoundingClientRect().width;
      x += (event.clientX - drag.x) * ratio; y += (event.clientY - drag.y) * ratio;
      drag.x = event.clientX; drag.y = event.clientY; draw();
    });
    ['pointerup', 'pointercancel', 'lostpointercapture'].forEach(type => canvas.addEventListener(type, () => { drag = null; }));
    canvas.addEventListener('keydown', event => {
      if (use.disabled || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
      event.preventDefault(); event.stopPropagation();
      const step = event.shiftKey ? 20 : 5;
      x += event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0;
      y += event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0;
      draw();
    });
    use.addEventListener('click', () => {
      if (use.disabled || !current()) return;
      use.disabled = reset.disabled = zoom.disabled = light.disabled = true;
      canvas.toBlob(blob => {
        if (!current()) return;
        if (!blob) { fail('Could not read this image. Please choose another.'); return; }
        const cropped = new File([blob], 'profile-crop.png', { type: 'image/png' });
        try { input.files = files(cropped); }
        catch (_) { fail('Could not read this image. Please choose another.'); return; }
        accepted.set(input, cropped);
        preview(input, cropped);
        close();
        confirmed.add(input);
        input.dispatchEvent(new Event('change', { bubbles: true }));
      }, 'image/png');
    });
    state.observer = new MutationObserver(() => { if (active === state && !input.isConnected) close(); });
    state.observer.observe(document.body, { childList: true, subtree: true });
    document.body.appendChild(dialog); dialog.showModal(); cancel.focus();
    const max = Number(input.dataset.profileCropMax || 10) * 1024 * 1024;
    if (!/^image\/(jpeg|png|gif|webp)$/.test(file.type)) { fail('Choose a JPEG, PNG, GIF, or WebP image.'); return; }
    if (file.size > max) { fail('This image is too large.'); return; }
    image.onload = () => {
      if (!current()) return;
      if (!image.naturalWidth || image.naturalWidth * image.naturalHeight > 40000000) { fail('This image is too large.'); return; }
      base = Math.max(640 / image.naturalWidth, 640 / image.naturalHeight);
      resetCrop(); use.disabled = reset.disabled = zoom.disabled = light.disabled = false; canvas.focus();
    };
    image.onerror = () => { if (current()) fail('Could not read this image. Please choose another.'); };
    image.src = state.url;
  }
  // Delegated drag feedback survives HTMX swaps and stays steady over child elements.
  let dragPicker = null;
  const clearDrag = () => {
    dragPicker?.classList.remove('dragging');
    dragPicker = null;
  };
  for (const type of ['dragenter', 'dragover', 'dragleave', 'drop']) {
    document.addEventListener(type, event => {
      const picker = event.target.closest?.('.tree_image_picker');
      const input = picker?.querySelector(selector);
      if (type === 'dragleave') {
        if (picker === dragPicker && !picker.contains(event.relatedTarget)) clearDrag();
        return;
      }
      if (!input || !Array.from(event.dataTransfer?.types || []).includes('Files')) {
        if (type === 'drop') clearDrag();
        return;
      }
      event.preventDefault();
      const blocked = input.matches(':disabled') || input.form?.dataset.personSaving === '1';
      if (type === 'drop') {
        clearDrag();
        // Keep the original file out of the form until the crop is confirmed.
        if (!blocked && event.dataTransfer.files[0]) open(input, event.dataTransfer.files[0]);
      } else {
        if (picker !== dragPicker || blocked) clearDrag();
        if (!blocked) { dragPicker = picker; picker.classList.add('dragging'); }
        event.dataTransfer.dropEffect = blocked ? 'none' : 'copy';
      }
    });
  }
  window.addEventListener('dragend', clearDrag);
  window.addEventListener('blur', clearDrag);
  document.addEventListener('change', event => {
    const input = event.target.closest?.(selector);
    if (!input) return;
    if (confirmed.delete(input)) return;
    const file = input.files?.[0];
    if (!file) { accepted.delete(input); return; }
    event.stopImmediatePropagation();
    input.files = files(accepted.get(input));
    open(input, file);
  }, true);
  document.addEventListener('reset', event => {
    event.target.querySelectorAll(selector).forEach(input => accepted.delete(input));
    if (active && event.target === active.input.form) close();
  }, true);
  document.addEventListener('submit', event => {
    if (active && event.target === active.input.form) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  // Shield the crop dialog from keyboard traps belonging to the form underneath it.
  window.addEventListener('keydown', event => {
    if (!active || !['Tab', 'Escape'].includes(event.key)) return;
    event.preventDefault(); event.stopImmediatePropagation();
    if (event.key === 'Escape') { close(); return; }
    const items = [...active.dialog.querySelectorAll('button:not(:disabled), input:not(:disabled), canvas')];
    const index = items.indexOf(document.activeElement);
    items[(index + (event.shiftKey ? -1 : 1) + items.length) % items.length]?.focus();
  }, true);
})();
