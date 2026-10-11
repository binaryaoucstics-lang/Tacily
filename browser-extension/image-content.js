(function () {
  "use strict";
  const images = new Map();
  const identities = new WeakMap();
  const autoPolicy = globalThis.ScreenTranslatorImageAutoPolicy;
  const token = globalThis.ScreenTranslatorDocumentToken || autoPolicy.randomIdentifier();
  const maximumPixels = 24000000;
  const maximumBase64 = 24 * 1024 * 1024;
  const geometry = globalThis.ScreenTranslatorImageGeometry;
  let imageAutoQueue = null;
  function expandedSocialImage(image) {
    let social = false;
    try { social = /^(?:www\.)?(?:x\.com|twitter\.com)$/iu.test(new URL(document.location?.href).hostname); } catch { }
    if (!social || !image?.isConnected) return false;
    for (let parent = image.parentElement; parent; parent = parent.parentElement) {
      if (parent.getAttribute?.("role") === "dialog" || parent.getAttribute?.("data-testid") === "swipe-to-dismiss") return true;
    }
    return false;
  }
  function automaticImageAdmitted(image) {
    return imageAutoQueue !== null && imageAutoQueue.config.enabled
      && (autoPolicy.pageAllowsImages(imageAutoQueue.config, globalThis.ScreenTranslatorPageAutoDecision) || expandedSocialImage(image));
  }
  function automaticImageAllowed(image) {
    if (!automaticImageAdmitted(image) || !imageAutoQueue.config.active || document.visibilityState !== "visible") return false;
    if (!image) return true;
    try { return autoPolicy.eligibleImage({ ...describe(image), complete: image.complete, connected: image.isConnected }); }
    catch { return false; }
  }

  function rememberImage(image, pixelSize) {
    const src = image.currentSrc || image.src;
    const previous = identities.get(image);
    const imageId = previous?.src === src ? previous.id : autoPolicy.randomIdentifier();
    identities.set(image, { id: imageId, src });
    const naturalSize = { width: image.naturalWidth, height: image.naturalHeight };
    const saved = images.get(imageId);
    const previousPixels = saved?.src === src && saved.naturalSize?.width === naturalSize.width
      && saved.naturalSize?.height === naturalSize.height ? saved.pixelSize : undefined;
    // Density descriptors can make naturalWidth CSS-sized while the decoded
    // original has twice as many pixels. Keep this local read identity off-wire.
    images.set(imageId, { image, src, naturalSize, pixelSize: pixelSize || previousPixels });
    if (images.size > 256) {
      const oldest = images.keys().next().value;
      if (oldest !== imageId) images.delete(oldest);
    }
    return imageId;
  }

  function savedImage(request) {
    const saved = images.get(request.imageId);
    if (request.documentToken !== token || !saved || saved.src !== (saved.image.currentSrc || saved.image.src)
        || saved.naturalSize?.width !== saved.image.naturalWidth || saved.naturalSize?.height !== saved.image.naturalHeight) throw new Error("Image changed");
    return saved.image;
  }

  function describe(image, styleCache) {
    return geometry.describeImageElement(image, globalThis, styleCache);
  }
  function imageAtPoint(x, y) {
    let hit = document.elementFromPoint(x, y);
    const seen = new Set();
    while (hit?.shadowRoot?.elementFromPoint && !seen.has(hit.shadowRoot)) {
      seen.add(hit.shadowRoot); const inner = hit.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === hit) break; hit = inner;
    }
    if (hit instanceof HTMLImageElement) return hit;
    // Viewers can place an empty interaction layer over the image. Never
    // select a photo behind actual text or controls.
    if (!hit || String(hit.textContent || "").trim() || hit.closest?.("button,input,textarea,[data-screen-translator]")) return null;
    const container = hit.closest?.('[data-testid="tweetPhoto"]') || hit;
    const candidates = container.querySelectorAll?.("img");
    return candidates?.length === 1 ? candidates[0] : null;
  }
  globalThis.ScreenTranslatorManualImages = {
    pick(x, y) {
      if (window !== top || document.visibilityState !== "visible" || ![x, y].every(Number.isFinite)) return null;
      const image = imageAtPoint(x, y);
      if (!image?.isConnected || !image.complete || image.naturalWidth <= 0 || image.naturalHeight <= 0) return null;
      try {
        const { clip } = describe(image);
        if (x < clip.left || y < clip.top || x >= clip.left + clip.width || y >= clip.top + clip.height) return null;
        return { imageId: rememberImage(image), documentToken: token };
      } catch { return null; }
    }
  };

  async function readImage(image, preview = false) {
    const admissionEpoch = imageAutoQueue?.epoch;
    if (preview && !automaticImageAllowed(image)) throw new Error("Automatic image gate closed");
    const bitmap = await createImageBitmap(image);
    const pixelSize = { width: bitmap.width, height: bitmap.height };
    if (preview && (!automaticImageAllowed(image) || admissionEpoch !== imageAutoQueue?.epoch)) {
      bitmap.close(); throw new Error("Automatic image gate closed");
    }
    if (bitmap.width * bitmap.height > maximumPixels) { bitmap.close(); throw new Error("Image too large"); }
    const canvas = document.createElement("canvas");
    const size = preview ? autoPolicy.previewSize(bitmap.width, bitmap.height) : bitmap;
    canvas.width = size.width; canvas.height = size.height;
    const context = canvas.getContext("2d");
    context.fillStyle = "white"; context.fillRect(0, 0, canvas.width, canvas.height);
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close();
    try {
      if (preview) {
        for (const quality of [0.88, 0.72, 0.55, 0.4]) {
          const previewBase64 = canvas.toDataURL("image/jpeg", quality).split(",")[1];
          if (previewBase64.length <= autoPolicy.maximumPreviewCharacters) return { previewBase64 };
        }
        // High entropy photographs can exceed the budget even at low JPEG quality.
        const smaller = document.createElement("canvas");
        const smallSize = autoPolicy.previewSize(canvas.width, canvas.height, 640);
        smaller.width = smallSize.width; smaller.height = smallSize.height;
        smaller.getContext("2d").drawImage(canvas, 0, 0, smaller.width, smaller.height);
        const previewBase64 = smaller.toDataURL("image/jpeg", 0.55).split(",")[1];
        if (previewBase64.length > autoPolicy.maximumPreviewCharacters) throw new Error("Image too large");
        return { previewBase64 };
      }
      const base64 = canvas.toDataURL("image/png").split(",")[1];
      if (base64.length > maximumBase64) throw new Error("Image too large");
      return { base64, pixelSize };
    } catch (error) {
      if (error.name !== "SecurityError") throw error;
      // The service worker can read selected/opted-in observed images with host permissions.
      return { url: image.currentSrc || image.src, pixelSize: preview ? undefined : pixelSize };
    }
  }

  chrome.runtime.onMessage.addListener((request, _sender, respond) => {
    if(request?.type?.startsWith("imageOverlay")) {respond(globalThis.ScreenTranslatorImageOverlay.receive(request,token,id=>images.get(id),describe));return false;}
    if (!["queryImage", "queryImageGeometry", "queryImagePreview"].includes(request?.type)) return false;
    (async () => {
      if (window !== top) throw new Error("Subframe requires screenshot fallback");
      if (request.type === "queryImageGeometry") {
        const image = savedImage(request), description = describe(image);
        return { found: true, imageId: request.imageId, documentToken: token,
          autoImageAllowed: automaticImageAllowed(image) && autoPolicy.eligibleImage(description),
          autoImageAdmitted: automaticImageAdmitted(image), ...description };
      }
      const x = request.x / devicePixelRatio, y = request.y / devicePixelRatio;
      let hit = typeof request.imageId === "string" ? savedImage(request) : imageAtPoint(x, y);
      // Traverse an open shadow root without trusting a page-supplied URL.
      while (hit?.shadowRoot?.elementFromPoint) {
        const inner = hit.shadowRoot.elementFromPoint(x, y);
        if (!inner || inner === hit) break;
        hit = inner;
      }
      if (!(hit instanceof HTMLImageElement)) throw new Error("No readable image at point");
      if (request.type === "queryImagePreview" && !automaticImageAllowed(hit)) throw new Error("Automatic image gate closed");
      const before = describe(hit), src = hit.currentSrc || hit.src;
      const { pixelSize, ...data } = await readImage(hit, request.type === "queryImagePreview");
      if (!hit.isConnected || src !== (hit.currentSrc || hit.src)
          || before.natural.width !== hit.naturalWidth || before.natural.height !== hit.naturalHeight) throw new Error("Image changed");
      const imageId = rememberImage(hit, pixelSize);
      return { found: true, imageId, documentToken: token, ...before, ...data };
    })().then(respond, error => {
      const errorCode = error?.message === "No readable image at point" ? "no_image_at_point"
        : error?.message === "Unsupported transform" ? "unsupported_image_transform"
        : error?.message === "Image changed" ? "image_changed"
        : error?.message === "Subframe requires screenshot fallback" ? "subframe_unsupported"
        : error?.message === "Automatic image gate closed" ? "automatic_image_gate_closed"
        : "image_pixels_unavailable";
      respond({ found: false, errorCode });
    });
    return true;
  });

  if (window !== top || !autoPolicy) return;
  const visible = new Set();
  let watched = new WeakSet();
  let scanIndex = 0, scanScheduled = false, unloaded = false, offerFrame = 0, rescanTimer = 0, pageGateSignature = null;
  const scheduleIdle = callback => typeof requestIdleCallback === "function"
    ? requestIdleCallback(callback, { timeout: 500 }) : setTimeout(callback, 32);

  function sourceVersion(image) {
    return `${image.currentSrc || image.src}|${image.naturalWidth}x${image.naturalHeight}`;
  }
  function current(image, version) {
    if (unloaded || !visible.has(image) || sourceVersion(image) !== version) return false;
    return automaticImageAllowed(image);
  }
  function offerVisible() {
    for (const image of visible) {
      if (!image.isConnected) { visible.delete(image); continue; }
      queue.offer(image, sourceVersion(image));
    }
  }
  const queue = new autoPolicy.AutoImageQueue({ isCurrent: current,
    isSameSource: (image, version) => image.isConnected && sourceVersion(image) === version,
    read: async (image, version) => {
      const imageId = rememberImage(image);
      if (!current(image, version)) throw new Error("Image changed");
      // The worker requests/encodes one preview at a time across all tabs.
      return { type: "autoImageCandidate", requestId: autoPolicy.randomIdentifier(), imageId, documentToken: token, ...describe(image) };
    },
    send: message => new Promise(resolve => {
      let complete = false;
      const finish = result => { if (!complete) { complete = true; clearTimeout(timer); resolve(result); } };
      const timer = setTimeout(() => finish({ needsTranslation: false, reason: "assessment_timeout" }), 45000);
      try {
        chrome.runtime.sendMessage(message, result => { void chrome.runtime.lastError; finish(result); });
      } catch { finish(null); }
    }),
    onSettled: () => scheduleIdle(offerVisible)
  });
  imageAutoQueue = queue;

  function refreshPageGate() {
    if (!queue.config.requirePageJudgement) { pageGateSignature = null; return; }
    // This value belongs to the extension's isolated world. An arbitrary page can
    // dispatch the wake-up event, but its detail can never authorize image pixels.
    const decision = globalThis.ScreenTranslatorPageAutoDecision;
    const signature = `${automaticImageAdmitted()}|${decision?.generation}`;
    if (signature !== pageGateSignature) {
      pageGateSignature = signature; queue.suspendPending();
    }
    if (queue.config.enabled && queue.config.active) offerVisible();
  }
  const intersection = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (entry.isIntersecting) visible.add(entry.target);
      else visible.delete(entry.target);
    }
    if (queue.config.enabled) offerVisible();
  }, { threshold: [0, 0.01, 0.05, 0.1, 0.25, 0.5] });
  const resizing = typeof ResizeObserver === "function" ? new ResizeObserver(scheduleVisibleOffer) : null;

  function watch(image) {
    if (!(image instanceof HTMLImageElement) || watched.has(image)) return;
    watched.add(image); intersection.observe(image); resizing?.observe(image);
  }
  function scan() {
    scanScheduled = false;
    if (!queue.config.enabled || unloaded) return;
    // The live collection avoids a synchronous querySelectorAll/DOM-tree walk on large readers.
    const pageImages = document.images;
    const end = Math.min(pageImages.length, scanIndex + 64);
    for (; scanIndex < end; scanIndex++) watch(pageImages[scanIndex]);
    if (scanIndex < pageImages.length) scheduleScan();
  }
  function scheduleScan() {
    if (scanScheduled || !queue.config.enabled || unloaded) return;
    scanScheduled = true; scheduleIdle(scan);
  }
  const mutations = new MutationObserver(records => {
    if (!queue.config.enabled || unloaded) return;
    for (const record of records) {
      if (record.type === "attributes" && record.target instanceof HTMLImageElement) {
        watch(record.target);
        if (visible.has(record.target)) queue.offer(record.target, sourceVersion(record.target));
      } else if (record.type === "attributes") {
        // Lightboxes also enlarge a retained image via an ancestor's class/transform.
        if (!record.target.closest?.("[data-screen-translator]")) scheduleVisibleOffer();
      } else if (record.type === "childList") {
        // Coalesce large SPAs' mutation bursts; insertions can precede the former cursor.
        if (!rescanTimer) rescanTimer = setTimeout(() => {
          rescanTimer = 0; scanIndex = 0; scheduleScan();
        }, 80);
      }
    }
  });
  function configure(config) {
    if (unloaded || !queue.configure(config)) return;
    refreshPageGate();
    if (queue.config.enabled) {
      observeMutations();
      scanIndex = 0; scheduleScan(); offerVisible();
    } else {
      mutations.disconnect(); intersection.disconnect(); resizing?.disconnect();
      watched = new WeakSet(); visible.clear();
    }
  }
  function observeMutations() {
    mutations.observe(document, { subtree: true, childList: true, attributes: true,
      attributeFilter: ["src", "srcset", "sizes", "width", "height", "style", "class", "hidden", "open", "aria-hidden"] });
  }
  function requestConfig() {
    try { chrome.runtime.sendMessage({ type: "queryImageAutoConfig" }, config => { void chrome.runtime.lastError; configure(config); }); }
    catch { /* Desktop/extension not ready yet; background pushes on the next connection. */ }
  }
  chrome.runtime.onMessage.addListener((request, _sender, respond) => {
    if (request?.type === "autoImageRetry") {
      if (queue.config.enabled && request.generation === queue.config.generation && request.documentToken === token) {
        try {
          const image = savedImage(request);
          queue.retry(image, sourceVersion(image));
          // Reentering the viewport/scroll/load schedules a retry. No immediate retry loop.
          respond({ found: true });
        } catch { respond({ found: false }); }
      } else respond({ found: false });
      return false;
    }
    if (request?.type !== "imageAutoConfig") return false;
    configure(request); respond({ found: true }); return false;
  });
  document.addEventListener("load", event => {
    if (!(event.target instanceof HTMLImageElement) || !queue.config.enabled) return;
    watch(event.target); if (visible.has(event.target)) queue.offer(event.target, sourceVersion(event.target));
  }, true);
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") { requestConfig(); offerVisible(); } });
  document.addEventListener("ScreenTranslatorPageAssessmentChanged", refreshPageGate);
  function scheduleVisibleOffer() {
    if (offerFrame || !queue.config.enabled || unloaded) return;
    offerFrame = requestAnimationFrame(() => { offerFrame = 0; offerVisible(); });
  }
  document.addEventListener("scroll", scheduleVisibleOffer, { capture: true, passive: true });
  document.addEventListener("click", scheduleVisibleOffer, { capture: true, passive: true });
  document.addEventListener("transitionend", scheduleVisibleOffer, { capture: true, passive: true });
  document.addEventListener("animationend", scheduleVisibleOffer, { capture: true, passive: true });
  document.addEventListener("fullscreenchange", scheduleVisibleOffer, { passive: true });
  addEventListener("resize", scheduleVisibleOffer, { passive: true });
  addEventListener("pagehide", () => {
    unloaded = true; queue.suspendPending(); mutations.disconnect(); intersection.disconnect(); resizing?.disconnect(); visible.clear();
    if (offerFrame) { cancelAnimationFrame(offerFrame); offerFrame = 0; }
    if (rescanTimer) { clearTimeout(rescanTimer); rescanTimer = 0; }
  });
  addEventListener("pageshow", event => { if (event.persisted) {
    unloaded = false; watched = new WeakSet(); requestConfig();
    if (queue.config.enabled) {
      observeMutations();
      scanIndex = 0; scheduleScan();
    }
  } });
  requestConfig();
})();
