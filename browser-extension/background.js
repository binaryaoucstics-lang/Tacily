"use strict";

importScripts("document-state.js");
importScripts("selected-image-fetch.js");
importScripts("image-auto-policy.js");
importScripts("browser-preferences.js");
importScripts("page-auto-background.js");
importScripts("manual-image-background.js");

const fetchSelectedImage = ScreenTranslatorSelectedImageFetch.createFetcher({
  fetch: globalThis.fetch.bind(globalThis),
  rules: chrome.declarativeNetRequest,
  extensionId: chrome.runtime.id
});

const HOST_NAME = "com.screentranslator.browser_bridge";
const RECONNECT_MIN_MS = 1000;
const RECONNECT_MAX_MS = 5000;
const browserKind = navigator.userAgent.includes("Edg/") ? "edge" : "chrome";

let nativePort;
let translationMotionEnabled = true;
async function pushTranslationMotionConfig(tab) {
  if (!Number.isInteger(tab?.id)) return;
  try { await chrome.tabs.sendMessage(tab.id, { type: "translationMotionConfig", enabled: translationMotionEnabled }, { frameId: 0 }); } catch { }
}
let reconnectTimer;
let reconnectDelayMs = RECONNECT_MIN_MS;
let lastGeneration = Date.now();
const documentsByFrame = new Map();
const activeTabByWindow = new Map();
let lastFocusedWindowId = null;
let imageAutoConfig = { enabled: false, targetLanguage: "简体中文", generation: 0 };
let autoPreviewQueue = Promise.resolve();
const pendingAssessments = new Map();
const pageAutoRouter = ScreenTranslatorPageAutoBackground.create({
  chrome, postNative, browserKind, ensureDocument: ensureTopFrameState,
  readDocument: tab => chrome.tabs.sendMessage(tab.id, { type: "queryDocumentState" }, { frameId: 0 })
});
const quickSettingsRouter = ScreenTranslatorBrowserPreferences.createQuickRouter({ chrome, postNative,
  isConnected: () => nativePort !== undefined && browserKind === "chrome", getConfig: pageAutoRouter.getConfig,
  readDocument: tab => chrome.tabs.sendMessage(tab.id, { type: "queryDocumentState" }, { frameId: 0 }) });
const manualImageRouter = ScreenTranslatorManualImageBackground.create({ chrome, postNative, getConfig: pageAutoRouter.getConfig,
  isConnected: () => nativePort !== undefined && browserKind === "chrome" });
const imageSiteRule = tab => ScreenTranslatorBrowserPreferences.rule(ScreenTranslatorBrowserPreferences.hostname(tab?.url),
  imageAutoConfig.siteWhitelist, imageAutoConfig.siteBlacklist);

function finishAssessment(requestId, result) {
  const pending = pendingAssessments.get(requestId);
  if (!pending) return;
  pendingAssessments.delete(requestId); clearTimeout(pending.timer); pending.respond(result);
}
function clearAssessments(reason) {
  for (const [requestId, pending] of pendingAssessments) finishAssessment(requestId, {
    needsTranslation: false, reason, generation: pending.generation });
}
async function pushImageAutoConfig(tab) {
  if (!Number.isInteger(tab?.id)) return;
  try {
    await chrome.tabs.sendMessage(tab.id, { ...imageAutoConfig, type: "imageAutoConfig",
      siteWhitelist: undefined, siteBlacklist: undefined,
      enabled: imageAutoConfig.enabled && browserKind === "chrome" && imageSiteRule(tab).mode !== "block", active: tab.active === true }, { frameId: 0 });
  } catch { /* Internal/reloading pages do not contain our top-frame observer. */ }
}
async function pushImageAutoConfigToTabs() {
  try { for (const tab of await chrome.tabs.query({})) void pushImageAutoConfig(tab); }
  catch { /* Browser shutdown. */ }
}
function updateImageAutoConfig(message) {
  const normalized = ScreenTranslatorImageAutoPolicy.normalizeConfig(message);
  if (!normalized || normalized.generation < imageAutoConfig.generation) return;
  const next = { ...normalized, siteWhitelist: ScreenTranslatorBrowserPreferences.domains(message.siteWhitelist),
    siteBlacklist: ScreenTranslatorBrowserPreferences.domains(message.siteBlacklist) };
  if (next.enabled === imageAutoConfig.enabled && next.generation === imageAutoConfig.generation
      && next.targetLanguage === imageAutoConfig.targetLanguage
      && next.requirePageJudgement === imageAutoConfig.requirePageJudgement
      && JSON.stringify(next.siteWhitelist) === JSON.stringify(imageAutoConfig.siteWhitelist || [])
      && JSON.stringify(next.siteBlacklist) === JSON.stringify(imageAutoConfig.siteBlacklist || [])) return;
  imageAutoConfig = next;
  clearAssessments("configuration_changed");
  void pushImageAutoConfigToTabs();
}

function frameKey(tabId, frameId) {
  return `${tabId}:${frameId}`;
}

function nextGeneration() {
  const now = Date.now();
  lastGeneration = Math.max(now, lastGeneration + 1);
  return lastGeneration;
}

function rememberDocument(
  tabId,
  frameId,
  token,
  devicePixelRatio,
  viewportSize) {
  const key = frameKey(tabId, frameId);
  const current = documentsByFrame.get(key);
  if (current && current.documentToken === token) {
    current.devicePixelRatio = devicePixelRatio;
    if (viewportSize) {
      current.viewportSize = viewportSize;
    }
    return current;
  }

  const state = {
    documentToken: token,
    navigationGeneration: nextGeneration(),
    devicePixelRatio,
    viewportSize: viewportSize ?? null
  };
  documentsByFrame.set(key, state);
  return state;
}

function forgetTab(tabId) {
  pageAutoRouter.cancelTab(tabId);
  const prefix = `${tabId}:`;
  for (const key of documentsByFrame.keys()) {
    if (key.startsWith(prefix)) {
      documentsByFrame.delete(key);
    }
  }
  for (const [requestId, pending] of pendingAssessments) {
    if (pending.tabId === tabId) finishAssessment(requestId, { needsTranslation: false, reason: "document_changed" });
  }
}

function invalidateTopFrame(tabId, windowId, reason, retireDocument = true) {
  const state = documentsByFrame.get(frameKey(tabId, 0));
  if (state) {
    postNative({
      type: "invalidated",
      browserWindowId: windowId,
      tabId,
      documentToken: state.documentToken,
      navigationGeneration: state.navigationGeneration,
      reason,
      frameId: 0
    });
  }
  if (retireDocument) forgetTab(tabId);
}

function rememberActiveTab(tabId, windowId) {
  activeTabByWindow.set(windowId, tabId);
  lastFocusedWindowId = windowId;
}

function markConnectionHealthy() {
  reconnectDelayMs = RECONNECT_MIN_MS;
}

function scheduleReconnect() {
  if (reconnectTimer !== undefined || nativePort !== undefined) {
    return;
  }

  const delay = reconnectDelayMs;
  reconnectDelayMs = Math.min(
    reconnectDelayMs * 2,
    RECONNECT_MAX_MS);
  reconnectTimer = setTimeout(() => {
    reconnectTimer = undefined;
    connectNativeHost();
  }, delay);
}

function connectNativeHost() {
  if (nativePort !== undefined) {
    return;
  }

  let connectedPort;
  try {
    connectedPort = chrome.runtime.connectNative(HOST_NAME);
  } catch {
    scheduleReconnect();
    return;
  }

  nativePort = connectedPort;
  connectedPort.onMessage.addListener(message => {
    markConnectionHealthy();
    if (message && message.type === "queryActiveTab") {
      void answerActiveTabQuery(message);
    } else if (message?.type === "queryBrowserDocument") {
      void answerDocumentQuery(message);
    } else if (message && ["queryImage", "queryImageGeometry"].includes(message.type)) {
      void answerImageQuery(message);
    } else if(message?.type?.startsWith("imageOverlay")) {
      void forwardImageOverlay(message);
      } else if (message?.type === "translationMotionConfig" && typeof message.enabled === "boolean") {
        translationMotionEnabled = message.enabled;
        void chrome.tabs.query({}).then(tabs => { for (const tab of tabs) void pushTranslationMotionConfig(tab); }, () => {});
      } else if (message?.type === "imageAutoConfig") {
      updateImageAutoConfig(message);
    } else if (message?.type === "autoImageAssessment") {
      void forwardAutoAssessment(message);
    } else if (message?.type === "pageAutoConfig") {
      pageAutoRouter.update(message);
    } else if (message?.type === "browserQuickSettingsResult") {
      void quickSettingsRouter.receive(message);
    } else if (message?.type === "manualImageStatus") {
      void manualImageRouter.forward(message);
    } else if (["pageAutoAssessment", "pageAutoTranslations"].includes(message?.type)) {
      void pageAutoRouter.forward(message);
    } else if (message?.type === "autoImageRetry") {
      void forwardAutoRetry(message);
    }
  });
  connectedPort.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    if (nativePort === connectedPort) {
      nativePort = undefined;
      pageAutoRouter.disconnect();
      quickSettingsRouter.disconnect();
      manualImageRouter.disconnect();
    imageAutoConfig = { ...imageAutoConfig, enabled: false };
      clearAssessments("bridge_disconnected");
      void pushImageAutoConfigToTabs();
    }
    scheduleReconnect();
  });

  postNative({
    type: "bridgeReady",
    browser: browserKind
  });
  void announceFocusedDocument();
}

function postNative(message) {
  if (nativePort === undefined) {
    scheduleReconnect();
    return false;
  }

  try {
    nativePort.postMessage(message);
    return true;
  } catch {
    nativePort = undefined;
    pageAutoRouter.disconnect();
    imageAutoConfig = { ...imageAutoConfig, enabled: false };
    clearAssessments("bridge_disconnected");
    void pushImageAutoConfigToTabs();
    scheduleReconnect();
    return false;
  }
}

function validDocumentMessage(message) {
  return (
    message &&
    typeof message.documentToken === "string" &&
    message.documentToken.length >= 16 &&
    message.documentToken.length <= 256 &&
    Number.isFinite(message.devicePixelRatio) &&
    message.devicePixelRatio >= 0.5 &&
    message.devicePixelRatio <= 8
  );
}

function normalizeBounds(browserWindow) {
  const values = [
    browserWindow.left,
    browserWindow.top,
    browserWindow.width,
    browserWindow.height
  ];
  if (!values.every(Number.isFinite)) {
    return null;
  }
  if (
    Math.abs(browserWindow.left) > 1000000 ||
    Math.abs(browserWindow.top) > 1000000 ||
    browserWindow.width <= 0 ||
    browserWindow.height <= 0 ||
    browserWindow.width > 1000000 ||
    browserWindow.height > 1000000
  ) {
    return null;
  }

  return {
    left: browserWindow.left,
    top: browserWindow.top,
    width: browserWindow.width,
    height: browserWindow.height
  };
}

async function forwardScrollBatch(message, sender) {
  if (
    !sender.tab ||
    sender.tab.active !== true ||
    !Number.isInteger(sender.tab.id) ||
    !Number.isInteger(sender.tab.windowId) ||
    !validDocumentMessage(message) ||
    !Array.isArray(message.scrolls)
  ) {
    return;
  }

  const frameId = Number.isInteger(sender.frameId) ? sender.frameId : 0;
  const state = rememberDocument(
    sender.tab.id,
    frameId,
    message.documentToken,
    message.devicePixelRatio,
    normalizeSize(message.viewportSize));

  for (const scroll of message.scrolls) {
    if (
      !scroll ||
      typeof scroll.target !== "string" ||
      scroll.target.length === 0 ||
      scroll.target.length > 256 ||
      !Number.isFinite(scroll.deltaXCss) ||
      !Number.isFinite(scroll.deltaYCss) ||
      Math.abs(scroll.deltaXCss) > 100000 ||
      Math.abs(scroll.deltaYCss) > 100000
    ) {
      continue;
    }

    const scrollContainer = scroll.target === "root"
      ? null
      : normalizeScrollContainer(scroll.scrollContainer);
    if (scroll.target !== "root" && !scrollContainer) {
      continue;
    }

    postNative({
      type: "scroll",
      browserWindowId: sender.tab.windowId,
      tabId: sender.tab.id,
      documentToken: state.documentToken,
      navigationGeneration: state.navigationGeneration,
      deltaXCss: scroll.deltaXCss,
      deltaYCss: scroll.deltaYCss,
      devicePixelRatio: message.devicePixelRatio,
      scrollContainer,
      targetId: scroll.target,
      frameId
    });
  }
}

function normalizeScrollContainer(container) {
  if (container == null) {
    return null;
  }

  const values = [
    container.left,
    container.top,
    container.width,
    container.height
  ];
  if (
    !values.every(Number.isFinite) ||
    Math.abs(container.left) > 1000000 ||
    Math.abs(container.top) > 1000000 ||
    container.width <= 0 ||
    container.height <= 0 ||
    container.width > 1000000 ||
    container.height > 1000000
  ) {
    return null;
  }

  return {
    left: container.left,
    top: container.top,
    width: container.width,
    height: container.height
  };
}

async function forwardInvalidation(message, sender) {
  if (
    !sender.tab ||
    sender.tab.active !== true ||
    !Number.isInteger(sender.tab.id) ||
    !Number.isInteger(sender.tab.windowId) ||
    !message ||
    typeof message.documentToken !== "string" ||
    message.documentToken.length === 0 ||
    message.documentToken.length > 256
  ) {
    return;
  }

  const frameId = Number.isInteger(sender.frameId) ? sender.frameId : 0;
  const key = frameKey(sender.tab.id, frameId);
  const state = documentsByFrame.get(key);
  if (!state || state.documentToken !== message.documentToken) {
    return;
  }

  documentsByFrame.delete(key);
  if (frameId !== 0) {
    return;
  }

  postNative({
    type: "invalidated",
    browserWindowId: sender.tab.windowId,
    tabId: sender.tab.id,
    documentToken: state.documentToken,
    navigationGeneration: state.navigationGeneration,
    reason: typeof message.reason === "string"
      ? message.reason
      : "documentInvalidated",
    frameId
  });
}

async function forwardHello(message, sender) {
  if (
    !sender.tab ||
    sender.tab.active !== true ||
    !Number.isInteger(sender.tab.id) ||
    !Number.isInteger(sender.tab.windowId) ||
    !validDocumentMessage(message) ||
    !normalizeSize(message.viewportSize)
  ) {
    return;
  }

  const frameId = Number.isInteger(sender.frameId) ? sender.frameId : 0;
  const viewportSize = normalizeSize(message.viewportSize);
  const state = rememberDocument(
    sender.tab.id,
    frameId,
    message.documentToken,
    message.devicePixelRatio,
    viewportSize);
  if (frameId !== 0) {
    return;
  }

  let browserWindow;
  try {
    browserWindow = await chrome.windows.get(sender.tab.windowId);
  } catch {
    return;
  }
  const browserWindowBounds = normalizeBounds(browserWindow);
  if (!browserWindowBounds) {
    return;
  }

  postHello(
    sender.tab,
    frameId,
    state,
    browserWindowBounds);
}

function postHello(tab, frameId, state, browserWindowBounds) {
  if (!state.viewportSize) {
    return false;
  }

  const posted = postNative({
    type: "hello",
    browser: browserKind,
    browserWindowId: tab.windowId,
    tabId: tab.id,
    documentToken: state.documentToken,
    navigationGeneration: state.navigationGeneration,
    devicePixelRatio: state.devicePixelRatio,
    viewportSize: state.viewportSize,
    browserWindowBounds,
    frameId
  });
  if (posted) {
    markConnectionHealthy();
  }
  return posted;
}

function normalizeSize(size) {
  if (
    !size ||
    !Number.isFinite(size.width) ||
    !Number.isFinite(size.height) ||
    size.width <= 0 ||
    size.height <= 0 ||
    size.width > 1000000 ||
    size.height > 1000000
  ) {
    return null;
  }

  return {
    width: size.width,
    height: size.height
  };
}

async function ensureTopFrameState(tab) {
  const key = frameKey(tab.id, 0);
  const current = documentsByFrame.get(key);
  return ScreenTranslatorDocumentState.recoverCompleteViewportState(
    current,
    async () => {
      let response;
      try {
        response = await chrome.tabs.sendMessage(
          tab.id,
          { type: "queryDocumentState" },
          { frameId: 0 });
      } catch {
        return null;
      }

      const viewportSize = normalizeSize(response?.viewportSize);
      if (!validDocumentMessage(response) || !viewportSize) {
        return null;
      }
      return rememberDocument(
        tab.id,
        0,
        response.documentToken,
        response.devicePixelRatio,
        viewportSize);
    });
}

async function announceFocusedDocument() {
  try {
    const browserWindow = await chrome.windows.getLastFocused();
    const tabs = await chrome.tabs.query({
      active: true,
      windowId: browserWindow.id
    });
    const tab = tabs[0];
    if (
      !tab ||
      !Number.isInteger(tab.id) ||
      !Number.isInteger(tab.windowId)
    ) {
      return;
    }

    await announceTab(tab, browserWindow);
  } catch {
    // Unsupported/internal pages simply remain in static translation mode.
  }
}

async function announceTab(tab, browserWindow = null) {
  if (
    !tab ||
    !Number.isInteger(tab.id) ||
    !Number.isInteger(tab.windowId)
  ) {
    return false;
  }

  let targetWindow = browserWindow;
  if (!targetWindow) {
    try {
      targetWindow = await chrome.windows.get(tab.windowId);
    } catch {
      return false;
    }
  }

  const browserWindowBounds = normalizeBounds(targetWindow);
  if (!browserWindowBounds) {
    return false;
  }

  const state = await ensureTopFrameState(tab);
  if (!state) {
    return false;
  }

  rememberActiveTab(tab.id, tab.windowId);
  return postHello(tab, 0, state, browserWindowBounds);
}

async function answerActiveTabQuery(query) {
  const requestId = typeof query.requestId === "string"
    ? query.requestId
    : null;

  try {
    const browserWindow = await chrome.windows.getLastFocused();
    const browserWindowBounds = normalizeBounds(browserWindow);
    const tabs = await chrome.tabs.query({
      active: true,
      windowId: browserWindow.id
    });
    const tab = tabs[0];
    if (
      !browserWindowBounds ||
      !tab ||
      !Number.isInteger(tab.id) ||
      !Number.isInteger(tab.windowId)
    ) {
      postActiveTabUnavailable(requestId);
      return;
    }

    const state = await ensureTopFrameState(tab);
    if (!state) {
      postActiveTabUnavailable(requestId);
      return;
    }

    rememberActiveTab(tab.id, tab.windowId);
    postNative({
      type: "activeTab",
      requestId,
      found: true,
      browser: browserKind,
      browserWindowId: tab.windowId,
      browserWindowBounds,
      tabId: tab.id,
      frameId: 0,
      documentToken: state.documentToken,
      navigationGeneration: state.navigationGeneration,
      devicePixelRatio: state.devicePixelRatio,
      viewportSize: state.viewportSize
    });
  } catch {
    postActiveTabUnavailable(requestId);
  }
}

function postActiveTabUnavailable(requestId) {
  postNative({
    type: "activeTab",
    requestId,
    found: false,
    browser: browserKind
  });
}

async function fetchAutoPreview(imageUrl, pageUrl) {
  const download = await fetchSelectedImage(imageUrl, pageUrl);
  if (!download.ok || !download.headers.get("content-type")?.startsWith("image/")) throw new Error("Image unavailable");
  const reader = download.body.getReader(), chunks = [];
  let length = 0;
  try {
    while (true) {
      const part = await reader.read();
      if (part.done) break;
      length += part.value.length;
      if (length > 18 * 1024 * 1024) throw new Error("Image too large");
      chunks.push(part.value);
    }
  } finally { await reader.cancel(); }
  const bitmap = await createImageBitmap(new Blob(chunks, { type: download.headers.get("content-type") }));
  try {
    if (bitmap.width * bitmap.height > 24000000) throw new Error("Image too large");
    for (const edge of [1280, 640]) {
      const size = ScreenTranslatorImageAutoPolicy.previewSize(bitmap.width, bitmap.height, edge);
      const canvas = new OffscreenCanvas(size.width, size.height), context = canvas.getContext("2d");
      context.fillStyle = "white"; context.fillRect(0, 0, size.width, size.height);
      context.drawImage(bitmap, 0, 0, size.width, size.height);
      for (const quality of [0.88, 0.72, 0.55, 0.4]) {
        const jpeg = await canvas.convertToBlob({ type: "image/jpeg", quality });
        if (jpeg.size > 196608) continue;
        const bytes = new Uint8Array(await jpeg.arrayBuffer()), strings = [];
        for (let i = 0; i < bytes.length; i += 32768) strings.push(String.fromCharCode(...bytes.subarray(i, i + 32768)));
        return btoa(strings.join(""));
      }
    }
    throw new Error("Preview too large");
  } finally { bitmap.close(); }
}

async function answerDocumentQuery(query) {
  const requestId = query?.requestId;
  const unavailable = () => postNative({ type: "browserDocumentReply", requestId, found: false });
  if (typeof requestId !== "string" || requestId.length > 64 || !Number.isInteger(query.tabId)
      || !Number.isInteger(query.browserWindowId) || typeof query.documentToken !== "string"
      || query.documentToken.length < 1 || query.documentToken.length > 256) { unavailable(); return; }
  try {
    const tab = await chrome.tabs.get(query.tabId);
    if (tab.windowId !== query.browserWindowId) { unavailable(); return; }
    const state = await chrome.tabs.sendMessage(tab.id, { type: "queryDocumentState" }, { frameId: 0 });
    const current = await chrome.tabs.get(query.tabId);
    if (!validDocumentMessage(state) || state.documentToken !== query.documentToken
        || current.windowId !== query.browserWindowId) { unavailable(); return; }
    rememberDocument(tab.id, 0, state.documentToken, state.devicePixelRatio, normalizeSize(state.viewportSize));
    postNative({ type: "browserDocumentReply", requestId, found: true, tabId: tab.id,
      browserWindowId: current.windowId, documentToken: state.documentToken, browser: browserKind, active: current.active === true,
      siteHost: ScreenTranslatorBrowserPreferences.hostname(current.url) || null });
  } catch { unavailable(); }
}

function autoEnabled(generation) {
  return nativePort !== undefined && browserKind === "chrome" && imageAutoConfig.enabled && generation === imageAutoConfig.generation;
}
async function forwardAutoCandidate(message, sender, respond) {
  const reject = reason => respond({ needsTranslation: false, reason, generation: message.generation });
  if (!autoEnabled(message.generation) || imageSiteRule(sender.tab).mode === "block" || sender.frameId !== 0 || !sender.tab?.active
      || !Number.isInteger(sender.tab.id) || !Number.isInteger(sender.tab.windowId)
      || !validDocumentMessage(message) || typeof message.requestId !== "string" || message.requestId.length > 64
      || typeof message.imageId !== "string" || message.imageId.length > 64
      || pendingAssessments.has(message.requestId) || pendingAssessments.size >= 4) {
    reject("automatic_translation_unavailable"); return;
  }
  const pending = { tabId: sender.tab.id, generation: message.generation, imageId: message.imageId,
    documentToken: message.documentToken, respond,
    timer: setTimeout(() => finishAssessment(message.requestId, { needsTranslation: false, reason: "assessment_timeout" }), 40000) };
  pendingAssessments.set(message.requestId, pending);
  const operation = autoPreviewQueue.then(async () => {
    if (!pendingAssessments.has(message.requestId) || !autoEnabled(message.generation)) throw new Error("Cancelled");
    const tab = await chrome.tabs.get(sender.tab.id), state = await ensureTopFrameState(tab);
    if (!tab.active || imageSiteRule(tab).mode === "block" || tab.windowId !== sender.tab.windowId || state?.documentToken !== message.documentToken) throw new Error("Document changed");
    const admission = await chrome.tabs.sendMessage(tab.id, { type: "queryImageGeometry", imageId: message.imageId,
      documentToken: message.documentToken }, { frameId: 0 });
    if (!admission?.found || admission.imageId !== message.imageId || admission.documentToken !== message.documentToken
        || !ScreenTranslatorImageAutoPolicy.eligibleImage(admission) || admission.autoImageAllowed === false
        || imageAutoConfig.requirePageJudgement && admission.autoImageAllowed !== true) throw new Error("Image changed");
    // Read by a registered identity; never fetch a page-supplied URL from a native message.
    let response = await chrome.tabs.sendMessage(tab.id, { type: "queryImagePreview", imageId: message.imageId,
      documentToken: message.documentToken }, { frameId: 0 });
    if (!response?.found || response.imageId !== message.imageId || response.documentToken !== message.documentToken) throw new Error("Image changed");
    const previewBase64 = response.previewBase64 || await fetchAutoPreview(response.url, tab.url);
    if (typeof previewBase64 !== "string" || !previewBase64.length || previewBase64.length > 262144
        || !/^[A-Za-z0-9+/]*={0,2}$/.test(previewBase64)) throw new Error("Invalid preview");
    response = await chrome.tabs.sendMessage(tab.id, { type: "queryImageGeometry", imageId: message.imageId,
      documentToken: message.documentToken }, { frameId: 0 });
    const current = await chrome.tabs.get(tab.id);
    if (!pendingAssessments.has(message.requestId) || !autoEnabled(message.generation) || !current.active
        || current.windowId !== sender.tab.windowId || imageSiteRule(current).mode === "block" || !response?.found
        || !ScreenTranslatorImageAutoPolicy.eligibleImage(response)
        || imageAutoConfig.requirePageJudgement && response.autoImageAllowed !== true
        || response.autoImageAllowed === false) throw new Error("Image changed");
    const posted = postNative({ type: "autoImageCandidate", requestId: message.requestId, imageId: message.imageId,
      documentToken: message.documentToken, tabId: tab.id, browserWindowId: tab.windowId, frameId: 0,
      generation: message.generation, natural: response.natural, previewBase64,
      siteHost: ScreenTranslatorBrowserPreferences.hostname(current.url) || undefined,
      devicePixelRatio: response.devicePixelRatio, viewportSize: response.viewportSize, display: response.display, clip: response.clip });
    if (!posted) throw new Error("Disconnected");
  });
  autoPreviewQueue = operation.catch(() => {});
  try { await operation; }
  catch { finishAssessment(message.requestId, { needsTranslation: false, reason: "image_unavailable", generation: message.generation }); }
}

async function forwardAutoAssessment(message) {
  const pending = pendingAssessments.get(message.requestId);
  if (!pending || message.imageId !== pending.imageId || message.documentToken !== pending.documentToken
      || message.tabId !== pending.tabId || message.generation !== pending.generation) return;
  if (!autoEnabled(message.generation)) { finishAssessment(message.requestId, { needsTranslation: false, reason: "configuration_changed" }); return; }
  let current = false;
  try {
    const tab = await chrome.tabs.get(pending.tabId);
    const geometry = await chrome.tabs.sendMessage(tab.id, { type: "queryImageGeometry", imageId: pending.imageId,
      documentToken: pending.documentToken }, { frameId: 0 });
    const admitted = geometry?.autoImageAdmitted ?? geometry?.autoImageAllowed;
    current = geometry?.found === true && geometry.imageId === pending.imageId
      && geometry.documentToken === pending.documentToken && admitted !== false
      && (!imageAutoConfig.requirePageJudgement || admitted === true);
  } catch { /* Tab closed or source changed while the local classifier was running. */ }
  finishAssessment(message.requestId, { type: "autoImageAssessment", requestId: message.requestId,
    imageId: pending.imageId, documentToken: pending.documentToken, tabId: pending.tabId,
    generation: pending.generation, needsTranslation: current && message.needsTranslation === true,
    reason: current && typeof message.reason === "string" ? message.reason.slice(0, 128) : "image_changed" });
}

async function forwardAutoRetry(message) {
  if (!autoEnabled(message.generation) || !Number.isInteger(message.tabId)
      || typeof message.imageId !== "string" || typeof message.documentToken !== "string") return;
  try {
    const tab = await chrome.tabs.get(message.tabId);
    const state = await ensureTopFrameState(tab);
    if (state?.documentToken !== message.documentToken) return;
    await chrome.tabs.sendMessage(tab.id, message, { frameId: 0 });
  } catch { /* Source/page no longer exists. */ }
}

async function answerImageQuery(request) {
  if (typeof request.requestId !== "string" || request.requestId.length > 64 || !Number.isInteger(request.tabId)) return;
  try {
    const tab = await chrome.tabs.get(request.tabId);
    if (tab.windowId !== request.browserWindowId || !tab.active && request.allowInactive !== true) throw new Error("Tab changed");
    let response = await chrome.tabs.sendMessage(tab.id, request, { frameId: 0 });
    if (!response?.found) throw Object.assign(new Error("No image"), { code: response?.errorCode || "no_image_at_point" });
    let base64 = response.base64;
    if (request.type === "queryImage" && !base64) {
      // Native selection/opted-in automatic candidates use the selected-image referrer path.
      const url = new URL(response.url);
      if (!["http:", "https:"].includes(url.protocol)) throw new Error("Unsupported source");
      const download = await fetchSelectedImage(url.href, tab.url);
      if (!download.ok) throw Object.assign(new Error("Image unavailable"), { code: `image_fetch_${download.status}` });
      if (!download.headers.get("content-type")?.startsWith("image/")) throw Object.assign(new Error("Image unavailable"), { code: "image_content_type_invalid" });
      const reader = download.body.getReader();
      const chunks = []; let length = 0;
      try {
        while (true) {
          const part = await reader.read();
          if (part.done) break;
          length += part.value.length;
          if (length > 18 * 1024 * 1024) throw new Error("Image too large");
          chunks.push(part.value);
        }
      } finally { await reader.cancel(); }
      const bitmap = await createImageBitmap(new Blob(chunks, { type: download.headers.get("content-type") }));
      if (bitmap.width * bitmap.height > 24000000) { bitmap.close(); throw new Error("Image too large"); }
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      const context = canvas.getContext("2d");
      context.fillStyle = "white"; context.fillRect(0, 0, canvas.width, canvas.height);
      context.drawImage(bitmap, 0, 0); bitmap.close();
      const png = await canvas.convertToBlob({ type: "image/png" });
      if (png.size > 18 * 1024 * 1024) throw new Error("Image too large");
      const bytes = new Uint8Array(await png.arrayBuffer());
      const strings = [];
      for (let i = 0; i < bytes.length; i += 32768) strings.push(String.fromCharCode(...bytes.subarray(i, i + 32768)));
      base64 = btoa(strings.join(""));
    }
    // Revalidate document, image identity and geometry after an asynchronous download.
    response = await chrome.tabs.sendMessage(tab.id, { type: "queryImageGeometry", imageId: response.imageId,
      documentToken: response.documentToken }, { frameId: 0 });
    const current = await chrome.tabs.get(tab.id);
    if (!response?.found || !current.active && request.allowInactive !== true || current.windowId !== request.browserWindowId
      || request.documentToken != null && response.documentToken !== request.documentToken
      || request.imageId != null && response.imageId !== request.imageId) throw new Error("Image changed");
    if (request.type === "queryImage") {
      if (typeof base64 !== "string" || base64.length === 0 || base64.length > 24 * 1024 * 1024) throw new Error("Image too large");
      for (let offset = 0, index = 0; offset < base64.length; offset += 262144, index++) {
        if (!postNative({ type: "browserImageChunk", requestId: request.requestId, index, data: base64.slice(offset, offset + 262144) })) return;
      }
    }
    postNative({ ...response, type: "browserImageReply", requestId: request.requestId, base64: undefined, url: undefined,
      base64Length: base64?.length ?? 0 });
  } catch (error) {
    const errorCode = typeof error?.code === "string" && /^[a-z0-9_]{1,64}$/.test(error.code) ? error.code : "image_query_failed";
    postNative({ type: "browserImageReply", requestId: request.requestId, found: false, errorCode });
  }
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (!message || typeof message.type !== "string") {
    return false;
  }

  const pageHandled = pageAutoRouter.content(message, sender, respond);
  if (pageHandled !== null) return pageHandled;
  if (message.type === "manualImageCandidate") { void manualImageRouter.request(message, sender, respond); return true; }
  if (message.type === "browserQuickSettings") {
    void quickSettingsRouter.request(message, sender, respond); return true;
  }
  if (message.type === "queryTranslationMotionConfig") {
    respond({ enabled: translationMotionEnabled });
  } else if (message.type === "queryImageAutoConfig") {
    respond({ ...imageAutoConfig, siteWhitelist: undefined, siteBlacklist: undefined,
      enabled: autoEnabled(imageAutoConfig.generation) && sender.frameId === 0 && imageSiteRule(sender.tab).mode !== "block",
      active: sender.tab?.active === true });
  } else if (message.type === "autoImageCandidate") {
    void forwardAutoCandidate(message, sender, respond); return true;
  } else if(message.type === "imageOverlayDismissed" && sender.frameId === 0 && Number.isInteger(sender.tab?.id)
      && typeof message.imageId === "string" && message.imageId.length <= 64
      && typeof message.documentToken === "string" && message.documentToken.length <= 256
      && typeof message.sessionId === "string" && message.sessionId.length <= 128) {
    postNative({type:"imageOverlayDismissed",tabId:sender.tab.id,imageId:message.imageId,
      documentToken:message.documentToken,sessionId:message.sessionId});
  } else if (message.type === "contentReady") {
    void forwardHello(message, sender);
    if (sender.frameId === 0) {
      void pageAutoRouter.pushConfig(sender.tab);
      void pushImageAutoConfig(sender.tab);
    }
  } else if (message.type === "scrollBatch") {
    void forwardScrollBatch(message, sender);
  } else if (message.type === "contentInvalidated") {
    void forwardInvalidation(message, sender);
  }
  return false;
});

chrome.tabs.onActivated.addListener(async activeInfo => {
  const previousTabId = activeTabByWindow.get(activeInfo.windowId);
  if (
    Number.isInteger(previousTabId) &&
    previousTabId !== activeInfo.tabId
  ) {
    invalidateTopFrame(
      previousTabId,
      activeInfo.windowId,
      "activeTabChanged", false);
    try { const previousTab = await chrome.tabs.get(previousTabId); await pageAutoRouter.pushConfig(previousTab); await pushImageAutoConfig(previousTab); } catch { /* Closed tab. */ }
  }

  let tab;
  try {
    tab = await chrome.tabs.get(activeInfo.tabId);
  } catch {
    return;
  }
  await announceTab(tab);
  await pageAutoRouter.pushConfig(tab);
  await pushImageAutoConfig(tab);
});

chrome.tabs.onRemoved.addListener((tabId, removeInfo) => {
  if (activeTabByWindow.get(removeInfo.windowId) === tabId) {
    activeTabByWindow.delete(removeInfo.windowId);
    invalidateTopFrame(tabId, removeInfo.windowId, "activeTabClosed");
  } else {
    forgetTab(tabId);
  }
});

async function verifyNavigationDocument(tabId, tab) {
  const key = frameKey(tabId, 0), before = documentsByFrame.get(key);
  if (!before) return;
  let state;
  try { state = await chrome.tabs.sendMessage(tabId, { type: "queryDocumentState" }, { frameId: 0 }); }
  catch { /* A real navigation can tear down the previous content world. */ }
  // Chrome also reports loading for a fragment jump while resources are still
  // loading. The content document token is the identity; loading alone is not.
  // Never let a delayed query retire a newer document announced in the meantime.
  if (documentsByFrame.get(key) !== before || state?.documentToken === before.documentToken) return;
  if (Number.isInteger(tab.windowId)) invalidateTopFrame(tabId, tab.windowId, "navigationStarted");
  else forgetTab(tabId);
}

chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  if (typeof changeInfo.url === "string") void pageAutoRouter.navigationChanged(tab);
  if (changeInfo.status === "loading" || changeInfo.status === "complete")
    await verifyNavigationDocument(tabId, tab);
  if (changeInfo.status === "complete") {
      void pushTranslationMotionConfig(tab);
    void pageAutoRouter.pushConfig(tab);
    void pushImageAutoConfig(tab);
  }
});

chrome.windows.onFocusChanged.addListener(async windowId => {
  if (windowId === chrome.windows.WINDOW_ID_NONE) {
    return;
  }

  if (
    Number.isInteger(lastFocusedWindowId) &&
    lastFocusedWindowId !== windowId
  ) {
    const previousTabId = activeTabByWindow.get(lastFocusedWindowId);
    if (Number.isInteger(previousTabId)) {
      invalidateTopFrame(
        previousTabId,
        lastFocusedWindowId,
        "browserWindowFocusChanged", false);
    }
  }

  try {
    const tabs = await chrome.tabs.query({
      active: true,
      windowId
    });
    await announceTab(tabs[0]);
  } catch {
    // A closing or internal browser window has no trackable document.
  }
});

connectNativeHost();

async function forwardImageOverlay(message) {
  if(!Number.isInteger(message.tabId)||typeof message.requestId!=="string")return;
  try {const answer=await chrome.tabs.sendMessage(message.tabId,message,{frameId:0});
    postNative({type:"imageOverlayAck",requestId:message.requestId,found:answer?.found===true});}
  catch {postNative({type:"imageOverlayAck",requestId:message.requestId,found:false});}
}
