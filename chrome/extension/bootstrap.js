(() => {
  let route = location.href;
  async function refresh(auto = false) {
    try {
      const reply = await chrome.runtime.sendMessage({kind:'get-settings'});
      if (reply?.settings) {
        globalThis.myPLaMoPreferences = reply.settings;
        document.dispatchEvent(new CustomEvent('myplamo-settings-changed'));
      }
      if (auto) await chrome.runtime.sendMessage({kind:'auto-start'});
    } catch { }
  }
  chrome.runtime.onMessage.addListener(message => {
    if (message.kind === 'settings-changed') {
      globalThis.myPLaMoPreferences = message.settings;
      document.dispatchEvent(new CustomEvent('myplamo-settings-changed'));
    }
  });
  refresh(true);
  setInterval(() => {
    if (location.href === route) return;
    route = location.href; globalThis.undoMyPLaMo?.(); refresh(true);
  },1000);
})();
