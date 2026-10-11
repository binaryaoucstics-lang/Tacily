"use strict";
// Transport/evidence only. The production policy, extractor and renderer are unmodified.
(function install(root) {
  const api = root.ScreenTranslatorPageAutoContent;
  if (!api || root.__websiteQa) throw new Error("qa_adapter_installation_invalid");
  const outbound = [], pending = new Map(), events = [], sources = new Map();
  const identities = new WeakMap(), locators = new Map(), previous = new Map();
  let serial = 0, session, observer, probeTimer, status = null, probeJob = null;
  const document = root.document;
  const timestamp = () => new Date().toISOString();
  function locator(node) {
    const parts = [];
    for (let current = node; current && current !== document; ) {
      const parent = current.parentNode;
      if (!parent && current.host) { parts.unshift("shadow"); current = current.host; continue; }
      if (!parent) break;
      parts.unshift(String(Array.prototype.indexOf.call(parent.childNodes, current)));
      current = parent;
    }
    return parts.join("/");
  }
  function identity(node, source) {
    const old = identities.get(node);
    if (old && old.source === source) return old.id;
    const path = locator(node), key = JSON.stringify([path, source]);
    const id = locators.get(key) || `source-${++serial}`;
    // Identity allocation is independent of evidence. Hidden nodes and production
    // predictions may have identities without ever becoming visible source rows.
    locators.set(key, id); identities.set(node, { id, source });
    return id;
  }
  // Independent traversal deliberately includes CODE/PRE/BUTTON/TIME and numeric text.
  function* textNodes(start) {
    const stack = [start], seen = new Set();
    while (stack.length) {
      const node = stack.pop(); if (!node || seen.has(node)) continue; seen.add(node);
      if (node.nodeType === 3) { yield node; continue; }
      if (node.nodeType === 1 && node.hasAttribute?.("data-screen-translator")) continue;
      const assigned = node.tagName === "SLOT" ? node.assignedNodes?.({ flatten: true }) : null;
      const children = assigned?.length ? assigned : node.shadowRoot ? [node.shadowRoot] : node.childNodes || [];
      for (let i = children.length - 1; i >= 0; i--) stack.push(children[i]);
    }
  }
  function geometry(node) {
    const range = document.createRange(); range.selectNodeContents(node);
    const rects = Array.from(range.getClientRects(), r => ({ x: r.x, y: r.y,
      width: r.width, height: r.height, documentX: r.x + root.scrollX, documentY: r.y + root.scrollY }));
    const parent = node.parentElement || node.getRootNode?.().host;
    let visible = !!parent;
    for (let element = parent; element; element = element.parentElement || element.getRootNode?.().host) {
      const style = root.getComputedStyle(element);
      if (style.visibility === "hidden" || style.visibility === "collapse" || style.display === "none"
          || style.opacity === "0") { visible = false; break; }
      if (element.tagName === "DETAILS" && !element.open) {
        const summary = Array.from(element.children || []).find(child => child.tagName === "SUMMARY");
        if (!summary?.contains(node)) { visible = false; break; }
      }
    }
    const rendered = visible && rects.some(r => r.width > 0 && r.height > 0);
    return { rects, ancestorsVisible: visible, rendered: !!rendered, inViewport: !!rendered && rects.some(r =>
      r.x < root.innerWidth && r.y < root.innerHeight && r.x + r.width > 0 && r.y + r.height > 0) };
  }
  function sample(phase) {
    const state = session?.inspect(), records = state?.records || new Map(), nodes = [];
    for (const node of textNodes(document.body)) {
      const record = records.get(node);
      const source = record?.source ?? node.nodeValue;
      if (!String(source || "").trim()) continue;
      const id = identity(node, source), layout = geometry(node), before = previous.get(id);
      // Empty Text ranges have no geometry. Retain the previous visible position
      // when an unchanged, still connected translated node disappears to blank.
      const disappeared = !String(node.nodeValue || "").trim() && before?.translated
        && sources.has(id) && layout.ancestorsVisible && node.isConnected !== false;
      if (!layout.rendered && !disappeared) continue;
      if (disappeared && !layout.rects.some(r => r.width > 0 && r.height > 0)) {
        layout.rects = before.rects; layout.inViewport = before.inViewport;
      }
      const parent = node.parentElement || node.getRootNode?.().host;
      const value = String(node.nodeValue || "");
      const row = { id, sourceText: source, locator: locator(node),
        firstSeen: sources.get(id)?.firstSeen || timestamp(),
        tag: parent?.tagName || "", role: parent?.getAttribute?.("role") || "",
        ...layout, displayedText: value, phase };
      // Only full independently observed rows enter history. If a node later
      // hides or detaches, the last complete row remains available for review.
      sources.set(id, { ...row }); nodes.push(row);
      const intentional = state?.originalView || state?.viewMode === "bilingual";
      // A changed production source receives a new identity. Unknown/changed authored
      // text is not a retraction. Track reversions only with retained source identity.
      const unchanged = record && record.source === source && (value === record.lastWritten || value === source);
      if (before?.translated && !intentional && unchanged && (value === source || value.trim() === "")
          && before.value !== value) events.push({ type: "retraction", sourceId: id, sourceText: source,
        previousText: before.value, displayedText: value, sourceUnchanged: true, phase, at: timestamp() });
      previous.set(id, { value, translated: !intentional && value !== source && !!value.trim(),
        rects: layout.rects, inViewport: layout.inViewport });
    }
    const predictions = [];
    for (const block of state?.blocks.values() || []) {
      const record = block.record;
      const sourceId = identity(record.node, record.source), matched = sources.has(sourceId);
      predictions.push({ id: block.id, sourceId,
        sourceEvidence: matched ? "matched" : "unmatched",
        auditIssue: matched ? null : "source_not_independently_visible",
        sourceText: record.source, text: block.text, groupId: block.groupId, eligible: true,
        completed: block.piece.complete === true && !block.piece.failed, failed: block.piece.failed === true,
        local: block.piece.local === true, fromCache: block.piece.cached === true });
    }
    const eligibleSources = new Set(predictions.map(value => value.sourceId));
    const productionEligibility = nodes.map(value => ({ sourceId: value.id,
      eligible: eligibleSources.has(value.id) }));
    return { at: timestamp(), finalUrl: root.location.href, title: document.title,
      viewport: { width: root.innerWidth, height: root.innerHeight, scrollX: root.scrollX, scrollY: root.scrollY },
      nodes, sourceNodes: Array.from(sources.values()), predictions, productionEligibility, status,
      pageDecision: typeof state?.assessed === "boolean" ? state.assessed : null,
      jobs: state?.jobs || 0, pending: state?.pending || 0, scanning: state?.scanning || false,
      rpcPending: pending.size, events: events.slice() };
  }
  function observation() { if (observer && session) observer.configure(session.inspect().observing); }
  function recordStatus(value) { status = value; events.push({ type: "status", value, at: timestamp() }); observation(); }
  const motion = root.ScreenTranslatorMotion || root.ScreenTranslatorMotionFactory.createTranslationMotion({ document, window: root });
  session = api.createPageAutoSession({ document, token: api.randomIdentifier(),
    getLocation: () => root.location.href, motion,
    getStyle: element => root.getComputedStyle(element), makeId: () => api.randomIdentifier(), viewportHeight: root.innerHeight,
    getSelection: () => root.getSelection(), isRenderedText: api.createRenderedTextTester(document), yieldWork: api.yieldToBrowser,
    send: message => new Promise(resolve => {
      const rpcId = `rpc-${++serial}`; pending.set(rpcId, resolve);
      outbound.push({ rpcId, message }); events.push({ type: "outbound", rpcId, message, at: timestamp() });
    }), onStatus: recordStatus, onView: value => events.push({ type: "view", value, at: timestamp() }),
    onDecision: value => events.push({ type: "decision", value, at: timestamp() }),
    onRootFound: tree => observer?.found(tree), onOpenRoots: trees => observer?.replace(trees) });
  observer = api.createOpenRootObserver({ document, createObserver: callback => new root.MutationObserver(callback),
    onMutations: mutations => {
      const records = session.inspect().records, changed = new Set();
      for (const mutation of mutations) {
        const record = records.get(mutation.target);
        if (mutation.type === "characterData" && record && mutation.target.nodeValue !== record.lastWritten
            && mutation.target.nodeValue !== record.source) changed.add(record.groupElement);
        if (mutation.type === "childList") {
          for (const peer of records.values()) if (peer.groupElement === mutation.target
              && !Array.from(records.values()).filter(r => r.groupElement === peer.groupElement)
                .every(r => r.node.isConnected && (r.node.nodeValue === r.source || r.node.nodeValue === r.lastWritten)))
            changed.add(peer.groupElement);
        }
      }
      for (const record of records.values()) if (changed.has(record.groupElement)) {
        previous.delete(identity(record.node, record.source));
        events.push({ type: "source-change", sourceId: identity(record.node, record.source), at: timestamp() });
      }
      session.mutated(mutations); sample("mutation");
    } });
  const listeners = [];
  function listen(target, type, callback, options) {
    target?.addEventListener(type, callback, options); listeners.push([target, type, callback, options]);
  }
  listen(document, "scroll", () => session.layoutChanged(), { capture: true, passive: true });
  listen(root, "resize", () => session.layoutChanged(), { passive: true });
  listen(root.navigation, "currententrychange", () => session.navigationChanged());
  listen(root, "popstate", () => session.navigationChanged());
  listen(root, "hashchange", () => session.navigationChanged());
  listen(document, "visibilitychange", () => session.visibilityChanged());
  listen(document, "selectionchange", () => session.selectionChanged());
  listen(root, "pagehide", () => { session.suspend(); observation(); });
  listen(root, "pageshow", event => { if (event.persisted) { session.resumeDocument(); observation(); } });
  probeTimer = root.setInterval(() => { if (session.inspect().active) void session.discoverOpenRoots(); }, 1500);
  // Browser Use CLI eval does not await Promises. Start synchronously and publish
  // the production extractor's result for later synchronous status evaluations.
  function startProbe(targetLanguage) {
    if (probeJob?.stage === "running") throw new Error("qa_probe_already_running");
    const job = { id: `probe-${++serial}`, stage: "running", result: null, error: null };
    probeJob = job;
    events.push({ type: "probe", id: job.id, stage: job.stage, at: timestamp() });
    Promise.resolve().then(() => api.extractPageSnapshot({ document,
      getStyle: element => root.getComputedStyle(element), makeId: () => api.randomIdentifier(),
      isRenderedText: api.createRenderedTextTester(document), targetLanguage
    })).then(snapshot => {
      if (probeJob !== job) return;
      if (!Array.isArray(snapshot?.samples) || !snapshot.pageSummary
          || typeof snapshot.pageSummary !== "object" || Array.isArray(snapshot.pageSummary))
        throw new Error("qa_probe_invalid_snapshot");
      job.result = { samples: snapshot.samples, statistics: snapshot.pageSummary };
      job.stage = "completed";
      events.push({ type: "probe", id: job.id, stage: job.stage, at: timestamp() });
    }).catch(() => {
      if (probeJob !== job) return;
      job.stage = "failed"; job.result = null; job.error = "production_probe_failed";
      events.push({ type: "probe", id: job.id, stage: job.stage, error: job.error, at: timestamp() });
    });
    return { id: job.id, stage: job.stage };
  }
  root.__websiteQa = {
    sample, startProbe, probeStatus: id => probeJob?.id === id ? { ...probeJob } : null,
    drain: () => outbound.splice(0),
    configure: config => { const ok = session.configure(config); observation(); return ok; },
    // Raw production frames go through receive. Accepted frames resolve transport RPCs.
    deliver: (rpcId, packet) => {
      if (typeof packet === "string") packet = JSON.parse(packet);
      events.push({ type: "inbound", rpcId, packet, at: timestamp() });
      if (packet.type === "pageAutoAccepted") {
        const resolve = pending.get(rpcId); pending.delete(rpcId); resolve?.({ accepted: true }); return !!resolve;
      }
      return session.receive(packet);
    },
    acknowledge: (rpcId, response) => { const resolve = pending.get(rpcId); pending.delete(rpcId); resolve?.(response); return !!resolve; },
    control: action => session.control(action),
    dispose: () => {
      probeJob = null;
      root.clearInterval(probeTimer); session.dispose(); observer.dispose(); motion.dispose();
      for (const [target, type, fn, options] of listeners) target?.removeEventListener(type, fn, options);
      for (const resolve of pending.values()) resolve({ accepted: false, reason: "qa_disposed" }); pending.clear();
    }
  };
})(globalThis);
