const {test} = require('node:test');
const assert = require('node:assert/strict');
const {JSDOM} = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname, '../extension/page.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
const hoverTick = () => new Promise(resolve => setTimeout(resolve,150));

function fixture(lang, body, options) {
  const dom = new JSDOM(`<html lang="${lang}"><body>${body}</body></html>`, {
    runScripts: 'outside-only', url: 'https://example.test'
  });
  const w = dom.window, calls = [], roots = [];
  w.myPLaMoPageOptions = options;
  let caretNode;
  w.document.caretRangeFromPoint = () => { if (!caretNode) return null; const range=w.document.createRange();range.setStart(caretNode,0);return range; };
  w.Range.prototype.getClientRects = () => [{left:20,top:30,right:220,bottom:50,width:200,height:20}];
  const attach = w.Element.prototype.attachShadow;
  w.Element.prototype.attachShadow = function(options) {
    const root = attach.call(this, options); roots.push(root); return root;
  };
  w.chrome = {runtime: {sendMessage: async message => {
    calls.push(message.text);
    return {translation: `訳文：${message.text}`};
  }}};
  w.eval(script);
  const move = (target,x=30,y=40,node=target.firstChild) => {caretNode=node;target.dispatchEvent(new w.MouseEvent('mousemove',{bubbles:true,clientX:x,clientY:y}));};
  return {w, dom, calls, roots,move};
}

test('English posts translate and restore with Japanese, English or automatic page language', async () => {
  for (const lang of ['ja-JP', 'ja', 'en', 'auto']) {
    const f = fixture(lang, '<h1>  An English post title  </h1><p>An English comment.</p>');
    try {
      await tick();
      assert.deepEqual(f.calls, ['An English post title', 'An English comment.'], lang);
      assert.equal(f.w.document.querySelector('h1').textContent, '  訳文：An English post title  ');
      assert.match(f.roots[0].querySelector('[role="status"]').textContent, /2件/);
      f.w.undoMyPLaMo();
      assert.equal(f.w.document.querySelector('h1').textContent, '  An English post title  ');
      assert.equal(f.w.document.querySelector('p').textContent, 'An English comment.');
    } finally { f.dom.window.close(); }
  }
});

test('hover shows original text safely, display settings apply live, and restore removes styling',async () => {
  const f=fixture('en','<p style="white-space:normal;overflow-wrap:normal">Original &lt;b&gt;text&lt;/b&gt;</p>');
  try {
    await tick();
    const p=f.w.document.querySelector('p'), tooltip=f.roots[0].querySelector('[role=tooltip]');
    assert.equal(p.style.whiteSpace,'pre-wrap'); assert.equal(p.style.overflowWrap,'anywhere');
    f.move(p); await hoverTick();
    assert.equal(tooltip.hidden,false); assert.equal(tooltip.textContent,'原文：\nOriginal <b>text</b>'); assert.equal(tooltip.querySelector('b'),null);
    f.move(f.w.document.body,500,500,p.firstChild); assert.equal(tooltip.hidden,true);
    f.w.myPLaMoPreferences={hoverOriginal:true,adjustStyle:false,hideOnLeave:false,highlight:'none'};
    f.w.document.dispatchEvent(new f.w.CustomEvent('myplamo-settings-changed'));
    assert.equal(p.style.whiteSpace,'normal');
    f.move(p); await hoverTick();
    f.move(f.w.document.body,500,500,p.firstChild); assert.equal(tooltip.hidden,false);
    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'Escape'})); assert.equal(tooltip.hidden,true);
    f.w.undoMyPLaMo(); assert.equal(p.textContent,'Original <b>text</b>'); assert.equal(p.style.overflowWrap,'normal');
  } finally {f.dom.window.close();}
});

test('disabled hover and adjustment respect preferences from the start',async () => {
  const f=fixture('en','<p>Hello world.</p>',{hoverOriginal:false,adjustStyle:false,to:'en'});
  try {
    await tick(); const p=f.w.document.querySelector('p'); f.move(p); await hoverTick();
    assert.equal(f.roots[0].querySelector('[role=tooltip]').hidden,true); assert.equal(p.style.whiteSpace,'');
    assert.match(f.roots[0].querySelector('[role=status]').textContent,/英語/);
  } finally {f.dom.window.close();}
});

test('wrapper padding never aggregates descendants and only the hit text has an original tooltip',async()=>{
  const f=fixture('en','<main><article id="wrapper"><p id="first">First paragraph.</p><p id="second">Second paragraph.</p></article></main>');
  try{
    await tick();const wrapper=f.w.document.querySelector('#wrapper'),first=f.w.document.querySelector('#first'),second=f.w.document.querySelector('#second'),tooltip=f.roots[0].querySelector('[role=tooltip]');
    f.move(wrapper,400,100,first.firstChild);await hoverTick();assert.equal(tooltip.hidden,true);
    f.move(wrapper,30,40,second.firstChild);await hoverTick();assert.equal(tooltip.textContent,'原文：\nSecond paragraph.');assert.equal(tooltip.style.pointerEvents,'none');
    f.move(wrapper,400,100,second.firstChild);assert.equal(tooltip.hidden,true);await hoverTick();assert.equal(tooltip.hidden,true);
  }finally{f.dom.window.close();}
});

test('moving across the same text does not rewrite or reposition the tooltip',async()=>{
  const f=fixture('en','<p>Same paragraph.</p>');
  try{
    await tick();const p=f.w.document.querySelector('p'),tooltip=f.roots[0].querySelector('[role=tooltip]');f.move(p);await hoverTick();
    let changes=0;const observer=new f.w.MutationObserver(records=>changes+=records.length);observer.observe(tooltip,{attributes:true,subtree:true,childList:true,characterData:true});
    f.move(p,60,40);f.move(p,100,40);await tick();assert.equal(changes,0);observer.disconnect();
  }finally{f.dom.window.close();}
});

test('a brief pass over text or restoring the page cancels a pending tooltip',async()=>{
  const f=fixture('en','<article><p>Original text.</p></article>');
  try{
    await tick();const p=f.w.document.querySelector('p'),wrapper=f.w.document.querySelector('article'),tooltip=f.roots[0].querySelector('[role=tooltip]');
    f.move(p);f.move(wrapper,500,500,p.firstChild);await hoverTick();assert.equal(tooltip.hidden,true);
    f.move(p);f.w.undoMyPLaMo();await hoverTick();assert.equal(tooltip.hidden,true);assert.equal(tooltip.isConnected,false);
  }finally{f.dom.window.close();}
});

test('explicit translation opt-outs, code, hidden text and input fields stay excluded', async () => {
  const f = fixture('ja-JP', `<p>Translate this comment.</p>
    <div translate="no"><p>Opted out</p></div><div class="notranslate">Do not translate</div>
    <pre>Source code</pre><code>Inline code</code><textarea>Private input</textarea>
    <input value="Private value"><div hidden>Hidden text</div><div aria-hidden="true">Hidden label</div>`);
  try {
    await tick();
    assert.deepEqual(f.calls, ['Translate this comment.']);
    assert.equal(f.w.document.querySelector('textarea').value, 'Private input');
    assert.equal(f.w.document.querySelector('input').value, 'Private value');
    assert.equal(f.w.document.querySelector('[translate="no"]').textContent, 'Opted out');
  } finally { f.dom.window.close(); }
});

test('new comments translate once and restore without overwriting later site edits', async () => {
  const f = fixture('ja-JP', '<p id="first">First comment.</p>');
  try {
    await tick();
    f.w.document.body.insertAdjacentHTML('beforeend', '<p id="next">New comment.</p>');
    await tick();
    assert.deepEqual(f.calls, ['First comment.', 'New comment.']);
    assert.equal(f.w.document.querySelector('#next').textContent, '訳文：New comment.');
    f.w.document.querySelector('#first').firstChild.data = 'Edited by the site.';
    // Restore immediately, before the observer handles the site's new text.
    f.w.undoMyPLaMo();
    assert.equal(f.w.document.querySelector('#first').textContent, 'Edited by the site.');
    assert.equal(f.w.document.querySelector('#next').textContent, 'New comment.');
    await tick();
    assert.deepEqual(f.calls, ['First comment.', 'New comment.']);
  } finally { f.dom.window.close(); }
});
