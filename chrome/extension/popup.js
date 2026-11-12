(() => {
  const $ = id => document.getElementById(id), S = MyPLaMoSettings;
  let settings = S.normalize(), tab, pages = [], saving = Promise.resolve();
  const say = text => { $('status').textContent = text; };
  async function send(message) { const reply = await chrome.runtime.sendMessage(message); if (reply?.error) throw new Error(reply.error); return reply; }
  function save() {
    const snapshot = structuredClone(settings);
    saving = saving.catch(() => {}).then(async () => { await send({kind:'save-settings',settings:snapshot}); say('設定を保存しました'); });
    saving.catch(e => say(e.message)); return saving;
  }
  function renderRules(name, list, search) {
    list.replaceChildren();
    const query = search.value.toLowerCase();
    for (const rule of settings[name].filter(p => p.toLowerCase().includes(query))) {
      const row = document.createElement('li'), text = document.createElement('span'), remove = document.createElement('button');
      text.className = 'url'; text.textContent = rule; remove.textContent = '削除'; remove.className = 'danger'; remove.setAttribute('aria-label',rule + 'を削除');
      remove.onclick = () => { settings[name] = settings[name].filter(p => p !== rule); save(); renderAllRules(); };
      row.append(text,remove); list.append(row);
    }
    if (!list.children.length) { const item = document.createElement('li'); item.textContent = settings[name].length ? '一致するURLはありません' : '登録したURLはありません'; list.append(item); }
  }
  function renderAllRules() { renderRules('filterPatterns',$('filter-list'),$('filter-search')); renderRules('plainPatterns',$('style-list'),$('style-search')); }
  async function addRule(name, pattern) {
    if (!S.validPattern(pattern)) throw new Error('http:// または https:// から始まるURLパターンを入力してください（例：https://example.com/*）。');
    if (settings[name].length >= 500) throw new Error('登録できるURLは500件までです。');
    if (!settings[name].includes(pattern)) settings[name].push(pattern);
    await save(); renderAllRules();
  }
  function confirmDelete(text, action) {
    $('confirm-text').textContent = text;
    $('confirm').onclose = () => { if ($('confirm').returnValue === 'delete') Promise.resolve(action()).catch(e => say(e.message)); };
    $('confirm').showModal();
  }
  function renderCache() {
    const size = pages.reduce((sum,p) => sum + p.bytes,0);
    const formatDate = time => new Date(time).toLocaleString('ja-JP');
    $('cache-summary').textContent = `翻訳済みページ：${pages.length} 件\nサイズ：${(size / 1024).toFixed(1)} KB` + (pages.length ? `\n期間：${formatDate(Math.min(...pages.map(p => p.created)))} ～ ${formatDate(Math.max(...pages.map(p => p.updated)))}` : '');
    $('cache-list').replaceChildren();
    for (const p of pages.filter(p => p.url.toLowerCase().includes($('cache-search').value.toLowerCase())).sort((a,b) => b.updated-a.updated)) {
      const row = document.createElement('li'), label = document.createElement('span'), detail = document.createElement('small'), remove = document.createElement('button');
      label.className = 'url'; label.textContent = p.url; detail.textContent = `${p.count} 件・${(p.bytes/1024).toFixed(1)} KB` + (tab?.url && S.pageUrl(tab.url) === p.url ? '・現在のページ' : ''); label.append(detail);
      remove.textContent = '削除'; remove.className = 'danger'; remove.setAttribute('aria-label',p.url + 'のキャッシュを削除');
      remove.onclick = () => confirmDelete('このページの翻訳キャッシュを削除しますか？',async () => { await send({kind:'cache-delete',url:p.url}); await loadCache(); });
      row.append(label,remove); $('cache-list').append(row);
    }
    if (!$('cache-list').children.length) { const row = document.createElement('li'); row.textContent = '該当するキャッシュはありません'; $('cache-list').append(row); }
  }
  async function loadCache() { pages = (await send({kind:'cache-list'})).pages; renderCache(); }
  for (const button of document.querySelectorAll('[data-panel]')) button.onclick = () => {
    for (const b of document.querySelectorAll('[data-panel]')) { if (b === button) b.setAttribute('aria-current','page'); else b.removeAttribute('aria-current'); }
    for (const section of document.querySelectorAll('main>section')) section.hidden = section.id !== button.dataset.panel;
    if (button.dataset.panel === 'cache') loadCache().catch(e => say(e.message));
  };
  for (const input of document.querySelectorAll('[data-setting]')) input.onchange = () => { settings[input.dataset.setting] = input.type === 'checkbox' ? input.checked : input.value; save(); };
  for (const form of document.querySelectorAll('[data-rule]')) form.onsubmit = event => { event.preventDefault(); const input = form.querySelector('input'); addRule(form.dataset.rule,input.value.trim()).then(() => {input.value='';},e => say(e.message)); };
  for (const button of document.querySelectorAll('[data-add]')) button.onclick = () => {
    try { const url = new URL(tab?.url); const pattern = button.dataset.scope === 'site' ? url.origin + '/*' : S.pageUrl(url.href); addRule(button.dataset.add,pattern).catch(e => say(e.message)); } catch { say('通常のWebページを開いてください。'); }
  };
  $('filter-search').oninput = $('style-search').oninput = renderAllRules;
  $('cache-search').oninput = renderCache;
  $('reset-style').onclick = () => confirmDelete('常体で翻訳するURLの登録をすべて解除しますか？',async () => { settings.plainPatterns=[]; await save(); renderAllRules(); });
  $('clear-cache').onclick = () => confirmDelete('すべての翻訳キャッシュを削除しますか？',async () => { await send({kind:'cache-delete'}); await loadCache(); say('キャッシュを削除しました'); });
  for (const [id,kind] of [['translate','translate-page'],['restore','restore-page']]) $(id).onclick = async () => {
    try { if (id === 'translate' && settings.from && settings.from === settings.to) throw new Error('翻訳元と翻訳先に異なる言語を選んでください。'); await saving; const reply = await send({kind}); say(reply.connectionNotice || (id === 'translate' ? 'ページで翻訳を開始しました' : '原文に戻しました')); } catch (e) { say(e.message); }
  };
  async function initialize() {
    const state = await send({kind:'state'}); settings = S.normalize(state.settings); tab = state.tab;
    $('current-page').textContent = tab?.title || tab?.url || 'Webページを開いてください';
    $('connection').textContent = state.info?.server ? `v${state.info.server.appVersion} に接続中` : state.info?.legacy ? '旧版の本体に接続中' : '本体に未接続';
    const supported = /^https?:\/\//.test(tab?.url || ''); $('translate').disabled = $('restore').disabled = !supported;
    let languages = state.info?.languages;
    if (!languages?.length) {
      const names = new Intl.DisplayNames(['ja'],{type:'language'});
      languages = 'ja en zh-hans zh-hant ko ar az be bg bn ca ceb de es et eu fa fr gu he hi hr hu hy id it jv ka kk km lt lv ml mr ms my ne nl pa pl ru si sk sl sr sw ta te th tl ur uz vi'.split(' ').map(id => ({id,name:names.of(id)}));
    }
    for (const select of [$('from'),$('to')]) {
      if (select.id === 'from') select.add(new Option('自動検出',''));
      for (const l of languages) select.add(new Option(l.name,l.id));
    }
    for (const g of state.info?.glossaries || []) $('glossary').add(new Option(`${g.name}（${g.count}語）`,g.id));
    if (settings.glossaryId && ![...$('glossary').options].some(o => o.value === settings.glossaryId)) $('glossary').add(new Option('選択済みの用語集（本体で確認してください）',settings.glossaryId));
    for (const input of document.querySelectorAll('[data-setting]')) { if (input.type === 'checkbox') input.checked = settings[input.dataset.setting]; else input.value = settings[input.dataset.setting]; }
    renderAllRules(); say(state.info?.glossaryError || state.connectionError || state.connectionNotice || (state.info?.legacy ? '言語・文体・用語集の設定には本体の更新が必要です。' : '設定の変更は自動で保存されます'));
  }
  initialize().catch(e => say(e.message));
})();
