"use strict";
(function (root) {
  function hostname(value) {
    if (typeof value !== "string" || !value.trim() || value.length > 2048) return "";
    let input = value.trim().replace(/^\*\./u, ""); if (/[\s\u0000-\u001f]/u.test(input)) return "";
    if (!input.includes("://")) input = `https://${input.includes(":") && !input.startsWith("[") && (input.match(/:/gu) || []).length > 1 ? `[${input}]` : input}`;
    try {
      const url = new URL(input);
      if (!["http:", "https:"].includes(url.protocol) || url.username || url.password) return "";
      const host = url.hostname.toLowerCase().replace(/\.$/u, "").replace(/^\[|\]$/gu, "");
      return host && host.length <= 253 && !/[*%]/u.test(host) ? host : "";
    } catch { return ""; }
  }
  function domains(value) { return Array.isArray(value) ? [...new Set(value.slice(0, 512).map(hostname).filter(Boolean))] : []; }
  function rule(host, white = [], black = []) {
    let mode = "default", domain = "";
    for (const [list, next] of [[white, "allow"], [black, "block"]]) for (const entry of list) {
      if ((host === entry || host.endsWith(`.${entry}`)) && entry.length >= domain.length) { mode = next; domain = entry; }
    }
    return { mode, domain };
  }
  function quickSettings(config, tab, connected = true) {
    const host = hostname(tab?.url), effective = rule(host, config.siteWhitelist, config.siteBlacklist);
    const directMode = config.siteBlacklist?.includes(host) ? "block" : config.siteWhitelist?.includes(host) ? "allow" : "default";
    return { connected, host, siteMode: directMode, effectiveMode: effective.mode, matchedDomain: effective.domain,
      autoPage: config.enabled === true, autoImages: config.autoImages === true,
      targetLanguage: config.targetLanguage || "zh-CN", concurrency: config.concurrency || 10 };
  }
  function patch(value) {
    if (!value || typeof value !== "object" || Array.isArray(value)) return null;
    const result = {}; const entries = Object.entries(value); if (!entries.length || entries.length > 5) return null;
    for (const [key, item] of entries) {
      if (["autoPage", "autoImages"].includes(key) && typeof item === "boolean"
          || key === "targetLanguage" && ["zh-CN", "zh-TW", "en", "ja", "ko"].includes(item)
          || key === "concurrency" && Number.isInteger(item) && item >= 1 && item <= 10
          || key === "siteMode" && ["default", "allow", "block"].includes(item)) result[key] = item;
      else return null;
    }
    return result;
  }
  function createQuickRouter({ chrome, postNative, isConnected, getConfig, readDocument }) {
    const pending = new Map();
    function finish(id, result) { const entry = pending.get(id); if (!entry) return; pending.delete(id); clearTimeout(entry.timer); entry.respond(result); }
    async function request(message, sender, respond) {
      let id;
      try {
        const changes = patch(message.changes), config = getConfig();
        if (!isConnected() || !config.manualAvailable) throw new Error("desktop_unavailable");
        if (sender.frameId !== 0 || !sender.tab?.active || typeof message.requestId !== "string" || !message.requestId.length
            || message.requestId.length > 64 || pending.has(message.requestId) || pending.size >= 8 || !changes
            || typeof message.documentToken !== "string" || message.documentToken.length > 256 || message.generation !== config.generation)
          throw new Error("invalid_request");
        id = message.requestId;
        pending.set(id, { respond, tabId: sender.tab.id, windowId: sender.tab.windowId, token: message.documentToken, host: hostname(sender.tab.url),
          timer: setTimeout(() => finish(id, { ok: false, reason: "settings_timeout" }), 10000) });
        const tab = await chrome.tabs.get(sender.tab.id), state = await readDocument(tab);
        const current = await chrome.tabs.get(tab.id);
        if (!pending.has(id)) return;
        if (!current.active || current.windowId !== sender.tab.windowId || state?.documentToken !== message.documentToken)
          throw new Error("document_changed");
        const host = hostname(current.url);
        if (changes.siteMode && !host) throw new Error("site_unavailable");
        pending.get(id).host = host;
        if (!postNative({ type: "browserQuickSettings", requestId: id, frameId: 0, tabId: tab.id, browserWindowId: current.windowId,
          documentToken: message.documentToken, generation: config.generation, siteHost: host || null, changes })) throw new Error("desktop_unavailable");
      } catch (error) {
        const result = { ok: false, reason: ["desktop_unavailable", "invalid_request", "document_changed", "site_unavailable"].includes(error.message) ? error.message : "settings_unavailable" };
        if (id && pending.has(id)) finish(id, result); else respond(result);
      }
    }
    async function receive(message) {
      const entry = pending.get(message.requestId);
      if (!entry || message.tabId !== entry.tabId || message.documentToken !== entry.token) return;
      try {
        const tab = await chrome.tabs.get(entry.tabId), state = await readDocument(tab);
        if (tab.windowId !== entry.windowId || state?.documentToken !== entry.token || hostname(tab.url) !== entry.host)
          return finish(message.requestId, { ok: false, reason: "document_changed" });
        finish(message.requestId, message.ok === true ? { ok: true, settings: quickSettings(getConfig(), tab, isConnected()) }
          : { ok: false, reason: message.reason || "settings_unavailable" });
      } catch { finish(message.requestId, { ok: false, reason: "document_changed" }); }
    }
    return { request, receive, disconnect() { for (const id of pending.keys()) finish(id, { ok: false, reason: "desktop_unavailable" }); } };
  }
  const api = { hostname, domains, rule, quickSettings, patch, createQuickRouter };
  root.ScreenTranslatorBrowserPreferences = api;
  if (typeof module !== "undefined") module.exports = api;
})(globalThis);
