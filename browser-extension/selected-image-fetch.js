"use strict";

(function (root) {
  function createFetcher({ fetch, rules, extensionId }) {
    const firstRuleId = 2100000, lastRuleId = 2199999;
    let nextRuleId = firstRuleId, cleanup;
    function fail(code) { const error = new Error(code); error.code = code; return error; }
    async function prepare() {
      if (!cleanup) cleanup = (async () => {
        const old = await rules.getSessionRules();
        const ids = old.filter(r => r.id >= firstRuleId && r.id <= lastRuleId).map(r => r.id);
        if (ids.length) await rules.updateSessionRules({ removeRuleIds: ids });
      })();
      await cleanup;
    }
    return async function fetchSelectedImage(imageUrl, pageUrl) {
      const image = new URL(imageUrl);
      if (!["http:", "https:"].includes(image.protocol)) throw fail("unsupported_image_source");
      const options = { credentials: "include", signal: AbortSignal.timeout(6000) };
      const response = await fetch(image.href, options);
      if (response.status !== 403) return response;
      // The image was selected on this page, but CDN hotlink checks reject
      // requests whose initiator is the extension rather than the reading page.
      if (!rules?.updateSessionRules || !rules?.getSessionRules) throw fail("image_referrer_permission_missing");
      let page;
      try { page = new URL(pageUrl); } catch { throw fail("image_referrer_unavailable"); }
      if (!["http:", "https:"].includes(page.protocol)) throw fail("image_referrer_unavailable");
      await response.body?.cancel();
      await prepare();
      const id = nextRuleId++;
      if (id > lastRuleId || image.href.length > 1800) throw fail("image_referrer_rule_limit");
      const regex = "^" + image.href.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "$";
      await rules.updateSessionRules({ addRules: [{ id, priority: 1,
        action: { type: "modifyHeaders", requestHeaders: [{ header: "referer", operation: "set", value: page.origin + "/" }] },
        condition: { regexFilter: regex, initiatorDomains: [extensionId], resourceTypes: ["xmlhttprequest"] }
      }] });
      try {
        return await fetch(image.href, { ...options, signal: AbortSignal.timeout(6000), cache: "no-store" });
      } finally {
        await rules.updateSessionRules({ removeRuleIds: [id] });
      }
    };
  }
  const api = { createFetcher };
  if (typeof module !== "undefined") module.exports = api;
  root.ScreenTranslatorSelectedImageFetch = api;
})(globalThis);
