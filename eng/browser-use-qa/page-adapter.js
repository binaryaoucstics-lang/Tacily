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
  function composedParent(node) {
    return node.assignedSlot || node.parentElement || node.parentNode?.host || node.getRootNode?.().host;
  }
  function intersect(rect, bounds) {
    const x = Math.max(rect.x, bounds.left), y = Math.max(rect.y, bounds.top);
    const right = Math.min(rect.x + rect.width, bounds.right), bottom = Math.min(rect.y + rect.height, bounds.bottom);
    return { x, y, width: Math.max(0, right - x), height: Math.max(0, bottom - y) };
  }
  function length(value, size) {
    if (value === "0") return 0;
    if (!/^-?(?:\d+(?:\.\d*)?|\.\d+)(px|%)$/.test(value)) return null;
    return parseFloat(value) * (value.endsWith("%") ? size / 100 : 1);
  }
  function clippingBounds(element, style) {
    const box = element.getBoundingClientRect?.();
    if (!box) return [];
    const left = box.x ?? box.left, top = box.y ?? box.top;
    const bounds = [];
    // The document scrollport is deliberately not a visual-source boundary:
    // loaded below-fold prose is evidence even before scrolling to it.
    const htmlStyle = document.documentElement && root.getComputedStyle(document.documentElement);
    const rootOverflow = element === document.documentElement || element === document.scrollingElement
      || (element === document.body && (!htmlStyle || ["", "visible"].includes(htmlStyle.overflowX || htmlStyle.overflow || "visible")
        && ["", "visible"].includes(htmlStyle.overflowY || htmlStyle.overflow || "visible")));
    const clips = value => ["hidden", "clip", "scroll", "auto", "overlay"].includes(value);
    const paint = /\b(paint|strict|content)\b/.test(style.contain || "");
    const overflowBox = !["inline", "contents"].includes(style.display);
    const clipX = paint || overflowBox && !rootOverflow && clips(style.overflowX || style.overflow);
    const clipY = paint || overflowBox && !rootOverflow && clips(style.overflowY || style.overflow);
    if (clipX || clipY) {
      // Client geometry excludes borders and scrollbar gutters. Scale client
      // dimensions into the same coordinates as transformed Range rectangles.
      const sx = element.offsetWidth > 0 ? box.width / element.offsetWidth : 1;
      const sy = element.offsetHeight > 0 ? box.height / element.offsetHeight : 1;
      const x = left + (element.clientLeft || 0) * sx, y = top + (element.clientTop || 0) * sy;
      const width = Number.isFinite(element.clientWidth) ? element.clientWidth * sx : box.width;
      const height = Number.isFinite(element.clientHeight) ? element.clientHeight * sy : box.height;
      bounds.push({ left: clipX ? x : -Infinity, right: clipX ? x + width : Infinity,
        top: clipY ? y : -Infinity, bottom: clipY ? y + height : Infinity });
    }
    // Legacy accessibility clipping applies only to positioned elements.
    const clip = /^(?:rect)\((.*)\)$/.exec(style.clip || "");
    if (clip && ["absolute", "fixed"].includes(style.position)) {
      const values = clip[1].trim().split(/[\s,]+/);
      const offsets = values.map((value, i) => value === "auto" ? [0, box.width, box.height, 0][i]
        : length(value, i % 2 ? box.width : box.height));
      if (offsets.length === 4 && offsets.every(value => value !== null))
        bounds.push({ left: left + offsets[3], top: top + offsets[0], right: left + offsets[1], bottom: top + offsets[2] });
    }
    const inset = /^inset\((.*?)\)(?:\s+border-box)?$/.exec(style.clipPath || "");
    if (inset) {
      const values = inset[1].split(/\s+round\s+/)[0].trim().split(/\s+/);
      if (values.length >= 1 && values.length <= 4) {
        const expanded = [values[0], values[1] || values[0], values[2] || values[0], values[3] || values[1] || values[0]];
        const offsets = expanded.map((value, i) => length(value, i % 2 ? box.width : box.height));
        if (offsets.every(value => value !== null)) bounds.push({ left: left + offsets[3], top: top + offsets[0],
          right: left + box.width - offsets[1], bottom: top + box.height - offsets[2] });
      }
    }
    return bounds;
  }
  function geometry(node, retainedRects) {
    const range = document.createRange(); range.selectNodeContents(node);
    let rects = retainedRects ? retainedRects.map(r => ({ x: r.documentX - root.scrollX,
      y: r.documentY - root.scrollY, width: r.width, height: r.height }))
      : Array.from(range.getClientRects(), r => ({ x: r.x, y: r.y, width: r.width, height: r.height }));
    const parent = composedParent(node), bounds = [];
    let visible = !!parent;
    const seen = new Set();
    for (let element = parent; element && !seen.has(element); element = composedParent(element)) {
      seen.add(element);
      const style = root.getComputedStyle(element);
      // Visibility is inherited but can be overridden on descendants. Display
      // and opacity suppress the entire subtree regardless of that override.
      if ((element === parent && ["hidden", "collapse"].includes(style.visibility)) || style.display === "none"
          || Number(style.opacity) === 0 || style.contentVisibility === "hidden") { visible = false; break; }
      if (element.tagName === "DETAILS" && !element.open) {
        const summary = Array.from(element.children || []).find(child => child.tagName === "SUMMARY");
        if (!seen.has(summary) && !summary?.contains(node)) { visible = false; break; }
      }
      bounds.push(...clippingBounds(element, style));
    }
    rects = rects.filter(r => r.width > 0 && r.height > 0).flatMap(rect => {
      let clipped = rect;
      for (const bound of bounds) clipped = intersect(clipped, bound);
      // A clipped accessibility aperture cannot supply readable visual text.
      // Use geometry, never class names or aria-hidden. Preserve narrow partial
      // lines whenever the aperture still has a readable extent on either axis.
      const accessibilityAperture = bounds.some(bound => bound.right - bound.left <= 1 && bound.bottom - bound.top <= 1
        && (rect.width > bound.right - bound.left || rect.height > bound.bottom - bound.top));
      if (clipped.width <= 0 || clipped.height <= 0 || accessibilityAperture) return [];
      return [{ ...clipped, documentX: clipped.x + root.scrollX, documentY: clipped.y + root.scrollY }];
    });
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
      let disappeared = !String(node.nodeValue || "").trim() && before?.translated
        && sources.has(id) && layout.ancestorsVisible && node.isConnected !== false;
      if (disappeared && !layout.rects.length) {
        const retained = geometry(node, before.rects);
        disappeared = retained.rendered;
        if (disappeared) { layout.rects = retained.rects; layout.inViewport = retained.inViewport; }
      }
      if (!layout.rendered && !disappeared) continue;
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
      // An identity translation may stream "FOI" then "FOIA". Growing an
      // unchanged-source prefix is not a withdrawal of a translated value.
      const originalPrefix = String(source).trim().startsWith(value.trim());
      previous.set(id, { value, translated: !intentional && value !== source && !!value.trim() && !originalPrefix,
        rects: layout.rects, inViewport: layout.inViewport });
    }
    const predictions = [];
    for (const block of state?.blocks.values() || []) {
      const record = block.record;
      const sourceId = identity(record.node, record.source), matched = sources.has(sourceId);
      const sourceIds = Array.from(new Set((block.atomicRecords || [record])
        .filter(member => String(member.source ?? "").trim().length > 0)
        .map(member => identity(member.node, member.source))));
      const allMatched = matched && sourceIds.every(id => sources.has(id));
      predictions.push({ id: block.id, sourceId, sourceIds,
        sourceEvidence: allMatched ? "matched" : "unmatched",
        auditIssue: allMatched ? null : "source_not_independently_visible",
        sourceText: record.source, text: block.text, groupId: block.groupId, eligible: true,
        completed: block.piece.complete === true && !block.piece.failed, failed: block.piece.failed === true,
        local: block.piece.local === true, fromCache: block.piece.cached === true });
    }
    const eligibleSources = new Set(predictions.flatMap(value => value.sourceIds));
    const currentSources = new Set(nodes.map(value => value.id));
    const productionEligibility = Array.from(sources.values(), value => ({ sourceId: value.id,
      eligible: eligibleSources.has(value.id), currentlyRendered: currentSources.has(value.id) }));
    return { at: timestamp(), finalUrl: root.location.href, title: document.title,
      viewport: { width: root.innerWidth, height: root.innerHeight, scrollX: root.scrollX, scrollY: root.scrollY },
      nodes, sourceNodes: Array.from(sources.values(), value => ({ ...value,
        currentlyRendered: currentSources.has(value.id) })), predictions, productionEligibility, status,
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
