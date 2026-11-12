const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const directory = path.join(__dirname,'../extension');
function fixture(data = {}) {
  const calls = [], scripts = [], broadcasts = []; let listener, info = {glossaries:[],modelId:'2b',glossaryRevision:'r1',server:{platform:'windows',appVersion:'1.3.0',capabilities:['options']}};
  const sandbox = {URL,TextEncoder,structuredClone,console};
  sandbox.chrome = {runtime:{id:'self',getURL:path=>'chrome-extension://self/'+path,onMessage:{addListener:f => {listener=f;}},onInstalled:{addListener(){}},onStartup:{addListener(){}}},storage:{local:{
    get:async key => structuredClone({[key]:data[key]}),set:async value => Object.assign(data,structuredClone(value))}},
    tabs:{query:async () => [{id:7,url:'https://example.com/article',title:'Article'}],sendMessage:async(...args)=>broadcasts.push(args)},
    scripting:{executeScript:async args=>scripts.push(args)},contextMenus:{onClicked:{addListener(){}},removeAll(f){f();},create(){}},alarms:{get:async()=>undefined,create(){},onAlarm:{addListener(){}}}};
  sandbox.MyPLaMoConnection={request:message=>sandbox.bridgeRequest(message),refresh:async()=>true};
  sandbox.importScripts = (...files) => { for (const file of files) if (file === 'settings.js') vm.runInContext(fs.readFileSync(path.join(directory,file),'utf8'),sandbox); };
  sandbox.bridgeRequest = async request => { calls.push(structuredClone(request)); return request.action === 'metadata' ? structuredClone(info) : {translation:`訳:${request.text}`,server:info.server}; };
  vm.createContext(sandbox); vm.runInContext(fs.readFileSync(path.join(directory,'worker.js'),'utf8'),sandbox);
  const popup = {id:'self'}, sender = {id:'self',tab:{id:7,url:'https://example.com/article'}};
  const send = (msg,from=sender) => new Promise(resolve => listener(msg,from,resolve));
  return {sandbox,send,popup,sender,data,calls,scripts,broadcasts,setInfo:value=>{info=value;},info};
}
test('worker forwards configured languages, site style, glossary and context',async () => {
  const f=fixture({preferences:{from:'en',to:'ja',glossaryId:'g1',plainPatterns:['https://example.com/*']}});
  const reply=await f.send({text:'Hello',context:'Article context'});
  assert.equal(reply.translation,'訳:Hello'); assert.deepEqual(f.calls[1],{text:'Hello',from:'en',to:'ja',style:'plain',glossaryId:'g1',context:'Article context'});
});
test('cache survives worker restart and invalidates with model, glossary and settings',async () => {
  const data={}, f=fixture(data); await f.send({text:'Hello'}); await f.send({text:'Hello'});
  assert.equal(f.calls.filter(c=>c.text).length,1);
  const next=fixture(data); await next.send({text:'Hello'}); assert.equal(next.calls.filter(c=>c.text).length,0);
  next.setInfo({...next.info,modelId:'8b'}); await next.send({kind:'state'},next.popup); await next.send({text:'Hello'});
  assert.equal(next.calls.filter(c=>c.text).length,1);
  next.setInfo({...next.info,modelId:'8b',glossaryRevision:'r2'}); await next.send({kind:'state'},next.popup); await next.send({text:'Hello'});
  assert.equal(next.calls.filter(c=>c.text).length,2);
  await next.send({kind:'save-settings',settings:{to:'en'}},next.popup); await next.send({text:'Hello'}); assert.equal(next.calls.filter(c=>c.text).length,3);
  const stats=await next.send({kind:'cache-list'},next.popup); assert.equal(stats.pages.length,1); assert.equal(stats.pages[0].count,4);
  await next.send({kind:'cache-delete',url:'https://example.com/article'},next.popup); assert.equal((await next.send({kind:'cache-list'},next.popup)).pages.length,0);
});
test('auto starts only on allowed pages; popup uses explicit start and restore actions',async () => {
  const f=fixture({preferences:{autoTranslate:true,filterEnabled:true,filterMode:'whitelist',filterPatterns:['https://allowed.test/*']}});
  await f.send({kind:'auto-start'}); assert.equal(f.scripts.length,0);
  await f.send({kind:'auto-start'},{tab:{id:8,url:'https://allowed.test/x'}}); assert.equal(f.scripts.length,2);
  await f.send({kind:'translate-page'},f.popup); assert.equal(f.scripts.at(-1).files[0],'page.js');
  await f.send({kind:'restore-page'},f.popup); assert.equal(typeof f.scripts.at(-1).func,'function');
});
test('settings broadcast to pages and tab senders cannot mutate preferences or erase cache',async () => {
  const f=fixture(); await f.send({kind:'save-settings',settings:{selectionButton:false}},f.popup);
  assert.equal(f.data.preferences.selectionButton,false); assert.equal(f.broadcasts.length,1);
  await f.send({kind:'save-settings',settings:{selectionButton:true}}); assert.equal(f.data.preferences.selectionButton,false);
  assert.equal((await f.send({kind:'get-settings'})).settings.selectionButton,false);
});
test('legacy apps handle basic translation but new options fail explicitly',async () => {
  const f=fixture(); f.sandbox.bridgeRequest=async request=>{if(request.action) throw new Error("The given key 'text' was not present"); return {translation:'legacy'};};
  assert.equal((await f.send({text:'Hello'})).translation,'legacy');
  assert.equal(f.data.translationCache,undefined);
  await f.send({kind:'save-settings',settings:{to:'en'}},f.popup); assert.match((await f.send({text:'Hello'})).error,/更新/);
});

test('legacy metadata errors identify a connected old server instead of reporting it offline',async () => {
  const f=fixture(); f.sandbox.bridgeRequest=async()=>{const error=new Error('The given key was not present in the dictionary.');error.server={platform:'windows',appVersion:'1.3.0',protocolVersion:1};throw error;};
  const state=await f.send({kind:'state'},f.popup); assert.equal(state.info.legacy,true); assert.equal(state.info.server.appVersion,'1.3.0'); assert.equal(state.connectionError,'');
});

test('offline menu state carries an informational notice without a connection error',async()=>{
  const f=fixture();f.sandbox.MyPLaMoConnection.request=async()=>{throw Object.assign(new Error('本体を起動してください。'),{code:'APP_OFFLINE'});};
  const state=await f.send({kind:'state'},f.popup);assert.equal(state.connectionError,'');assert.equal(state.connectionNotice,'本体を起動してください。');
});

test('offline page translation shows a connection notice without injecting an error panel',async()=>{
  const f=fixture();f.sandbox.MyPLaMoConnection.refresh=async()=>false;f.sandbox.MyPLaMoConnection.notice='本体を起動してください。';
  const reply=await f.send({kind:'translate-page'},f.popup);assert.equal(reply.ok,false);assert.equal(reply.connectionNotice,'本体を起動してください。');assert.equal(f.scripts.length,0);
});
test('deleting cache while a request is pending prevents its result being written back',async () => {
  const f=fixture(); let release, started; const began=new Promise(r=>started=r);
  const original=f.sandbox.bridgeRequest; f.sandbox.bridgeRequest=async request=>{if(request.action)return original(request); started();return new Promise(r=>{release=r;});};
  const pending=f.send({text:'Hello'}); await began;
  const deletion=f.send({kind:'cache-delete'},f.popup); await deletion;
  assert.equal((await f.send({kind:'cache-list'},f.popup)).pages.length,0);
  release({translation:'late'}); await pending;
  assert.equal((await f.send({kind:'cache-list'},f.popup)).pages.length,0);
});
