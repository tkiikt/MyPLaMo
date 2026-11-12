// Identical endpoint and messages on Windows MyPLaMo and MyPLaMo for Mac.
// Keep the manifest public key unchanged: both apps allow only this extension ID.
function bridgeRequest(message) {
  return new Promise((resolve, reject) => {
    if (message?.action !== 'metadata' && (typeof message?.text !== "string" || !message.text.trim() || message.text.length > 2000)) {
      reject(new Error("文章を短くしてください（最大2,000文字）。"));
      return;
    }
    const socket = new WebSocket("ws://127.0.0.1:47832/translate");
    let settled = false;
    const timer = setTimeout(() => finish(new Error("翻訳がタイムアウトしました。本体のモデルと接続を確認してください。")), 490000);
    function finish(error, reply) {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
      socket.close();
      if (error) reject(error); else resolve(reply);
    }
    const failed = () => { const error = new Error("MyPLaMo（Windows）またはMyPLaMo for Macを起動し、「Chromeからの翻訳を受け付ける」をオンにしてから再試行してください。"); error.code = 'APP_OFFLINE'; finish(error); };
    socket.onopen = () => socket.send(JSON.stringify({...message, protocolVersion: 1}));
    socket.onmessage = event => {
      try {
        const reply = JSON.parse(event.data);
        if (reply.server !== undefined) {
          const server = reply.server;
          if (!server || server.protocolVersion !== 1) {
            finish(new Error("本体と拡張機能の通信バージョンが対応していません。両方を更新してください。")); return;
          }
          if (!["macos", "windows"].includes(server.platform) || typeof server.appVersion !== "string" || !/^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$/.test(server.appVersion)) {
            finish(new Error("本体の接続情報を確認できません。本体を更新してください。")); return;
          }
        }
        if (reply.working === true) return;
        reply.connectionLabel = reply.server
          ? (reply.server.platform === "macos" ? "MyPLaMo for Mac" : "MyPLaMo（Windows）") + " v" + reply.server.appVersion + "に接続中"
          : "MyPLaMoに接続中（本体情報非対応の旧版・バージョン未確認）";
        if (typeof reply.error === "string") { const error = new Error(reply.error); error.server = reply.server; finish(error); }
        else if (message.action === 'metadata' && Array.isArray(reply.glossaries)) finish(null,reply);
        else if (typeof reply.translation === "string" && reply.translation.length) finish(null, reply);
        else finish(new Error("翻訳エンジンから正しい応答を受信できませんでした。"));
      } catch (error) { finish(error); }
    };
    socket.onerror = failed;
    socket.onclose = failed;
  });
}
