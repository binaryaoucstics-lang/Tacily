"use strict";
(function exposeManualImageRouter(root) {
  function create({ chrome, postNative, getConfig, isConnected }) {
    const requests = new Map();
    const bounded = (text, max) => typeof text === "string" && text.length > 0 && text.length <= max;
    function finish(id, response, remove = false) {
      const entry = requests.get(id); if (!entry) return;
      if (entry.respond) { const respond = entry.respond; entry.respond = null; clearTimeout(entry.timer); respond(response); }
      if (remove) { clearTimeout(entry.timer); requests.delete(id); }
    }
    async function request(message, sender, respond) {
      const config = getConfig();
      if (!isConnected() || !config.manualAvailable || message.generation !== config.generation
          || sender.frameId !== 0 || !sender.tab?.active || !Number.isInteger(sender.tab.id) || !Number.isInteger(sender.tab.windowId)
          || !bounded(message.requestId, 64) || !bounded(message.imageId, 64) || !bounded(message.documentToken, 256)
          || requests.has(message.requestId) || requests.size >= 4) { respond({ accepted: false, reason: "manual_image_unavailable" }); return; }
      const entry = { tabId: sender.tab.id, windowId: sender.tab.windowId, token: message.documentToken,
        imageId: message.imageId, generation: message.generation, respond, timer: null };
      requests.set(message.requestId, entry);
      entry.timer = setTimeout(() => finish(message.requestId, { accepted: false, reason: "desktop_unavailable" }, true), 12000);
      try {
        const tab = await chrome.tabs.get(entry.tabId);
        const document = await chrome.tabs.sendMessage(tab.id, { type: "queryDocumentState" }, { frameId: 0 });
        const image = await chrome.tabs.sendMessage(tab.id, { type: "queryImageGeometry", imageId: entry.imageId, documentToken: entry.token }, { frameId: 0 });
        const current = await chrome.tabs.get(tab.id), currentConfig = getConfig();
        if (!requests.has(message.requestId) || !current.active || current.windowId !== entry.windowId || document?.documentToken !== entry.token
            || !image?.found || image.imageId !== entry.imageId || image.documentToken !== entry.token
            || !isConnected() || !currentConfig.manualAvailable || currentConfig.generation !== entry.generation) throw new Error("stale selection");
        if (!postNative({ type: "manualImageCandidate", requestId: message.requestId, imageId: entry.imageId,
          documentToken: entry.token, generation: entry.generation, tabId: entry.tabId, browserWindowId: entry.windowId, frameId: 0 })) throw new Error("disconnected");
      } catch { finish(message.requestId, { accepted: false, reason: "image_changed" }, true); }
    }
    async function forward(message) {
      const entry = requests.get(message?.requestId);
      if (!entry || message.tabId !== entry.tabId || message.documentToken !== entry.token || message.imageId !== entry.imageId
          || message.generation !== entry.generation || !["translating", "completed", "failed", "cancelled"].includes(message.state)) return;
      const running = message.state === "translating";
      finish(message.requestId, { accepted: running || message.state === "completed", reason: message.reason }, !running);
      if (running) { clearTimeout(entry.timer); entry.timer = setTimeout(() => requests.delete(message.requestId), 10 * 60 * 1000); }
      try {
        const current = await chrome.tabs.get(entry.tabId);
        const document = await chrome.tabs.sendMessage(entry.tabId, { type: "queryDocumentState" }, { frameId: 0 });
        if (current.windowId !== entry.windowId || document?.documentToken !== entry.token) return;
        await chrome.tabs.sendMessage(entry.tabId, { type: "manualImageStatus", requestId: message.requestId,
          imageId: entry.imageId, documentToken: entry.token, generation: entry.generation,
          state: message.state, reason: typeof message.reason === "string" ? message.reason.slice(0, 64) : "" }, { frameId: 0 });
      } catch { /* Navigated or closed tab. */ }
    }
    function disconnect() { for (const id of requests.keys()) finish(id, { accepted: false, reason: "desktop_unavailable" }, true); }
    return { request, forward, disconnect };
  }
  root.ScreenTranslatorManualImageBackground = { create };
  if (typeof module !== "undefined" && module.exports) module.exports = root.ScreenTranslatorManualImageBackground;
})(globalThis);
