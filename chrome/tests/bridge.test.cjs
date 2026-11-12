const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const crypto = require('node:crypto');
const path = require('node:path');
const root = path.join(__dirname, '..');
function fixture() {
  const sockets = [], timers = new Map();
  class WebSocket {
    constructor(url) { this.url = url; sockets.push(this); }
    send(value) { this.sent = JSON.parse(value); }
    close() { this.closed = true; }
  }
  const sandbox = {WebSocket, setTimeout(fn) {timers.set(1, fn); return 1;}, clearTimeout(id) {timers.delete(id);}};
  vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(path.join(root, 'extension/bridge.js'), 'utf8'), sandbox);
  return {...sandbox, sockets, timers};
}
test('keeps the published extension identity for Windows and Mac', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json')));
  const hash = crypto.createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
  const id = [...hash].map(x => String.fromCharCode(97 + parseInt(x, 16))).join('');
  assert.equal(id, 'oefekpcnnmijlldkpeplijencgdnakbb');
  assert.equal(manifest.manifest_version, 3);
});
test('same wire protocol resolves both Windows and Mac replies and closes socket', async () => {
  const f = fixture(), result = f.bridgeRequest({text: 'Hello'}), s = f.sockets[0];
  assert.equal(s.url, 'ws://127.0.0.1:47832/translate');
  s.onopen(); assert.deepEqual(s.sent, {text:'Hello', protocolVersion:1});
  s.onmessage({data:JSON.stringify({working:true})});
  assert.equal(s.closed, undefined);
  s.onmessage({data:JSON.stringify({translation:'こんにちは'})});
  assert.equal((await result).translation, 'こんにちは');
  assert.equal(s.closed, true); assert.equal(f.timers.size, 0);
});
test('server and connection failures are surfaced', async () => {
  let f = fixture(), result = f.bridgeRequest({text:'Hello'});
  f.sockets[0].onmessage({data:'{"error":"model not selected"}'});
  await assert.rejects(result, /model not selected/);
  f = fixture(); result = f.bridgeRequest({text:'Hello'}); f.sockets[0].onerror();
  await assert.rejects(result, /MyPLaMo for Mac/);
});
test('malformed and empty replies fail and release connection', async () => {
  for (const data of ['{broken', '{}', '{"translation":""}']) {
    const f = fixture(), result = f.bridgeRequest({text:'Hello'});
    f.sockets[0].onmessage({data}); await assert.rejects(result); assert.equal(f.sockets[0].closed,true);
  }
});
test('length bounds and timeout do not hang service worker', async () => {
  const f = fixture();
  await assert.rejects(f.bridgeRequest({text:' '.repeat(4)}));
  await assert.rejects(f.bridgeRequest({text:'a'.repeat(2001)}));
  assert.equal(f.sockets.length,0);
  const result = f.bridgeRequest({text:'a'.repeat(2000)});
  f.timers.get(1)(); await assert.rejects(result, /タイムアウト/); assert.equal(f.sockets[0].closed,true);
});

test('identifies Mac and Windows and preserves legacy translation', async () => {
  for (const platform of ['macos', 'windows', null]) {
    const f = fixture(), result = f.bridgeRequest({text:'Hello'});
    const reply = {translation:'こんにちは'};
    if (platform) reply.server = {platform, appVersion:'0.2.1', protocolVersion:1};
    f.sockets[0].onmessage({data:JSON.stringify(reply)});
    assert.match((await result).connectionLabel, platform === 'macos' ? /MyPLaMo for Mac v0.2.1/ : platform === 'windows' ? /Windows.*v0.2.1/ : /バージョン未確認/);
  }
});
test('rejects incompatible and malformed server metadata', async () => {
  for (const server of [null, {platform:'macos',appVersion:'0.2.1',protocolVersion:2}, {platform:'other',appVersion:'0.2.1',protocolVersion:1}, {platform:'macos',appVersion:'<bad>',protocolVersion:1}]) {
    const f = fixture(), result = f.bridgeRequest({text:'Hello'});
    f.sockets[0].onmessage({data:JSON.stringify({server,translation:'unused'})});
    await assert.rejects(result); assert.equal(f.sockets[0].closed,true);
  }
});

test('metadata handshake and translation options reach the server without dropping fields',async () => {
  const f=fixture(); const metadata=f.bridgeRequest({action:'metadata'}); f.sockets[0].onopen();
  assert.deepEqual(f.sockets[0].sent,{action:'metadata',protocolVersion:1});
  f.sockets[0].onmessage({data:JSON.stringify({glossaries:[],modelId:'2b',server:{platform:'windows',appVersion:'1.4.0',protocolVersion:1,capabilities:['options']}})});
  assert.equal((await metadata).modelId,'2b');
  const message={text:'Hello',from:'en',to:'ja',style:'plain',glossaryId:'g1',context:'News'};
  const pending=f.bridgeRequest(message); f.sockets[1].onopen(); assert.deepEqual(f.sockets[1].sent,{...message,protocolVersion:1});
  f.sockets[1].onmessage({data:'{"translation":"こんにちは"}'}); await pending;
});
