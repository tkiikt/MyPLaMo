const {test} = require('node:test');
const assert = require('node:assert/strict');
const {JSDOM} = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname,'../extension/selection.js'),'utf8');
const tick = () => new Promise(r => setTimeout(r,110));
function fixture(reply = {translation:'こんにちは',server:{platform:'windows',appVersion:'1.3.0',protocolVersion:1},connectionLabel:'MyPLaMo（Windows） v1.3.0に接続中'}) {
  const dom = new JSDOM('<body><p>Hello world</p><input value="private"><div contenteditable="true">Private edit</div></body>', {runScripts:'outside-only',url:'https://example.test'});
  const w = dom.window, calls = [], roots = [], copies = [];
  const attach = w.Element.prototype.attachShadow;
  w.Element.prototype.attachShadow = function(o) { const s = attach.call(this,o); roots.push(s); return s; };
  w.Range.prototype.getClientRects = () => [{left:30,top:30,bottom:60}];
  w.chrome = {runtime:{sendMessage: async msg => {calls.push(msg);return typeof reply === 'function' ? reply() : reply;}}};
  Object.defineProperty(w.navigator,'clipboard',{value:{writeText:async text => copies.push(text)}});
  w.eval(script);
  async function select(text='Hello world', selector='p') {
    const el = w.document.querySelector(selector); el.textContent = text;
    const range = w.document.createRange(); range.selectNodeContents(el);
    w.getSelection().removeAllRanges(); w.getSelection().addRange(range);
    el.dispatchEvent(new w.Event('pointerup',{bubbles:true})); await tick();
    return roots.at(-1);
  }
  return {w,dom,calls,roots,copies,select};
}
test('selection shows icon; click translates without changing page; copy and close work',async () => {
  const f=fixture(); try {
    const s=await f.select(); assert.ok(s.querySelector('.launch img')); assert.equal(f.calls.length,0);
    s.querySelector('.launch').click(); await tick();
    assert.equal(f.calls[0].text,'Hello world'); assert.equal(s.querySelector('.result').textContent,'こんにちは');
    assert.equal(s.querySelector('header .connection').textContent,'v1.3.0に接続中');
    assert.equal(s.querySelector('.result + .connection'),null);
    assert.equal(f.w.document.querySelector('p').textContent,'Hello world');
    assert.ok(s.querySelector('.copy svg.clipboard'));
    assert.equal(s.querySelector('.copy').textContent.trim(),'');
    s.querySelector('.copy').click(); await tick(); assert.deepEqual(f.copies,['こんにちは']);
    assert.equal(s.querySelector('.copy').dataset.state,'copied');
    assert.equal(s.querySelector('.copy').getAttribute('aria-label'),'コピーしました');
    s.querySelector('.close').click(); assert.equal(s.host.isConnected,false);
  } finally {f.dom.window.close();}
});

test('connection header supports Mac and legacy servers without a platform label',async () => {
  for(const server of [{platform:'macos',appVersion:'0.2.1',protocolVersion:1},undefined]) {
    const f=fixture({translation:'こんにちは',server}); try {
      const s=await f.select(); s.querySelector('.launch').click(); await tick();
      assert.equal(s.querySelector('header .connection').textContent,server?'v0.2.1に接続中':'接続中（バージョン未確認）');
    } finally {f.dom.window.close();}
  }
});
test('oversized selection is rejected without sending; connection errors are visible',async () => {
  for(const long of [true,false]) {
    const f=fixture({error:'本体を起動してください'}); try {
      const s=await f.select(long?'x'.repeat(2001):'Hello'); s.querySelector('.launch').click(); await tick();
      assert.match(s.querySelector('.result').textContent,long?/2,000文字/:/本体を起動/);
      assert.equal(f.calls.length,long?0:1); assert.equal(s.querySelector('.copy').disabled,true);
    } finally {f.dom.window.close();}
  }
});
test('dismissed requests cannot reopen or overwrite a later selection',async () => {
  let resolve; const f=fixture(() => new Promise(r=>{resolve=r;})); try {
    const old=await f.select(); old.querySelector('.launch').click(); await tick();
    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keyup',{key:'Escape',bubbles:true}));
    const next=await f.select('New text'); resolve({translation:'Old result'}); await tick();
    assert.equal(old.host.isConnected,false); assert.ok(next.querySelector('.launch'));
    assert.equal(next.querySelector('.result'),null);
  } finally {f.dom.window.close();}
});
test('editable text is excluded; keyboard selection and outside dismissal work',async () => {
  const f=fixture(); try {
    await f.select('Private edit','[contenteditable]'); assert.equal(f.roots.length,0);
    const s=await f.select();
    f.w.document.body.dispatchEvent(new f.w.Event('pointerdown',{bubbles:true})); assert.equal(s.host.isConnected,false);
    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keyup',{key:'ArrowRight',bubbles:true})); await tick();
    assert.ok(f.roots.at(-1).querySelector('.launch'));
  } finally {f.dom.window.close();}
});
test('translation output is text, never executable markup',async () => {
  const f=fixture({translation:'<img src=x onerror=alert(1)>'}); try {
    const s=await f.select();s.querySelector('.launch').click();await tick();
    assert.equal(s.querySelector('.result img'),null);assert.match(s.querySelector('.result').textContent,/<img/);
  } finally {f.dom.window.close();}
});

test('selection launcher can be disabled and re-enabled without reloading',async () => {
  const f=fixture(); try {
    f.w.myPLaMoPreferences={selectionButton:false}; await f.select(); assert.equal(f.roots.length,0);
    f.w.myPLaMoPreferences={selectionButton:true}; const s=await f.select(); assert.ok(s.querySelector('.launch'));
    f.w.myPLaMoPreferences={selectionButton:false}; f.w.document.dispatchEvent(new f.w.CustomEvent('myplamo-settings-changed'));
    assert.equal(s.host.isConnected,false);
  } finally {f.dom.window.close();}
});
