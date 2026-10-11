"use strict";
const test=require("node:test"),assert=require("node:assert/strict"),fs=require("node:fs"),path=require("node:path"),vm=require("node:vm");
function fixture(){
  const frames=[],listeners=new Map(),sent=[],hosts=[];
  class Node{constructor(){this.dataset={};this.style={setProperty:(key,value)=>{this.style[key]=value;}};this.isConnected=true;}
    append(node){node.isConnected=true;}attachShadow(){return new Node();}setAttribute(){}remove(){this.isConnected=false;}}
  const image={isConnected:true,naturalWidth:600,naturalHeight:600,currentSrc:"source.png",src:"source.png"};
  const context=vm.createContext({document:{hidden:false,documentElement:new Node(),createElement:tag=>{const node=new Node();if(tag==="div")hosts.push(node);return node;}},
    performance,requestAnimationFrame:fn=>{frames.push(fn);return frames.length;},cancelAnimationFrame(){},
    addEventListener:(name,fn)=>listeners.set(name,fn),chrome:{runtime:{sendMessage:message=>{sent.push(message);return Promise.resolve();}}}});
  vm.runInContext(fs.readFileSync(path.join(__dirname,"..","image-overlay.js"),"utf8"),context);
  const token="document-1234567890",base={imageId:"image-1",documentToken:token,sessionId:"session-1234567890"};
  const receive=message=>context.ScreenTranslatorImageOverlay.receive({...base,...message},token,()=>({image,src:"source.png"}),()=>({display:{left:0,top:0,width:600,height:600},clip:{left:0,top:0,width:600,height:600}}));
  const draw=()=>{const fn=frames.shift();if(fn)fn();};
  function publish(sessionId=base.sessionId,generation=1){
    assert.equal(receive({type:"imageOverlayBegin",sessionId,generation,enabled:true,sourceSize:{width:600,height:600}}).found,true);
    assert.equal(receive({type:"imageOverlayChunk",sessionId,id:"tile",revision:1,index:0,data:"QUJDRA=="}).found,true);
    assert.equal(receive({type:"imageOverlayCommit",sessionId,id:"tile",revision:1,length:8,bounds:{x:10,y:10,width:32,height:32}}).found,true);draw();
  }
  return{receive,publish,draw,hosts,listeners,sent,base};
}
test("Esc publishes full session identity and late streaming state cannot resurrect the overlay",()=>{
  const f=fixture();f.publish();assert.equal(f.hosts[0].style.display,"block");
  f.listeners.get("keydown")({key:"Escape"});f.draw();assert.equal(f.hosts[0].style.display,"none");
  assert.equal(f.sent[0].type,"imageOverlayDismissed");assert.equal(f.sent[0].imageId,f.base.imageId);
  assert.equal(f.sent[0].documentToken,f.base.documentToken);assert.equal(f.sent[0].sessionId,f.base.sessionId);
  assert.equal(f.receive({type:"imageOverlayState",enabled:true,ids:["tile"]}).found,true);
  assert.equal(f.receive({type:"imageOverlayChunk",id:"tile",revision:2,index:0,data:"QUJDRA=="}).found,true);
  assert.equal(f.receive({type:"imageOverlayCommit",id:"tile",revision:2,length:8,bounds:{x:20,y:20,width:32,height:32}}).found,true);
  f.draw();assert.equal(f.hosts[0].style.display,"none");
});
test("repeated begin for dismissed session stays hidden, genuinely new session can be displayed",()=>{
  const f=fixture();f.publish();f.listeners.get("keydown")({key:"Escape"});f.draw();
  assert.equal(f.receive({type:"imageOverlayBegin",generation:1,enabled:true,sourceSize:{width:600,height:600}}).found,true);
  f.draw();assert.equal(f.hosts[0].style.display,"none");
  f.publish("session-new",2);assert.equal(f.hosts.at(-1).style.display,"block");
});
test("Alt remains a temporary source peek and does not emit dismissal",()=>{
  const f=fixture();f.publish();f.listeners.get("keydown")({key:"Alt"});f.draw();
  assert.equal(f.hosts[0].style.display,"none");assert.equal(f.sent.length,0);
  f.listeners.get("keyup")({key:"Alt"});f.draw();assert.equal(f.hosts[0].style.display,"block");
});
