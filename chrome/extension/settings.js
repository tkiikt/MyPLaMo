(() => {
  if (globalThis.MyPLaMoSettings) return;
  const defaults = {from:'',to:'ja',glossaryId:'',autoTranslate:false,hoverOriginal:true,selectionButton:true,
    adjustStyle:true,hideOnLeave:true,highlight:'none',filterEnabled:false,filterMode:'blacklist',filterPatterns:[],plainPatterns:[]};
  function validPattern(pattern) {
    if (typeof pattern !== 'string' || pattern.length > 2048 || !/^https?:\/\/[^/]+\//.test(pattern)) return false;
    try { const u = new URL(pattern.replaceAll('*','wildcard')); return !u.username && !u.password && !u.hash && !!u.hostname; } catch { return false; }
  }
  function matches(url, pattern) {
    if (!validPattern(pattern)) return false;
    const expression = pattern.split('*').map(p => p.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')).join('.*');
    try { return new RegExp('^' + expression + '$').test(pageUrl(url)); } catch { return false; }
  }
  function pageUrl(value) { const url = new URL(value); url.hash = ''; return url.href; }
  function normalize(raw = {}) {
    const value = {...defaults};
    for (const name of ['autoTranslate','hoverOriginal','selectionButton','adjustStyle','hideOnLeave','filterEnabled']) if (typeof raw[name] === 'boolean') value[name] = raw[name];
    for (const name of ['from','to','glossaryId']) if (typeof raw[name] === 'string' && raw[name].length <= 80) value[name] = raw[name];
    if (['none','subtle','strong'].includes(raw.highlight)) value.highlight = raw.highlight;
    if (['blacklist','whitelist'].includes(raw.filterMode)) value.filterMode = raw.filterMode;
    for (const name of ['filterPatterns','plainPatterns']) value[name] = [...new Set((Array.isArray(raw[name]) ? raw[name] : []).filter(validPattern))].slice(0,500);
    return value;
  }
  function effective(settings, url) { return {...settings,style:settings.plainPatterns.some(p => matches(url,p)) ? 'plain' : 'polite'}; }
  function shouldAuto(settings, url) {
    if (!settings.autoTranslate || !/^https?:\/\//.test(url)) return false;
    const listed = settings.filterPatterns.some(p => matches(url,p));
    return !settings.filterEnabled || (settings.filterMode === 'whitelist' ? listed : !listed);
  }
  globalThis.MyPLaMoSettings = {defaults,normalize,validPattern,matches,pageUrl,effective,shouldAuto};
})();
