// A caught HTTP connection failure is quiet in Chrome's extension error log.
// Probe before opening a WebSocket, whose native connection error is logged even
// when its onerror handler handles it.
const MyPLaMoConnection = (() => {
  const title = 'MyPLaMo 翻訳メニュー';
  const notice = 'MyPLaMo本体を起動し、「Chromeからの翻訳を受け付ける」をオンにしてください。';
  const sizes = [16,24,32,48,64];
  const normalIcons = Object.fromEntries(sizes.map(size => [size,`icons/myplamo-${size}.png`]));
  let checking, warningIcons, shown, indicator = Promise.resolve();

  async function drawWarningIcons() {
    const response = await fetch(chrome.runtime.getURL('icons/myplamo-256.png'));
    const bitmap = await createImageBitmap(await response.blob());
    try {
      const icons = {};
      for (const size of sizes) {
        const canvas = new OffscreenCanvas(size,size), ctx = canvas.getContext('2d');
        ctx.drawImage(bitmap,0,0,size,size);
        const center = size * .75, radius = size * .23;
        ctx.beginPath(); ctx.arc(center,center,radius,0,Math.PI*2);
        ctx.fillStyle = '#facc15'; ctx.fill();
        ctx.lineWidth = Math.max(1,size/24); ctx.strokeStyle = '#ffffff'; ctx.stroke();
        // Use vector strokes, so cold-start service workers do not depend on
        // Chrome's font service having an active document renderer.
        ctx.fillStyle = '#3b2f00';
        const markWidth = Math.max(1,size*.07);
        ctx.fillRect(center-markWidth/2,center-radius*.58,markWidth,radius*.75);
        ctx.beginPath(); ctx.arc(center,center+radius*.5,markWidth*.55,0,Math.PI*2); ctx.fill();
        icons[size] = ctx.getImageData(0,0,size,size);
      }
      return icons;
    } finally { bitmap.close(); }
  }

  function display(connected) {
    indicator = indicator.catch(() => {}).then(async () => {
      if (shown === connected) return;
      if (connected) await chrome.action.setIcon({path:normalIcons});
      else {
        warningIcons ||= drawWarningIcons().catch(error => {warningIcons=undefined;throw error;});
        await chrome.action.setIcon({imageData:await warningIcons});
      }
      await chrome.action.setBadgeText({text:''});
      await chrome.action.setTitle({title:connected ? title : 'MyPLaMo本体が未接続です。' + notice});
      shown = connected;
    });
    return indicator;
  }

  function refresh() {
    if (checking) return checking;
    checking = (async () => {
      let available = false;
      try {
        // Both existing Windows/Mac bridge versions answer HTTP here, even
        // though a normal GET is rejected for lacking the WebSocket upgrade.
        await fetch('http://127.0.0.1:47832/translate',{cache:'no-store',signal:AbortSignal.timeout(1500)});
        available = true;
      } catch { }
      await display(available);
      return available;
    })().finally(() => {checking=undefined;});
    return checking;
  }

  async function request(message) {
    if (!await refresh()) {
      const error = new Error(notice); error.code = 'APP_OFFLINE'; throw error;
    }
    try { return await bridgeRequest(message); }
    catch (error) {
      if (error.code === 'APP_OFFLINE') await display(false);
      throw error;
    }
  }
  return {refresh,request,notice};
})();
