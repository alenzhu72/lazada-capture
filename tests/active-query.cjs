const fs=require('fs'),vm=require('vm'),assert=require('assert');
const source=fs.readFileSync(require('path').join(__dirname,'../FastProbe.js'),'utf8');
const endpoint='https://acs-m.lazada.sg/h5/mtop.global.detail.web.getdetailinfo/1.0/';
const path='https://www.lazada.sg/products/test-i12-s123.html';
function make(){let callback, requests=0, timers=new Map(), id=0, time=1000;
 const c={URL,URLSearchParams,JSON,Date:{now:()=>time},location:{href:path,protocol:'https:',hostname:'www.lazada.sg'},navigator:{userAgent:'test-browser'},document:{cookie:'session=test-current',body:{innerText:''},querySelector:()=>null,querySelectorAll:()=>[]},MutationObserver:class{observe(){}},XMLHttpRequest:class{open(){}send(){}},setTimeout:f=>{timers.set(++id,f);return id},clearTimeout:i=>timers.delete(i),getComputedStyle:()=>({})};
 c.window=c;c.lib={mtop:{request:(options,ok)=>{requests++;callback=ok; assert.equal(JSON.parse(options.data.cookieParams).session,'test-current');return undefined;}}};vm.createContext(c);vm.runInContext(source,c);
 c.__lh07.capture(endpoint,'data='+encodeURIComponent(JSON.stringify({deviceType:'pc',path,uri:'test-i12-s123',cookieParams:'{}',headerParams:'{}',requestParams:'{}'})));
 c.__lh07.active.enabled=true;
 return {s:c.__lh07,c,requests:()=>requests,reply:max=>callback({ret:['SUCCESS::ok'],data:{module:JSON.stringify({skuInfos:{'123':{skuId:'123',quantity:{limit:{max}}}}})}}),advance:ms=>time+=ms,fail:()=>callback({ret:['FAIL_SYS_USER_VALIDATE']})};}
let results=[];function test(n,f){f();results.push('PASS '+n)}
test('Captured request template matches SKU',()=>assert.equal(make().s.template.sku,'123'));
test('One in-flight request',()=>{let x=make();x.s.query(100);x.s.query(100);assert.equal(x.requests(),1)});
test('Zero inventory and delay after response',()=>{let x=make();x.s.query(100);x.reply(0);assert(!x.s.active.positive);x.s.query(100);assert.equal(x.requests(),1);x.advance(100);x.s.query(100);assert.equal(x.requests(),2)});
test('Positive inventory stops further queries',()=>{let x=make();x.s.query(100);x.reply(2);assert(x.s.active.positive);x.advance(1000);x.s.query(100);assert.equal(x.requests(),1)});
test('Cancelled response ignored',()=>{let x=make();x.s.query(100);x.s.disable();x.reply(2);assert(!x.s.active.positive)});
test('Verification failure pauses',()=>{let x=make();x.s.query(100);x.fail();x.advance(1000);x.s.query(100);assert(x.s.active.stopped);assert.equal(x.requests(),1)});
test('SKU switch refuses old template',()=>{let x=make();x.c.location.href=path.replace('s123','s456');x.s.query(100);assert.equal(x.requests(),0)});
test('Non Lazada domain rejected',()=>{let x=make();x.c.location.hostname='example.org';x.s.query(100);assert(x.s.active.stopped);assert.equal(x.requests(),0)});
test('Missing SDK reports unavailable and stops',()=>{let x=make();x.c.lib={};x.s.query(100);x.advance(15001);x.s.query(100);assert(x.s.active.stopped);assert.equal(x.requests(),0)});
test('Verification page stops before request',()=>{let x=make();x.c.document.body.innerText='Please drag the slider to verify';x.s.query(100);assert(x.s.active.stopped);assert.equal(x.requests(),0)});
console.log(results.join('\n'));


