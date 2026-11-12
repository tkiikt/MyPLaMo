importScripts('bridge.js', 'settings.js', 'connection.js');
const S = MyPLaMoSettings;
let queue = Promise.resolve(), storageQueue = Promise.resolve(), cacheEpoch = 0;
const serialized = task => { const result = queue.then(task); queue = result.catch(() => {}); return result; };
const withCache = task => { const result = storageQueue.then(task); storageQueue = result.catch(() => {}); return result; };
const preferences = async () => S.normalize((await chrome.storage.local.get('preferences')).preferences);
async function serverInfo() {
  try { return await MyPLaMoConnection.request({action:'metadata'}); }
  catch (error) {
    if (error.server && !error.server.capabilities?.includes('options') || /文章|text|プロパティ|property|キー|dictionary|given key/i.test(error.message))
      return {legacy:true,glossaries:[],server:error.server};
    throw error;
  }
}
async function cacheData() { return (await chrome.storage.local.get('translationCache')).translationCache || {pages:{}}; }
const size = value => new TextEncoder().encode(JSON.stringify(value)).length;
async function saveCache(cache) {
  let pages = Object.entries(cache.pages).sort((a,b) => a[1].updated - b[1].updated);
  while (pages.length > 100 || size(cache) > 6000000) { const next = pages.shift(); if (!next) break; delete cache.pages[next[0]]; }
  await chrome.storage.local.set({translationCache:cache});
}
async function translate(message, sender) {
  const text = message.text;
  if (typeof text !== 'string' || !text.trim() || text.length > 2000) throw new Error('文章を短くしてください（最大2,000文字）。');
  const url = S.pageUrl(sender.tab.url || sender.url);
  const settings = message.options ? S.normalize(message.options) : await preferences();
  const options = S.effective(settings, url);
  if (options.from && options.from === options.to) throw new Error('翻訳元と翻訳先に異なる言語を選んでください。');
  const context = typeof message.context === 'string' ? message.context.slice(0,1200) : '';
  const info = await serverInfo();
  const supported = info.server?.capabilities?.includes('options');
  if (!supported && (options.from || options.to !== 'ja' || options.glossaryId || options.style === 'plain'))
    throw new Error('この設定には対応したMyPLaMo本体が必要です。本体を更新してください。');
  const request = supported ? {text,from:options.from,to:options.to,style:options.style,glossaryId:options.glossaryId,context} : {text};
  if (!supported) return MyPLaMoConnection.request(request);
  const epoch = cacheEpoch;
  const key = JSON.stringify([request,info.server?.appVersion,info.modelId,info.glossaryRevision]);
  const page = (await cacheData()).pages[url];
  if (page?.entries?.[key]) return page.entries[key];
  const reply = await MyPLaMoConnection.request(request);
  await withCache(async () => {
    if (epoch !== cacheEpoch) return;
    const cache = await cacheData();
    const current = cache.pages[url] ||= {created:Date.now(),updated:Date.now(),entries:{}};
    current.updated = Date.now(); current.entries[key] = reply;
    const keys = Object.keys(current.entries); while (keys.length > 1500) delete current.entries[keys.shift()];
    try { await saveCache(cache); } catch { }
  });
  return reply;
}
async function translatePage(tab, restore = false) {
  if (!tab?.id || !/^https?:\/\//.test(tab.url || '')) throw new Error('通常のWebページを開いてください。');
  const target = {tabId:tab.id};
  if (restore) {
    await chrome.scripting.executeScript({target,func:() => globalThis.undoMyPLaMo?.()}); return {ok:true};
  }
  if (!await MyPLaMoConnection.refresh()) return {ok:false,connectionNotice:MyPLaMoConnection.notice};
  const options = S.effective(await preferences(), tab.url);
  await chrome.scripting.executeScript({target,func:value => { globalThis.undoMyPLaMo?.(); globalThis.myPLaMoPageOptions = value; },args:[options]});
  await chrome.scripting.executeScript({target,files:['page.js']});
  return {ok:true};
}
async function broadcast(settings) {
  const tabs = await chrome.tabs.query({url:['http://*/*','https://*/*']});
  await Promise.allSettled(tabs.map(tab => chrome.tabs.sendMessage(tab.id,{kind:'settings-changed',settings:S.effective(settings,tab.url)})));
}
async function handle(message, sender) {
  const extensionPage = sender.id === chrome.runtime.id && sender.url?.startsWith(chrome.runtime.getURL(''));
  if (sender.tab && !extensionPage) {
    if (message.kind === 'get-settings') return {settings:S.effective(await preferences(),sender.tab.url || sender.url)};
    if (message.kind === 'auto-start') {
      if (S.shouldAuto(await preferences(),sender.tab.url || sender.url)) await translatePage(sender.tab);
      return {ok:true};
    }
    if (typeof message.text === 'string') return serialized(() => translate(message,sender));
    return;
  }
  if (sender.id !== chrome.runtime.id) return;
  if (message.kind === 'state') {
    const [tab] = await chrome.tabs.query({active:true,currentWindow:true});
    let info = {}, connectionError = '', connectionNotice = '';
    try { info = await serverInfo(); }
    catch (e) { if (e.code === 'APP_OFFLINE') connectionNotice = e.message; else connectionError = e.message; }
    return {settings:await preferences(),tab:tab ? {id:tab.id,url:tab.url || '',title:tab.title || ''} : null,info,connectionError,connectionNotice};
  }
  if (message.kind === 'save-settings') {
    const settings = S.normalize(message.settings);
    await chrome.storage.local.set({preferences:settings}); await broadcast(settings); return {settings};
  }
  if (message.kind === 'translate-page' || message.kind === 'restore-page') {
    const [tab] = await chrome.tabs.query({active:true,currentWindow:true});
    return translatePage(tab,message.kind === 'restore-page');
  }
  if (message.kind === 'cache-list') return withCache(async () => {
    const cache = await cacheData();
    return {pages:Object.entries(cache.pages).map(([url,p]) => ({url,created:p.created,updated:p.updated,bytes:size(p),count:Object.keys(p.entries).length}))};
  });
  if (message.kind === 'cache-delete') {
    cacheEpoch++;
    return withCache(async () => { const cache = await cacheData(); if (message.url) delete cache.pages[message.url]; else cache.pages = {}; await saveCache(cache); return {ok:true}; });
  }
}
chrome.runtime.onMessage.addListener((message,sender,respond) => {
  handle(message,sender).then(respond,error => respond({error:error.message})); return true;
});
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => chrome.contextMenus.create({id:'translate-page',title:'MyPLaMoでWEB全体を翻訳',contexts:['page','selection','link','image','video','audio','editable','frame'],documentUrlPatterns:['http://*/*','https://*/*']}));
  MyPLaMoConnection.refresh().catch(() => {});
});
chrome.contextMenus.onClicked.addListener((info,tab) => { if (info.menuItemId === 'translate-page') translatePage(tab).catch(() => {}); });

// Alarms also wake the MV3 worker after it has been suspended. Opening the menu
// or translating checks again immediately, so recovery does not need a reload.
chrome.alarms.get('myplamo-connection').then(alarm => {
  if (!alarm) return chrome.alarms.create('myplamo-connection',{periodInMinutes:1});
}).catch(() => {});
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'myplamo-connection') MyPLaMoConnection.refresh().catch(() => {}); });
chrome.runtime.onStartup.addListener(() => { MyPLaMoConnection.refresh().catch(() => {}); });
