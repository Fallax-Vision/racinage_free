(() => {
  const api = async (action, payload = {}, signal) => {
    const shell = document.querySelector('[data-portable-ai-shell]');
    if (!shell) throw new Error('The local AI assistant is unavailable.');
    const response = await fetch('/local-ai-api', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'X-Racinage-CSRF': shell.dataset.csrf || '',
      },
      body: JSON.stringify({ action, ...payload }),
      signal,
    });
    const result = await response.json().catch(() => ({ ok: false, message: 'Unexpected local service response.' }));
    if (!response.ok || !result.ok) throw new Error(result.message || 'The local AI request failed.');
    return result.data || {};
  };

  const setSetupStatus = (message, type = '') => {
    const element = document.querySelector('[data-portable-ai-setup-status]');
    if (!element) return;
    element.textContent = message;
    element.className = `local-ai-setup-status ${type}`.trim();
  };

  const setupForm = document.querySelector('[data-portable-ai-setup]');
  setupForm?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const submitter = event.submitter;
    const action = submitter?.dataset.localAiAction || 'test';
    const data = new FormData(setupForm);
    const payload = {
      provider: String(data.get('provider') || ''),
      endpoint: String(data.get('endpoint') || ''),
      model: String(data.get('model') || ''),
      token: String(data.get('token') || ''),
    };
    setupForm.querySelectorAll('button').forEach((button) => { button.disabled = true; });
    setSetupStatus(action === 'discover' ? 'Discovering local models...' : 'Testing the local model...', 'loading');
    try {
      if (action === 'save') {
        const result = await api('save_config', payload);
        setSetupStatus(`Saved ${result.provider} configuration. Test it before using CRUD actions.`, 'success');
      } else if (action === 'discover') {
        const result = await api('discover', payload);
        const list = setupForm.querySelector('[data-portable-ai-models]');
        list.replaceChildren();
        (result.models || []).forEach((model) => {
          const option = document.createElement('option');
          option.value = model;
          list.appendChild(option);
        });
        setSetupStatus(`${(result.models || []).length} local model(s) discovered.`, 'success');
      } else {
        const result = await api('test', payload);
        const ready = result.model_readiness === 'crud_ready';
        setSetupStatus(
          ready
            ? 'Connected. Native structured tools passed, so confirmed CRUD previews are enabled.'
            : 'Connected for writing and questions. Structured tools did not pass, so CRUD previews stay disabled.',
          ready ? 'success' : 'notice',
        );
      }
    } catch (error) {
      setSetupStatus(error.message, 'error');
    } finally {
      setupForm.querySelectorAll('button').forEach((button) => { button.disabled = false; });
    }
  });

  const shell = document.querySelector('[data-portable-ai-shell]');
  if (!shell) return;
  const sidebar = shell.querySelector('[data-portable-ai-sidebar]');
  const messages = shell.querySelector('[data-portable-ai-messages]');
  const form = shell.querySelector('[data-portable-ai-chat-form]');
  const input = shell.querySelector('[data-portable-ai-input]');
  const status = shell.querySelector('[data-portable-ai-status]');
  const stop = shell.querySelector('[data-portable-ai-stop]');
  let controller;

  const open = () => {
    shell.classList.add('is-open');
    sidebar.setAttribute('aria-hidden', 'false');
    input.focus();
  };
  const close = () => {
    shell.classList.remove('is-open');
    sidebar.setAttribute('aria-hidden', 'true');
  };
  const setStatus = (message, type = '') => {
    status.textContent = message || '';
    status.className = `portable-ai-status ${type}`.trim();
  };
  const addMessage = (role, content) => {
    const article = document.createElement('article');
    article.className = `portable-ai-message ${role}`;
    const label = document.createElement('strong');
    label.textContent = role === 'user' ? 'You' : 'Local AI';
    const text = document.createElement('p');
    text.textContent = content;
    article.append(label, text);
    messages.appendChild(article);
    messages.scrollTop = messages.scrollHeight;
  };
  const renderPreview = (preview) => {
    const article = document.createElement('article');
    article.className = `portable-ai-preview tier-${preview.tier || 1}`;
    const heading = document.createElement('strong');
    heading.textContent = `Confirmation tier ${preview.tier || 1}`;
    const summary = document.createElement('p');
    summary.textContent = preview.summary || 'Review this local change.';
    const details = document.createElement('pre');
    details.textContent = JSON.stringify(preview.arguments || {}, null, 2);
    const apply = document.createElement('button');
    apply.type = 'button';
    apply.className = 'button';
    apply.textContent = 'Apply local change';
    apply.addEventListener('click', async () => {
      const warning = Number(preview.tier || 1) >= 2
        ? 'Apply this elevated local change? Racinage will recheck that the records have not changed.'
        : 'Apply this local change?';
      if (!window.confirm(warning)) return;
      apply.disabled = true;
      try {
        const result = await api('apply', { preview_token: preview.token });
        addMessage('assistant', result.message || 'The local change was applied.');
        article.remove();
      } catch (error) {
        setStatus(error.message, 'error');
        apply.disabled = false;
      }
    });
    article.append(heading, summary, details, apply);
    messages.appendChild(article);
    messages.scrollTop = messages.scrollHeight;
  };

  shell.addEventListener('click', (event) => {
    if (event.target.closest('[data-portable-ai-open]')) open();
    if (event.target.closest('[data-portable-ai-close]')) close();
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && shell.classList.contains('is-open')) close();
  });
  stop.addEventListener('click', () => controller?.abort());
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const prompt = input.value.trim();
    if (!prompt) return;
    addMessage('user', prompt);
    input.value = '';
    controller = new AbortController();
    stop.hidden = false;
    form.querySelector('button[type=submit]').disabled = true;
    setStatus('Running on your selected local model...', 'loading');
    try {
      const result = await api('chat', { prompt, page: shell.dataset.page || 'family' }, controller.signal);
      addMessage('assistant', result.message || 'The local model returned no text.');
      if (result.preview) renderPreview(result.preview);
      setStatus(`${result.provider || 'Local'} - ${result.model || ''} - processed on this device`, 'success');
    } catch (error) {
      setStatus(error.name === 'AbortError' ? 'Local request stopped.' : error.message, error.name === 'AbortError' ? 'notice' : 'error');
    } finally {
      controller = undefined;
      stop.hidden = true;
      form.querySelector('button[type=submit]').disabled = false;
    }
  });
})();
