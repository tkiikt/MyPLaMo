(() => {
  if (globalThis.undoMyPLaMo) return;
  let active = true;
  let busy = false;
  let count = 0;
  let interrupted = false;
  let connectionLabel = "";
  let options = {from:'',to:'ja',hoverOriginal:true,adjustStyle:true,hideOnLeave:true,highlight:'none',...globalThis.myPLaMoPageOptions};
  const queue = new Set();
  const originals = new WeakMap();
  const cache = new Map();
  const styles = new Map();
  const excluded = 'script,style,noscript,template,textarea,input,pre,code,svg,math,[translate="no"],.notranslate,[hidden],[aria-hidden="true"]';
  const panel = document.createElement("div");
  panel.style.cssText = "all:initial!important;position:fixed!important;bottom:18px!important;right:18px!important;z-index:2147483647!important";
  const shadow = panel.attachShadow({mode: "closed"});
  shadow.innerHTML = '<style>section{display:flex;gap:12px;align-items:center;background:#183331;color:white;padding:10px 14px;border-radius:12px;box-shadow:0 2px 14px #0004;font:14px system-ui;max-width:min(480px,85vw)}button{font:inherit;color:#173832;background:#e2f4ef;border:0;border-radius:6px;padding:5px 9px;white-space:nowrap;cursor:pointer}</style><section><span role="status"></span><button>原文に戻す</button></section>';
  document.documentElement.append(panel);
  const label = shadow.querySelector("span");
  let shownNode, pendingNode, hoverTimer;
  const tooltip = document.createElement('aside');
  tooltip.setAttribute('role','tooltip'); tooltip.hidden = true;
  tooltip.style.cssText = 'position:fixed;box-sizing:border-box;width:max-content;max-width:min(700px,calc(100vw - 24px));max-height:45vh;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;background:#283444;color:#f5f7fb;padding:14px 18px;border-radius:10px;box-shadow:0 5px 20px #0004;font:15px/1.6 system-ui';
  tooltip.style.pointerEvents = options.hideOnLeave ? 'none' : 'auto';
  shadow.append(tooltip);
  const say = message => { label.textContent = connectionLabel ? connectionLabel + "\n" + message : message; label.style.whiteSpace = "pre-line"; };
  say("MyPLaMo　翻訳を準備中…");

  function eligible(node) {
    const el = node.parentElement;
    // Page language can describe the UI while posts/comments use another language.
    return node.isConnected && el && !el.closest(excluded) && !el.isContentEditable &&
      /\p{L}/u.test(node.data);
  }

  function adjust(element) {
    if (!styles.has(element)) styles.set(element,{whiteSpace:element.style.whiteSpace,overflowWrap:element.style.overflowWrap});
    element.style.whiteSpace = 'pre-wrap'; element.style.overflowWrap = 'anywhere';
  }
  function restoreStyle(element,prior) {
    if (element.style.whiteSpace === 'pre-wrap') element.style.whiteSpace = prior.whiteSpace;
    if (element.style.overflowWrap === 'anywhere') element.style.overflowWrap = prior.overflowWrap;
  }
  function hitText(x,y) {
    // Caret hit-testing can return the nearest text even over padding. Require
    // the pointer to be inside one of that text node's rendered line boxes too.
    const caret = document.caretPositionFromPoint?.(x,y);
    const node = caret?.offsetNode ?? document.caretRangeFromPoint?.(x,y)?.startContainer;
    if (node?.nodeType !== Node.TEXT_NODE || !node.isConnected) return;
    const original = originals.get(node);
    if (!original || node.data !== original.translated || !eligible(node)) return;
    const range = document.createRange();
    const start = node.data.search(/\S/u), end = node.data.trimEnd().length;
    if (start < 0) return;
    range.setStart(node,start); range.setEnd(node,end);
    const rect = Array.from(range.getClientRects()).find(r => r.width > 0 && r.height > 0 && x >= r.left && x < r.right && y >= r.top && y < r.bottom);
    if (rect) return {node,original,rect};
  }
  function cancelHover() { clearTimeout(hoverTimer); hoverTimer = undefined; pendingNode = undefined; }
  function showOriginal(hit) {
    tooltip.textContent = '原文：\n' + hit.original.source; tooltip.hidden = false;
    shownNode = hit.node;
    const rect = hit.rect, box = tooltip.getBoundingClientRect();
    tooltip.style.left = Math.max(12,Math.min(rect.left,innerWidth - box.width - 12)) + 'px';
    const above = rect.top - box.height - 8;
    const top = above >= 12 ? above : rect.bottom + 8;
    tooltip.style.top = Math.max(12,Math.min(top,innerHeight - box.height - 12)) + 'px';
  }
  function hover(event) {
    if (!options.hoverOriginal) return;
    if (event.composedPath().includes(panel)) {
      cancelHover(); if (options.hideOnLeave) hideTooltip(); return;
    }
    const hit = hitText(event.clientX,event.clientY);
    if (!hit) { cancelHover(); if (options.hideOnLeave) hideTooltip(); return; }
    if (hit.node === shownNode && !tooltip.hidden) { cancelHover(); return; }
    if (hit.node === pendingNode) return;
    cancelHover();
    if (options.hideOnLeave) hideTooltip();
    pendingNode = hit.node;
    hoverTimer = setTimeout(() => {
      pendingNode = undefined; hoverTimer = undefined;
      const current = hitText(event.clientX,event.clientY);
      if (active && options.hoverOriginal && current?.node === hit.node) showOriginal(current);
    },120);
  }
  function leave(event) { if (!event.relatedTarget) { cancelHover(); if (options.hideOnLeave) hideTooltip(); } }
  function hideTooltip() { cancelHover(); shownNode = undefined; tooltip.hidden = true; }
  function scrollTooltip(event) { if (!event.composedPath().includes(panel)) { cancelHover(); if (options.hideOnLeave) hideTooltip(); } }
  function escapeTooltip(event) { if (event.key === 'Escape') hideTooltip(); }
  function settingsChanged() {
    const next = globalThis.myPLaMoPreferences; if (!next) return;
    for (const name of ['hoverOriginal','adjustStyle','hideOnLeave','highlight']) options[name] = next[name];
    tooltip.style.pointerEvents = options.hideOnLeave ? 'none' : 'auto';
    if (!options.hoverOriginal) hideTooltip();
    for (const [el,prior] of styles) { if (options.adjustStyle) adjust(el); else restoreStyle(el,prior); }
  }
  document.addEventListener('mousemove',hover);
  document.addEventListener('mouseout',leave);
  document.addEventListener('click',hideTooltip);
  document.addEventListener('keydown',escapeTooltip);
  document.addEventListener('myplamo-settings-changed',settingsChanged);
  window.addEventListener('scroll',scrollTooltip,true);
  window.addEventListener('resize',hideTooltip);

  function add(node) {
    if (node.nodeType === Node.TEXT_NODE) {
      if (eligible(node) && originals.get(node)?.translated !== node.data) queue.add(node);
      return;
    }
    const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) add(walker.currentNode);
  }

  function* splitText(text) {
    while (text.length > 1400) {
      const part = text.slice(0, 1400);
      const stop = Math.max(...Array.from(part.matchAll(/[.!?。！？](?:\s|$)/g), m => m.index + 1));
      let boundary = stop > 500 ? stop : part.lastIndexOf(" ") + 1;
      if (boundary < 500) boundary = /[\uD800-\uDBFF]$/.test(part) ? 1399 : 1400;
      yield text.slice(0, boundary);
      text = text.slice(boundary);
    }
    yield text;
  }

  async function local(text, context) {
    const source = text.trim();
    const key = JSON.stringify([source,context]);
    if (!cache.has(key)) {
      const reply = await chrome.runtime.sendMessage({text: source,options,context});
      if (reply?.error || !reply?.translation) throw new Error(reply?.error || "翻訳エンジンが応答しませんでした。");
      connectionLabel = reply.connectionLabel || "";
      if (cache.size >= 1500) cache.delete(cache.keys().next().value);
      cache.set(key, reply.translation);
    }
    return text.replace(/\S[\s\S]*\S|\S/u, () => cache.get(key));
  }

  async function drain() {
    if (!active || busy || interrupted) return;
    busy = true;
    try {
      for (const node of queue) {
        queue.delete(node);
        if (!eligible(node) || originals.get(node)?.translated === node.data) continue;
        const source = node.data;
        const element = node.parentElement;
        const background = element.style.backgroundColor;
        if (options.highlight !== 'none') element.style.backgroundColor = options.highlight === 'strong' ? 'rgb(255, 215, 110)' : 'rgb(255, 245, 204)';
        say("翻訳中…　" + count + "件完了");
        let translated = "";
        // Keep paragraphs within the model context, preferring sentence boundaries.
        const pieces = splitText(source);
        try {
          for (const piece of pieces) {
            if (!active || node.data !== source || !node.isConnected) break;
            translated += piece.trim() ? await local(piece,document.title.slice(0,200) + '\n' + source.slice(0,900)) : piece;
          }
        } finally { if (['rgb(255, 215, 110)','rgb(255, 245, 204)'].includes(element.style.backgroundColor)) element.style.backgroundColor = background; }
        if (!active || node.data !== source || !eligible(node)) continue;
        originals.set(node, {source, translated});
        node.data = translated;
        if (options.adjustStyle) adjust(element);
        count++;
      }
      const targetName = new Intl.DisplayNames(['ja'],{type:'language'}).of(options.to || 'ja');
      say(count ? targetName + "に翻訳済み　" + count + "件" : "翻訳する文章がありません");
    } catch (error) {
      interrupted = true;
      say(error.message);
    } finally { busy = false; }
  }

  const observer = new MutationObserver(records => {
    for (const record of records) {
      if (record.type === "characterData") add(record.target);
      else for (const node of record.addedNodes) add(node);
    }
    drain();
  });
  function restore() {
    active = false;
    observer.disconnect();
    queue.clear();
    hideTooltip();
    document.removeEventListener('mousemove',hover); document.removeEventListener('mouseout',leave);
    document.removeEventListener('click',hideTooltip); document.removeEventListener('keydown',escapeTooltip);
    document.removeEventListener('myplamo-settings-changed',settingsChanged);
    window.removeEventListener('scroll',scrollTooltip,true); window.removeEventListener('resize',hideTooltip);
    for (const [el,prior] of styles) restoreStyle(el,prior);
    styles.clear();
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node = walker.currentNode;
      const prior = originals.get(node);
      if (prior && node.data === prior.translated) node.data = prior.source;
    }
    panel.remove();
    delete globalThis.undoMyPLaMo;
  }
  globalThis.undoMyPLaMo = restore;
  shadow.querySelector("button").onclick = restore;
  observer.observe(document.body, {subtree:true, childList:true, characterData:true});
  add(document.body);
  drain();
})();


