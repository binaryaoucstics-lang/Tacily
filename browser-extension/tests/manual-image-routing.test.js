"use strict";
const test=require("node:test"),assert=require("node:assert/strict");
const {create}=require("../manual-image-background.js");
function fixture(){
  const tab={id:10,windowId:4,active:true},state={documentToken:"doc"},geometry={found:true,imageId:"img",documentToken:"doc"};
  const config={manualAvailable:true,generation:1,enabled:false,siteMode:"block"},native=[],content=[];
  const chrome={tabs:{get:async()=>({...tab}),sendMessage:async(id,packet)=>{
    content.push(packet);return packet.type==="queryDocumentState"?{...state}:packet.type==="queryImageGeometry"?{...geometry}:{};
  }}};
  const router=create({chrome,postNative:p=>{native.push(p);return true;},getConfig:()=>config,isConnected:()=>true});
  const request={type:"manualImageCandidate",requestId:"req",imageId:"img",documentToken:"doc",generation:1};
  return {router,tab,state,geometry,config,native,content,request,sender:{tab,frameId:0},status:{type:"manualImageStatus",requestId:"req",imageId:"img",documentToken:"doc",generation:1,tabId:10}};
}
test("manual image opt-in works with automatic mode disabled or blacklisted, using verified IDs without pixels or URLs",async t=>{
  const f=fixture();t.after(()=>f.router.disconnect());let answer;
  await f.router.request({...f.request,url:"https://evil.invalid",previewBase64:"secret"},f.sender,result=>answer=result);
  assert.equal(answer,undefined);assert.deepEqual(f.native,[{type:"manualImageCandidate",requestId:"req",imageId:"img",documentToken:"doc",generation:1,tabId:10,browserWindowId:4,frameId:0}]);
  await f.router.forward({...f.status,state:"translating"});assert.equal(answer.accepted,true);assert.equal(f.content.at(-1).state,"translating");
  await f.router.forward({...f.status,state:"completed"});assert.equal(f.content.at(-1).state,"completed");
});
test("foreign frames, inactive tabs, stale source and repeated request IDs are rejected before dispatch",async t=>{
  for(const change of [f=>f.sender.frameId=1,f=>f.tab.active=false,f=>f.geometry.imageId="different",f=>f.state.documentToken="navigated",f=>f.config.generation=2]){
    const f=fixture();t.after(()=>f.router.disconnect());change(f);let answer;await f.router.request(f.request,f.sender,result=>answer=result);assert.equal(answer.accepted,false);assert.equal(f.native.length,0);
  }
  const f=fixture();t.after(()=>f.router.disconnect());await f.router.request(f.request,f.sender,()=>{});let duplicate;
  await f.router.request(f.request,f.sender,p=>duplicate=p);assert.equal(duplicate.accepted,false);assert.equal(f.native.length,1);
});
test("native statuses cannot cross image, tab or generation; navigated pages receive no final status",async t=>{
  const f=fixture();t.after(()=>f.router.disconnect());let answer;await f.router.request(f.request,f.sender,p=>answer=p);
  for(const fields of [{imageId:"other"},{generation:2},{tabId:11},{documentToken:"other"}])await f.router.forward({...f.status,state:"completed",...fields});
  assert.equal(answer,undefined);await f.router.forward({...f.status,state:"translating"});assert.equal(answer.accepted,true);
  f.state.documentToken="new";const before=f.content.filter(p=>p.type==="manualImageStatus").length;await f.router.forward({...f.status,state:"completed"});
  assert.equal(f.content.filter(p=>p.type==="manualImageStatus").length,before);
});
test("disconnect resolves a pending manual picker instead of leaving the dot busy",async()=>{
  const f=fixture();let answer;await f.router.request(f.request,f.sender,p=>answer=p);f.router.disconnect();assert.deepEqual(answer,{accepted:false,reason:"desktop_unavailable"});
});
