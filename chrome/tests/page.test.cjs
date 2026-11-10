const {test} = require('node:test');
const assert = require('node:assert/strict');
const {JSDOM} = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname, '../extension/page.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));

function fixture(lang, body) {
  const dom = new JSDOM(`<html lang="${lang}"><body>${body}</body></html>`, {
    runScripts: 'outside-only', url: 'https://example.test'
  });
  const w = dom.window, calls = [], roots = [];
  const attach = w.Element.prototype.attachShadow;
  w.Element.prototype.attachShadow = function(options) {
    const root = attach.call(this, options); roots.push(root); return root;
  };
  w.chrome = {runtime: {sendMessage: async message => {
    calls.push(message.text);
    return {translation: `訳文：${message.text}`};
  }}};
  w.eval(script);
  return {w, dom, calls, roots};
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
