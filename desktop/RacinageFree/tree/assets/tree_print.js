const escapeXml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&apos;'}[c]));
const papers = [['A4',297,210],['A3',420,297],['A2',594,420],['A1',841,594],['A0',1189,841]];

async function photoData(url, signal) {
  const original = new URL(url, location.href);
  const larger = new URL(original);
  if (/\/images\/persons\/[a-f0-9]+_sm\.webp$/i.test(larger.pathname)) larger.pathname = larger.pathname.replace(/_sm\.webp$/i, '_lg.webp');
  for (const source of [...new Set([larger.href, original.href])]) {
    const response = await fetch(source, { credentials: 'same-origin', cache: 'force-cache', signal });
    if (!response.ok) continue;
    const blob = await response.blob();
    if (!blob.type.startsWith('image/')) continue;
    return new Promise((resolve, reject) => {
      const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = reject; reader.readAsDataURL(blob);
    });
  }
  throw new Error('photo-unavailable');
}

function treeSvg({ cards, paths, title }, images) {
  const left = Math.min(...cards.map(c => c.left)) - 18;
  const top = Math.min(...cards.map(c => c.top)) - 48;
  const width = Math.max(...cards.map(c => c.right)) - left + 18;
  const height = Math.max(...cards.map(c => c.bottom)) - top + 42;
  const measure = document.createElement('canvas').getContext('2d'); measure.font = '11px Arial';
  const wrap = (name, maxWidth) => {
    const lines = [''];
    for (const character of name) {
      if (measure.measureText(lines.at(-1) + character).width > maxWidth) lines.push('');
      lines[lines.length - 1] += character;
    }
    if (lines.length > 3) lines[2] = lines[2].slice(0, -1) + '…';
    return lines.slice(0, 3);
  };
  const nodes = cards.map((card, index) => {
    const x = card.left, y = card.top, center = x + card.width / 2;
    const imageSize = Math.min(56, card.width - 12);
    const lines = wrap(card.name, card.width - 8);
    const height = Math.max(card.height, imageSize + 16 + lines.length * 12);
    return `<g><title>${escapeXml(card.name)}</title><rect x="${x}" y="${y}" width="${card.width}" height="${height}" rx="8" fill="${card.dead ? '#fff3f3' : '#fff'}" stroke="#8e9ba0"/>
      <clipPath id="photo${index}"><rect x="${center-imageSize/2}" y="${y+5}" width="${imageSize}" height="${imageSize}" rx="${imageSize/2}"/></clipPath>
      <image href="${escapeXml(images.get(card.image) || '')}" x="${center-imageSize/2}" y="${y+5}" width="${imageSize}" height="${imageSize}" preserveAspectRatio="xMidYMid slice" clip-path="url(#photo${index})"/>
      <text x="${center}" y="${y+imageSize+17}" text-anchor="middle" font-family="Arial,sans-serif" font-size="11" fill="#26383e">${lines.map((line,i)=>`<tspan x="${center}" dy="${i?12:0}">${escapeXml(line)}</tspan>`).join('')}</text></g>`;
  }).join('');
  return { width, height, svg: `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" role="img" aria-label="${escapeXml(title)}"><rect width="100%" height="100%" fill="white"/><text x="${width/2}" y="22" text-anchor="middle" font-family="Arial,sans-serif" font-size="16" fill="#26383e">${escapeXml(title)}</text><g transform="translate(${-left},${-top})"><g fill="none" stroke="#8e9ba0" stroke-width="1.5">${paths.map(d=>`<path d="${escapeXml(d)}"/>`).join('')}</g>${nodes}</g></svg>` };
}

export async function printTree({ t, signal, dialog, snapshot }) {
  const controller = new AbortController();
  const abort = () => controller.abort();
  signal.addEventListener('abort', abort, { once:true });
  if (signal.aborted) controller.abort();
  const content = document.createElement('div'); content.className = 'tree_print_preview';
  const label = document.createElement('label'); label.textContent = t('Paper size');
  const select = document.createElement('select'); select.disabled = true; label.append(select);
  const status = document.createElement('p'); status.textContent = t('Preparing preview...'); status.setAttribute('role','status');
  const quality = document.createElement('p'); quality.textContent = t('Vector text and lines. Photos use the highest available size. Choose 300 dpi or higher in your printer settings.');
  const frame = document.createElement('iframe'); frame.title = t('Print preview'); frame.tabIndex=-1; frame.setAttribute('sandbox','allow-same-origin allow-modals');
  content.append(label, status, frame, quality);
  let drawing;
  const update = () => new Promise(resolve => {
    const paper = papers[Number(select.value) || 0];
    frame.onload = resolve;
    frame.srcdoc = `<!doctype html><html><head><meta charset="utf-8"><title>${escapeXml(t('Print Tree'))}</title><style>@page{size:${paper[0]} landscape;margin:8mm}html,body{margin:0;width:100%;height:100%;background:white}svg{display:block;width:100%;height:100%}@media print{html,body{width:${paper[1]-16}mm;height:${paper[2]-16}mm}svg{width:100%;height:100%;break-inside:avoid}}</style></head><body>${drawing.svg}</body></html>`;
  });
  select.addEventListener('change', async () => {
    const button = document.getElementById('treeUtilityConfirm'); if (button) button.disabled = true;
    await update(); if (button?.isConnected && !signal.aborted) button.disabled = false;
  }, { signal });
  try { await dialog({type:'print', title:t('Print preview'), message:'', content, confirmText:t('Print Tree'), cancelText:t('Cancel'),
    onConfirm:()=>{frame.contentWindow.focus();frame.contentWindow.print();},
    ready:async()=>{
      const data = await snapshot();
      if (!data.cards.length) throw new Error(t('No results found'));
      const images = new Map(), urls = [...new Set(data.cards.map(c=>c.image).filter(Boolean))];
      try {
        await Promise.all(Array.from({length:Math.min(6,urls.length)}, async()=>{
          while (urls.length && !controller.signal.aborted) { const url=urls.shift(); images.set(url,await photoData(url,controller.signal)); }
        }));
      } catch (error) { throw new Error(t('Some photos could not be loaded. Cancel and try again to include them.')); }
      if (signal.aborted || !content.isConnected) return;
      drawing = treeSvg(data, images);
      const fit = paper => Math.min((paper[1]-16)/(drawing.width*25.4/96),(paper[2]-16)/(drawing.height*25.4/96));
      const recommended = Math.max(0, papers.findIndex(paper=>fit(paper)>=1));
      const best = papers.some(paper=>fit(paper)>=1) ? recommended : papers.length-1;
      papers.forEach((paper,i)=>select.add(new Option(`${paper[0]} - ${paper[1]} × ${paper[2]} mm - ${t('Landscape')}${i===best?' ('+t('Recommended')+')':''}`,String(i))));
      select.value=String(best);select.disabled=false;
      status.textContent=fit(papers[best])<0.8?t('This tree is very large. Use a larger custom paper size or print a Partial Tree for readable names.'):'';
      status.hidden=!status.textContent;
      await update();
    }
  }); } finally {
    controller.abort(); signal.removeEventListener('abort',abort); frame.srcdoc='';
  }
}
