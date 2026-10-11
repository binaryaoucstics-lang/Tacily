"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { createFetcher } = require("../selected-image-fetch.js");

function fixture(status = 403) {
  const updates = [], rules = new Map([[2100010, {id:2100010}], [8, {id:8}]]);
  let calls = 0, cancel = 0;
  const fetcher = createFetcher({ extensionId: "translator", rules: {
    getSessionRules: async () => [...rules.values()],
    updateSessionRules: async update => {
      updates.push(update);
      for (const id of update.removeRuleIds || []) rules.delete(id);
      for (const rule of update.addRules || []) rules.set(rule.id, rule);
    }
  }, fetch: async (_url, options) => {
    calls++;
    if (calls === 1) return {status,body:{cancel:async()=>cancel++}};
    assert.equal(options.cache, "no-store");
    return {status:200};
  }});
  return { fetcher, rules, updates, calls:()=>calls, cancel:()=>cancel };
}
test("403 retry uses only page origin and an exact own-extension image rule, then removes it", async () => {
  const f=fixture(); const url="https://cdn.example/image.png?a=1.2&b=x+y";
  assert.equal((await f.fetcher(url,"https://reader.example/book?private=secret#page")).status,200);
  const rule=f.updates.find(u=>u.addRules).addRules[0];
  assert.equal(rule.action.requestHeaders[0].value,"https://reader.example/");
  assert.deepEqual(rule.condition.initiatorDomains,["translator"]);
  assert.deepEqual(rule.condition.resourceTypes,["xmlhttprequest"]);
  const pattern=new RegExp(rule.condition.regexFilter);
  assert.equal(pattern.test(url),true);
  assert.equal(pattern.test(url+"more"),false);
  assert.equal(pattern.test("https://cdn.example/other.png"),false);
  assert.deepEqual([...f.rules.keys()],[8]);
  assert.equal(f.cancel(),1); assert.equal(f.calls(),2);
});
test("successful downloads and authentication failures do not retry or change rules",async()=>{
  for(const status of [200,401,404]) {const f=fixture(status);assert.equal((await f.fetcher("https://cdn.example/a.png","https://reader.example/")).status,status);assert.equal(f.calls(),1);assert.equal(f.updates.length,0);}
});
test("network failure still removes the transient rule",async()=>{
  const active=new Map();let calls=0;
  const fetcher=createFetcher({extensionId:"translator",rules:{getSessionRules:async()=>[],updateSessionRules:async u=>{for(const r of u.addRules||[])active.set(r.id,r);for(const id of u.removeRuleIds||[])active.delete(id);}},
    fetch:async()=>{if(++calls===1)return{status:403,body:{cancel:async()=>{}}};throw new Error("offline");}});
  await assert.rejects(fetcher("https://cdn.example/a.png","https://reader.example/"),/offline/);
  assert.equal(active.size,0);
});
test("missing permission and non-web referrers fail without broad rules",async()=>{
  const fetcher=createFetcher({fetch:async()=>({status:403}),extensionId:"translator"});
  await assert.rejects(fetcher("https://cdn.example/a.png","https://reader.example/"),/image_referrer_permission_missing/);
  const f=fixture();await assert.rejects(f.fetcher("https://cdn.example/a.png","file:///private.txt"),/image_referrer_unavailable/);assert.equal(f.updates.length,0);
});
