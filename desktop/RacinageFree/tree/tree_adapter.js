/* Racinage Free shell glue for the vendored hosted tree script: the two helpers the hosted global
   script.js provides (feedback toast, content dialog) and the saved horizontal/vertical view. */
(() => {
  'use strict';
  if (!window.racinageShowFeedback) window.racinageShowFeedback = (message, type) => {
    let popup = document.getElementById('auto_disapear');
    if (!popup) { popup = document.createElement('div'); popup.id = 'auto_disapear'; document.body.appendChild(popup); }
    const kind = type === 'fail' || type === 'error' ? 'fail' : (['success', 'warning', 'info'].includes(type) ? type : 'success');
    popup.className = 'feedback_popup center-top';
    popup.style.display = 'block';
    popup.setAttribute('role', kind === 'fail' || kind === 'warning' ? 'alert' : 'status');
    const wrapper = document.createElement('div'); wrapper.className = 'wrapper_feedback ' + kind;
    const body = document.createElement('div'); body.className = 'feedback_popup_content';
    const copy = document.createElement('p'); copy.textContent = String(message || '');
    body.append(copy); wrapper.append(body); popup.replaceChildren(wrapper);
    clearTimeout(popup._timer);
    popup._timer = setTimeout(() => { popup.style.display = 'none'; }, 4000);
    return true;
  };
  if (!window.racinageContentDialog) window.racinageContentDialog = (title, html, stacked = false) => {
    if (!stacked) [...document.querySelectorAll('[data-content-dialog]')].reverse().forEach(dialog => dialog.close());
    const trigger = document.activeElement;
    const dialog = document.createElement('dialog'); dialog.className = 'racinage_content_dialog'; dialog.dataset.contentDialog = '';
    const header = document.createElement('header'); const heading = document.createElement('h2'); heading.textContent = title;
    heading.id = 'racinage-content-dialog-title-' + (window.racinageContentDialogCount = (window.racinageContentDialogCount || 0) + 1);
    dialog.setAttribute('aria-labelledby', heading.id);
    const close = document.createElement('button'); close.type = 'button'; close.textContent = '×';
    close.setAttribute('aria-label', (window.RACINAGE_TREE_I18N || {}).Close || 'Close');
    close.addEventListener('click', () => dialog.close()); header.append(heading, close);
    const content = document.createElement('div'); content.dataset.dialogContent = ''; content.innerHTML = html;
    const tooltip = document.createElement('span'); tooltip.className = 'tree_insights_tooltip'; tooltip.hidden = true; tooltip.setAttribute('role', 'tooltip');
    dialog.append(header, content, tooltip); document.body.append(dialog);
    const show = (item, x, y) => {
      tooltip.hidden = !item;
      if (!item) return;
      tooltip.textContent = item.dataset.insightValue;
      tooltip.style.left = Math.max(8, Math.min(x + 12, innerWidth - tooltip.offsetWidth - 8)) + 'px';
      tooltip.style.top = Math.max(8, Math.min(y + 16, innerHeight - tooltip.offsetHeight - 8)) + 'px';
    };
    dialog.addEventListener('pointermove', event => show(event.target.closest('[data-insight-value]'), event.clientX, event.clientY));
    dialog.addEventListener('pointerleave', () => { tooltip.hidden = true; });
    dialog.addEventListener('focusin', event => { const item = event.target.closest('[data-insight-value]'); const rect = item?.getBoundingClientRect(); show(item, rect?.left || 0, rect?.bottom || 0); });
    dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
    dialog.addEventListener('close', () => { dialog.remove(); if (trigger?.isConnected) trigger.focus({ preventScroll: true }); }, { once: true });
    dialog.showModal(); close.focus();
    return dialog;
  };
  // The hosted view preference is saved by its global script; Free keeps it with the other tree settings.
  document.addEventListener('click', event => {
    const button = event.target.closest('[data-view-preference="trees"] [data-tree-view]');
    const container = document.getElementById('tree_container');
    if (!button || !container) return;
    const data = new FormData();
    data.set('action', 'save_view'); data.set('view', button.dataset.treeView);
    fetch('/includes/tree_save.include.php', { method: 'POST', body: data, credentials: 'same-origin', headers: { 'X-Racinage-CSRF': container.dataset.treePreferenceCsrf || '' } }).catch(() => {});
  });
})();
