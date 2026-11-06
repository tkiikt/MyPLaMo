(() => {
  if (globalThis.undoMyPLaMo) return;
  let active = true;
  let busy = false;
  let count = 0;
  let interrupted = false;
  let connectionLabel = "";
  const queue = new Set();
  const originals = new WeakMap();
  const cache = new Map();
  const excluded = 'script,style,noscript,template,textarea,input,pre,code,svg,math,[translate="no"],.notranslate,[hidden],[aria-hidden="true"]';
  const panel = document.createElement("div");
  panel.style.cssText = "all:initial!important;position:fixed!important;bottom:18px!important;right:18px!important;z-index:2147483647!important";
  const shadow = panel.attachShadow({mode: "closed"});
  shadow.innerHTML = '<style>section{display:flex;gap:12px;align-items:center;background:#183331;color:white;padding:10px 14px;border-radius:12px;box-shadow:0 2px 14px #0004;font:14px system-ui;max-width:min(480px,85vw)}button{font:inherit;color:#173832;background:#e2f4ef;border:0;border-radius:6px;padding:5px 9px;white-space:nowrap;cursor:pointer}</style><section><span role="status"></span><button>原文に戻す</button></section>';
  document.documentElement.append(panel);
  const label = shadow.querySelector("span");
  const say = message => { label.textContent = connectionLabel ? connectionLabel + "\n" + message : message; label.style.whiteSpace = "pre-line"; };
  say("MyPLaMo　翻訳を準備中…");

  function eligible(node) {
    const el = node.parentElement;
    return node.isConnected && el && !el.closest(excluded) && !el.isContentEditable &&
      !/^ja(?:-|$)/i.test(el.closest("[lang]")?.lang || "") && /\p{L}/u.test(node.data);
  }

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

  async function local(text) {
    const source = text.trim();
    if (!cache.has(source)) {
      const reply = await chrome.runtime.sendMessage({text: source});
      if (reply?.error || !reply?.translation) throw new Error(reply?.error || "翻訳エンジンが応答しませんでした。");
      connectionLabel = reply.connectionLabel || "";
      if (cache.size >= 1500) cache.delete(cache.keys().next().value);
      cache.set(source, reply.translation);
    }
    return text.replace(/\S[\s\S]*\S|\S/u, () => cache.get(source));
  }

  async function drain() {
    if (!active || busy || interrupted) return;
    busy = true;
    try {
      for (const node of queue) {
        queue.delete(node);
        if (!eligible(node) || originals.get(node)?.translated === node.data) continue;
        const source = node.data;
        say("翻訳中…　" + count + "件完了");
        let translated = "";
        // Keep paragraphs within the model context, preferring sentence boundaries.
        const pieces = splitText(source);
        for (const piece of pieces) {
          if (!active || node.data !== source || !node.isConnected) break;
          translated += piece.trim() ? await local(piece) : piece;
        }
        if (!active || node.data !== source || !eligible(node)) continue;
        originals.set(node, {source, translated});
        node.data = translated;
        count++;
      }
      say(count ? "日本語に翻訳済み　" + count + "件" : "翻訳する文章がありません");
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


