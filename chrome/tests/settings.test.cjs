const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require('node:path').join(__dirname,'../extension/settings.js'),'utf8');
const f = {URL}; vm.createContext(f); vm.runInContext(source,f); const S = f.MyPLaMoSettings;
test('URL rules match exact pages, whole sites and wildcards without regex interpretation',() => {
  assert.equal(S.matches('https://example.com/a?x=1#part','https://example.com/*'),true);
  assert.equal(S.matches('https://example.com/a?x=1','https://example.com/a?x=1'),true);
  assert.equal(S.matches('https://exampleXcom/a','https://example.com/*'),false);
  assert.equal(S.matches('https://evil.test/?https://example.com/a','https://example.com/*'),false);
  assert.equal(S.matches('https://news.example.com/a','https://*.example.com/*'),true);
  assert.equal(S.matches('http://example.com/a','https://example.com/*'),false);
  for (const rule of ['javascript:alert(1)','file:///secret','https://user:pass@example.com/*','https://example.com/*#fragment']) assert.equal(S.validPattern(rule),false);
});
test('auto translation defaults off and respects blacklist, whitelist and disabled filters',() => {
  const s = S.normalize({autoTranslate:true,filterEnabled:true,filterPatterns:['https://example.com/*']});
  assert.equal(S.shouldAuto(S.normalize(),'https://example.com/'),false);
  assert.equal(S.shouldAuto(s,'https://example.com/a'),false);
  assert.equal(S.shouldAuto(s,'https://other.test/'),true);
  s.filterMode = 'whitelist'; assert.equal(S.shouldAuto(s,'https://example.com/a'),true); assert.equal(S.shouldAuto(s,'https://other.test/'),false);
  s.filterEnabled = false; assert.equal(S.shouldAuto(s,'https://other.test/'),true); assert.equal(S.shouldAuto(s,'chrome://settings'),false);
});
test('site style and validated stored settings are deterministic',() => {
  const s = S.normalize({plainPatterns:['https://news.test/*'],hoverOriginal:'false',highlight:'bad',filterPatterns:[null,'https://a.test/*','https://a.test/*']});
  assert.equal(S.effective(s,'https://news.test/x').style,'plain'); assert.equal(S.effective(s,'https://else.test/').style,'polite');
  assert.equal(s.hoverOriginal,true); assert.equal(s.highlight,'none'); assert.equal(s.filterPatterns.length,1);
});
