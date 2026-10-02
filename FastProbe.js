(() => {
  if (window.__lh07) return;
  const norm = s => String(s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = e => { const s = getComputedStyle(e); return e.getClientRects().length > 0 && s.display !== 'none' && s.visibility !== 'hidden' && s.visibility !== 'collapse'; };
  const available = e => !e.disabled && e.getAttribute('aria-disabled') !== 'true' && !e.closest('[inert]');
  const state = window.__lh07 = { api: {}, apiCount: 0, apiError: '', changes: 0, template: null,
    active: {busy:false, stopped:false, enabled:false, count:0, message:'未启用', nextAt:0, positive:false, sku:'', generation:0,waitSince:0} };
  const detailApi = '/mtop.global.detail.web.getdetailinfo/';
  const skuFrom = path => String(path).match(/-s(\d+)\.html/i)?.[1] || '';
  state.capture = (url, body) => {
    if (!String(url).toLowerCase().includes(detailApi) || typeof body !== 'string') return;
    try {
      const data = JSON.parse(new URLSearchParams(body).get('data'));
      if (data?.deviceType !== 'pc' || !data.path || !skuFrom(data.path)) return;
      state.template = { data, sku:skuFrom(data.path), appKey:new URL(url,location.href).searchParams.get('appKey') || '' };
    } catch { }
  };
  state.disable = () => { state.active.enabled=false; state.active.generation++; state.active.positive=false; };
  state.find = name => {
    const aliases = name === 'buy now' ? ['buy now','立即购买'] : name === 'place order now' ? ['place order now','立即下单'] : [name];
    const all = [...document.querySelectorAll('button,[role="button"],input[type="button"],input[type="submit"],a')];
    const matched = all.filter(e => aliases.includes(norm(e.getAttribute('aria-label') || e.innerText || e.value || e.textContent)));
    const shown = matched.filter(visible);
    const enabled = shown.filter(available);
    return { element: enabled.length === 1 ? enabled[0] : null, count: matched.length, visible: shown.length, enabled: enabled.length,
      reason: enabled.length > 1 ? '多个可用同名按钮' : enabled.length === 1 ? '可购买' : shown.length ? '按钮禁用' : matched.length ? '按钮隐藏' : '未出现购买按钮' };
  };
  state.accept = (url, body) => {
    if (!String(url).includes('/mtop.global.detail.web.getdetailinfo/')) return;
    try {
      const data = typeof body === 'string' ? JSON.parse(body) : body;
      if (!Array.isArray(data?.ret) || !data.ret.some(v => String(v).startsWith('SUCCESS'))) { state.apiError = '接口返回非成功状态'; return; }
      const module = typeof data.data?.module === 'string' ? JSON.parse(data.data.module) : data.data?.module;
      if (!module?.skuInfos) return;
      for (const [key, info] of Object.entries(module.skuInfos)) {
        const sku = String(info?.skuId || key);
        const max = info?.quantity?.limit?.max;
        if (typeof max !== 'number' || !Number.isFinite(max)) continue;
        state.api[sku] = { max, text: String(info.quantity.text || ''), at: Date.now() };
      }
      state.apiCount++; state.apiError = '';
    } catch { state.apiError = '接口响应无法解析'; }
  };
  const originalFetch = window.fetch;
  if (originalFetch) window.fetch = function(...args) {
    const requestUrl = typeof args[0] === 'string' ? args[0] : args[0]?.url;
    state.capture(requestUrl, args[1]?.body);
    if (typeof Request !== 'undefined' && args[0] instanceof Request && String(requestUrl).includes(detailApi)) {
      try { args[0].clone().text().then(body=>state.capture(requestUrl,body)).catch(()=>{}); } catch { }
    }
    const result = originalFetch.apply(this, args);
    result.then(response => {
      if (response.url.includes('/mtop.global.detail.web.getdetailinfo/')) response.clone().text().then(body => state.accept(response.url,body)).catch(() => {});
    }).catch(() => {});
    return result;
  };
  const send = XMLHttpRequest.prototype.send;
  const xhrOpen = XMLHttpRequest.prototype.open, xhrUrls = new WeakMap();
  XMLHttpRequest.prototype.open = function(method,url,...rest) { xhrUrls.set(this,String(url)); return xhrOpen.call(this,method,url,...rest); };
  XMLHttpRequest.prototype.send = function(...args) {
    state.capture(xhrUrls.get(this), args[0]);
    this.addEventListener('load', () => {
      try {
        if (this.responseURL.includes('/mtop.global.detail.web.getdetailinfo/')) state.accept(this.responseURL, this.responseType === 'json' ? this.response : this.responseText);
      } catch { state.apiError = '接口响应不可读取'; }
    }, { once: true });
    return send.apply(this,args);
  };
  state.query = interval => {
    const a=state.active, sku=skuFrom(location.href);
    if (!a.enabled || a.stopped || a.busy || a.positive || Date.now()<a.nextAt) return;
    if (!a.waitSince) a.waitSince=Date.now();
    const blocked=!!document.querySelector('script[src*="sufei-punish"]') || /please drag the slider to verify|slide to verify|verify you are human/i.test(document.body?.innerText||'');
    if(blocked){a.message='页面要求验证，请手动完成后重新打开商品并启动';a.stopped=true;return;}
    if (location.protocol !== 'https:' || !(location.hostname === 'lazada.sg' || location.hostname.endsWith('.lazada.sg'))) { a.message='主动查询只支持Lazada新加坡站'; a.stopped=true; return; }
    if (!sku) { a.message='请进入包含SKU的商品详情页'; return; }
    if (!state.template || state.template.sku!==sku) { a.message='等待页面原始详情请求；请重新打开商品以捕获模板';if(Date.now()-a.waitSince>15000)a.stopped=true;return; }
    const sdk=window.lib?.mtop || window.mtop;
    if (typeof sdk?.request !== 'function') { a.message='页面未暴露MTop请求组件，无法主动查询';if(Date.now()-a.waitSince>15000)a.stopped=true;return; }
    const data=JSON.parse(JSON.stringify(state.template.data));
    // Read current same-site cookies in memory; never persist or log credential values.
    const current={}; document.cookie.split(';').forEach(part=>{const i=part.indexOf('=');if(i>0)current[part.slice(0,i).trim()]=part.slice(i+1);});
    data.cookieParams=JSON.stringify(current);
    data.headerParams=JSON.stringify({'user-agent':navigator.userAgent});
    a.busy=true; a.sku=sku; a.message='查询中'; a.count++;
    const generation=a.generation, started=Date.now(); let settled=false;
    const finish = (result,failed=false) => {
      if(settled)return; settled=true;
      clearTimeout(timeout); a.busy=false; a.nextAt=Date.now()+Math.max(100,interval); a.duration=Date.now()-started;
      if(!a.enabled || a.generation!==generation || skuFrom(location.href)!==sku)return;
      try {
        if (failed || !Array.isArray(result?.ret) || !result.ret.some(v=>String(v).startsWith('SUCCESS'))) { a.stopped=true;a.message='查询失败、会话过期或需验证；已暂停主动查询';return; }
        const module=typeof result.data?.module==='string'?JSON.parse(result.data.module):result.data?.module;
        const info=module?.skuInfos?.[sku] || Object.values(module?.skuInfos||{}).find(x=>String(x?.skuId)===sku);
        const max=info?.quantity?.limit?.max;
        if(typeof max!=='number' || !Number.isFinite(max)){a.stopped=true;a.message='响应缺少当前SKU的可购数量';return;}
        state.accept('https://acs-m.lazada.sg/h5'+detailApi+'1.0/',result);
        a.positive=max>0; a.message=max>0?'主动查询发现有货':'缺货（可购上限0）';
      } catch { a.stopped=true; a.message='库存响应解析失败'; }
    };
    const timeout=setTimeout(()=>finish(null,true),6500);
    try {
      const request={api:'mtop.global.detail.web.getDetailInfo',v:'1.0',type:'POST',dataType:'json',timeout:5000,isSec:1,AntiCreep:true,sessionOption:'AutoLoginOnly',data};
      if(state.template.appKey)request.appKey=state.template.appKey;
      const promise=sdk.request(request,r=>finish(r),r=>finish(r,true));
      if(promise?.then)promise.then(r=>finish(r),r=>finish(r,true));
    } catch { finish(null,true); }
  };
  state.read = (name,sku) => {
    const currentSku = sku || location.href.match(/-s(\d+)\.html/i)?.[1] || '';
    const hit = state.find(name), api = state.api[currentSku];
    return { ...hit, url: location.href, title: document.querySelector('h1')?.innerText || '', changes: state.changes,
      apiCount: state.apiCount, apiError: state.apiError, apiMax: api?.max ?? -1,
      activePositive: state.active.positive, activeBusy: state.active.busy, activeCount: state.active.count,
      activeStopped: state.active.stopped, activeMessage:state.active.message, activeDuration:state.active.duration || 0,
      apiAge: api ? Date.now()-api.at : -1, apiAvailable: !!api && Date.now()-api.at < 15000 && api.max > 0 };
  };
  // Cache mutation timing for diagnostics; detection is a single synchronous page query.
  new MutationObserver(() => { state.changes++; }).observe(document, {subtree:true, childList:true, attributes:true, characterData:true});
})();
