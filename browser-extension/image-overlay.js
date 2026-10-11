(function () {
  "use strict";
  const maxBytes = 128 * 1024 * 1024;
  const states = new Map();
  let retainedBytes = 0, frame = 0, originalHeld = false;
  const motion = globalThis.ScreenTranslatorMotion;
  function announceImageView() {
    const visible = [...states.values()].filter(state => state.active && state.enabled && !state.dismissed);
    const next = visible.some(state => state.renderedTiles.size > 0);
    const activity = { preparing: visible.filter(state => state.motionPhase === "preparing").length,
      translating: visible.filter(state => ["translating", "typesetting"].includes(state.motionPhase)
        || state.motionPhase === "complete" && !tilesReady(state)).length,
      completed: visible.filter(state => ["complete", "cached"].includes(state.motionPhase)
        && state.renderedTiles.size > 0 && tilesReady(state)).length };
    if (next === globalThis.ScreenTranslatorHasTranslatedImages
        && JSON.stringify(activity) === JSON.stringify(globalThis.ScreenTranslatorImageTranslationActivity)) return;
    globalThis.ScreenTranslatorHasTranslatedImages = next;
    globalThis.ScreenTranslatorImageTranslationActivity = Object.freeze(activity);
    if (typeof Event === "function") dispatchEvent(new Event("ScreenTranslatorImageTranslationChanged"));
  }
  function close(state, forget = false) {
    motion?.cancelImage(state.imageId);
    for (const item of state.retiringNodes.values()) item.node.remove(); state.retiringNodes.clear();
    state.host?.remove(); state.host = null; state.nodes.clear(); state.renderedTiles.clear(); state.pendingNodes.clear(); state.hostProperties.clear();
    state.layoutDirty = true;
    if (forget) state.active = false;
    if (forget) { retainedBytes -= state.bytes; states.delete(state.imageId); }
    announceImageView();
  }
  function invalidate(state) { state.enabled = false; close(state, true); }
  function tilesReady(state) { return !state.pendingNodes.size && [...state.tiles.values()].every(tile => state.renderedTiles.get(tile.id) === tile); }
  function hostFor(state) {
    if (state.host?.isConnected) return;
    const host = document.createElement("div");
    host.dataset.screenTranslatorOverlay = "true";
    host.style.cssText = "all:initial!important;position:fixed!important;z-index:2147483645!important;pointer-events:none!important;overflow:hidden!important;contain:layout style paint!important;isolation:isolate!important;";
    const shadow = host.attachShadow({ mode: "closed" });
    document.documentElement.append(host); state.host = host; state.shadow = shadow;
    for (const tile of state.tiles.values()) putNode(state, tile);
  }
  function putNode(state, tile) {
    // Keep the previous rendered revision until its replacement is decoded.
    // Reusing an img and changing src exposes the source underneath between decodes.
    const node = document.createElement("img"), host = state.host;
    node.setAttribute("aria-hidden", "true");
    node.style.cssText = "position:absolute;display:block;max-width:none;max-height:none;margin:0;border:0;padding:0;pointer-events:none;";
    state.pendingNodes.set(tile.id, node);
    node.src = `data:image/png;base64,${tile.data}`;
    const insert = () => {
      if (!state.active || state.dismissed || state.host !== host || !host?.isConnected
          || state.pendingNodes.get(tile.id) !== node || state.tiles.get(tile.id) !== tile) return;
      state.pendingNodes.delete(tile.id);
      if (state.tileLayout) layoutNode(node, tile, state.tileLayout);
      const previous = state.nodes.get(tile.id), previousTile = state.renderedTiles.get(tile.id);
      state.retiringNodes.get(tile.id)?.node.remove(); state.retiringNodes.delete(tile.id);
      for (const animation of previous?.getAnimations?.() || []) { try { animation.finish(); } catch { animation.cancel(); } }
      state.shadow.append(node); state.nodes.set(tile.id, node); state.renderedTiles.set(tile.id, tile);
      announceImageView();
      if (motion?.motionAllowed() && !state.motionCached) {
        if (previous) state.retiringNodes.set(tile.id, { node: previous, tile: previousTile });
        motion.fadeTile(node, () => { previous?.remove(); if (state.retiringNodes.get(tile.id)?.node === previous) state.retiringNodes.delete(tile.id); });
      } else previous?.remove();
      if (state.motionPhase === "complete" && tilesReady(state)) motion?.imageState(state.imageId, state.saved.image, "complete", state.documentToken);
      state.layoutDirty = true; schedule();
    };
    if (typeof node.decode === "function") void node.decode().then(insert, () => {
      if (state.pendingNodes.get(tile.id) === node) state.pendingNodes.delete(tile.id);
    });
    else insert();
  }
  function layoutNode(node, tile, layout) {
    const b = tile.bounds;
    node.style.left = `${layout.x + b.x * layout.sx}px`; node.style.top = `${layout.y + b.y * layout.sy}px`;
    node.style.width = `${b.width * layout.sx}px`; node.style.height = `${b.height * layout.sy}px`;
  }
  function setHostProperty(state, property, value) {
    if (state.hostProperties.get(property) === value) return;
    state.hostProperties.set(property, value); state.host.style.setProperty(property, value, "important");
  }
  function pageOriginal(state) {
    const view = globalThis.ScreenTranslatorPageViewState;
    return view?.enabled === true && view.documentToken === state.documentToken && view.mode === "original";
  }
  function update() {
    frame = 0;
    const layouts = [], styleCache = new Map();
    // Read every source/ancestor first. Alternating geometry reads and style
    // writes for many translated images otherwise forces repeated layout work.
    for (const state of states.values()) {
      const saved = state.lookup(state.imageId) || state.saved;
      if (!saved || saved.src !== (saved.image.currentSrc || saved.image.src)
          || saved.image.naturalWidth !== state.naturalSize.width || saved.image.naturalHeight !== state.naturalSize.height) {
        invalidate(state); continue;
      }
      if (!saved.image.isConnected || !state.enabled || originalHeld || pageOriginal(state) || document.hidden) {
        layouts.push({ state, shape: null }); continue;
      }
      let shape;
      try { shape = state.describe(saved.image, styleCache); } catch { layouts.push({ state, shape: null }); continue; }
      if (shape.clip.width <= 0 || shape.clip.height <= 0) shape = null;
      layouts.push({ state, shape });
    }
    for (const { state, shape } of layouts) {
      if (!shape) { if (state.host) setHostProperty(state, "display", "none"); continue; }
      const {display,clip} = shape;
      state.tileLayout = { x: display.left - clip.left, y: display.top - clip.top,
        sx: display.width / state.sourceSize.width, sy: display.height / state.sourceSize.height };
      hostFor(state);
      setHostProperty(state, "display", "block");
      setHostProperty(state, "left", `${clip.left}px`); setHostProperty(state, "top", `${clip.top}px`);
      setHostProperty(state, "width", `${clip.width}px`); setHostProperty(state, "height", `${clip.height}px`);
      const layoutKey = [display.left - clip.left, display.top - clip.top, display.width, display.height].join("|");
      if (!state.layoutDirty && state.layoutKey === layoutKey) continue;
      state.layoutKey = layoutKey; state.layoutDirty = false;
      for(const tile of state.renderedTiles.values()) {const node=state.nodes.get(tile.id); if(!node) continue;
        layoutNode(node, tile, state.tileLayout); }
      for (const item of state.retiringNodes.values()) if (item.tile) layoutNode(item.node, item.tile, state.tileLayout);
    }
    if ([...states.values()].some(s=>s.enabled && !pageOriginal(s)) && !originalHeld && !document.hidden) frame = requestAnimationFrame(update);
  }
  function schedule() { if (!frame) frame = requestAnimationFrame(update); }
  function trim() {
    while ((retainedBytes > maxBytes || states.size > 32) && states.size > 1) close(states.values().next().value, true);
  }
  globalThis.ScreenTranslatorImageOverlay = {
    receive(request, token, lookup, describe) {
      if (request.documentToken !== token || typeof request.imageId !== "string") return {found:false};
      let state=states.get(request.imageId);
      const saved=lookup(request.imageId) || state?.saved;
      if(!saved || !saved.image.isConnected || saved.src !== (saved.image.currentSrc || saved.image.src)
        ||saved.naturalSize&&(saved.naturalSize.width!==saved.image.naturalWidth||saved.naturalSize.height!==saved.image.naturalHeight)) return {found:false};
      if(request.type === "imageOverlayBegin") {
        const pixelSize = saved.pixelSize || { width: saved.image.naturalWidth, height: saved.image.naturalHeight };
        if(!Number.isSafeInteger(request.generation) || typeof request.sessionId !== "string" || !request.sourceSize
          ||request.sourceSize.width!==pixelSize.width ||request.sourceSize.height!==pixelSize.height) return {found:false};
        if(state && (request.generation<state.generation ||request.generation===state.generation&&request.sessionId!==state.sessionId)) return {found:false};
        if(!state ||state.sessionId!==request.sessionId) {if(state)close(state,true);
          state={imageId:request.imageId,documentToken:token,sessionId:request.sessionId,generation:request.generation,sourceSize:request.sourceSize,lookup,describe,
            naturalSize:{width:saved.image.naturalWidth,height:saved.image.naturalHeight},
            enabled:true,dismissed:false,active:true,saved,tiles:new Map(),nodes:new Map(),renderedTiles:new Map(),pendingNodes:new Map(),
            retiringNodes:new Map(),motionPhase:request.phase || "idle",motionCached:request.phase === "cached",
            hostProperties:new Map(),layoutDirty:true,transfers:new Map(),bytes:0,host:null};states.set(request.imageId,state); }
        state.enabled=!state.dismissed&&request.enabled!==false;
        if (state.enabled) motion?.imageState(state.imageId, saved.image, state.motionPhase, token);
        trim();announceImageView();schedule();return {found:true};
      }
      if(!state ||request.sessionId!==state.sessionId ||state.naturalSize.width!==saved.image.naturalWidth
        ||state.naturalSize.height!==saved.image.naturalHeight) return {found:false};
      // Esc closes this translation session permanently. Late stream/style messages
      // still receive an ACK so desktop cancellation/cleanup cannot block on an old frame.
      if(state.dismissed&&["imageOverlayState","imageOverlayChunk","imageOverlayCommit"].includes(request.type))return {found:true};
      if(request.type === "imageOverlayState") {state.enabled=request.enabled===true;
        if (["preparing", "translating", "typesetting", "complete", "cached", "idle"].includes(request.phase)) {
          state.motionPhase = request.phase === "translating" && state.tiles.size ? "typesetting" : request.phase;
          state.motionCached ||= request.phase === "cached";
        }
        if (state.enabled && (state.motionPhase !== "complete" || tilesReady(state))) motion?.imageState(state.imageId, saved.image, state.motionPhase, token);
        else if (!state.enabled) motion?.cancelImage(state.imageId);
        if(Array.isArray(request.ids))for(const [id,tile] of state.tiles)if(!request.ids.includes(id)) {state.tiles.delete(id);state.nodes.get(id)?.remove();state.nodes.delete(id);state.renderedTiles.delete(id);state.pendingNodes.delete(id);state.retiringNodes.get(id)?.node.remove();state.retiringNodes.delete(id);state.bytes-=tile.data.length;retainedBytes-=tile.data.length;}
        announceImageView();
        schedule();return {found:true}; }
      if(typeof request.id!=="string" ||request.id.length>64 ||!Number.isSafeInteger(request.revision))return {found:false};
      if((state.tiles.get(request.id)?.revision??-1)>=request.revision) return {found:true};
      if(request.type === "imageOverlayChunk") {
        if(typeof request.data!=="string" ||request.data.length>262144 ||!Number.isSafeInteger(request.index)||request.index<0||request.index>96)return {found:false};
        const now=performance.now(); for(const [id,transfer] of state.transfers) if(now-transfer.at>10000)state.transfers.delete(id);
        let transfer=state.transfers.get(request.id);
        if(!transfer ||transfer.revision!==request.revision) {if(request.index!==0 ||transfer&&transfer.revision>request.revision)return {found:false};
          transfer={revision:request.revision,parts:[],length:0,at:now};state.transfers.set(request.id,transfer); }
        if(request.index!==transfer.parts.length ||transfer.length+request.data.length>24*1024*1024)return {found:false};
        transfer.parts.push(request.data);transfer.length+=request.data.length;return {found:true};
      }
      if(request.type === "imageOverlayCommit") {
        const transfer=state.transfers.get(request.id),b=request.bounds;
        if(!transfer ||transfer.revision!==request.revision ||request.length!==transfer.length ||!b
          ||![b.x,b.y,b.width,b.height].every(Number.isFinite) ||b.x<0||b.y<0||b.width<=0||b.height<=0
          ||b.x+b.width>state.sourceSize.width||b.y+b.height>state.sourceSize.height)return {found:false};
        const data=transfer.parts.join(""); if(!/^[A-Za-z0-9+/]*={0,2}$/.test(data))return {found:false};
        state.transfers.delete(request.id);const old=state.tiles.get(request.id);if(old){state.bytes-=old.data.length;retainedBytes-=old.data.length;}
        const tile={id:request.id,revision:request.revision,bounds:b,data};state.tiles.set(request.id,tile);state.bytes+=data.length;retainedBytes+=data.length;
        if (state.motionPhase === "translating") { state.motionPhase = "typesetting"; motion?.imageState(state.imageId, saved.image, "typesetting", token); }
        if(state.host)putNode(state,tile);trim();schedule();return {found:true};
      }
      return {found:false};
    }
  };
  addEventListener("keydown", event=>{if(event.key==="Alt"){originalHeld=true;schedule();}
    if(event.key==="Escape")for(const state of states.values())if(state.enabled){state.enabled=false;state.dismissed=true;state.transfers.clear();schedule();
      announceImageView();
      motion?.cancelImage(state.imageId);
      void chrome.runtime.sendMessage({type:"imageOverlayDismissed",imageId:state.imageId,documentToken:state.documentToken,sessionId:state.sessionId});}},true);
  addEventListener("keyup",event=>{if(event.key==="Alt"){originalHeld=false;schedule();}},true);
  addEventListener("blur",()=>{originalHeld=false;schedule();});
  addEventListener("visibilitychange",schedule);
  // The event merely wakes us; authorization comes from the isolated-world
  // state, never from page-controlled event.detail.
  addEventListener("ScreenTranslatorPageViewChanged",schedule,true);
  addEventListener("pagehide",()=>{if(frame)cancelAnimationFrame(frame);frame=0;for(const state of states.values())close(state,true);});
})();
