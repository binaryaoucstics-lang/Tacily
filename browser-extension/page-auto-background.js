(function exposePageRouter(root) {
  "use strict";
  const preferences = root.ScreenTranslatorBrowserPreferences || (typeof require === "function" ? require("./browser-preferences.js") : null);
  function create({ chrome, postNative, browserKind, ensureDocument, readDocument = ensureDocument }) {
    let config = { enabled: false, manualAvailable: false, targetLanguage: "zh-CN", generation: 0, judgementMode: "Rules", concurrency: 10,
      cacheScope: "", cacheEpoch: 0, siteWhitelist: [], siteBlacklist: [], autoImages: false };
    const pending = new Map();
    const reservations = new Map();
    const documents = new Map();
    const token = value => typeof value === "string" && value.length > 0 && value.length <= 256;
    const identifier = (value, maximum) => token(value) && value.length <= maximum;
    const revision = value => Number.isSafeInteger(value) && value >= 1;
    const manual = value => value === "selection" || value === "page";
    const permitted = mode => manual(mode) ? config.manualAvailable : config.enabled;
    const siteRule = tab => preferences.rule(preferences.hostname(tab?.url), config.siteWhitelist, config.siteBlacklist);
    const autoPermitted = (mode, tab) => permitted(mode) && (manual(mode) || siteRule(tab).mode !== "block");
    function tabConfig(tab) {
      return { ...config, siteWhitelist: undefined, siteBlacklist: undefined, type: "pageAutoConfig",
        active: tab.active === true, enabled: config.enabled && browserKind === "chrome" && siteRule(tab).mode !== "block",
        siteMode: siteRule(tab).mode, manualAvailable: config.manualAvailable && browserKind === "chrome",
        quickSettings: preferences.quickSettings(config, tab, config.manualAvailable && browserKind === "chrome") };
    }
    function validIdentity(message) {
      return message && token(message.documentToken) && revision(message.revision)
        && Number.isSafeInteger(message.generation) && message.generation === config.generation;
    }
    async function pushConfig(tab) {
      if (!Number.isInteger(tab?.id)) return;
      try { await chrome.tabs.sendMessage(tab.id, tabConfig(tab), { frameId: 0 }); }
      catch { /* Closed or internal tab. */ }
    }
    async function navigationChanged(tab) {
      if (browserKind !== "chrome" || !Number.isInteger(tab?.id)) return;
      // Same-document pagination has no loading/complete event. Notify the
      // content session without retiring its document or pausing native work.
      try { await chrome.tabs.sendMessage(tab.id, { type: "pageAutoNavigation" }, { frameId: 0 }); }
      catch { /* A committed full navigation starts a new content session. */ }
    }
    async function pushAll() {
      try { for (const tab of await chrome.tabs.query({})) void pushConfig(tab); }
      catch { /* Shutdown. */ }
    }
    function update(message) {
      if (!Number.isSafeInteger(message.generation) || message.generation < config.generation
          || typeof message.enabled !== "boolean"
          || message.cacheScope != null && (typeof message.cacheScope !== "string" || !/^[a-f0-9]{64}$/i.test(message.cacheScope))
          || message.cacheEpoch != null && (!Number.isSafeInteger(message.cacheEpoch) || message.cacheEpoch < 0)) return;
      const next = { enabled: message.enabled, manualAvailable: message.manualAvailable === true, generation: message.generation,
        targetLanguage: typeof message.targetLanguage === "string" ? message.targetLanguage.slice(0, 64) : "zh-CN",
        judgementMode: "Rules",
        concurrency: Number.isInteger(message.concurrency) ? Math.max(1, Math.min(10, message.concurrency)) : 10,
        cacheScope: message.cacheScope?.toLowerCase() ?? "", cacheEpoch: message.cacheEpoch ?? 0,
        siteWhitelist: preferences.domains(message.siteWhitelist), siteBlacklist: preferences.domains(message.siteBlacklist), autoImages: message.autoImages === true };
      if (JSON.stringify(next) === JSON.stringify(config)) return;
      const keepManual = ["generation", "targetLanguage", "judgementMode", "cacheScope", "cacheEpoch", "manualAvailable"]
        .every(key => next[key] === config[key]);
      config = next;
      if (keepManual) {
        // The automatic admission switch shares a generation with explicit user
        // work. Changing it must not retract an already accepted manual stream.
        for (const [id, entry] of pending) if (!manual(entry.manualMode) && (!config.enabled
            || preferences.rule(entry.siteHost || "", config.siteWhitelist, config.siteBlacklist).mode === "block")) pending.delete(id);
      } else { pending.clear(); documents.clear(); }
      void pushAll();
    }
    function disconnect() { config = { ...config, enabled: false, manualAvailable: false }; pending.clear(); documents.clear(); void pushAll(); }
    function cancelTab(tabId) {
      for (const entry of reservations.values()) if (entry.tabId === tabId) entry.cancelled = true;
      const known = documents.get(tabId);
      if (known) postNative({ ...known, type: "pageAutoCancel", reason: "tab_changed" });
      documents.delete(tabId);
      for (const [id, entry] of pending) if (entry.tabId === tabId) pending.delete(id);
    }
    function validBlocks(message) {
      if (!Array.isArray(message.blocks) || !message.blocks.length || message.blocks.length > 48) return false;
      let chars = 0; const ids = new Set();
      for (const block of message.blocks) {
        if (!identifier(block?.id, 128) || ids.has(block.id) || typeof block.text !== "string" || !block.text.trim()
            || block.text.length > 4096 || block.context != null && (typeof block.context !== "string" || block.context.length > 512)
            || block.groupId != null && !identifier(block.groupId, 128)
            || block.foreignHanContext !== undefined && typeof block.foreignHanContext !== "boolean"
            || Object.hasOwn(block, "contextKind") && !["source", "selection"].includes(block.contextKind)) return false;
        const hasSourceMetadata = ["sourceGroupHash", "sourceGroupLength", "sourceStart", "sourceLength"]
          .some(field => Object.hasOwn(block, field));
        if (hasSourceMetadata && (typeof block.sourceGroupHash !== "string"
            || !/^[a-f0-9]{64}$/i.test(block.sourceGroupHash)
            || !Number.isSafeInteger(block.sourceGroupLength) || block.sourceGroupLength < 1 || block.sourceGroupLength > 1000000
            || !Number.isSafeInteger(block.sourceStart) || block.sourceStart < 0
            || !Number.isSafeInteger(block.sourceLength) || block.sourceLength !== block.text.length
            || block.sourceStart + block.sourceLength > block.sourceGroupLength)) return false;
        ids.add(block.id); chars += block.text.length + (block.context?.length ?? 0);
      }
      if (chars > 32768) return false;
      if (message.samples != null) {
        if (!Array.isArray(message.samples) || message.samples.length > 64) return false;
        let sampleChars = 0;
        for (const sample of message.samples) {
          if (typeof sample?.text !== "string" || sample.text.length > 4096
              || !["body", "heading", "title", "navigation", "code", "form"].includes(sample.kind)) return false;
          sampleChars += sample.text.length;
        }
        if (sampleChars > 12000) return false;
      }
      return true;
    }
    function summary(value) {
      if (value == null) return undefined;
      if (typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid summary");
      const result = {};
      for (const key of ["totalCharacters", "hanCharacters", "kanaCharacters", "latinCharacters", "proseBlocks",
        "hanProseCharacters", "latinProseCharacters", "kanaProseCharacters"]) {
        if (value[key] == null) continue;
        if (!Number.isSafeInteger(value[key]) || value[key] < 0 || value[key] > 100000000) throw new Error("Invalid count");
        result[key] = value[key];
      }
      if (value.sourceHash != null) {
        if (typeof value.sourceHash !== "string" || !/^[a-f0-9]{1,64}$/i.test(value.sourceHash)) throw new Error("Invalid hash");
        result.sourceHash = value.sourceHash;
      }
      return result;
    }
    async function accept(message, sender, respond) {
      let reserved = false;
      let reservation;
      let failureReason = "invalid_request";
      try {
        if (!permitted(message.manualMode)) { failureReason = "desktop_unavailable"; throw new Error("Disconnected"); }
        if (message.generation !== config.generation) { failureReason = "configuration_changed"; throw new Error("Configuration"); }
        if (pending.size + reservations.size >= 24) { failureReason = "desktop_busy"; throw new Error("Busy"); }
        if (browserKind !== "chrome" || !permitted(message.manualMode) || message.manualMode != null && !manual(message.manualMode)
            || sender.frameId !== 0 || !sender.tab?.active
            || !validIdentity(message) || !identifier(message.requestId, 64) || pending.has(message.requestId) || reservations.has(message.requestId)
            || !validBlocks(message)) throw new Error("Rejected");
        const pageSummary = summary(message.pageSummary);
        reservation = { tabId: sender.tab.id, cancelled: false };
        reservations.set(message.requestId, reservation); reserved = true;
        failureReason = "page_unavailable";
        const tab = await chrome.tabs.get(sender.tab.id);
        const state = await readDocument(tab);
        const current = await chrome.tabs.get(sender.tab.id);
        failureReason = !current.active ? "tab_inactive" : "document_changed";
        if (reservation.cancelled || !current.active || current.windowId !== sender.tab.windowId || state?.documentToken !== message.documentToken
            || !autoPermitted(message.manualMode, current) || !validIdentity(message)) throw new Error("Changed");
        const identity = { requestId: message.requestId, tabId: tab.id, browserWindowId: tab.windowId,
          documentToken: message.documentToken, generation: message.generation, revision: message.revision, frameId: 0,
          ...(manual(message.manualMode) ? { manualMode: message.manualMode } : {}) };
        identity.siteHost = preferences.hostname(current.url) || undefined;
        const old = documents.get(tab.id);
        if (old && old.documentToken === identity.documentToken && old.revision > identity.revision) throw new Error("Old revision");
        const outgoing = { ...identity, type: "pageAutoCandidate", blocks: message.blocks.map(block => ({ id: block.id, text: block.text,
            ...(block.groupId != null ? { groupId: block.groupId } : {}), ...(block.context != null ? { context: block.context } : {}),
            ...(block.foreignHanContext === true ? { foreignHanContext: true } : {}),
            ...(block.contextKind !== undefined ? { contextKind: block.contextKind } : {}),
            ...(block.sourceGroupHash != null ? { sourceGroupHash: block.sourceGroupHash.toLowerCase(),
              sourceGroupLength: block.sourceGroupLength, sourceStart: block.sourceStart, sourceLength: block.sourceLength } : {}) })),
          samples: message.samples?.map(sample => ({ text: sample.text, kind: sample.kind })), pageSummary,
          documentLanguage: typeof message.documentLanguage === "string" && /^[a-z]{2,8}(?:-[a-z0-9]{1,8}){0,5}$/i.test(message.documentLanguage)
            ? message.documentLanguage : "" };
        failureReason = "desktop_unavailable";
        if (!postNative(outgoing)) throw new Error("Disconnected");
        // A real source revision replaces old routing identities. A late cancel
        // can arrive after the new revision; do not let those retired requests
        // consume the bounded pending budget or receive stale output.
        for (const [id, entry] of pending) if (entry.tabId === identity.tabId
            && (entry.documentToken !== identity.documentToken || entry.revision < identity.revision)) pending.delete(id);
        pending.set(message.requestId, { ...identity, ids: new Set(message.blocks.map(block => block.id)) });
        documents.set(tab.id, identity);
        respond({ accepted: true });
      } catch { respond({ accepted: false, reason: failureReason }); }
      finally { if (reserved) reservations.delete(message.requestId); }
    }
    async function control(message, sender) {
      if (sender.frameId !== 0 || !sender.tab || (!config.enabled && !config.manualAvailable) || !validIdentity(message)) return;
      if (message.requestId != null && !identifier(message.requestId, 64)) return;
      let known = documents.get(sender.tab.id);
      if (!known || known.documentToken !== message.documentToken) {
        // Chinese-only and image-only pages may decide locally and send no text candidate.
        // Their pause still needs to cancel a currently running automatic image request.
        try {
          const tab = await chrome.tabs.get(sender.tab.id), state = await readDocument(tab);
          const current = await chrome.tabs.get(sender.tab.id);
          if (!current.active || current.windowId !== sender.tab.windowId || state?.documentToken !== message.documentToken
              || (!config.enabled && !config.manualAvailable) || !validIdentity(message)) return;
          known = { tabId: current.id, browserWindowId: current.windowId, documentToken: message.documentToken,
            generation: message.generation, revision: message.revision, frameId: 0 };
          documents.set(current.id, known);
        } catch { return; }
      }
      if (message.documentToken !== known.documentToken
          || message.generation !== known.generation || !revision(message.revision)
          || message.revision < known.revision) return;
      if (message.type === "pageAutoControl" && !["pause", "resume", "original"].includes(message.action)) return;
      postNative({ type: message.type, tabId: known.tabId, browserWindowId: known.browserWindowId,
        documentToken: known.documentToken, generation: known.generation, revision: message.revision, frameId: 0,
        requestId: identifier(message.requestId, 64) ? message.requestId : undefined, action: message.action });
      if (message.type === "pageAutoCancel") for (const [id, entry] of pending)
        if (entry.tabId === known.tabId && entry.revision === message.revision && (!message.requestId || id === message.requestId)) pending.delete(id);
    }
    async function hashPageSource(message, sender, respond) {
      try {
        if (browserKind !== "chrome" || sender.frameId !== 0 || !sender.tab?.active
            || !Number.isInteger(sender.tab.id) || !Number.isInteger(sender.tab.windowId)
            || !token(message.documentToken) || typeof message.text !== "string"
            || message.text.length < 1 || message.text.length > 1000000
            || message.generation != null && (!Number.isSafeInteger(message.generation) || message.generation !== config.generation))
          throw new Error("Rejected");
        const tab = await chrome.tabs.get(sender.tab.id), state = await readDocument(tab);
        const current = await chrome.tabs.get(sender.tab.id);
        if (!current.active || current.windowId !== sender.tab.windowId || state?.documentToken !== message.documentToken)
          throw new Error("Changed");
        // HTTP content scripts can lack WebCrypto. Hash only in the extension's
        // secure background; never send or save this local source text anywhere.
        const bytes = new TextEncoder().encode(message.text);
        const digest = new Uint8Array(await root.crypto.subtle.digest("SHA-256", bytes));
        respond({ hash: Array.from(digest, value => value.toString(16).padStart(2, "0")).join("") });
      } catch { respond({ hash: null, reason: "source_unavailable" }); }
    }
    function content(message, sender, respond) {
      if (message?.type === "hashPageSource") { void hashPageSource(message, sender, respond); return true; }
      if (message?.type === "queryPageAutoConfig") {
        respond({ ...tabConfig(sender.tab || {}), active: sender.frameId === 0 && sender.tab?.active === true,
          enabled: config.enabled && browserKind === "chrome" && sender.frameId === 0 && siteRule(sender.tab).mode !== "block",
          manualAvailable: config.manualAvailable && browserKind === "chrome" && sender.frameId === 0 }); return false;
      }
      if (message?.type === "pageAutoCandidate") { void accept(message, sender, respond); return true; }
      if (["pageAutoCancel", "pageAutoControl"].includes(message?.type)) { void control(message, sender); return false; }
      return null;
    }
    async function forward(message) {
      const entry = pending.get(message.requestId);
      if (!entry || !permitted(entry.manualMode) || !validIdentity(message) || message.tabId !== entry.tabId
          || message.documentToken !== entry.documentToken || message.revision !== entry.revision
          || message.browserWindowId != null && message.browserWindowId !== entry.browserWindowId
          || message.manualMode != null && message.manualMode !== entry.manualMode) return;
      try {
        const tab = await chrome.tabs.get(entry.tabId), state = await readDocument(tab);
        // A selected document can finish while its tab is hidden. Admission above
        // still requires the active tab; results stay bound to the accepted identity.
        if (tab.windowId !== entry.browserWindowId || state?.documentToken !== entry.documentToken || !autoPermitted(entry.manualMode, tab)
            || !pending.has(message.requestId) || !validIdentity(message)
            || documents.get(entry.tabId)?.revision !== entry.revision) return;
        const safe = { ...message, ...(manual(entry.manualMode) ? { manualMode: entry.manualMode } : {}) };
        if (manual(entry.manualMode) && message.type === "pageAutoAssessment") safe.allowImages = false;
        if (message.type === "pageAutoTranslations") {
          if (!Array.isArray(message.updates) || message.updates.length > 128) return;
          safe.updates = message.updates.filter(update => entry.ids.has(update?.id) && typeof update.text === "string"
            && update.text.length <= 16384 && typeof update.complete === "boolean")
            .map(update => ({ id: update.id, text: update.text, complete: update.complete, failed: update.failed === true, fromCache: update.fromCache === true }));
        }
        await chrome.tabs.sendMessage(entry.tabId, safe, { frameId: 0 });
        if (message.finished || message.type === "pageAutoAssessment" && message.needsTranslation === false) pending.delete(message.requestId);
      } catch { /* Document was replaced. */ }
    }
    return { update, disconnect, cancelTab, pushConfig, navigationChanged, content, forward, getConfig: () => config, tabConfig };
  }
  root.ScreenTranslatorPageAutoBackground = { create };
  if (typeof module === "object") module.exports = { create };
})(globalThis);
