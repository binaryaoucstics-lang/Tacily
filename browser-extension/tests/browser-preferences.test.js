"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { hostname, domains, rule, patch, createQuickRouter } = require("../browser-preferences.js");
const tick = () => new Promise(resolve => setImmediate(resolve));

test("domain policy normalizes IDNs, strips URL paths and handles exact and specific subdomain rules", () => {
  assert.equal(hostname(" HTTPS://WWW.Example.com/path?private=ignored "), "www.example.com");
  assert.equal(hostname("*.Example.com"), "example.com"); assert.equal(hostname("例え.jp"), "xn--r8jz45g.jp");
  assert.equal(hostname("https://user:password@example.com"), ""); assert.equal(hostname("javascript:alert(1)"), "");
  assert.deepEqual(domains(["Example.com", "example.com", "bad domain"]), ["example.com"]);
  assert.deepEqual(rule("news.example.com", ["news.example.com"], ["example.com"]), { mode: "allow", domain: "news.example.com" });
  assert.equal(rule("news.example.com", ["news.example.com"], ["news.example.com"]).mode, "block");
  assert.equal(rule("notexample.com", [], ["example.com"]).mode, "default");
});

test("quick patch exposes only validated nonsecret preferences", () => {
  assert.deepEqual(patch({ autoPage: true, autoImages: false, concurrency: 10, targetLanguage: "ja", siteMode: "allow" }),
    { autoPage: true, autoImages: false, concurrency: 10, targetLanguage: "ja", siteMode: "allow" });
  for (const value of [{ apiKey: "dummy" }, { concurrency: 11 }, { autoImages: 1 }, { targetLanguage: "unknown" }, { siteMode: "invalid" }, {}])
    assert.equal(patch(value), null);
});

function fixture(options = {}) {
  const sent = [], tab = { id: 11, windowId: 7, active: true, url: "https://reader.example/path?private=discard" };
  let config = { generation: 1, manualAvailable: true, enabled: true, targetLanguage: "zh-CN", concurrency: 10, siteWhitelist: [], siteBlacklist: [] }, connected = true;
  const router = createQuickRouter({ chrome: { tabs: { get: async () => ({ ...tab }) } }, postNative: message => { sent.push(message); return true; },
    isConnected: () => connected, getConfig: () => config, readDocument: options.readDocument || (async () => ({ documentToken: "document" })) });
  const sender = { frameId: 0, tab }, request = { requestId: "request", documentToken: "document", generation: 1, changes: { siteMode: "block" }, siteHost: "forged.example" };
  return { router, sent, tab, sender, request, setConnected: value => { connected = value; }, setConfig: value => { config = value; } };
}

test("quick settings use the verified tab host and wait for the matching saved acknowledgement", async () => {
  const f = fixture(); let reply;
  await f.router.request(f.request, f.sender, value => { reply = value; });
  assert.equal(reply, undefined); assert.equal(f.sent[0].siteHost, "reader.example"); assert.equal(JSON.stringify(f.sent[0]).includes("private=discard"), false);
  await f.router.receive({ requestId: "request", tabId: 99, documentToken: "document", ok: true }); assert.equal(reply, undefined);
  f.setConfig({ generation: 1, manualAvailable: true, enabled: true, targetLanguage: "zh-CN", concurrency: 10, siteBlacklist: ["reader.example"] });
  await f.router.receive({ requestId: "request", tabId: 11, documentToken: "document", ok: true });
  assert.equal(reply.ok, true); assert.equal(reply.settings.siteMode, "block"); assert.equal(reply.settings.effectiveMode, "block"); f.router.disconnect();
});

test("inactive, subframe, stale and unrecognized preferences cannot update saved settings", async () => {
  const f = fixture(); const replies = [];
  for (const [request, sender] of [[{ ...f.request, generation: 0 }, f.sender], [f.request, { ...f.sender, frameId: 1 }],
    [f.request, { ...f.sender, tab: { ...f.tab, active: false } }], [{ ...f.request, changes: { apiKey: "dummy" } }, f.sender]])
    await f.router.request(request, sender, result => replies.push(result));
  assert.equal(f.sent.length, 0); assert.equal(replies.every(result => result.ok === false), true); f.router.disconnect();
});

test("a settings request is reserved before async document lookup; disconnect releases waiting UI", async () => {
  let release; const f = fixture({ readDocument: () => new Promise(resolve => { release = resolve; }) });
  let first, second;
  const waiting = f.router.request(f.request, f.sender, value => { first = value; }); await tick();
  await f.router.request(f.request, f.sender, value => { second = value; }); assert.equal(second.ok, false);
  f.router.disconnect(); assert.equal(first.reason, "desktop_unavailable");
  release({ documentToken: "document" }); await waiting; assert.equal(f.sent.length, 0);
});
