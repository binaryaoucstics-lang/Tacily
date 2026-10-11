"use strict";
const test=require("node:test"),assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm"),crypto=require("node:crypto");
const tick=()=>new Promise(resolve=>setImmediate(resolve));
function event(){const listeners=[];return{addListener:listener=>listeners.push(listener),emit:(...args)=>listeners.map(listener=>listener(...args))};}
function deferred(){let resolve;const promise=new Promise(r=>{resolve=r;});return{promise,resolve};}
function fixture(options={}){
  const nativeMessages=[],tabMessages=[],timers=new Set(),token="01234567-89ab-4cde-8123-0123456789ab";
  const tabs=new Map([[11,{id:11,windowId:7,active:true,url:"https://reader.example/private?url-secret=1",title:"private-title-secret"}],
    [12,{id:12,windowId:7,active:false,url:"https://other.example/?private=2",title:"other-private-title"}]]);
  const tokens=new Map([[11,token],[12,"22222222-2222-4222-8222-222222222222"]]);
  const image={found:true,imageId:"image-1",documentToken:token,autoImageAllowed:true,natural:{width:850,height:1200},
    display:{left:0,top:0,width:425,height:600},clip:{left:0,top:0,width:425,height:600},viewportSize:{width:1280,height:720},devicePixelRatio:1.25};
  const port={onMessage:event(),onDisconnect:event(),postMessage:message=>{nativeMessages.push(message);}};
  const chrome={runtime:{id:"extension",lastError:null,connectNative:()=>port,onMessage:event()},declarativeNetRequest:{},
    tabs:{onActivated:event(),onRemoved:event(),onUpdated:event(),get:async id=>{if(options.getTab)await options.getTab(id);const tab=tabs.get(id);if(!tab)throw new Error("closed");const snapshot={...tab};options.afterGet?.(id);return snapshot;},
      query:async query=>[...tabs.values()].filter(tab=>(!query?.active||tab.active)&&(!Number.isInteger(query?.windowId)||tab.windowId===query.windowId)),
      sendMessage:async(id,message,settings)=>{
        tabMessages.push({id,message,settings});
        if(message.type==="queryDocumentState")return{documentToken:tokens.get(id),devicePixelRatio:1.25,viewportSize:image.viewportSize};
        if(message.type==="queryImagePreview")return options.crossOrigin?{...image,url:"https://cdn.example/protected.png"}:{...image,previewBase64:"QUJDRA=="};
        if(message.type==="queryImageGeometry")return{...image};
        return{found:true};
      }},windows:{WINDOW_ID_NONE:-1,onFocusChanged:event(),getLastFocused:async()=>({id:7,left:0,top:0,width:1280,height:800}),
        get:async()=>({id:7,left:0,top:0,width:1280,height:800})}};
  let context;
  context=vm.createContext({chrome,navigator:{userAgent:options.edge?"Edg/154":"Chrome/154"},URL,AbortSignal,Blob,Uint8Array,
    crypto:crypto.webcrypto,TextEncoder,
    btoa:input=>Buffer.from(input,"binary").toString("base64"),
    fetch:options.fetch|| (async()=>{throw new Error("Unexpected network");}),
    createImageBitmap:async()=>({width:850,height:1200,close(){}}),
    OffscreenCanvas:class{getContext(){return{fillRect(){},drawImage(){}};}async convertToBlob(){return new Blob([Buffer.from("JPEG")],{type:"image/jpeg"});}},
    setTimeout:(fn,ms)=>{const timer=setTimeout(fn,ms);timer.unref();timers.add(timer);return timer;},clearTimeout:timer=>{clearTimeout(timer);timers.delete(timer);},
    importScripts:(...files)=>files.forEach(file=>vm.runInContext(fs.readFileSync(path.join(__dirname,"..",file),"utf8"),context))});
  vm.runInContext(fs.readFileSync(path.join(__dirname,"..","background.js"),"utf8"),context);
  const sender={tab:tabs.get(11),frameId:0};
  const candidate={type:"pageAutoCandidate",requestId:"page-request-1",documentToken:token,generation:1,revision:1,
    blocks:[{id:"block-1",text:"This public article explains a simple example for readers.",context:"Public article",groupId:"paragraph-1"}],
    samples:[{text:"This public article explains a simple example for readers.",kind:"body"}],
    pageSummary:{totalCharacters:56,hanCharacters:0,kanaCharacters:0,latinCharacters:45,hangulCharacters:0,
      hanProseCharacters:0,latinProseCharacters:45,kanaProseCharacters:0,proseBlocks:1,sourceHash:"abc12345"},documentLanguage:"en"};
  function send(message,senderOverride=sender){let result;const returns=chrome.runtime.onMessage.emit(message,senderOverride,value=>{result=value;});return{returns,get result(){return result;}};}
  function configure(overrides={}){port.onMessage.emit({type:"pageAutoConfig",enabled:true,targetLanguage:"zh-CN",generation:1,judgementMode:"Hybrid",concurrency:3,...overrides});}
  function reply(overrides={}){port.onMessage.emit({type:"pageAutoTranslations",requestId:candidate.requestId,tabId:11,documentToken:token,generation:1,revision:1,
    updates:[{id:"block-1",text:"这篇公开文章说明了一个简单示例。",complete:false}],...overrides});}
  return{nativeMessages,tabMessages,port,chrome,tabs,tokens,token,image,sender,candidate,send,configure,reply,
    sendAsync:(message,senderOverride=sender)=>new Promise(resolve=>chrome.runtime.onMessage.emit(message,senderOverride,resolve)),
    dispose:()=>{for(const timer of timers)clearTimeout(timer);}};
}

test("blacklisted page queries and auto candidates are blocked while explicit selection remains admitted", async t => {
  const f = fixture(); t.after(f.dispose); await tick(); f.configure({ manualAvailable: true, siteBlacklist: ["reader.example"] }); await tick();
  const config = f.send({ type: "queryPageAutoConfig" }).result;
  assert.equal(config.enabled, false); assert.equal(config.manualAvailable, true); assert.equal(config.quickSettings.siteMode, "block");
  assert.equal((await f.sendAsync(f.candidate)).accepted, false);
  assert.equal(f.nativeMessages.some(message => message.type === "pageAutoCandidate"), false);
  assert.equal((await f.sendAsync({ ...f.candidate, requestId: "manual", manualMode: "selection" })).accepted, true);
  const sent = f.nativeMessages.find(message => message.type === "pageAutoCandidate"); assert.equal(sent.siteHost, "reader.example");
});

test("site rule specificity and global quick changes propagate to current tab config", async t => {
  const f = fixture(); t.after(f.dispose); await tick(); f.tabs.get(11).url = "https://news.example.com/content";
  f.configure({ manualAvailable: true, siteBlacklist: ["example.com"], siteWhitelist: ["news.example.com"], autoImages: true, concurrency: 4 }); await tick();
  const config = f.send({ type: "queryPageAutoConfig" }).result;
  assert.equal(config.enabled, true); assert.equal(config.siteMode, "allow"); assert.equal(config.quickSettings.host, "news.example.com");
  assert.equal(config.quickSettings.autoImages, true); assert.equal(config.quickSettings.concurrency, 4); assert.equal(config.siteBlacklist, undefined);
});

test("quick settings route uses browser-owned hostname and acknowledges the native result", async t => {
  const f = fixture(); t.after(f.dispose); await tick(); f.configure({ manualAvailable: true }); await tick();
  const request = f.send({ type: "browserQuickSettings", requestId: "quick", generation: 1, documentToken: f.token, siteHost: "forged.example", changes: { siteMode: "allow" } });
  await tick(); await tick(); const sent = f.nativeMessages.find(message => message.type === "browserQuickSettings");
  assert.equal(sent.siteHost, "reader.example"); assert.equal(request.result, undefined);
  f.configure({ manualAvailable: true, siteWhitelist: ["reader.example"] });
  f.port.onMessage.emit({ type: "browserQuickSettingsResult", requestId: "quick", tabId: 11, documentToken: f.token, ok: true });
  await tick(); await tick(); assert.equal(request.result.ok, true); assert.equal(request.result.settings.siteMode, "allow");
});
test("native page config keeps inactive Chrome documents enabled with a separate admission flag",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({concurrency:99});await tick();
  const active=f.tabMessages.find(m=>m.id===11&&m.message.type==="pageAutoConfig"),inactive=f.tabMessages.find(m=>m.id===12&&m.message.type==="pageAutoConfig");
  assert.equal(active.settings.frameId,0);assert.equal(active.message.enabled,true);assert.equal(active.message.concurrency,10);
  assert.equal(inactive.message.enabled,true);assert.equal(inactive.message.active,false);
  const subframe=f.send({type:"queryPageAutoConfig"},{...f.sender,frameId:1});assert.equal(subframe.result.enabled,false);
  const edge=fixture({edge:true});t.after(edge.dispose);edge.configure();await tick();assert.equal(edge.send({type:"queryPageAutoConfig"}).result.enabled,false);
});

test("loading with the same document token keeps admitted text streaming across fragment navigation",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();await tick();
  f.send(f.candidate);await tick();await tick();
  f.reply();await tick();
  const before=f.nativeMessages.length;
  f.tabs.get(11).url='https://reader.example/private?url-secret=1#pricing';
  f.chrome.tabs.onUpdated.emit(11,{status:'loading',url:f.tabs.get(11).url},f.tabs.get(11));
  await tick();await tick();
  assert.equal(f.nativeMessages.slice(before).some(p=>p.type==='invalidated'||p.type==='pageAutoCancel'),false);
  f.reply({finished:true,updates:[{id:'block-1',text:'完整译文',complete:true}]});await tick();await tick();
  assert.equal(f.tabMessages.some(p=>p.message.type==='pageAutoTranslations'&&p.message.finished&&p.message.updates[0]?.text==='完整译文'),true);
});

test("URL-only SPA pagination notifies top-frame content without retiring its native document or forwarding its URL",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();await tick();
  f.send(f.candidate);await tick();await tick();
  const nativeBefore=f.nativeMessages.length,tabBefore=f.tabMessages.length;
  f.tabs.get(11).url="https://reader.example/spaces?p=3&private-query=example";
  f.chrome.tabs.onUpdated.emit(11,{url:f.tabs.get(11).url},f.tabs.get(11));await tick();await tick();
  const notification=f.tabMessages.slice(tabBefore).find(p=>p.message.type==="pageAutoNavigation");
  assert.deepEqual(JSON.parse(JSON.stringify(notification)),{id:11,message:{type:"pageAutoNavigation"},settings:{frameId:0}});
  assert.equal(f.nativeMessages.length,nativeBefore);
  f.reply({finished:true,updates:[{id:"block-1",text:"仍有效的当前任务结果",complete:true}]});await tick();await tick();
  assert.equal(f.tabMessages.some(p=>p.message.type==="pageAutoTranslations"&&p.message.finished),true);
  assert.equal(JSON.stringify(f.nativeMessages).includes("private-query"),false);
});

test("reload is retired when the new document commits even if loading briefly queried the old token",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();await tick();
  f.send(f.candidate);await tick();await tick();
  f.chrome.tabs.onUpdated.emit(11,{status:'loading'},f.tabs.get(11));await tick();
  f.tokens.set(11,'33333333-3333-4333-8333-333333333333');
  f.chrome.tabs.onUpdated.emit(11,{status:'complete'},f.tabs.get(11));await tick();await tick();
  assert.equal(f.nativeMessages.some(p=>p.type==='invalidated'&&p.reason==='navigationStarted'),true);
  const count=f.tabMessages.filter(p=>p.message.type==='pageAutoTranslations').length;
  f.reply({finished:true});await tick();await tick();
  assert.equal(f.tabMessages.filter(p=>p.message.type==='pageAutoTranslations').length,count);
});

test("opaque cache scope and explicit clearing epoch reach active and inactive content config",async t=>{
  const f=fixture();t.after(f.dispose);await tick();const cacheScope="ab".repeat(32);
  f.configure({cacheScope,cacheEpoch:3});await tick();
  for(const id of [11,12]){
    const packet=f.tabMessages.find(entry=>entry.id===id&&entry.message.type==="pageAutoConfig").message;
    assert.equal(packet.cacheScope,cacheScope);assert.equal(packet.cacheEpoch,3);
  }
  const queried=f.send({type:"queryPageAutoConfig"}).result;
  assert.equal(queried.cacheScope,cacheScope);assert.equal(queried.cacheEpoch,3);
  f.configure({generation:2,cacheScope,cacheEpoch:4});await tick();
  assert.equal(f.send({type:"queryPageAutoConfig"}).result.cacheEpoch,4);
});

test("malformed cache identity cannot replace a valid config or carry source/provider secrets",async t=>{
  const f=fixture();t.after(f.dispose);await tick();const cacheScope="cd".repeat(32);
  f.configure({cacheScope,cacheEpoch:2});await tick();
  for(const change of [{cacheScope:"https://private-provider.example/?key=secret"},{cacheScope:"a".repeat(63)},
    {cacheScope:45},{cacheEpoch:-1},{cacheEpoch:Number.MAX_SAFE_INTEGER+1},{cacheEpoch:"3"}]){
    f.configure({generation:2,cacheScope,cacheEpoch:2,...change});await tick();
    const packet=f.send({type:"queryPageAutoConfig"}).result;
    assert.equal(packet.generation,1);assert.equal(packet.cacheScope,cacheScope);assert.equal(packet.cacheEpoch,2);
  }
});

test("manual capability requires a live Chrome config and remains independent from the automatic master",async t=>{
  const f=fixture();t.after(f.dispose);await tick();
  const request={...f.candidate,manualMode:"selection",samples:undefined};
  const before=f.send(request);await tick();assert.equal(before.result.accepted,false);
  f.configure({enabled:false,manualAvailable:true});await tick();
  const config=f.send({type:"queryPageAutoConfig"}).result;
  assert.equal(config.enabled,false);assert.equal(config.manualAvailable,true);
  assert.equal(f.send({type:"queryPageAutoConfig"},{...f.sender,frameId:1}).result.manualAvailable,false);
  const accepted=f.send(request);await tick();await tick();assert.equal(accepted.result.accepted,true);
  assert.equal(f.nativeMessages.find(packet=>packet.type==="pageAutoCandidate").manualMode,"selection");
  const edge=fixture({edge:true});t.after(edge.dispose);await tick();edge.configure({enabled:false,manualAvailable:true});await tick();
  assert.equal(edge.send({type:"queryPageAutoConfig"}).result.manualAvailable,false);
});

test("manual page streams and completes while automatic translation is disabled, retaining known IDs and window binding",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  const accepted=f.send({...f.candidate,manualMode:"page",samples:undefined});await tick();await tick();assert.equal(accepted.result.accepted,true);
  f.reply({manualMode:"page",browserWindowId:99});await tick();
  f.reply({manualMode:"selection",browserWindowId:7});await tick();
  assert.equal(f.tabMessages.some(packet=>packet.message.type==="pageAutoTranslations"),false);
  f.reply({manualMode:"page",browserWindowId:7,updates:[{id:"block-1",text:"流式译文",complete:false},{id:"unknown-id",text:"非法节点",complete:true}]});await tick();
  const prefix=f.tabMessages.find(packet=>packet.message.type==="pageAutoTranslations").message;
  assert.equal(prefix.manualMode,"page");assert.equal(prefix.updates.length,1);assert.equal(prefix.updates[0].text,"流式译文");
  f.tabs.get(11).active=false;
  f.reply({manualMode:"page",browserWindowId:7,finished:true,updates:[{id:"block-1",text:"完整译文",complete:true}]});await tick();
  assert.equal(f.tabMessages.filter(packet=>packet.message.type==="pageAutoTranslations").length,2);
});

test("manual assessment can never authorize automatic image translation",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  f.send({...f.candidate,manualMode:"selection"});await tick();await tick();
  f.port.onMessage.emit({type:"pageAutoAssessment",requestId:f.candidate.requestId,tabId:11,browserWindowId:7,
    documentToken:f.token,generation:1,revision:1,manualMode:"selection",needsTranslation:true,allowImages:true});await tick();
  const packet=f.tabMessages.find(entry=>entry.message.type==="pageAutoAssessment").message;
  assert.equal(packet.allowImages,false);assert.equal(packet.manualMode,"selection");
});

test("manual requests keep all admission guards and cannot escape with an unknown mode or oversized identity",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  const changes=[{manualMode:"everything"},{generation:0},{revision:0},{documentToken:"old-document-token"},
    {blocks:[{id:"x".repeat(129),text:"A foreign sentence worth translating."}]},
    {blocks:[{id:"same",text:"English source"},{id:"same",text:"Another source"}]},
    {blocks:[{id:"block-1",text:"x".repeat(4097)}]}, {requestId:"x".repeat(65)}];
  for(let index=0;index<changes.length;index++){
    const request=f.send({...f.candidate,manualMode:"selection",requestId:`manual-invalid-${index}`,...changes[index]});await tick();await tick();
    assert.equal(request.result.accepted,false,`case ${index}`);
  }
  const subframe=f.send({...f.candidate,manualMode:"selection"},{...f.sender,frameId:2});await tick();assert.equal(subframe.result.accepted,false);
  f.tabs.get(11).active=false;const hidden=f.send({...f.candidate,manualMode:"selection"});await tick();assert.equal(hidden.result.accepted,false);
  assert.equal(f.nativeMessages.some(packet=>packet.type==="pageAutoCandidate"),false);
});

test("explicit manual request cancellation works while automatic translation is disabled and blocks late output",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  f.send({...f.candidate,manualMode:"selection"});await tick();await tick();
  f.send({type:"pageAutoCancel",requestId:f.candidate.requestId,documentToken:f.token,generation:1,revision:1});await tick();
  const cancellation=f.nativeMessages.find(packet=>packet.type==="pageAutoCancel");
  assert.equal(cancellation.requestId,f.candidate.requestId);assert.equal(cancellation.documentToken,f.token);
  f.reply({manualMode:"selection"});await tick();assert.equal(f.tabMessages.some(packet=>packet.message.type==="pageAutoTranslations"),false);
});

test("bridge disconnect removes manual capability and prevents pending manual results",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  f.send({...f.candidate,manualMode:"page"});await tick();await tick();
  f.port.onDisconnect.emit();await tick();
  assert.equal(f.send({type:"queryPageAutoConfig"}).result.manualAvailable,false);
  f.reply({manualMode:"page"});await tick();assert.equal(f.tabMessages.some(packet=>packet.message.type==="pageAutoTranslations"),false);
});

test("changing only the automatic admission master preserves an accepted manual stream and its final result",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({manualAvailable:true});await tick();
  f.send({...f.candidate,manualMode:"page"});await tick();await tick();
  f.reply({manualMode:"page",updates:[{id:"block-1",text:"译文前缀",complete:false}]});await tick();
  f.configure({enabled:false,manualAvailable:true});await tick();
  assert.equal(f.send({type:"queryPageAutoConfig"}).result.enabled,false);
  f.reply({manualMode:"page",finished:true,updates:[{id:"block-1",text:"完整译文",complete:true}]});await tick();
  const frames=f.tabMessages.filter(entry=>entry.message.type==="pageAutoTranslations");
  assert.equal(frames.length,2);assert.equal(frames[1].message.updates[0].complete,true);
  assert.equal(f.nativeMessages.filter(message=>message.type==="pageAutoCandidate").length,1);
});

test("cache scope changes still retire accepted manual work before late results can reach content",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true,cacheScope:"ab".repeat(32)});await tick();
  f.send({...f.candidate,manualMode:"page"});await tick();await tick();
  f.configure({enabled:false,manualAvailable:true,cacheScope:"cd".repeat(32)});await tick();
  f.reply({manualMode:"page",finished:true});await tick();
  assert.equal(f.tabMessages.some(entry=>entry.message.type==="pageAutoTranslations"),false);
});

test("bounded semantic source group metadata reaches native without adding full paragraph source",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  const source="A selected foreign sentence.";
  const request=f.send({...f.candidate,manualMode:"selection",blocks:[{id:"selected-1",text:source,groupId:"paragraph-1",
    sourceGroupHash:"AB".repeat(32),sourceGroupLength:256,sourceStart:12,sourceLength:source.length,
    fullParagraph:"private-unselected-source-should-not-be-forwarded"}]});await tick();await tick();assert.equal(request.result.accepted,true);
  const block=f.nativeMessages.find(message=>message.type==="pageAutoCandidate").blocks[0];
  assert.equal(block.sourceGroupHash,"ab".repeat(32));assert.equal(block.sourceStart,12);
  assert.equal(block.sourceLength,source.length);assert.equal(block.sourceGroupLength,256);
  assert.equal(JSON.stringify(block).includes("private-unselected-source"),false);
});

test("source group metadata is optional as a whole but malformed or partial ranges are rejected",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();await tick();
  const block={id:"block-1",text:"Foreign sentence",sourceGroupHash:"ab".repeat(32),sourceGroupLength:128,sourceStart:10,sourceLength:16};
  const changes=[{sourceGroupHash:undefined},{sourceGroupHash:"a".repeat(63)},{sourceGroupLength:undefined},
    {sourceGroupLength:1000001},{sourceStart:-1},{sourceStart:120},{sourceLength:15},{sourceLength:"16"},{sourceGroupLength:NaN}];
  for(let index=0;index<changes.length;index++){
    const request=f.send({...f.candidate,requestId:`bad-source-${index}`,blocks:[{...block,...changes[index]}]});await tick();await tick();
    assert.equal(request.result.accepted,false,`range ${index}`);
  }
  const plain={id:"block-1",text:"Foreign sentence"};
  const explicitNulls=[{sourceGroupHash:null},{sourceGroupLength:null},{sourceStart:null},{sourceLength:null},
    {sourceGroupHash:null,sourceGroupLength:null,sourceStart:null,sourceLength:null},
    {sourceGroupHash:undefined},{sourceGroupHash:"ab".repeat(32)}];
  for(let index=0;index<explicitNulls.length;index++){
    const request=f.send({...f.candidate,requestId:`null-source-${index}`,blocks:[{...plain,...explicitNulls[index]}]});await tick();await tick();
    assert.equal(request.result.accepted,false,`explicit metadata ${index}`);
  }
  assert.equal(f.nativeMessages.some(message=>message.type==="pageAutoCandidate"),false);
});

test("selection foreign Han context is a strict boolean flag and sends no unselected parent text",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  for(const [index,value] of [true,false].entries()){
    const result=f.send({...f.candidate,requestId:`han-${index}`,manualMode:"selection",blocks:[{id:"han-link",text:"東京都",
      foreignHanContext:value,parentSource:"この親段落は送信しないでください"}]});await tick();await tick();assert.equal(result.result.accepted,true);
    const block=f.nativeMessages.filter(message=>message.type==="pageAutoCandidate").at(-1).blocks[0];
    assert.equal(block.foreignHanContext===true,value);assert.equal("parentSource" in block,false);
    f.reply({requestId:`han-${index}`,manualMode:"selection",finished:true});await tick();
  }
  for(const [index,value] of [null,"true",1,0,{},[]].entries()){
    const result=f.send({...f.candidate,requestId:`invalid-han-${index}`,manualMode:"selection",blocks:[{id:"han-link",text:"東京都",foreignHanContext:value}]});
    await tick();assert.equal(result.result.accepted,false);
  }
  assert.equal(f.nativeMessages.filter(message=>message.type==="pageAutoCandidate").length,2);
});

test("source and selection context kind markers reach native and explicit null or invalid markers are rejected",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  for(const [index,value] of ["source","selection"].entries()){
    const request=f.send({...f.candidate,manualMode:"selection",requestId:`context-${index}`,
      blocks:[{id:"block-1",text:"Authored source",context:"Authored source",contextKind:value}]});await tick();await tick();
    assert.equal(request.result.accepted,true);
    const outgoing=f.nativeMessages.filter(message=>message.type==="pageAutoCandidate").at(-1);
    assert.equal(outgoing.blocks[0].contextKind,value);
    f.reply({requestId:`context-${index}`,manualMode:"selection",finished:true});await tick();
  }
  for(const [index,value] of [null,undefined,"independent","Source",1,true,{},[]].entries()){
    const request=f.send({...f.candidate,manualMode:"selection",requestId:`invalid-context-${index}`,
      blocks:[{id:"block-1",text:"Authored source",contextKind:value}]});await tick();await tick();
    assert.equal(request.result.accepted,false,`context marker ${index}`);
  }
  assert.equal(f.nativeMessages.filter(message=>message.type==="pageAutoCandidate").length,2);
});

test("HTTP source hash fallback uses secure local WebCrypto and emits neither raw source nor a native request",async t=>{
  const f=fixture();t.after(f.dispose);await tick();
  const text="This local source contains 日本語 and 🦊.";
  const result=await f.sendAsync({type:"hashPageSource",documentToken:f.token,text,generation:0,
    sourceUrl:"https://private-reader.example/",apiKey:"must-not-leave-local"});
  assert.equal(result.hash,crypto.createHash("sha256").update(text,"utf8").digest("hex"));
  assert.equal(Object.keys(result).length,1);
  assert.equal(f.nativeMessages.some(message=>JSON.stringify(message).includes(text)),false);
  assert.equal(f.nativeMessages.some(message=>message.type==="hashPageSource"),false);
});

test("local source hash fallback rejects malformed payloads and unbound frame document generation identities",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure({enabled:false,manualAvailable:true});await tick();
  const base={type:"hashPageSource",documentToken:f.token,text:"A local safe source",generation:1};
  const changes=[{text:undefined},{text:null},{text:42},{text:{}},{text:[]},{text:""},{text:"x".repeat(1000001)},
    {documentToken:"old-document-token"},{generation:0},{generation:"1"}];
  for(const changed of changes){
    const result=await f.sendAsync({...base,...changed});assert.equal(result.hash,null);
  }
  assert.equal((await f.sendAsync(base,{...f.sender,frameId:2})).hash,null);
  const wrongWindow={...f.sender,tab:{...f.sender.tab,windowId:99}};
  assert.equal((await f.sendAsync(base,wrongWindow)).hash,null);
  f.tabs.get(11).active=false;
  assert.equal((await f.sendAsync(base)).hash,null);
  assert.equal(f.nativeMessages.some(message=>message.type==="hashPageSource"||message.type==="pageAutoCandidate"),false);
});
test("native candidate accepted ACK is immediate and never waits for translation",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();const request=f.send(f.candidate);assert.equal(request.returns[0],true);await tick();
  assert.equal(request.result.accepted,true);assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCandidate").length,1);
  assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,0);
  f.reply();await tick();assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,1);
});
test("duplicate config preserves accepted pending request and unknown translation IDs are removed",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();const request=f.send(f.candidate);await tick();assert.equal(request.result.accepted,true);
  f.configure({judgementMode:"hybrid"});await tick();f.configure({enabled:false,generation:0});
  f.reply({updates:[{id:"block-1",text:"公开文字。",complete:false},{id:"unknown-id",text:"Do not write this",complete:true}]});await tick();
  const delivered=f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations");assert.equal(delivered.length,1);
  assert.deepEqual(Array.from(delivered[0].message.updates,u=>u.id),["block-1"]);
});

test("source revisions retire old routing entries even when a late cancel arrives after the new revision",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();
  for(let revision=1;revision<=12;revision++){
    const accepted=f.send({...f.candidate,requestId:`revision-${revision}`,revision});await tick();await tick();
    assert.equal(accepted.result.accepted,true,`revision ${revision}`);
    if(revision>1)f.send({type:"pageAutoCancel",documentToken:f.token,generation:1,revision:revision-1,requestId:`revision-${revision-1}`});
  }
  assert.equal(f.nativeMessages.filter(message=>message.type==="pageAutoCandidate").length,12);
  f.reply({requestId:"revision-1",revision:1});await tick();
  assert.equal(f.tabMessages.filter(entry=>entry.message.type==="pageAutoTranslations").length,0);
  f.reply({requestId:"revision-12",revision:12});await tick();
  assert.equal(f.tabMessages.filter(entry=>entry.message.type==="pageAutoTranslations").length,1);
});
test("block/sample budgets and duplicate IDs reject before native forwarding",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();
  const changes=[{blocks:Array.from({length:49},(_,i)=>({id:`b${i}`,text:"readable text"}))},
    {blocks:[{id:"b",text:"x".repeat(4097)}]},{blocks:[{id:"b",text:"visible",context:"x".repeat(513)}]},
    {blocks:Array.from({length:9},(_,i)=>({id:`b${i}`,text:"x".repeat(4000)}))},
    {blocks:[{id:"same",text:"One"},{id:"same",text:"Two"}]},
    {samples:Array.from({length:65},()=>({text:"text",kind:"body"}))},
    {samples:Array.from({length:4},()=>({text:"x".repeat(4096),kind:"body"}))}];
  for(let i=0;i<changes.length;i++){const request=f.send({...f.candidate,...changes[i],requestId:`budget-${i}`});await tick();assert.equal(request.result.accepted,false,`budget ${i}`);}
  assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCandidate").length,0);
});
test("candidate wire projection contains no URL/title/password metadata from tab or nested extras",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();
  const request=f.send({...f.candidate,url:"url-secret",title:"private-title-secret",password:"password-secret",
    blocks:[{...f.candidate.blocks[0],url:"url-secret",title:"private-title-secret",password:"password-secret"}],
    samples:[{...f.candidate.samples[0],password:"password-secret",url:"url-secret"}],
    pageSummary:{...f.candidate.pageSummary,url:"url-secret",title:"private-title-secret",password:"password-secret"}});await tick();
  assert.equal(request.result.accepted,true);const outgoing=f.nativeMessages.find(m=>m.type==="pageAutoCandidate");
  const encoded=JSON.stringify(outgoing);assert.equal(/url-secret|private-title-secret|password-secret/.test(encoded),false);
  assert.equal(outgoing.blocks[0].text,f.candidate.blocks[0].text);assert.equal(outgoing.pageSummary.sourceHash,"abc12345");
});
test("document language hint and summary fingerprint cannot carry page URL/password metadata",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();
  const request=f.send({...f.candidate,documentLanguage:"https://private-language.example/?password-secret=1",
    pageSummary:{...f.candidate.pageSummary,sourceHash:"https://private-hash.example/?password-secret=1"}});await tick();
  const forwarded=f.nativeMessages.filter(m=>m.type==="pageAutoCandidate");
  assert.equal(forwarded.some(m=>/https:\/\/|password-secret/.test(JSON.stringify(m))),false);
  assert.ok(request.result);
});
test("malformed or over-budget page summary rejects before native forwarding",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();
  for(const [index,summary] of ["not-summary",{...f.candidate.pageSummary,totalCharacters:-1},
    {...f.candidate.pageSummary,latinCharacters:Number.POSITIVE_INFINITY},{...f.candidate.pageSummary,sourceHash:"x".repeat(1024)}].entries()){
    const request=f.send({...f.candidate,requestId:`summary-${index}`,pageSummary:summary});await tick();assert.equal(request.result.accepted,false,`summary ${index}`);
  }
  assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCandidate").length,0);
});
test("optional summary is omitted on the wire, preserving the nullable native protocol",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();const request=f.send({...f.candidate,pageSummary:null});await tick();
  assert.equal(request.result.accepted,true);const outgoing=f.nativeMessages.find(m=>m.type==="pageAutoCandidate");
  assert.equal(outgoing.pageSummary,undefined);assert.equal(JSON.stringify(outgoing).includes('"pageSummary"'),false);
});
test("accepted hidden document receives results while stale generation/revision and true navigation remain rejected",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();f.send(f.candidate);await tick();
  for(const change of [{generation:0},{revision:0},{documentToken:"old-document"},{tabId:12}]){f.reply(change);await tick();}
  assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,0);
  f.send({...f.candidate,requestId:"new-source",revision:2,blocks:[{id:"new-block",text:"This source text changed after a page mutation."}]});await tick();
  f.reply();await tick();assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,0);
  f.tabs.get(11).active=false;f.reply({requestId:"new-source",revision:2,updates:[{id:"new-block",text:"新文本",complete:false}]});await tick();
  assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,1);
  f.tabs.get(11).active=true;f.tokens.set(11,"33333333-3333-4333-8333-333333333333");f.chrome.tabs.onUpdated.emit(11,{status:"loading"},f.tabs.get(11));
  f.reply({requestId:"new-source",revision:2,updates:[{id:"new-block",text:"新文本",complete:false}]});await tick();
  assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,1);
});
test("concurrent identical request ID is accepted once across document-await boundary",async t=>{
  const wait=deferred(),f=fixture({getTab:()=>wait.promise});t.after(f.dispose);await tick();f.configure();
  const first=f.send(f.candidate),second=f.send(f.candidate);wait.resolve();await tick();await tick();
  assert.equal([first.result.accepted,second.result.accepted].filter(Boolean).length,1);
  assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCandidate").length,1);
});
test("duplicate semantic config during accept await retains reservation and accepted request",async t=>{
  const wait=deferred(),f=fixture({getTab:()=>wait.promise});t.after(f.dispose);await tick();f.configure();
  const first=f.send(f.candidate);f.configure({judgementMode:"hybrid"});const duplicate=f.send(f.candidate);
  wait.resolve();await tick();await tick();assert.equal(first.result.accepted,true);assert.equal(duplicate.result.accepted,false);
  f.reply();await tick();assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,1);
});
test("disconnect during accept await prevents later acknowledgement or native candidate",async t=>{
  const wait=deferred(),f=fixture({getTab:()=>wait.promise});t.after(f.dispose);await tick();f.configure();
  const request=f.send(f.candidate);f.port.onDisconnect.emit();wait.resolve();await tick();await tick();
  assert.equal(request.result.accepted,false);assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCandidate").length,0);
});
test("active-tab switch after tabs.get snapshot but during document await rejects old candidate",async t=>{
  let switchTab=()=>{},switched=false;
  const f=fixture({afterGet:id=>{if(id===11&&!switched){switched=true;switchTab();}}});t.after(f.dispose);await tick();f.configure();
  switchTab=()=>{f.tabs.get(11).active=false;f.tabs.get(12).active=true;f.chrome.tabs.onActivated.emit({tabId:12,windowId:7});};
  const request=f.send(f.candidate);await tick();await tick();
  assert.equal(request.result.accepted,false);assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCandidate").length,0);
});
test("disconnect disables observers and old pending updates cannot arrive",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();f.send(f.candidate);await tick();
  f.port.onDisconnect.emit();await tick();f.reply();await tick();
  assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,0);
  assert.equal(f.send({type:"queryPageAutoConfig"}).result.enabled,false);
  assert.ok(f.tabMessages.some(m=>m.message.type==="pageAutoConfig"&&m.message.enabled===false));
});
test("switching tabs preserves accepted document and completing while hidden causes zero new requests on return",async t=>{
  const f=fixture();t.after(f.dispose);await tick();f.configure();f.send(f.candidate);await tick();
  const announced=f.nativeMessages.find(m=>m.type==="hello"&&m.tabId===11);
  f.tabs.get(11).active=false;f.tabs.get(12).active=true;f.chrome.tabs.onActivated.emit({tabId:12,windowId:7});await tick();await tick();
  assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCancel").length,0);
  const hidden=f.tabMessages.filter(m=>m.id===11&&m.message.type==="pageAutoConfig").at(-1).message;
  assert.equal(hidden.enabled,true);assert.equal(hidden.active,false);
  f.reply({finished:true,updates:[{id:"block-1",text:"完整译文",complete:true}]});await tick();
  assert.equal(f.tabMessages.filter(m=>m.message.type==="pageAutoTranslations").length,1);
  f.tabs.get(12).active=false;f.tabs.get(11).active=true;f.chrome.tabs.onActivated.emit({tabId:11,windowId:7});await tick();await tick();
  assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCandidate").length,1);
  assert.equal(f.nativeMessages.filter(m=>m.type==="pageAutoCancel").length,0);
  const returned=f.nativeMessages.filter(m=>m.type==="hello"&&m.tabId===11).at(-1);
  assert.equal(returned.documentToken,announced.documentToken);
  assert.equal(returned.navigationGeneration,announced.navigationGeneration);
});
test("full-image fetch finishing after mixed-page gate closes cannot emit an image candidate",async t=>{
  const fetchWait=deferred();let fetched=false;
  const f=fixture({crossOrigin:true,fetch:async()=>{fetched=true;await fetchWait.promise;let read=false;return{ok:true,status:200,headers:{get:()=>"image/png"},body:{getReader:()=>({read:async()=>read?{done:true}:(read=true,{done:false,value:new Uint8Array([1,2,3])}),cancel:async()=>{}})}};}});
  t.after(f.dispose);await tick();f.port.onMessage.emit({type:"imageAutoConfig",enabled:true,generation:1,targetLanguage:"zh-CN",requirePageJudgement:true});
  const request=f.send({type:"autoImageCandidate",requestId:"image-request",imageId:f.image.imageId,documentToken:f.token,generation:1,devicePixelRatio:1.25});
  await tick();await tick();assert.equal(fetched,true);f.image.autoImageAllowed=false;fetchWait.resolve();await tick();await tick();
  assert.equal(request.result.needsTranslation,false);assert.equal(f.nativeMessages.filter(m=>m.type==="autoImageCandidate").length,0);
});

test("locally skipped Chinese or image-only document can forward pause without a text candidate", async t => {
  const f=fixture(); t.after(f.dispose); f.configure(); await tick();
  f.send({type:"pageAutoControl",action:"pause",documentToken:f.token,generation:1,revision:1});
  await tick(); await tick(); await tick();
  const packet=f.nativeMessages.find(message=>message.type==="pageAutoControl");
  assert.ok(packet); assert.equal(packet.action,"pause"); assert.equal(packet.tabId,11);
  assert.equal(packet.browserWindowId,7); assert.equal(packet.documentToken,f.token);
  assert.equal(f.nativeMessages.some(message=>message.type==="pageAutoCandidate"),false);
});
test("no-candidate pause with stale document, generation, subframe or inactive tab cannot reach native", async t => {
  const f=fixture(); t.after(f.dispose); f.configure(); await tick();
  const pause={type:"pageAutoControl",action:"pause",documentToken:f.token,generation:1,revision:1};
  f.send({...pause,documentToken:"old-page-document-token"});
  f.send({...pause,generation:0});
  f.send(pause,{...f.sender,frameId:2});
  await tick(); await tick();
  f.tabs.get(11).active=false; f.send(pause); await tick(); await tick();
  assert.equal(f.nativeMessages.some(message=>message.type==="pageAutoControl"),false);
});
test("new locally skipped document can pause after a previous translated document in the same tab", async t => {
  const f=fixture(); t.after(f.dispose); f.configure(); await tick();
  f.send(f.candidate); await tick(); await tick();
  const nextToken="33333333-3333-4333-8333-333333333333";
  f.tokens.set(11,nextToken);
  f.send({type:"pageAutoControl",action:"pause",documentToken:nextToken,generation:1,revision:1});
  await tick(); await tick(); await tick();
  const pauses=f.nativeMessages.filter(message=>message.type==="pageAutoControl");
  assert.equal(pauses.length,1); assert.equal(pauses[0].documentToken,nextToken);
  f.send({type:"pageAutoControl",action:"pause",documentToken:f.token,generation:1,revision:1});
  await tick(); await tick();
  assert.equal(f.nativeMessages.filter(message=>message.type==="pageAutoControl").length,1);
});
