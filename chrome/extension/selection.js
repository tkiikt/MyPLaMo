(() => {
  if (globalThis.myPLaMoSelectionInstalled) return;
  globalThis.myPLaMoSelectionInstalled = true;
  let host, shadow, anchor, selected = '', generation = 0, timer;
  const excluded = 'input,textarea,select,[contenteditable]:not([contenteditable="false"])';
  function dismiss() {
    generation++;
    host?.remove(); host = shadow = null;
  }
  function position() {
    if (!host || !anchor) return;
    const width = host.getBoundingClientRect().width;
    const height = host.getBoundingClientRect().height;
    const left = Math.max(8, Math.min(anchor.left, innerWidth - width - 8));
    const below = anchor.bottom + 8;
    const top = below + height <= innerHeight - 8 ? below : Math.max(8, anchor.top - height - 8);
    host.style.setProperty('left', left + 'px', 'important');
    host.style.setProperty('top', top + 'px', 'important');
  }
  function create() {
    host = document.createElement('div');
    host.setAttribute('translate', 'no');
    host.style.cssText = 'all:initial!important;position:fixed!important;z-index:2147483647!important;display:block!important;max-width:calc(100vw - 16px)!important';
    shadow = host.attachShadow({mode:'closed'});
    shadow.innerHTML = `<style>
      :host{color-scheme:light}*{box-sizing:border-box}button{font:14px system-ui;cursor:pointer;border:0}
      button:focus-visible{outline:3px solid #65c4ff;outline-offset:3px}
      .launch{width:40px;height:40px;padding:6px;border-radius:50%;background:white;box-shadow:0 3px 16px #0004;display:grid;place-items:center}
      .launch img{width:28px;height:28px;pointer-events:none}
      section{width:480px;max-width:calc(100vw - 16px);max-height:calc(100vh - 16px);overflow:auto;background:#283444;color:#f5f7fb;border-radius:14px;box-shadow:0 6px 24px #0004;padding:18px;font:16px/1.6 system-ui;text-align:left}
      header{display:flex;align-items:center;gap:8px;color:#b7c4d5;font-size:13px}.heading{flex:1;min-width:0;display:flex;align-items:baseline;flex-wrap:wrap;gap:2px 10px}header strong{font-weight:500}
      header button{flex:none;display:grid;place-items:center;width:30px;height:30px;background:transparent;color:#e3ebf6;padding:5px;border-radius:5px}header button:hover{background:#ffffff20}button:disabled{opacity:.45;cursor:default}
      .copy svg{width:18px;height:18px;pointer-events:none}.copy .copied,.copy .failed{display:none}.copy[data-state="copied"] .clipboard,.copy[data-state="failed"] .clipboard{display:none}.copy[data-state="copied"] .copied,.copy[data-state="failed"] .failed{display:block}
      .result{margin:10px 0 0;white-space:pre-wrap;overflow-wrap:anywhere;user-select:text}.connection{font-size:11px;overflow-wrap:anywhere}
    </style>`;
    // Keep the page's selected text when pressing our launcher.
    host.addEventListener('pointerdown', event => {
      if (event.composedPath().some(el => el.classList?.contains('launch'))) event.preventDefault();
      event.stopPropagation();
    });
    host.addEventListener('pointerup', event => event.stopPropagation());
    document.documentElement.append(host);
  }
  function inspect() {
    if (host && shadow.activeElement) return;
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || !selection.rangeCount) { dismiss(); return; }
    const range = selection.getRangeAt(0);
    const element = node => node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement;
    if ([selection.anchorNode, selection.focusNode].some(node => element(node)?.closest(excluded)) || element(document.activeElement)?.closest(excluded)) { dismiss(); return; }
    const text = selection.toString().trim();
    if (!text) { dismiss(); return; }
    if (host && text === selected) return;
    dismiss(); selected = text;
    const rects = range.getClientRects();
    const rect = rects[rects.length - 1] || range.getBoundingClientRect();
    if (!rect || rect.bottom < 0 || rect.top > innerHeight) return;
    anchor = {left:rect.left, top:rect.top, bottom:rect.bottom};
    create();
    const button = document.createElement('button');
    button.className = 'launch'; button.type = 'button';
    button.title = 'MyPLaMoで選択した文章を翻訳'; button.setAttribute('aria-label', button.title);
    const img = document.createElement('img'); img.src = "data:image/png;base64,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"; img.alt = '';
    button.append(img); shadow.append(button);
    button.addEventListener('click', translate);
    position();
  }
  async function translate() {
    const token = ++generation, currentShadow = shadow, text = selected;
    currentShadow.querySelector('.launch').remove();
    const section = document.createElement('section');
    section.setAttribute('role','dialog'); section.setAttribute('aria-label','MyPLaMo 選択文の翻訳');
    section.innerHTML = `<header><div class="heading"><strong>MyPLaMo · 翻訳</strong><span class="connection"></span></div><button type="button" class="copy" disabled aria-label="訳文をコピー" title="訳文をコピー">
      <svg class="clipboard" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="M8 4H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2h-2"/><rect x="8" y="2" width="8" height="4" rx="1"/></svg>
      <svg class="copied" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="m5 12 4 4L19 6"/></svg>
      <svg class="failed" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="9"/><path d="M12 7v6m0 4h0"/></svg>
      </button><button type="button" class="close" aria-label="翻訳を閉じる">×</button></header><div class="result" role="status">翻訳中…</div>`;
    currentShadow.append(section);
    section.querySelector('.close').onclick = dismiss;
    const output = section.querySelector('.result'), copy = section.querySelector('.copy');
    let translation = '';
    copy.onclick = async () => {
      try {
        if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(translation);
        else {
          const field = document.createElement('textarea'); field.value = translation;
          field.style.cssText = 'position:fixed;opacity:0'; currentShadow.append(field); field.focus(); field.select();
          const ok = document.execCommand('copy'); field.remove(); copy.focus();
          if (!ok) throw new Error('copy failed');
        }
        copy.dataset.state = 'copied';
        copy.title = 'コピーしました';
        copy.setAttribute('aria-label', copy.title);
      } catch {
        copy.dataset.state = 'failed';
        copy.title = 'コピーできません';
        copy.setAttribute('aria-label', copy.title);
      }
    };
    position();
    if (text.length > 2000) { output.textContent = '選択した文章が長すぎます。2,000文字以内で選択してください。'; position(); return; }
    try {
      const reply = await chrome.runtime.sendMessage({text});
      if (token !== generation) return;
      if (reply?.error || !reply?.translation) throw new Error(reply?.error || '翻訳エンジンが応答しませんでした。');
      translation = reply.translation; output.textContent = translation; copy.disabled = false;
      section.querySelector('.connection').textContent = reply.server
        ? 'v' + reply.server.appVersion + 'に接続中'
        : '接続中（バージョン未確認）';
    } catch (error) {
      if (token !== generation) return;
      output.textContent = /Extension context invalidated/i.test(error.message) ? '拡張機能が更新されました。このページを再読み込みしてください。' : error.message;
    }
    if (token === generation) position();
  }
  function schedule(event) {
    if (host && event?.composedPath().includes(host)) return;
    clearTimeout(timer); timer = setTimeout(inspect, 80);
  }
  document.addEventListener('pointerup', schedule);
  document.addEventListener('keyup', event => { if (event.key === 'Escape') dismiss(); else schedule(event); });
  document.addEventListener('pointerdown', event => { if (host && !event.composedPath().includes(host)) dismiss(); });
  window.addEventListener('scroll', event => { if (!host || !event.composedPath().includes(host)) dismiss(); }, true);
  window.addEventListener('resize', dismiss);
})();
