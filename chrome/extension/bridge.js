// Identical endpoint and messages on Windows MyPLaMo and MyPLaMo for Mac.
// Keep the manifest public key unchanged: both apps allow only this extension ID.
function bridgeRequest(message) {
  return new Promise((resolve, reject) => {
    if (typeof message?.text !== "string" || !message.text.trim() || message.text.length > 2000) {
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
    const failed = () => finish(new Error("MyPLaMo（Windows）またはMyPLaMo for Macを起動し、「Chromeからの翻訳を受け付ける」をオンにしてから再試行してください。"));
    socket.onopen = () => socket.send(JSON.stringify({text: message.text}));
    socket.onmessage = event => {
      try {
        const reply = JSON.parse(event.data);
        if (reply.working === true) return;
        if (typeof reply.error === "string") finish(new Error(reply.error));
        else if (typeof reply.translation === "string" && reply.translation.length) finish(null, reply);
        else finish(new Error("翻訳エンジンから正しい応答を受信できませんでした。"));
      } catch (error) { finish(error); }
    };
    socket.onerror = failed;
    socket.onclose = failed;
  });
}
