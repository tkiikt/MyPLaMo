function bridgeRequest(message) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket("ws://127.0.0.1:47832/translate");
    const failed = () => reject(new Error("MyPLaMoを起動し、「Chromeからの翻訳を受け付ける」をオンにしてから再試行してください。"));
    socket.onopen = () => socket.send(JSON.stringify(message));
    socket.onmessage = event => {
      try {
        const reply = JSON.parse(event.data);
        if (reply.working) return;
        if (reply.error) reject(new Error(reply.error));
        else resolve(reply);
      } catch (error) { reject(error); }
    };
    socket.onerror = failed;
    socket.onclose = failed;
  });
}

