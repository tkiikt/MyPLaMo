importScripts("bridge.js");
let queue = Promise.resolve();

async function translate(text) {

  return bridgeRequest({text});
}
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (!sender.tab || typeof message.text !== "string" || message.text.length > 2000) return;
  queue = queue.then(() => translate(message.text))
    .then(respond, error => respond({error: error.message})).catch(() => {});
  return true;
});

const menuId = "translate-page";
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: menuId,
    title: "MyPLaMoでWEB全体を翻訳",
    contexts: ["page", "selection", "link", "image", "video", "audio", "editable", "frame"],
    documentUrlPatterns: ["http://*/*", "https://*/*"]
  });
});

async function translatePage(tab, toggle) {
  if (!tab?.id) return;
  const target = {tabId: tab.id};
  try {
    if (toggle) {
      const [state] = await chrome.scripting.executeScript({
        target,
        func: () => {
          if (!globalThis.undoMyPLaMo) return false;
          globalThis.undoMyPLaMo();
          return true;
        }
      });
      if (state.result) return;
    }
    await chrome.scripting.executeScript({target, files: ["page.js"]});
  } catch {
    await chrome.action.setBadgeText({tabId: tab.id, text: "対象外"});
    await chrome.action.setTitle({tabId: tab.id, title: "通常のWebページを開いてください"});
  }
}

chrome.action.onClicked.addListener(tab => translatePage(tab, true));
chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === menuId) translatePage(tab, false);
});
