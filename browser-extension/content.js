(function installScrollObserver() {
  "use strict";

  const accumulator = new globalThis.ScrollAccumulator();
  const targetIds = new WeakMap();
  const documentToken = createDocumentToken();
  globalThis.ScreenTranslatorDocumentToken = documentToken;
  let initialDevicePixelRatio = readDevicePixelRatio();
  let nextTargetId = 1;
  let animationFrame = 0;
  let invalidated = false;
  let suspended = false;

  function createDocumentToken() {
    if (typeof globalThis.crypto.randomUUID === "function") {
      return globalThis.crypto.randomUUID();
    }

    const bytes = new Uint8Array(16);
    globalThis.crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, value =>
      value.toString(16).padStart(2, "0"));
    return [
      hex.slice(0, 4).join(""),
      hex.slice(4, 6).join(""),
      hex.slice(6, 8).join(""),
      hex.slice(8, 10).join(""),
      hex.slice(10, 16).join("")
    ].join("-");
  }

  function readDevicePixelRatio() {
    const ratio = globalThis.devicePixelRatio;
    return Number.isFinite(ratio) && ratio > 0 ? ratio : 1;
  }

  function isRootTarget(target) {
    return (
      target === document ||
      target === document.documentElement ||
      target === document.body ||
      target === document.scrollingElement
    );
  }

  function getTargetId(element) {
    let id = targetIds.get(element);
    if (!id) {
      id = `element-${nextTargetId}`;
      nextTargetId += 1;
      targetIds.set(element, id);
    }
    return id;
  }

  function readRootScroll() {
    const x = Number.isFinite(globalThis.scrollX)
      ? globalThis.scrollX
      : document.documentElement?.scrollLeft ?? 0;
    const y = Number.isFinite(globalThis.scrollY)
      ? globalThis.scrollY
      : document.documentElement?.scrollTop ?? 0;
    return { target: "root", x, y };
  }

  function readNestedScroll(element) {
    if (!(element instanceof Element)) {
      return null;
    }

    const rect = element.getBoundingClientRect();
    const values = [
      element.scrollLeft,
      element.scrollTop,
      rect.left,
      rect.top,
      rect.width,
      rect.height
    ];
    if (!values.every(Number.isFinite)) {
      return null;
    }

    return {
      target: getTargetId(element),
      x: element.scrollLeft,
      y: element.scrollTop,
      metadata: {
        scrollContainer: {
          left: rect.left,
          top: rect.top,
          width: rect.width,
          height: rect.height
        }
      }
    };
  }

  function canScroll(element) {
    return (
      element instanceof Element &&
      (
        element.scrollHeight > element.clientHeight ||
        element.scrollWidth > element.clientWidth
      )
    );
  }

  function primeNestedTargets(event) {
    if (invalidated || suspended) {
      return;
    }

    const path = typeof event.composedPath === "function"
      ? event.composedPath()
      : [event.target];
    for (const candidate of path) {
      if (!canScroll(candidate) || isRootTarget(candidate)) {
        continue;
      }

      const position = readNestedScroll(candidate);
      if (position) {
        accumulator.observe(
          position.target,
          position.x,
          position.y,
          position.metadata);
      }
    }
  }

  function scheduleFlush() {
    if (animationFrame !== 0 || invalidated || suspended) {
      return;
    }

    animationFrame = globalThis.requestAnimationFrame(() => {
      animationFrame = 0;
      const scrolls = accumulator.flush();
      if (scrolls.length === 0 || invalidated || suspended) {
        return;
      }

      sendRuntimeMessage({
        type: "scrollBatch",
        documentToken,
        devicePixelRatio: readDevicePixelRatio(),
        viewportSize: readViewportSize(),
        scrolls
      });
    });
  }

  function onScroll(event) {
    if (invalidated || suspended) {
      return;
    }

    const position = isRootTarget(event.target)
      ? readRootScroll()
      : readNestedScroll(event.target);
    if (
      position &&
      accumulator.observe(
        position.target,
        position.x,
        position.y,
        position.metadata)
    ) {
      scheduleFlush();
    }
  }

  function sendRuntimeMessage(message) {
    try {
      const result = chrome.runtime.sendMessage(message);
      if (result && typeof result.catch === "function") {
        result.catch(() => {});
      }
    } catch {
      // A sleeping/restarting service worker will be available on a later event.
    }
  }

  function invalidate(reason) {
    if (invalidated) {
      return;
    }

    invalidated = true;
    accumulator.invalidate();
    if (animationFrame !== 0) {
      globalThis.cancelAnimationFrame(animationFrame);
      animationFrame = 0;
    }

    sendRuntimeMessage({
      type: "contentInvalidated",
      documentToken,
      reason
    });
  }

  function onResize() {
    if (suspended) return;
    if (readDevicePixelRatio() !== initialDevicePixelRatio) {
      invalidate("devicePixelRatioChanged");
    }
  }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message || message.type !== "queryDocumentState") {
      return false;
    }

    sendResponse({
      documentToken,
      devicePixelRatio: readDevicePixelRatio(),
      viewportSize: readViewportSize()
    });
    return false;
  });

  const root = readRootScroll();
  accumulator.observe(root.target, root.x, root.y);
  document.addEventListener("scroll", onScroll, {
    capture: true,
    passive: true
  });
  for (const inputEvent of ["wheel", "pointerdown", "touchstart", "keydown"]) {
    document.addEventListener(inputEvent, primeNestedTargets, {
      capture: true,
      passive: true
    });
  }
  globalThis.addEventListener("resize", onResize, { passive: true });
  globalThis.addEventListener(
    "pagehide",
    event => {
      if (!event.persisted) { invalidate("documentUnloaded"); return; }
      // BFCache freezes this same document. Drop pending screen movement, while
      // retaining its token and the translation content scripts' completed state.
      suspended = true;
      accumulator.invalidate();
      if (animationFrame !== 0) {
        globalThis.cancelAnimationFrame(animationFrame);
        animationFrame = 0;
      }
    });
  globalThis.addEventListener("pageshow", event => {
    if (!event.persisted || invalidated || !suspended) return;
    suspended = false;
    accumulator.invalidate();
    const restored = readRootScroll();
    accumulator.observe(restored.target, restored.x, restored.y);
    initialDevicePixelRatio = readDevicePixelRatio();
    sendRuntimeMessage({
      type: "contentReady", documentToken, devicePixelRatio: initialDevicePixelRatio,
      viewportSize: readViewportSize()
    });
  });

  sendRuntimeMessage({
    type: "contentReady",
    documentToken,
    devicePixelRatio: initialDevicePixelRatio,
    viewportSize: readViewportSize()
  });

  function readViewportSize() {
    const width = globalThis.innerWidth;
    const height = globalThis.innerHeight;
    return {
      width: Number.isFinite(width) && width >= 0 ? width : 0,
      height: Number.isFinite(height) && height >= 0 ? height : 0
    };
  }
})();
