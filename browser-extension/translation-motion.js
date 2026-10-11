"use strict";
(function exposeMotion(root) {
  // Only append-only stream tails are paced. Finals, cache hits, failures, view
  // changes and changed source identities always commit synchronously.
  function createStreamWriter({ requestFrame, cancelFrame, now, allowMotion, duration = 100 }) {
    const pending = new Map(); let frame = 0;
    function stopIdle() { if (!pending.size && frame) { cancelFrame(frame); frame = 0; } }
    function draw(time) {
      frame = 0;
      for (const [key, item] of pending) {
        if (!item.current()) { pending.delete(key); continue; }
        const progress = Math.min(1, Math.max(0, (time - item.start) / duration));
        const tail = Array.from(item.target.slice(item.base.length));
        const value = (progress >= 1 ? item.target : item.base + tail.slice(0, Math.floor(tail.length * progress)).join("")) + item.padding;
        if (!item.commit(value) || progress >= 1) pending.delete(key);
      }
      if (pending.size) frame = requestFrame(draw);
    }
    function write(key, value, options) {
      const before = options.read();
      const padding = options.padding && before.endsWith(options.padding) && value.endsWith(options.padding) ? options.padding : "";
      const base = padding ? before.slice(0, -padding.length) : before, target = padding ? value.slice(0, -padding.length) : value;
      if (!options.current()) { pending.delete(key); stopIdle(); return false; }
      if (!allowMotion() || options.immediate || !target.startsWith(base) || !base
          || target.length - base.length > 180 || value === before) {
        pending.delete(key); stopIdle(); return options.commit(value);
      }
      const old = pending.get(key);
      const item = old && target.startsWith(old.target) && padding === old.padding
        ? { ...old, ...options, target, padding }
        : { base, target, start: now(), ...options, padding };
      pending.set(key, item); if (!frame) frame = requestFrame(draw); return true;
    }
    function clear(commitLatest = false) {
      if (frame) cancelFrame(frame); frame = 0;
      if (commitLatest) for (const item of pending.values()) if (item.current()) item.commit(item.target + item.padding);
      pending.clear();
    }
    return { write, clear, cancel: key => { pending.delete(key); stopIdle(); }, count: () => pending.size };
  }

  function createTranslationMotion(environment) {
    const document = environment.document, view = environment.window || root;
    const requestFrame = environment.requestFrame || (fn => view.requestAnimationFrame(fn));
    const cancelFrame = environment.cancelFrame || (id => view.cancelAnimationFrame(id));
    const now = environment.now || (() => view.performance.now());
    const media = view.matchMedia?.("(prefers-reduced-motion: reduce)");
    const entries = new Map(), reveals = new WeakSet(), animations = new Set();
    let enabled = true, host = null, shadow = null, frame = 0, lastLayout = -Infinity, disposed = false, altHeld = false, nativeVideoFullscreen = false;
    function fullscreen() { return !!(document.fullscreenElement || document.webkitFullscreenElement || document.webkitIsFullScreen || nativeVideoFullscreen); }
    function motionAllowed() { return enabled && !media?.matches && !document.hidden && !fullscreen(); }
    const writer = createStreamWriter({ requestFrame, cancelFrame, now, allowMotion: motionAllowed });
    const css = `:host{all:initial;position:fixed;inset:0;z-index:2147483646;pointer-events:none;contain:layout style;overflow:hidden}
      *{box-sizing:border-box;pointer-events:none} .item{position:absolute;overflow:hidden;isolation:isolate;contain:layout style paint}
      .text{height:3px;border-radius:4px;background:linear-gradient(90deg,#6698e000,#6698e030,#9c82f024,#9c82f000);opacity:.8}
      .ribbon{position:absolute;inset:0;width:38%;background:linear-gradient(90deg,transparent,#609ae085,#ac90ee65,transparent);transform:translateX(-110%);animation:travel 1.9s cubic-bezier(.4,0,.2,1) infinite}
      .image{border:1px solid #82a8de55;border-radius:7px}.image .ribbon{height:2px;inset:0 auto auto 0;width:46%;animation-duration:2.6s}
      .badge{position:absolute;right:8px;display:flex;gap:6px;align-items:center;padding:5px 9px;border:1px solid #b2c6e55c;border-radius:20px;background:#f6faffeb;color:#355577;font:500 11px/1.3 system-ui,sans-serif;white-space:nowrap;box-shadow:0 2px 8px #152c4c0d}
      .dot{height:5px;width:5px;border-radius:50%;background:#6899d2;animation:breathe 1.8s ease-in-out infinite}.corner{position:absolute;width:13px;height:13px;border-color:#8cafdfb0;border-style:solid}
      .tl{left:0;top:0;border-width:2px 0 0 2px;border-radius:6px 0 0}.br{right:0;bottom:0;border-width:0 2px 2px 0;border-radius:0 0 6px}
      .done{animation:leave .38s ease-out forwards}.done .ribbon{display:none}.done .dot{animation:none;background:#5fa79b}
      @keyframes travel{to{transform:translateX(370%)}}@keyframes breathe{50%{opacity:.5;transform:scale(.8)}}@keyframes leave{to{opacity:0}}
      @media(prefers-reduced-motion:reduce){.ribbon,.dot,.done{animation:none}.ribbon{display:none}}
    `;
    function ensureHost() {
      if (host?.isConnected) return;
      host = document.createElement("div"); host.setAttribute("data-screen-translator", "motion"); host.setAttribute("aria-hidden", "true");
      host.style.cssText = "all:initial!important;position:fixed!important;inset:0!important;z-index:2147483646!important;pointer-events:none!important;contain:layout style!important;overflow:hidden!important;display:block!important";
      shadow = host.attachShadow({ mode: "closed" });
      const style = document.createElement("style"); style.textContent = css; shadow.append(style);
      document.documentElement.append(host);
    }
    function changeStyle(entry, name, value) {
      if (entry.properties.get(name) === value) return;
      entry.properties.set(name, value); entry.node.style[name] = value;
    }
    function blocked(entry) {
      const state = view.ScreenTranslatorPageViewState;
      return document.hidden || fullscreen() || !enabled || altHeld
        || entry.type === "image" && state?.enabled && state.documentToken === entry.token && state.mode === "original";
    }
    function visible(bounds) { return bounds && bounds.width > 0 && bounds.height > 0 && bounds.bottom > 0
      && bounds.top < (view.innerHeight || 1000) && bounds.right > 0 && bounds.left < (view.innerWidth || 2000); }
    function layout(time) {
      frame = 0;
      if (disposed || !entries.size || document.hidden || !enabled || fullscreen() || altHeld) return;
      if (time - lastLayout < 30) { frame = requestFrame(layout); return; }
      lastLayout = time;
      // Read before writing, and keep geometry work bounded by visible effects.
      const measured = [];
      for (const [key, entry] of entries) {
        if (!entry.element.isConnected || entry.type === "image" && entry.src !== (entry.element.currentSrc || entry.element.src)) { remove(key); continue; }
        const bounds = blocked(entry) ? null : entry.element.getBoundingClientRect();
        measured.push({ entry, bounds });
      }
      for (const { entry, bounds } of measured) {
        const shown = visible(bounds); changeStyle(entry, "display", shown ? "block" : "none");
        if (!shown) continue;
        const image = entry.type === "image";
        changeStyle(entry, "left", `${bounds.left}px`);
        changeStyle(entry, "top", `${image ? bounds.top : bounds.bottom - 2}px`);
        changeStyle(entry, "width", `${image ? bounds.width : Math.min(bounds.width, 120)}px`);
        if (image) {
          changeStyle(entry, "height", `${bounds.height}px`);
          const badgeVisible = bounds.top >= 0 && bounds.width >= 160;
          if (entry.badgeVisible !== badgeVisible) { entry.badge.style.visibility = badgeVisible ? "visible" : "hidden"; entry.badgeVisible = badgeVisible; }
        }
      }
      if (entries.size && !document.hidden) frame = requestFrame(layout);
    }
    function schedule() { if (!disposed && entries.size && !frame && !document.hidden) frame = requestFrame(layout); }
    function remove(key) {
      const entry = entries.get(key); if (!entry) return;
      clearTimeout(entry.timer); entry.node.remove(); entries.delete(key);
      if (!entries.size) { if (frame) cancelFrame(frame); frame = 0; host?.remove(); host = shadow = null; }
    }
    function add(key, element, type, token) {
      if (!enabled || disposed || !element?.isConnected) return null;
      const before = entries.get(key); if (before) return before;
      if (entries.size >= 12) return null;
      ensureHost();
      const node = document.createElement("div"); node.className = `item ${type}`; node.style.display = "none";
      const ribbon = document.createElement("span"); ribbon.className = "ribbon"; node.append(ribbon);
      const entry = { node, element, type, token, properties: new Map(), src: element.currentSrc || element.src, timer: null };
      if (type === "image") {
        const badge = document.createElement("span"), dot = document.createElement("span"), label = document.createElement("span");
        badge.className = "badge"; badge.style.top = "8px"; dot.className = "dot"; badge.append(dot, label); node.append(badge);
        for (const corner of ["tl", "br"]) { const edge = document.createElement("span"); edge.className = `corner ${corner}`; node.append(edge); }
        entry.label = label; entry.badge = badge;
      }
      shadow.append(node); entries.set(key, entry); schedule(); return entry;
    }
    function textBegin(element, visibleHint) {
      // Offscreen text still translates. Only its visual indicator is omitted.
      if (!enabled || disposed || entries.has(element) || entries.size >= 12 || !element?.getBoundingClientRect) return;
      // The scanner already read visibility before any indicator DOM writes.
      // Reusing it avoids a layout flush between each of twelve insertions.
      if (typeof visibleHint === "boolean" ? !visibleHint : !visible(element.getBoundingClientRect())) return;
      add(element, element, "text");
    }
    function textEnd(element, successful = true, cached = false) {
      const entry = entries.get(element); if (!entry) return;
      if (!successful || cached || !motionAllowed()) { remove(element); return; }
      entry.node.classList.add("done"); clearTimeout(entry.timer); entry.timer = setTimeout(() => remove(element), 390);
    }
    function softReveal(element, cached = false) {
      if (cached || !motionAllowed() || !element?.animate || reveals.has(element)) return;
      if (!visible(element.getBoundingClientRect())) return;
      reveals.add(element);
      const base = Number(view.getComputedStyle?.(element)?.opacity ?? 1);
      const animation = element.animate([{ opacity: base * .88 }, { opacity: base }], { duration: 190, easing: "cubic-bezier(.2,.7,.3,1)" });
      animations.add(animation); animation.finished.then(() => animations.delete(animation), () => animations.delete(animation));
    }
    function fadeTile(element, after = () => {}) {
      if (!motionAllowed() || !element?.animate) { after(); return; }
      const animation = element.animate([{ opacity: .35 }, { opacity: 1 }], { duration: 150, easing: "cubic-bezier(.2,.7,.3,1)" });
      animations.add(animation);
      const finished = () => { animations.delete(animation); after(); };
      animation.finished.then(finished, finished);
    }
    function imageState(id, element, phase, token) {
      const key = `image:${id}`;
      if (["cached", "idle", "failed", "cancelled"].includes(phase)) { remove(key); return; }
      const entry = phase === "complete" ? entries.get(key) : add(key, element, "image", token);
      if (!entry) return;
      entry.label.textContent = phase === "complete" ? "已完成" : phase === "preparing" ? "准备图像…" : phase === "typesetting" ? "流式嵌字…" : "正在翻译…";
      clearTimeout(entry.timer);
      if (phase === "complete") {
        if (!motionAllowed()) { remove(key); return; }
        entry.node.classList.add("done"); entry.timer = setTimeout(() => remove(key), 390);
      } else entry.timer = setTimeout(() => remove(key), 95000); // failed/disconnected native work cannot leave a resident badge
      schedule();
    }
    function clearText(commitLatest = false) {
      writer.clear(commitLatest);
      for (const [key, entry] of entries) if (entry.type === "text") remove(key);
    }
    function finishAnimations() { for (const animation of animations) { try { animation.finish(); } catch { animation.cancel(); } } animations.clear(); }
    function preferencesChanged() {
      if (!motionAllowed()) { writer.clear(true); finishAnimations(); }
      if (host) host.style.setProperty("display", document.hidden || fullscreen() || !enabled ? "none" : "block", "important");
      lastLayout = -Infinity; schedule(); view.dispatchEvent?.(new view.Event("ScreenTranslatorMotionChanged"));
    }
    const handlers = [["visibilitychange", preferencesChanged], ["fullscreenchange", preferencesChanged],
      ["webkitfullscreenchange", preferencesChanged],
      ["webkitbeginfullscreen", event => { if (event.target?.tagName === "VIDEO") { nativeVideoFullscreen = true; preferencesChanged(); } }],
      ["webkitendfullscreen", event => { if (event.target?.tagName === "VIDEO") { nativeVideoFullscreen = false; preferencesChanged(); } }],
      ["ScreenTranslatorPageViewChanged", preferencesChanged], ["blur", () => { altHeld = false; preferencesChanged(); }],
      ["keydown", event => { if (event.key === "Alt") { altHeld = true; schedule(); } }],
      ["keyup", event => { if (event.key === "Alt") { altHeld = false; schedule(); } }],
      ["pagehide", () => { writer.clear(); finishAnimations(); for (const key of entries.keys()) remove(key); }]];
    for (const [type, listener] of handlers) view.addEventListener?.(type, listener, true);
    media?.addEventListener?.("change", preferencesChanged);
    return { textBegin, textEnd, softReveal, fadeTile, imageState, clearText, writer, motionAllowed,
      setEnabled(value) { enabled = value === true; if (!enabled) { writer.clear(true); finishAnimations(); } preferencesChanged(); },
      getEnabled: () => enabled && !media?.matches,
      cancelImage: id => remove(`image:${id}`),
      dispose() { disposed = true; writer.clear(); finishAnimations(); for (const key of entries.keys()) remove(key);
        for (const [type, listener] of handlers) view.removeEventListener?.(type, listener, true); media?.removeEventListener?.("change", preferencesChanged); },
      inspect: () => ({ effects: entries.size, pendingText: writer.count(), activeAnimations: animations.size, enabled, reducedMotion: !!media?.matches }) };
  }
  const api = { createStreamWriter, createTranslationMotion };
  root.ScreenTranslatorMotionFactory = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (root.document && root.requestAnimationFrame) {
    root.ScreenTranslatorMotion = createTranslationMotion({ document: root.document, window: root });
    root.chrome?.runtime?.onMessage?.addListener((message, _sender, respond) => {
      if (message?.type !== "translationMotionConfig" || typeof message.enabled !== "boolean") return false;
      root.ScreenTranslatorMotion.setEnabled(message.enabled); respond?.({ found: true }); return false;
    });
    try { root.chrome?.runtime?.sendMessage({ type: "queryTranslationMotionConfig" }, reply => {
      void root.chrome.runtime.lastError;
      if (typeof reply?.enabled === "boolean") root.ScreenTranslatorMotion.setEnabled(reply.enabled);
    }); } catch { }
  }
})(globalThis);
