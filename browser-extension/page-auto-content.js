"use strict";
(function exposePageContent(root) {
  const policy = root.ScreenTranslatorPageAutoPolicy || (typeof require === "function" ? require("./page-auto-policy.js") : null);
  const excludedTags = new Set(["SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "CODE", "PRE", "KBD", "SAMP", "SVG", "MATH",
    "CANVAS", "INPUT", "TEXTAREA", "SELECT", "OPTION", "IFRAME", "FRAME"]);
  const semanticTags = new Set(["P", "H1", "H2", "H3", "H4", "H5", "H6", "LI", "TD", "TH", "DT", "DD",
    "BLOCKQUOTE", "FIGCAPTION", "BUTTON", "SUMMARY", "DIV", "SECTION", "ARTICLE"]);
  function composedParent(node) {
    if (node?.assignedSlot) return node.assignedSlot;
    if (node?.parentElement) return node.parentElement;
    const tree = node?.getRootNode?.();
    return tree?.host || node?.host || null;
  }
  function unrenderedLightNode(node) {
    const parent = node?.parentElement;
    if (parent?.shadowRoot && !node.assignedSlot) return true;
    return parent?.tagName === "SLOT" && (parent.assignedNodes?.() || []).length > 0;
  }
  function hiddenByClosedDetails(node) {
    let branch = node;
    for (let parent = composedParent(node); parent; branch = parent, parent = composedParent(parent)) {
      if (parent.tagName !== "DETAILS" || parent.open || parent.hasAttribute?.("open")) continue;
      const summary = Array.from(parent.childNodes || []).find(child => child.tagName === "SUMMARY");
      if (branch !== summary) return true;
    }
    return false;
  }
  function waitingForPageContent(document) {
    if (document.readyState === "loading") return true;
    const title = String(document.title || "").trim();
    return /^(?:just a moment[.\u2026]*|please wait[.\u2026]*|请稍候[.\u2026]*|正在验证.*)$/iu.test(title)
      || /\/\.well-known\/sgcaptcha\//iu.test(document.location?.pathname || "")
      || !!document.getElementById?.("challenge-running") || !!document.getElementById?.("cf-challenge-running");
  }
  function pageRouteKey(href) {
    try {
      const url = new URL(href);
      // An in-page anchor keeps its live translation. Hash-based app routes,
      // paths and pagination/filter queries identify different content.
      return url.origin + url.pathname + url.search + (/^#(?:\/|!)/u.test(url.hash) ? url.hash : "");
    } catch { return null; }
  }
  function* walkAccessibleNodes(start, roots = new Set(), onRoot = () => {}) {
    const stack = [start], seen = new WeakSet();
    while (stack.length) {
      const node = stack.pop(); if (!node || seen.has(node)) continue; seen.add(node);
      if (unrenderedLightNode(node) || node.nodeType === 1 && (excludedTags.has(node.tagName) || node.hasAttribute?.("data-screen-translator"))) continue;
      yield node;
      const assigned = node.tagName === "SLOT" ? node.assignedNodes?.({ flatten: true }) : null;
      const children = assigned?.length ? assigned : node.childNodes || [];
      for (let index = children.length - 1; index >= 0; index--) stack.push(children[index]);
      const shadow = node.nodeType === 1 ? node.shadowRoot : null;
      if (shadow && shadow.mode !== "closed" && !roots.has(shadow)) {
        roots.add(shadow); onRoot(shadow); stack.push(shadow);
      }
    }
  }
  function renderingStylePermitted(element, style, fragmentGroups) {
    const hidden = element.hidden || element.hasAttribute?.("hidden");
    // HTML hidden's default display:none can be overridden by authored CSS.
    // Require computed style evidence; range geometry is checked by callers.
    if (hidden && !style?.display) return false;
    // Until-found uses content-visibility:hidden rather than display:none.
    // Display alone therefore cannot prove that its contents are revealed.
    const untilFound = element.hidden === "until-found" || element.getAttribute?.("hidden")?.toLowerCase() === "until-found";
    if (untilFound && !style?.contentVisibility) return false;
    return style?.display !== "none" && style?.visibility !== "hidden" && style?.visibility !== "collapse"
      && style?.contentVisibility !== "hidden" && (style?.opacity !== "0" || decorativeFragment(element, fragmentGroups));
  }
  function permitted(node, getStyle, fragmentGroups) {
    if (unrenderedLightNode(node) || hiddenByClosedDetails(node)) return false;
    for (let parent = composedParent(node); parent; parent = composedParent(parent)) {
      if (typeof parent.tagName !== "string") continue;
      if (unrenderedLightNode(parent)) return false;
      if (excludedTags.has(parent.tagName) || parent.isContentEditable
          || parent.getAttribute?.("aria-hidden") === "true" || parent.hasAttribute?.("data-screen-translator")) return false;
      const style = getStyle(parent);
      if (!renderingStylePermitted(parent, style, fragmentGroups)) return false;
    }
    return true;
  }
  function groupElement(node) {
    // X nests spans, links and divs inside one tweetText. Keep its language
    // evidence and source identity together across React inline re-renders.
    for (let parent = composedParent(node); parent; parent = composedParent(parent)) {
      if (parent.getAttribute?.("data-testid") === "tweetText") return parent;
      if (["ARTICLE", "MAIN", "BODY"].includes(parent.tagName)) break;
    }
    const clock = isolatedClockElement(node); if (clock) return clock;
    for (let parent = composedParent(node); parent; parent = composedParent(parent)) {
      if (parent.tagName === "SPAN" && inlineCalendarLabel(parent)) return parent;
      if (parent.tagName === "A") {
        const container = composedParent(parent);
        let boundary = container;
        while (boundary && !semanticTags.has(boundary.tagName) && !["NAV", "HEADER", "FOOTER", "ASIDE", "BODY"].includes(boundary.tagName)) boundary = composedParent(boundary);
        const proseBoundary = ["P", "H1", "H2", "H3", "H4", "H5", "H6", "LI", "TD", "TH", "DT", "DD", "BLOCKQUOTE", "FIGCAPTION"].includes(boundary?.tagName);
        const inlineProse = Array.from(boundary?.childNodes || []).some(child => child.nodeType === 3 && String(child.nodeValue || "").trim());
        // Independently grouped links in navigation keep their own source
        // boundary when decorative/label siblings or wrappers are added. Inline
        // links in a paragraph/list sentence must still share its fact context.
        if (!isProseGroup(parent) && boundary && !inlineProse && !proseBoundary) return parent;
        // A list/grid of independent article cards is not one paragraph. Keep
        // ordinary links embedded in prose on their existing paragraph group.
        if (container && !proseBoundary && !inlineProse) {
          const authored = Array.from(container.childNodes || []).filter(child => child.nodeType === 1
            || child.nodeType === 3 && String(child.nodeValue || "").trim());
          if (authored.length && authored.every(child => child.nodeType === 1 && child.tagName === "A")) return parent;
        }
      }
      if (semanticTags.has(parent.tagName)) return parent;
    }
    return composedParent(node);
  }
  function isolatedClockElement(node) {
    const parent = composedParent(node);
    if (node?.nodeType === 3 && !policy.isTimestamp(node.nodeValue)) return null;
    if (!["SPAN", "TIME"].includes(parent?.tagName) || !policy.isTimestamp(parent.textContent)) return null;
    for (let ancestor = composedParent(parent); ancestor; ancestor = composedParent(ancestor)) {
      // A time embedded in authored prose still changes that paragraph's facts.
      if (["P", "H1", "H2", "H3", "H4", "H5", "H6", "LI", "TD", "TH", "BLOCKQUOTE", "FIGCAPTION"].includes(ancestor.tagName)) return null;
      if (semanticTags.has(ancestor.tagName)) return parent;
    }
    return null;
  }
  function plainSocialParagraph(element) {
    if (element?.getAttribute?.("data-testid") !== "tweetText") return false;
    let visited = 0;
    for (const node of walkAccessibleNodes(element)) {
      if (++visited > 512) return false;
      if (node !== element && node.nodeType === 1 && !["SPAN", "DIV"].includes(node.tagName)) return false;
    }
    return true;
  }
  function decorativeFragment(element, groups) {
    return element?.tagName === "SPAN" && groups?.has(groupElement({ parentElement: element }));
  }
  function fragmentedInlineGroup(element, previous) {
    if (!element || !["P", "H1", "H2", "H3", "H4", "H5", "H6"].includes(element.tagName)) return false;
    let count = 0, visited = 0;
    for (const node of walkAccessibleNodes(element)) {
      if (++visited > 512) return false;
      if (node === element) continue;
      if (node.nodeType === 1 && node.tagName !== "SPAN") return false;
      if (node.nodeType !== 3) continue;
      const source = previous?.get(node)?.source ?? node.nodeValue;
      if (!String(source || "").trim()) continue;
      if (composedParent(node)?.tagName !== "SPAN" || String(source).trim().length > 48 || /\s/u.test(String(source).trim())) return false;
      count++;
    }
    return count >= 8;
  }
  function fragmentStyleRuns(group, getStyle) {
    const runs = []; let key = null;
    for (const record of group.records) {
      const style = getStyle(composedParent(record.node));
      const next = JSON.stringify([style?.color, style?.fontWeight, style?.fontStyle, style?.fontFamily, style?.fontSize, style?.textDecorationLine]);
      if (next !== key) { runs.push([]); key = next; }
      runs.at(-1).push(record);
    }
    // Respect meaningful color/emphasis runs, without making letter animation
    // spans independent translation units or moving interactive elements.
    return runs.length <= 6 ? runs : null;
  }
  function inlineCalendarLabel(element) {
    for (let parent = composedParent(element); parent; parent = composedParent(parent)) {
      if (["P", "H1", "H2", "H3", "H4", "H5", "H6", "LI", "TD", "TH", "BLOCKQUOTE", "FIGCAPTION"].includes(parent.tagName)) return false;
      if (semanticTags.has(parent.tagName)) break;
    }
    let visited = 0;
    for (const node of walkAccessibleNodes(element)) {
      if (++visited > 64) break;
      if (node.tagName === "TIME") return true;
    }
    return false;
  }
  function hasInlineCalendar(record, element) {
    for (let parent = composedParent(record.node); parent; parent = composedParent(parent)) {
      if (parent.tagName === "TIME") return true;
      if (parent === element) break;
    }
    return false;
  }
  function calendarCanReorder(element) {
    const pending = [...(element.childNodes || [])];
    while (pending.length) {
      const node = pending.pop();
      if (["A", "BUTTON", "INPUT", "TEXTAREA", "SELECT", "CODE", "PRE"].includes(node.tagName) || node.isContentEditable) return false;
      pending.push(...(node.childNodes || []));
    }
    return true;
  }
  function renderedRecord(record, tester, layout) {
    return !tester || tester(record.atomicOwner?.node || record.node, layout);
  }
  function paragraphAtPoint(environment, x, y) {
    const { document, getStyle } = environment;
    if (![x, y].every(Number.isFinite)) return null;
    let hit = document.elementFromPoint?.(x, y);
    const roots = new Set();
    while (hit?.shadowRoot?.elementFromPoint && !roots.has(hit.shadowRoot)) {
      roots.add(hit.shadowRoot);
      const inside = hit.shadowRoot.elementFromPoint(x, y);
      if (!inside || inside === hit) break;
      hit = inside;
    }
    if (!hit || ["BODY", "HTML", "IMG"].includes(hit.tagName) || !permitted({ parentElement: hit }, getStyle)) return null;
    const range = document.createRange?.(); if (!range) return null;
    let work = 0;
    const started = root.performance?.now?.() ?? Date.now();
    try {
      // A local, bounded hit test. Empty paragraph padding may fall back to a
      // nearby line; a blank page/card never selects an entire container.
      for (let container = hit, depth = 0; container && depth < 3; container = composedParent(container), depth++) {
        if (["BODY", "HTML"].includes(container.tagName)) break;
        let best = null, distance = 24 * 24;
        for (const node of walkAccessibleNodes(container)) {
          if (++work > 512 || (root.performance?.now?.() ?? Date.now()) - started > 12) return best ? groupElement(best) : null;
          if (node.nodeType !== 3 || !String(node.nodeValue || "").trim() || !permitted(node, getStyle)) continue;
          range.selectNodeContents(node);
          let rectangles = 0;
          for (const rect of range.getClientRects()) {
            if (++rectangles > 64) break;
            if (rect.width <= 0 || rect.height <= 0) continue;
            const dx = Math.max(rect.left - x, 0, x - rect.right), dy = Math.max(rect.top - y, 0, y - rect.bottom);
            const value = dx * dx + dy * dy;
            if (value < distance) { distance = value; best = node; }
          }
        }
        if (best) return groupElement(best);
      }
    } catch { return null; }
    finally { range.detach?.(); }
    return null;
  }
  function isProseGroup(element) {
    for (let parent = element; parent; parent = composedParent(parent)) {
      if (["NAV", "HEADER", "FOOTER", "ASIDE", "FORM"].includes(parent.tagName)
          || ["navigation", "banner", "contentinfo"].includes(parent.getAttribute?.("role"))) return false;
    }
    return true;
  }
  function isMainGroup(element) {
    for (let parent = element; parent; parent = composedParent(parent))
      if (parent.tagName === "MAIN" || parent.tagName === "ARTICLE" || parent.getAttribute?.("role") === "main") return true;
    return false;
  }
  function inViewport(element, height = 800) {
    const bounds = element?.getBoundingClientRect?.();
    return bounds && bounds.bottom > 0 && bounds.top < height;
  }
  const scanSliceMilliseconds = 4;
  let yieldChannel = null;
  const yieldQueue = [];
  function yieldToBrowser() {
    if (typeof root.MessageChannel === "function") {
      if (!yieldChannel) {
        yieldChannel = new root.MessageChannel();
        yieldChannel.port1.onmessage = () => yieldQueue.shift()?.();
      }
      return new Promise(resolve => { yieldQueue.push(resolve); yieldChannel.port2.postMessage(0); });
    }
    if (typeof root.scheduler?.yield === "function") return root.scheduler.yield();
    return new Promise(resolve => setTimeout(resolve, 0));
  }
  function normalizeRectangle(rect) {
    if (!rect) return null;
    // DOMRect edges and x/y/width/height are equivalent representations. Do
    // not replace supplied invalid numbers with another field or an origin.
    for (const key of ["left", "right", "top", "bottom", "x", "y", "width", "height"]) {
      if (rect[key] !== undefined && !Number.isFinite(rect[key])) return null;
    }
    function axis(start, end, origin, extent) {
      start ??= origin;
      extent ??= start !== undefined && end !== undefined ? end - start : undefined;
      if (start === undefined && end !== undefined && extent !== undefined) start = end - extent;
      if (end === undefined && start !== undefined && extent !== undefined) end = start + extent;
      if (extent !== undefined && (!Number.isFinite(extent) || extent < 0)
          || start !== undefined && !Number.isFinite(start) || end !== undefined && !Number.isFinite(end)) return null;
      if (start !== undefined && end !== undefined && (end < start || (end > start) !== (extent > 0))) return null;
      return { start, end, extent };
    }
    const x = axis(rect.left, rect.right, rect.x, rect.width), y = axis(rect.top, rect.bottom, rect.y, rect.height);
    if (!x || !y) return null;
    // Some range providers expose dimensions without all positions. Those
    // prove unclipped area, but cannot prove intersection on a clipped axis.
    return { left: x.start, right: x.end, top: y.start, bottom: y.end, width: x.extent, height: y.extent };
  }
  function createScanLayout(document, getStyle = element => document.defaultView?.getComputedStyle?.(element)) {
    let styles = new WeakMap(), rectangles = new WeakMap(), clipping = new WeakMap();
    function style(element) {
      if (!styles.has(element)) {
        const value = getStyle(element);
        styles.set(element, { display: value?.display, visibility: value?.visibility, opacity: value?.opacity, contentVisibility: value?.contentVisibility,
          overflow: value?.overflow, overflowX: value?.overflowX, overflowY: value?.overflowY,
          position: value?.position, clip: value?.clip, clipPath: value?.clipPath });
      }
      return styles.get(element);
    }
    function bounds(element) {
      if (!rectangles.has(element)) rectangles.set(element, normalizeRectangle(element?.getBoundingClientRect?.()));
      return rectangles.get(element);
    }
    function clips(element) {
      const path = []; let parent = element;
      while (parent && !clipping.has(parent)) { path.push(parent); parent = composedParent(parent); }
      let inherited = parent ? clipping.get(parent) : [];
      for (let index = path.length - 1; index >= 0; index--) {
        const current = path[index];
        const value = style(current);
        if (value.display !== "contents") {
          const x = ["hidden", "clip"].includes(value.overflowX || value.overflow);
          const y = ["hidden", "clip"].includes(value.overflowY || value.overflow);
          // Below-fold prose may lie outside the document scrollport. Authored
          // rect/inset clips still apply there; display:contents has no box.
          if ((x || y) && current !== document.body && current !== document.documentElement) {
            inherited = [...inherited, { bounds: bounds(current), x, y }];
          }
          const legacy = ["absolute", "fixed"].includes(value.position) && /^rect\(/iu.test(value.clip || "");
          const inset = /^inset\(/iu.test(value.clipPath || "");
          if (legacy || inset) {
            const rect = bounds(current);
            for (const css of [legacy && value.clip, inset && value.clipPath]) {
              const clipped = rectangularClipBounds(css, rect, current);
              if (css) inherited = [...inherited, { bounds: clipped, x: true, y: true }];
            }
          }
        }
        clipping.set(current, inherited);
      }
      return inherited;
    }
    return { style, bounds, clips, reset() { styles = new WeakMap(); rectangles = new WeakMap(); clipping = new WeakMap(); } };
  }
  function rectangularClipBounds(css, rect, element) {
    if (!css || !rect || ![rect.left, rect.right, rect.top, rect.bottom].every(Number.isFinite)) return null;
    // Computed rect/inset lengths are local to the border box. Percentages use
    // its actual extent; pixel lengths also follow an ordinary scaled box.
    const width = rect.right - rect.left, height = rect.bottom - rect.top;
    const scaleX = element.offsetWidth > 0 ? width / element.offsetWidth : 1;
    const scaleY = element.offsetHeight > 0 ? height / element.offsetHeight : 1;
    function length(token, extent, scale, auto) {
      if (token === "auto") return auto;
      const match = /^(-?(?:\d+(?:\.\d*)?|\.\d+))(px|%)?$/iu.exec(token);
      if (!match || !match[2] && Number(match[1]) !== 0) return NaN;
      return Number(match[1]) * (match[2] === "%" ? extent / 100 : scale);
    }
    const legacy = /^rect\(([^()]+)\)$/iu.exec(css);
    const inset = /^inset\(([^()]+)\)(?:\s+border-box)?$/iu.exec(css);
    if (!legacy && !inset) return null;
    let tokens = (legacy || inset)[1].trim().toLowerCase().split(legacy ? /[\s,]+/u : /\s+/u);
    if (legacy && tokens.length !== 4 || inset && (tokens.length < 1 || tokens.length > 4)) return null;
    if (inset) tokens = [tokens[0], tokens[1] || tokens[0], tokens[2] || tokens[0], tokens[3] || tokens[1] || tokens[0]];
    const offsets = tokens.map((token, index) => length(token, index % 2 ? width : height, index % 2 ? scaleX : scaleY,
      legacy ? [0, width, height, 0][index] : NaN));
    if (!offsets.every(Number.isFinite)) return null;
    const [top, right, bottom, left] = offsets;
    return { left: rect.left + left, top: rect.top + top,
      right: legacy ? rect.left + right : rect.right - right,
      bottom: legacy ? rect.top + bottom : rect.bottom - bottom };
  }
  function createRenderedTextTester(document) {
    const range = document.createRange();
    return (node, layout) => {
      try {
        if (hiddenByClosedDetails(node)) return false;
        const clips = (layout || createScanLayout(document)).clips(composedParent(node));
        let apertureLeft = -Infinity, apertureRight = Infinity, apertureTop = -Infinity, apertureBottom = Infinity;
        for (const clip of clips) {
          if (!clip.bounds || clip.x && ![clip.bounds.left, clip.bounds.right].every(Number.isFinite)
              || clip.y && ![clip.bounds.top, clip.bounds.bottom].every(Number.isFinite)) return false;
          if (clip.x) { apertureLeft = Math.max(apertureLeft, clip.bounds.left); apertureRight = Math.min(apertureRight, clip.bounds.right); }
          if (clip.y) { apertureTop = Math.max(apertureTop, clip.bounds.top); apertureBottom = Math.min(apertureBottom, clip.bounds.bottom); }
        }
        const tinyAperture = apertureRight - apertureLeft <= 1 && apertureBottom - apertureTop <= 1;
        range.selectNodeContents(node);
        for (const rect of range.getClientRects()) {
          const bounds = normalizeRectangle(rect);
          if (!bounds || !(bounds.width > 0 && bounds.height > 0)) continue;
          if (!clips.length) return true;
          if (clips.some(clip => clip.x && ![bounds.left, bounds.right].every(Number.isFinite)
              || clip.y && ![bounds.top, bounds.bottom].every(Number.isFinite))) continue;
          let left = bounds.left, right = bounds.right, top = bounds.top, bottom = bounds.bottom;
          for (const clip of clips) {
            if (clip.x) { left = Math.max(left, clip.bounds.left); right = Math.min(right, clip.bounds.right); }
            if (clip.y) { top = Math.max(top, clip.bounds.top); bottom = Math.min(bottom, clip.bounds.bottom); }
          }
          // A larger glyph range reduced to a 1x1 accessibility aperture does
          // not display readable text. Do not impose a minimum on real layout
          // or reject an ordinary partly visible line at a larger clip edge.
          const cropped = left > bounds.left || right < bounds.right || top > bounds.top || bottom < bounds.bottom;
          const positiveX = clips.some(clip => clip.x) ? right > left : bounds.width > 0;
          const positiveY = clips.some(clip => clip.y) ? bottom > top : bounds.height > 0;
          if (positiveX && positiveY && !(tinyAperture && cropped)) return true;
        }
      } catch { /* Detached source nodes are not eligible. */ }
      finally {
        // A resident Range must not retain the last removed shadow text node.
        try { range.selectNodeContents(document.documentElement || document); range.collapse?.(true); } catch { /* Navigating document. */ }
      }
      return false;
    };
  }
  function selectedTextSlices(document, selection) {
    const selected = new Map();
    if (!selection || selection.isCollapsed || !Number.isInteger(selection.rangeCount) || selection.rangeCount < 1) return selected;
    const ranges = [];
    for (let index = 0; index < Math.min(selection.rangeCount, 8); index++) {
      try { const range = selection.getRangeAt(index); if (!range.collapsed) ranges.push(range); } catch { /* Detached selection. */ }
    }
    for (const node of walkAccessibleNodes(document.body)) {
      if (node.nodeType !== 3 || node.isConnected === false) continue;
      const length = String(node.nodeValue || "").length, intervals = [];
      for (const range of ranges) {
        try {
          if (!range.intersectsNode(node)) continue;
          const start = range.startContainer === node ? range.startOffset : 0;
          const end = range.endContainer === node ? range.endOffset : length;
          if (Number.isInteger(start) && Number.isInteger(end) && start >= 0 && end <= length && end > start) intervals.push({ start, end });
        } catch { /* Crossing a closed tree or a removed node is not a readable selection. */ }
      }
      intervals.sort((left, right) => left.start - right.start);
      const merged = [];
      for (const interval of intervals) {
        const previous = merged.at(-1);
        if (previous && interval.start <= previous.end) previous.end = Math.max(previous.end, interval.end);
        else merged.push({ ...interval });
      }
      if (merged.length) selected.set(node, merged);
    }
    return selected;
  }
  function applySelectedPieces(record, intervals) {
    const next = []; let at = 0;
    for (const piece of record.pieces) {
      const end = at + piece.source.length;
      const cuts = [...new Set([at, end, ...intervals.flatMap(interval => [Math.max(at, interval.start), Math.min(end, interval.end)])
        .filter(value => value > at && value < end)])].sort((left, right) => left - right);
      for (let index = 0; index < cuts.length - 1; index++) {
        const start = cuts[index], stop = cuts[index + 1], selected = intervals.some(interval => interval.start <= start && interval.end >= stop);
        if (start === at && stop === end && !selected) { next.push({ ...piece, selected: false }); continue; }
        const source = piece.source.slice(start - at, stop - at);
        for (const part of policy.splitText(source)) next.push({ source: part, value: part, selected });
      }
      at = end;
    }
    record.pieces = next;
  }
  async function sourceDigest(text, environment) {
    const implementation = root.crypto?.subtle || (typeof require === "function" ? require("node:crypto").webcrypto.subtle : null);
    if (!implementation) {
      const hash = await environment?.hashSourceGroup?.(text);
      return typeof hash === "string" && /^[a-f0-9]{64}$/iu.test(hash) ? hash.toLowerCase() : null;
    }
    const digest = await implementation.digest("SHA-256", new TextEncoder().encode(text));
    return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, "0")).join("");
  }
  function canonicalSource(source) {
    const raw = String(source || ""), positions = new Array(raw.length + 1).fill(0);
    const first = raw.match(/^\s*/u)[0].length, last = raw.length - raw.match(/\s*$/u)[0].length;
    let text = "", at = first;
    while (at < last) {
      if (canonicalSpace(raw.charCodeAt(at))) {
        let end = at + 1; while (end < last && canonicalSpace(raw.charCodeAt(end))) end++;
        text += " "; for (let index = at + 1; index <= end; index++) positions[index] = text.length;
        at = end;
      } else { text += raw[at++]; positions[at] = text.length; }
    }
    for (let index = Math.max(first, last); index <= raw.length; index++) positions[index] = text.length;
    return { text, positions };
  }
  function canonicalSpace(code) { return code === 32 || code === 9 || code === 10 || code === 12 || code === 13; }
  function randomIdentifier(implementation = root.crypto) {
    if (typeof implementation?.randomUUID === "function") return implementation.randomUUID();
    const bytes = new Uint8Array(16); implementation.getRandomValues(bytes);
    bytes[6] = bytes[6] & 0x0f | 0x40; bytes[8] = bytes[8] & 0x3f | 0x80;
    const hex = [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  async function extractPageSnapshot(environment, previous = new Map()) {
    const { document, getStyle, makeId, yieldWork = () => Promise.resolve(), viewportHeight = 800, targetLanguage = "简体中文" } = environment;
    const records = [], groups = new Map(), openRoots = new Set(), groupParents = new WeakMap();
    let fragmentDelay = 0, visibleFragmentDelay = 0;
    let eligibility = new WeakMap(); const checkedFragments = new WeakSet();
    const fragmentGroups = environment.fragmentGroups || new WeakSet();
    const layout = createScanLayout(document, getStyle);
    environment.onScanLayoutReady?.(() => { layout.reset(); eligibility = new WeakMap(); });
    const now = environment.now || (() => root.performance?.now?.() ?? Date.now());
    let sliceStart = now(), workUnits = 0;
    function sliceExpired() { return ++workUnits % 32 === 0 && now() - sliceStart >= scanSliceMilliseconds; }
    async function yieldSlice() {
      await yieldWork(); sliceStart = now();
      if (environment.isCurrent && !environment.isCurrent()) throw new Error("scan_cancelled");
    }
    if (environment.isCurrent && !environment.isCurrent()) throw new Error("scan_cancelled");
    function eligibleText(node) {
      if (unrenderedLightNode(node) || hiddenByClosedDetails(node)) return false;
      const path = []; let allowed = true;
      for (let parent = composedParent(node); parent; parent = composedParent(parent)) {
        if (eligibility.has(parent)) { allowed = eligibility.get(parent); break; }
        path.push(parent);
        if (unrenderedLightNode(parent)) { allowed = false; break; }
        if (excludedTags.has(parent.tagName) || parent.isContentEditable
            || parent.getAttribute?.("aria-hidden") === "true" || parent.hasAttribute?.("data-screen-translator")) { allowed = false; break; }
        const style = layout.style(parent);
        if (!renderingStylePermitted(parent, style, fragmentGroups)) { allowed = false; break; }
      }
      for (const element of path) eligibility.set(element, allowed);
      return allowed;
    }
    for (const node of walkAccessibleNodes(document.body, openRoots, environment.onRootFound)) {
      if (sliceExpired()) await yieldSlice();
      if (node.nodeType !== 3) continue;
      const candidateGroup = groupElement(node);
      if (candidateGroup && !checkedFragments.has(candidateGroup)) {
        checkedFragments.add(candidateGroup);
        if (fragmentedInlineGroup(candidateGroup, previous)) fragmentGroups.add(candidateGroup);
        else fragmentGroups.delete(candidateGroup);
      }
      const before = previous.get(node);
      const source = before && node.nodeValue === before.lastWritten ? before.source : node.nodeValue;
      if (typeof source !== "string" || !source.length) continue;
      // Animated word wrappers include separator nodes with zero-width ranges
      // at line breaks. They remain part of the authored sentence and identity.
      const fragmentSeparator = fragmentGroups.has(candidateGroup) && !source.trim();
      const allowed = eligibleText(node) && (fragmentSeparator || !environment.isRenderedText || renderedRecord(before || { node }, environment.isRenderedText, layout));
      environment.onTextEligibility?.(node, allowed);
      if (!allowed) continue;
      const parent = composedParent(node);
      if (!parent) continue;
      if (!groupParents.has(parent)) groupParents.set(parent, groupElement(node));
      const element = groupParents.get(parent); if (!element) continue;
      let group = groups.get(element);
      if (!group) { const prose = isProseGroup(element); group = { id: makeId(), element, text: "", records: [], prose,
        kind: /^H[1-6]$/u.test(element.tagName) ? "heading" : !prose ? "navigation" : "body", main: isMainGroup(element) }; groups.set(element, group); }
      const renderSlots = new Set();
      for (let ancestor = composedParent(node); ancestor; ancestor = composedParent(ancestor)) if (ancestor.tagName === "SLOT") renderSlots.add(ancestor);
      const bounds = layout.bounds(element);
      const record = { node, source, lastWritten: node.nodeValue, groupId: group.id, groupKind: group.kind, groupElement: element,
        groupOrder: group.records.length, groupSourceStart: group.text.length, renderSlots, pieces: [], before, renderEligible: true,
        visible: !!bounds && bounds.bottom > 0 && bounds.top < viewportHeight };
      for (const sourcePiece of policy.splitText(source)) {
        record.pieces.push({ source: sourcePiece, value: sourcePiece });
      }
      group.records.push(record); group.text += source; records.push(record);
    }
    for (const group of groups.values()) {
      if (sliceExpired()) await yieldSlice();
      group.canonical = canonicalSource(group.text);
    }
    const canonicalGroups = [...groups.values()].map(group => ({ ...group, text: group.canonical.text })).filter(group => group.text.length);
    const socialFeed = policy.isSocialFeed(document.location?.href);
    const eligibleGroups = socialFeed ? [...groups.values()].map(group => ({...group,
      text: canonicalSource(policy.socialForeignText(group.text, targetLanguage)).text})).filter(group => group.text) : canonicalGroups;
    const proseGroups = eligibleGroups.filter(group => group.prose);
    const summary = policy.summarize(proseGroups, eligibleGroups);
    const blocks = [];
    const selectedGroups = [];
    const pageHasJapaneseProse = summary.pageSummary.kanaProseCharacters >= 8;
    const selectedRanges = environment.selectedRanges;
    await Promise.all([...groups.values()].map(async group => {
      if (!policy.hasLanguageText(group.text) || group.canonical.text.length > 1000000) return;
      const same = group.records.find(record => record.before?.groupElement === group.element
        && record.before.groupText === group.text && record.before.groupHash)?.before;
      group.sourceDigest = same?.groupHash || await sourceDigest(group.canonical.text, environment);
    }));
    if (environment.isCurrent && !environment.isCurrent()) throw new Error("scan_cancelled");
    for (const group of groups.values()) {
      if (sliceExpired()) await yieldSlice();
      const groupBlockStart = blocks.length;
      const unchanged = group.records.every(record => record.before?.groupId && record.before.source === record.source
        && record.before.groupElement === group.element && record.before.groupText === group.text);
      if (unchanged) {
        group.id = group.records[0].before.groupId;
        for (const record of group.records) record.groupId = group.id;
      }
      for (const record of group.records) {
        // Carry a prefix/completed translation only when the whole semantic
        // paragraph is unchanged. A changed date or inline neighbour must not
        // leave the other fragments displaying facts from the previous paragraph.
        const before = record.before;
        if (before?.source === record.source && before.groupElement === group.element && before.groupText === group.text)
          record.pieces = before.pieces.map(piece => ({ ...piece }));
        record.groupText = group.text; record.groupHash = group.sourceDigest; delete record.before;
        if (selectedRanges) applySelectedPieces(record, selectedRanges.get(record.node) || []);
      }
      if (!policy.hasLanguageText(group.text)) continue;
      if (socialFeed && policy.socialChineseParagraph(group.text, targetLanguage)) {
        for (const record of group.records) for (const piece of record.pieces) {
          delete piece.id; piece.value = piece.source; piece.complete = false; piece.failed = false;
        }
        continue;
      }
      const hasKana = policy.languageCounts(group.text).kanaCharacters > 0;
      const foreignText = socialFeed ? policy.socialForeignText(group.text, targetLanguage) : group.text;
      const excludedSocialLines = socialFeed && foreignText !== group.text;
      if (excludedSocialLines) for (const record of group.records) {
        // Keep line breaks and raw restoration, but make Chinese lines a local
        // literal segment before model blocks/context/samples are constructed.
        const sources = (record.source.match(/[^\r\n]*(?:\r\n|[\r\n]|$)/gu) || []).filter(Boolean).flatMap(source => policy.splitText(source));
        if (record.pieces.map(piece=>piece.source).join("") === record.source && sources.length === record.pieces.length
            && sources.every((source,index)=>source===record.pieces[index].source)) continue;
        record.pieces = sources.map(source => ({source,value:source}));
      }
      const foreignHanContext = hasKana || !socialFeed && pageHasJapaneseProse && (group.prose || /^H[1-6]$/u.test(group.element.tagName));
      const localGroup = policy.localCalendarText(group.canonical.text, targetLanguage);
      const atomicCalendar = localGroup === null && group.canonical.text.length <= policy.maximumBlockCharacters
        && group.records.length > 1 && group.records.some(record => hasInlineCalendar(record, group.element) || policy.localCalendarText(record.source, targetLanguage) !== null)
        && calendarCanReorder(group.element) && (!selectedRanges || group.records.every(record => record.pieces.every(piece => piece.selected)));
      const socialAtomic = socialFeed && !excludedSocialLines && !selectedRanges && plainSocialParagraph(group.element)
        && group.canonical.text.length <= policy.maximumBlockCharacters;
      for (const record of group.records) record.socialAtomic = socialAtomic;
      const fragmentRuns = socialAtomic ? [group.records] : !selectedRanges && fragmentGroups.has(group.element) && group.canonical.text.length <= policy.maximumBlockCharacters
        ? fragmentStyleRuns(group, getStyle) : null;
      if (fragmentRuns && !socialAtomic && environment.fragmentStability) {
        const delay = environment.fragmentStability(group.element, group.text);
        if (delay > 0) {
          fragmentDelay = Math.max(fragmentDelay, delay);
          if (group.records.some(record => record.visible)) visibleFragmentDelay = Math.max(visibleFragmentDelay, delay);
          continue;
        }
      }
      if (atomicCalendar || fragmentRuns) {
        for (const segment of atomicCalendar ? [group.records] : fragmentRuns) {
          const raw = segment.map(record => record.source).join(""), canonical = canonicalSource(raw).text;
          if (!policy.hasLanguageText(canonical) || policy.skipTranslationText(canonical, { targetLanguage, kind: group.kind, foreignHanContext })) continue;
          const owner = segment.findLast(record => record.source.trim()) || segment.at(-1);
          const previousId = owner.pieces[0]?.id;
          for (const record of segment) {
            const previous = record.pieces.length === 1 && record.pieces[0].source === record.source ? record.pieces[0] : null;
            const piece = previous ? { ...previous } : { source: record.source, value: record.source };
            delete piece.id; piece.atomicPeer = record !== owner; piece.local = false;
            record.pieces = [piece]; record.atomicOwner = owner;
          }
          const piece = owner.pieces[0];
          const sourceStart = group.canonical.positions[segment[0].groupSourceStart + raw.match(/^\s*/u)[0].length];
          const complete = piece.complete === true && piece.text === canonical;
          Object.assign(piece, { id: complete && previousId || makeId(), text: canonical, prefix: raw.match(/^\s*/u)[0],
            suffix: raw.match(/\s*$/u)[0], complete });
          blocks.push({ id: piece.id, text: piece.text, groupId: group.id, context: group.canonical.text.slice(0, 512),
            contextKind: selectedRanges ? "selection" : "source", ...(group.sourceDigest ? { sourceGroupHash: group.sourceDigest,
              sourceGroupLength: group.canonical.text.length, sourceStart, sourceLength: piece.text.length } : {}),
            visible: segment.some(record => record.visible), record: owner, piece, atomicRecords: segment });
        }
        continue;
      }
      for (const record of group.records) delete record.atomicOwner;
      let groupContext = selectedRanges
        ? canonicalSource(group.records.flatMap(record => record.pieces.filter(piece => piece.selected).map(piece => piece.source)).join("")).text
        : group.canonical.text;
      if (socialFeed && !selectedRanges) groupContext = canonicalSource(foreignText).text;
      if (selectedRanges && groupContext.trim()) selectedGroups.push({ text: groupContext, kind: group.kind });
      for (const record of group.records) record.selectionScoped = !!selectedRanges && !!groupContext.trim();
      for (const record of group.records) {
        let sourceAt = record.groupSourceStart;
        for (const piece of record.pieces) {
          const pieceAt = sourceAt; sourceAt += piece.source.length;
          if (selectedRanges && !piece.selected) continue;
        const calendarValue = policy.localCalendarText(piece.source, targetLanguage);
        const localValue = localGroup !== null ? calendarValue : null;
        delete piece.atomicPeer;
        if (calendarValue === null && (excludedSocialLines && policy.socialChineseParagraph(piece.source, targetLanguage)
            || policy.skipTranslationText(piece.source, { targetLanguage, kind: group.kind,
              foreignHanContext: foreignHanContext || socialFeed && !excludedSocialLines, explicitSelection: !!selectedRanges }))) {
          // Page-language evidence can change while this heading/label stays
          // identical. A now-native Han-only label must not carry an earlier
          // Japanese-context translation into an English article.
          delete piece.id; delete piece.local; piece.value = piece.source; piece.complete = false; piece.failed = false; continue;
        }
        // Decide at paragraph level: a <strong>東京都</strong> fragment inside
        // Japanese prose is not an independent Chinese label.
        const prefix = piece.source.match(/^\s*/u)[0], suffix = piece.source.match(/\s*$/u)[0];
        const start = group.canonical.positions[pieceAt + prefix.length], end = group.canonical.positions[pieceAt + piece.source.length - suffix.length];
        Object.assign(piece, { id: piece.id || makeId(), text: group.canonical.text.slice(start, end), prefix,
          suffix, complete: piece.complete === true });
        piece.local = localValue !== null;
        if (piece.local) { piece.value = localValue; piece.complete = true; piece.failed = false; piece.cached = true; }
        blocks.push({ id: piece.id, text: piece.text, groupId: group.id,
          context: groupContext.trim().slice(0, 512), contextKind: selectedRanges ? "selection" : "source",
          ...(foreignHanContext && policy.alreadyTargetText(piece.source, targetLanguage) ? { foreignHanContext: true } : {}),
          ...(group.sourceDigest ? { sourceGroupHash: group.sourceDigest, sourceGroupLength: group.canonical.text.length,
            sourceStart: start, sourceLength: piece.text.length } : {}),
          visible: record.visible, record, piece });
        }
      }
      if (selectedRanges) {
        const selectedBlocks = blocks.slice(groupBlockStart); let context = "", previous = null;
        for (const block of selectedBlocks) {
          if (previous && (block.sourceStart === undefined || previous.sourceStart === undefined
              || block.sourceStart > previous.sourceStart + previous.sourceLength)) context += " ";
          context += block.text; previous = block;
        }
        for (const block of selectedBlocks) block.context = context.slice(0, 512);
      }
    }
    blocks.sort((left, right) => Number(right.visible) - Number(left.visible));
    // Exact authored source, rather than rendered translations or a small hash,
    // identifies an unchanged document decision across tab and BFCache returns.
    const sourceSignature = JSON.stringify(canonicalGroups.map(group => [group.kind, group.text]));
    return { records, blocks, openRoots, sourceSignature, fragmentDelay, visibleFragmentDelay, ...(selectedRanges ? policy.summarize(selectedGroups) : summary) };
  }
  function createPageAutoSession(environment) {
    environment.fragmentGroups = new WeakSet();
    const fragmentSources = new WeakMap();
    environment.fragmentStability = (element, source) => {
      const now = Date.now(), before = fragmentSources.get(element);
      if (!before || before.source !== source) { fragmentSources.set(element, { source, changedAt: now }); return 220; }
      return Math.max(0, 220 - (now - before.changedAt));
    };
    const { document, token, send, onStatus = () => {}, onDecision = () => {}, onView = () => {}, schedule = callback => setTimeout(callback, 0), clearSchedule = clearTimeout } = environment;
    let config = { enabled: false, active: true, manualAvailable: false, generation: 0, targetLanguage: "简体中文", judgementMode: "program", concurrency: 10,
      chineseProseShareThresholdPercent: 50, cacheScope: "", cacheEpoch: 0 };
    let revision = 0, records = new Map(), blocks = new Map(), queue = [], jobs = new Map(), assessed = null, knownOpenRoots = new Set();
    let paused = false, viewMode = "translation", disposed = false, suspended = false, scanTimer = null, scanning = false, scanAgain = false, firstRequestId = null;
    let scanEligibility = null, resetScanLayout = null;
    let preparationRetries = 0;
    let manualMode = null, manualRunning = false, selectionPending = false, pickingParagraph = false, selectedRanges = null, selectionSources = null;
    let manualImageRequest = null;
    const annotations = new Map(), ownedNodes = new WeakSet();
    const completedGroups = new Map(), decisionMemo = new Map();
    let completedCharacters = 0, decisionCharacters = 0, snapshotKey = null, localRejection = null;
    let routeKey = pageRouteKey(environment.getLocation?.() || document.location?.href);
    const maximumCompletedCharacters = 8 * 1024 * 1024, maximumCompletedGroups = 16384;
    function groupKey(groupRecords) {
      if (groupRecords.length && groupRecords.every(record => record.socialAtomic))
        return JSON.stringify([config.targetLanguage, groupRecords[0].groupKind, "social-plain", canonicalSource(groupRecords.map(record => record.source).join("")).text]);
      return JSON.stringify([config.targetLanguage, groupRecords[0]?.groupKind, groupRecords.map(record => record.source)]);
    }
    function cacheCompletedGroups(changedElements) {
      const groups = new Map();
      for (const record of records.values()) {
        const element = record.groupElement;
        if (changedElements && !changedElements.has(element)) continue;
        if (record.node.isConnected === false || groupElement(record.node) !== element) continue;
        if (!groups.has(element)) groups.set(element, []); groups.get(element).push(record);
      }
      for (const groupRecords of groups.values()) {
        groupRecords.sort((left, right) => left.groupOrder - right.groupOrder);
        const pieces = groupRecords.flatMap(record => record.pieces);
        if (!pieces.some(piece => piece.id) || pieces.some(piece => piece.id && (!piece.complete || piece.failed))) continue;
        const source = groupRecords.map(part => part.source).join("");
        if (groupRecords.some(record => record.groupText !== source)) continue;
        const key = groupKey(groupRecords);
        const values = groupRecords.map(record => record.pieces.map(piece => ({ source: piece.source,
          value: piece.value, eligible: !!piece.id })));
        const characters = key.length + values.flat().reduce((sum, piece) => sum + piece.source.length + piece.value.length, 0);
        if (characters > maximumCompletedCharacters) continue;
        const old = completedGroups.get(key); if (old) { completedCharacters -= old.characters; completedGroups.delete(key); }
        completedGroups.set(key, { values, characters, selectionScoped: groupRecords.some(record => record.selectionScoped),
          ...(groupRecords.every(record => record.socialAtomic) ? { socialValue: values.flat().map(piece => piece.value).join("") } : {}) }); completedCharacters += characters;
        while (completedCharacters > maximumCompletedCharacters || completedGroups.size > maximumCompletedGroups) {
          const first = completedGroups.keys().next().value;
          completedCharacters -= completedGroups.get(first).characters; completedGroups.delete(first);
        }
      }
    }
    function reuseCompletedGroups(snapshot) {
      const groups = new Map();
      for (const record of snapshot.records) {
        if (!groups.has(record.groupElement)) groups.set(record.groupElement, []);
        groups.get(record.groupElement).push(record);
      }
      for (const groupRecords of groups.values()) {
        const key = groupKey(groupRecords), entry = completedGroups.get(key); if (!entry) continue;
        if (typeof entry.socialValue === "string" && groupRecords.every(record => record.socialAtomic)
            && groupRecords.flatMap(record => record.pieces).filter(piece => piece.id).length === 1) {
          for (const record of groupRecords) for (const piece of record.pieces) {
            piece.value = piece.id ? entry.socialValue : ""; piece.complete = true; piece.failed = false; piece.cached = true;
          }
          completedGroups.delete(key); completedGroups.set(key, entry); continue;
        }
        // Keep paragraph context and inline boundaries exact. In particular a
        // changed date cannot reuse its neighbour's old translated fact, and a
        // Han label loses eligibility when its Japanese context disappears.
        const compatible = groupRecords.every((record, index) => record.pieces.length === entry.values[index]?.length
          && record.pieces.every((piece, at) => piece.source === entry.values[index][at].source
            && !!piece.id === entry.values[index][at].eligible));
        if (!compatible) continue;
        for (const [index, record] of groupRecords.entries()) for (const [at, piece] of record.pieces.entries()) {
          if (!piece.id && !piece.atomicPeer) continue;
          piece.value = entry.values[index][at].value; piece.complete = true; piece.failed = false;
        }
        completedGroups.delete(key); completedGroups.set(key, entry);
      }
    }
    function rememberDecision(key, value) {
      if (!key || key.length > 1024 * 1024) return;
      if (decisionMemo.has(key)) { decisionCharacters -= key.length; decisionMemo.delete(key); }
      decisionMemo.set(key, value); decisionCharacters += key.length;
      while (decisionMemo.size > 8 || decisionCharacters > 2 * 1024 * 1024) {
        const first = decisionMemo.keys().next().value; decisionCharacters -= first.length; decisionMemo.delete(first);
      }
    }
    function identity(requestId) { return { requestId, documentToken: token, generation: config.generation, revision }; }
    function current(message) {
      const job = jobs.get(message?.requestId);
      return !disposed && !suspended && (config.enabled && !paused || job?.manualMode && config.manualAvailable) && message?.documentToken === token
        && message.generation === config.generation && message.revision === revision && jobs.has(message.requestId);
    }
    function readable() { return !disposed && !suspended && config.active && document.visibilityState !== "hidden"; }
    function active() { return readable() && (config.enabled && !paused && !manualMode || manualRunning && config.manualAvailable); }
    function canRender() { return readable() && ((config.enabled || config.manualAvailable) && !paused || manualRunning && config.manualAvailable); }
    function notify(state, detail = "") {
      const values = [...blocks.values()];
      const failedCount = values.filter(block => block.piece.failed).length;
      onStatus({ state: manualImageRequest ? "image_translating" : pickingParagraph ? state === "paragraph_not_found" ? state : "paragraph_pending"
          : state === "completed" && failedCount ? "partial" : state, detail, paused: paused && !manualRunning,
        manualAvailable: config.manualAvailable, manualMode, selectionPending, pickingParagraph, originalView: viewMode === "original", viewMode,
        hasTranslation: [...records.values()].some(record => record.pieces.some(piece => piece.id && !piece.failed && piece.value !== piece.source)),
        completed: values.filter(block => block.piece.complete && !block.piece.failed).length, failedCount, total: blocks.size });
    }
    function decision(isAssessed = false, allowImages = false) {
      onDecision({ enabled: active() && !manualMode, assessed: isAssessed && !manualMode, allowImages: active() && !manualMode && isAssessed && allowImages,
        generation: config.generation, revision });
    }
    function viewChanged() {
      onView({ documentToken: token, generation: config.generation, revision, enabled: (config.enabled || config.manualAvailable) && !disposed, mode: viewMode });
    }
    function removeAnnotation(element) {
      const annotation = annotations.get(element); if (!annotation) return;
      annotation.remove?.(); annotations.delete(element);
    }
    function clearAnnotations() { for (const element of annotations.keys()) removeAnnotation(element); }
    function removeClonedAnnotations(element) {
      const managed = annotations.get(element);
      for (const child of [...(element?.childNodes || [])]) if (child !== managed
          && child.getAttribute?.("data-screen-translator") === "page-bilingual") {
        // cloneNode(true) also copies our display-only note. Its authored text
        // has already been matched before this call; retire only the orphaned
        // note so original/bilingual toggles continue to have exactly one owner.
        ownedNodes.add(child); child.remove?.();
      }
    }
    function renderBilingual(changedElements = null) {
      if (viewMode !== "bilingual" || !canRender() || typeof document.createElement !== "function") { clearAnnotations(); return; }
      const groups = new Map();
      for (const record of records.values()) {
        if (changedElements && !changedElements.has(record.groupElement)) continue;
        if (record.node.isConnected === false || record.node.nodeValue !== record.lastWritten
            || !permitted(record.node, environment.getStyle, environment.fragmentGroups) || !renderedRecord(record, environment.isRenderedText)) continue;
        const element = record.groupElement; if (!element || groupElement(record.node) !== element) continue;
        let group = groups.get(element); if (!group) { group = { text: "", hasTranslation: false }; groups.set(element, group); }
        group.text += (record.selectionScoped ? record.pieces.filter(piece => piece.id && !piece.failed && piece.value !== piece.source) : record.pieces)
          .map(piece => piece.value).join("");
        group.hasTranslation ||= record.pieces.some(piece => piece.id && !piece.failed && piece.value !== piece.source);
      }
      for (const element of annotations.keys()) if ((!changedElements || changedElements.has(element)) && !groups.get(element)?.hasTranslation) removeAnnotation(element);
      for (const [element, group] of groups) {
        if (!group.hasTranslation) continue;
        removeClonedAnnotations(element);
        let annotation = annotations.get(element);
        if (!annotation || annotation.parentElement !== element) {
          removeAnnotation(element);
          annotation = document.createElement("span"); annotation.setAttribute("data-screen-translator", "page-bilingual");
          annotation.setAttribute("role", "note"); annotation.setAttribute("aria-label", "译文"); ownedNodes.add(annotation);
          annotation.style.cssText = "display:block!important;box-sizing:border-box!important;font:inherit;color:inherit;white-space:pre-wrap;overflow-wrap:anywhere;margin-block:0.42em 0;padding-inline-start:0.6em;border-inline-start:2px solid #8ba4b980;max-width:100%";
          const label = document.createElement("span"); label.setAttribute("data-screen-translator", "page-bilingual-label");
          label.style.cssText = "font:inherit;font-size:0.75em;opacity:0.6;margin-inline-end:0.65em"; label.textContent = "译文";
          const text = document.createElement("span"); text.setAttribute("data-screen-translator", "page-bilingual-text");
          ownedNodes.add(label); ownedNodes.add(text); annotation.append(label, text); element.append(annotation); annotations.set(element, annotation);
        }
        // textContent is the only sink: translated HTML is always literal text.
        const text = annotation.childNodes[1]; if (text.textContent !== group.text) text.textContent = group.text;
      }
    }
    function restore() {
      clearAnnotations();
      for (const record of records.values()) if (record.node.isConnected !== false && record.node.nodeValue === record.lastWritten) {
        if (record.node.nodeValue !== record.source) record.node.nodeValue = record.source;
        record.lastWritten = record.source;
      }
    }
    function cancel(reason, restoreText = true, preserveProgress = false) {
      environment.motion?.clearText(!restoreText && preserveProgress);
      if (jobs.size) {
        // A request-less cancellation pauses the native automatic session.
        // DOM/route replacement retires work, while an explicit pause below
        // sends pageAutoControl and owns the document-wide pause separately.
        for (const requestId of jobs.keys())
          void send({ type: "pageAutoCancel", ...identity(requestId), reason });
      }
      for (const job of jobs.values()) clearTimeout(job.deadline);
      jobs.clear(); queue = []; assessed = null; firstRequestId = null; localRejection = null;
      if (restoreText) restore();
      if (!preserveProgress) for (const record of records.values()) for (const piece of record.pieces) {
        piece.value = piece.source; piece.complete = false; piece.failed = false;
      }
      revision++;
      decision(); viewChanged();
    }
    function write(record, streaming = false, cached = false) {
      if (!canRender() || record.node.isConnected === false || record.node.nodeValue !== record.lastWritten
          || !permitted(record.node, environment.getStyle, environment.fragmentGroups) || !renderedRecord(record, environment.isRenderedText)) return false;
      const text = viewMode === "translation" ? record.pieces.map(piece => piece.value).join("") : record.source;
      const commit = value => {
        if (record.node.isConnected === false || record.node.nodeValue !== record.lastWritten || !canRender()
            || !permitted(record.node, environment.getStyle, environment.fragmentGroups)) return false;
        if (record.node.nodeValue !== value) record.node.nodeValue = value;
        record.lastWritten = value; return true;
      };
      if (streaming && viewMode === "translation" && environment.motion) {
        const done = record.pieces.every(piece => !piece.id || piece.complete);
        const success = !record.pieces.some(piece => piece.failed);
        environment.motion.softReveal(record.groupElement, cached || !success);
        return environment.motion.writer.write(record.node, text, { read: () => record.node.nodeValue,
          current: () => canRender() && record.node.isConnected !== false && record.node.nodeValue === record.lastWritten,
          commit, padding: record.source.match(/\s*$/u)[0], immediate: done || cached || !success });
      }
      environment.motion?.writer.cancel(record.node);
      return commit(text);
    }
    function failUnfinished(job) {
      const changedElements = new Set();
      for (const id of job.ids) {
        const block = blocks.get(id);
        if (block && !block.piece.complete) {
          block.piece.value = block.piece.source; block.piece.failed = true; block.piece.complete = true;
          for (const record of block.atomicRecords || []) if (record !== block.record) {
            for (const piece of record.pieces) { piece.value = piece.source; piece.complete = false; }
            write(record);
          }
          write(block.record); changedElements.add(block.record.groupElement);
          environment.motion?.textEnd(block.record.groupElement, false);
        }
      }
      renderBilingual(changedElements);
    }
    function retryPreparation(reason) {
      if (!active() || preparationRetries >= 3
          || !["page-unavailable", "page_unavailable", "automatic-queue-unavailable", "manual-queue-unavailable", "desktop_unavailable",
            "desktop_busy", "page_busy", "configuration_changed"].includes(reason)) return false;
      preparationRetries++;
      for (const record of records.values()) for (const piece of record.pieces) if (piece.failed) {
        piece.failed = false; piece.complete = false; piece.value = piece.source;
      }
      firstRequestId = null; queue = []; assessed = null; revision++; decision(); viewChanged();
      notify("retrying", reason);
      scheduleScan(["desktop_busy", "page_busy"].includes(reason) ? [1000, 2500, 6000][preparationRetries - 1]
        : [250, 500, 1000][preparationRetries - 1]); return true;
    }
    async function scan() {
      scanTimer = null;
      if (!active() || !document.body) return;
      // A resumed tab must not replace the block IDs of a still-running batch.
      if (jobs.size) { scanAgain = true; return; }
      if (scanning) { scanAgain = true; return; }
      scanning = true; scanEligibility = new WeakMap(); const atRevision = revision;
      try {
        if (!manualMode && waitingForPageContent(document)) {
          assessed = null; queue = []; decision(); notify("waiting_for_page"); return;
        }
        if (manualMode === "selection" && ![...(selectionSources || new Map())].every(([node, source]) => {
          const before = records.get(node);
          return node.isConnected !== false && (before && node.nodeValue === before.lastWritten ? before.source : node.nodeValue) === source;
        })) {
          manualRunning = false; selectedRanges = null; selectionSources = null; selectionPending = true; notify("selection_changed"); return;
        }
        const snapshot = await extractPageSnapshot({ ...environment, targetLanguage: config.targetLanguage,
          ...(manualMode === "selection" ? { selectedRanges: selectedRanges || new Map() } : {}),
          onScanLayoutReady: reset => { resetScanLayout = reset; },
          onTextEligibility: (node, allowed) => scanEligibility?.set(node, allowed),
          isCurrent: () => active() && atRevision === revision }, records);
        if (!active() || atRevision !== revision) return;
        if (snapshot.fragmentDelay > 0) scheduleScan(snapshot.fragmentDelay);
        // Always judge the fresh full-body summary. Changed readable carousel
        // prose can keep the same local rejection without retiring a decision
        // that made no requests; a changed rule result still leaves that state.
        const verdict = manualMode || config.siteMode === "allow" ? null : policy.localVerdict(snapshot.pageSummary, config.targetLanguage, config.chineseProseShareThresholdPercent);
        const sameLocalRejection = assessed === false && !!verdict && localRejection?.reason === verdict.reason;
        if (snapshotKey !== null && snapshotKey !== snapshot.sourceSignature && assessed !== null && !sameLocalRejection) {
          // A deferred layout/addition scan has no live jobs. Reassess changed
          // authored evidence, retaining already committed paragraph values.
          assessed = null; firstRequestId = null; revision++; viewChanged();
        }
        snapshotKey = snapshot.sourceSignature;
        reuseCompletedGroups(snapshot);
        knownOpenRoots = snapshot.openRoots;
        environment.onOpenRoots?.(knownOpenRoots);
        const nextRecords = new Map(snapshot.records.map(record => [record.node, record]));
        // A scroll-driven collapsed component still owns its translated text.
        // Keep its original snapshot while hidden so showing it cannot mistake
        // our Chinese output for newly authored page content.
        for (const [node, record] of records) if (!nextRecords.has(node) && node.isConnected !== false) nextRecords.set(node, record);
        records = nextRecords;
        blocks = new Map(snapshot.blocks.map(block => [block.id, block])); queue = snapshot.blocks.filter(block => !block.piece.complete);
        // New untouched source already passed the scan's rendering proof. It
        // needs neither another layout walk nor a no-op text-node mutation.
        for (const record of snapshot.records) if (record.node.nodeValue !== record.source
            || record.pieces.some(piece => piece.value !== piece.source)) write(record);
        renderBilingual();
        if (verdict) {
          assessed = false; queue = [];
          // Page admission governs new work. Added Chinese prose or a changed
          // threshold must keep published translations of unchanged source.
          // Source invalidation and explicit original/pause controls own rollback.
          rememberDecision(snapshotKey, { needsTranslation: false, allowImages: verdict.reason !== "mixed_chinese_english", reason: verdict.reason });
          localRejection = verdict;
          if (!sameLocalRejection) decision(true, verdict.reason !== "mixed_chinese_english");
          notify("skipped", verdict.reason); return;
        }
        localRejection = null;
        const remembered = manualMode || config.siteMode === "allow" ? null : decisionMemo.get(snapshotKey);
        if (remembered) {
          assessed = remembered.needsTranslation; decision(true, remembered.allowImages);
          if (!assessed) { queue = []; notify("skipped", remembered.reason || "not_needed"); return; }
        }
        // Start with complete above-the-fold animated phrases instead of
        // sending a footer batch ahead of the headline's short settling window.
        if (!firstRequestId && snapshot.visibleFragmentDelay > 0) { assessed = null; decision(); notify("assessing"); return; }
        if (!queue.length && snapshot.fragmentDelay > 0) { assessed = null; decision(); notify("assessing"); return; }
        if (!queue.length) { assessed = blocks.size > 0; manualRunning = false; decision(true, remembered ? remembered.allowImages : true); notify(assessed ? "completed" : "skipped", assessed ? "" : "no_readable_text"); return; }
        if (assessed === null) { decision(); notify("assessing"); }
        pump(snapshot);
      } catch { if (active() && atRevision === revision && !retryPreparation("page_unavailable")) { decision(); notify("failed", "page_unavailable"); } }
      finally { scanning = false; scanEligibility = resetScanLayout = null; if (scanAgain) { scanAgain = false; scheduleScan(); } }
    }
    function scheduleScan(delay = 0) {
      if (!active() || scanTimer !== null) return;
      scanTimer = schedule(scan, delay);
    }
    function pump(snapshot) {
      if (!active() || assessed === false) return;
      // The desktop translates the batch's semantic blocks with bounded workers.
      // One content batch in flight avoids multiplying desktop concurrency.
      const maxJobs = 1;
      while (queue.length && jobs.size < maxJobs) {
        if (assessed === null && firstRequestId) break;
        const batch = []; let characters = 0;
        while (queue.length && batch.length < policy.maximumBatchBlocks) {
          const group = [];
          for (const block of queue) { if (block.groupId !== queue[0].groupId) break; group.push(block); }
          const groupCharacters = group.reduce((sum, block) => sum + block.text.length + (block.context?.length || 0), 0);
          if (batch.length && batch.at(-1).groupId !== queue[0].groupId
              && (batch.length + group.length > policy.maximumBatchBlocks || characters + groupCharacters > policy.maximumBatchCharacters)) break;
          const next = queue[0]; const size = next.text.length + (next.context?.length || 0);
          if (batch.length && characters + size > policy.maximumBatchCharacters) break;
          queue.shift(); batch.push(next); characters += size;
        }
        const requestId = environment.makeId(); const ids = new Set(batch.map(block => block.id));
        const job = { ids, deadline: null, manualMode }; jobs.set(requestId, job); if (!firstRequestId) firstRequestId = requestId;
        for (const block of batch) environment.motion?.textBegin(block.record.groupElement, block.visible);
        job.expire = () => {
          if (!jobs.has(requestId)) return;
          jobs.delete(requestId);
          void send({ type: "pageAutoCancel", ...identity(requestId), reason: "translation_timeout" });
          if (assessed === null && retryPreparation("page_unavailable")) return;
          failUnfinished(job);
          if (assessed === null) { assessed = false; queue = []; }
          decision();
          notify("failed", "translation_timeout");
          if (assessed === true) pump();
        };
        job.deadline = setTimeout(job.expire, assessed === null ? 12000 : 90000); job.deadline.unref?.();
        const packet = { type: "pageAutoCandidate", ...identity(requestId), documentLanguage: document.documentElement?.lang || "",
          ...(manualMode ? { manualMode } : {}),
          blocks: batch.map(({ id, text, groupId, context, contextKind, foreignHanContext, sourceGroupHash, sourceGroupLength, sourceStart, sourceLength }) =>
            ({ id, text, groupId, ...(context ? { context, contextKind } : {}),
              ...(foreignHanContext ? { foreignHanContext: true } : {}),
              ...(sourceGroupHash ? { sourceGroupHash, sourceGroupLength, sourceStart, sourceLength } : {}) })),
          ...(snapshot ? { pageSummary: snapshot.pageSummary, samples: snapshot.samples } : {}) };
        const sentRevision = revision;
        Promise.resolve(send(packet)).then(response => {
          if (sentRevision !== revision || !jobs.has(requestId)) return;
          if (typeof response?.needsTranslation === "boolean") receive({ type: "pageAutoAssessment", ...identity(requestId), ...response });
          else if (response?.accepted === false || response?.error) {
            clearTimeout(job.deadline); jobs.delete(requestId);
            if (retryPreparation(response.reason || response.error || "desktop_unavailable")) return;
            failUnfinished(job);
            if (requestId === firstRequestId && assessed === null) { assessed = false; queue = []; }
            decision();
            notify("failed", response.reason || response.error || "desktop_unavailable");
            if (assessed === true) pump();
          }
        }, () => {
          if (sentRevision !== revision || !jobs.has(requestId)) return;
          clearTimeout(job.deadline); jobs.delete(requestId);
          if (retryPreparation("desktop_unavailable")) return;
          failUnfinished(job);
          if (assessed === null) { assessed = false; queue = []; }
          decision();
          notify("failed", "desktop_unavailable");
          if (assessed === true) pump();
        });
      }
      if (assessed === true) {
        if (!jobs.size && !queue.length) manualRunning = false;
        notify(jobs.size || queue.length ? "translating" : "completed");
      }
      if (!jobs.size && !queue.length) for (const record of records.values())
        environment.motion?.textEnd(record.groupElement, !record.pieces.some(piece => piece.failed), record.pieces.every(piece => !piece.id || piece.cached));
      if (!jobs.size && !queue.length && scanAgain) { scanAgain = false; scheduleScan(); }
    }
    function receive(message) {
      if (message?.type === "pageAutoConfig") return configure(message);
      if (message?.type === "manualImageStatus") {
        if (disposed || !manualImageRequest || message.requestId !== manualImageRequest || message.documentToken !== token || message.generation !== config.generation) return false;
        if (message.state === "translating") notify("image_translating");
        else if (["completed", "failed", "cancelled"].includes(message.state)) {
          manualImageRequest = null; notify(message.state === "completed" ? "image_completed" : "image_failed", message.reason || "image_unavailable");
        }
        return true;
      }
      if (!current(message)) return false;
      if (message.type === "pageAutoAssessment") {
        if (message.requestId !== firstRequestId || typeof message.needsTranslation !== "boolean") return false;
        assessed = message.needsTranslation;
        const firstJob = jobs.get(message.requestId);
        if (assessed && firstJob?.expire) {
          clearTimeout(firstJob.deadline); firstJob.deadline = setTimeout(firstJob.expire, 90000); firstJob.deadline.unref?.();
        }
        if (!manualMode) rememberDecision(snapshotKey, { needsTranslation: assessed, allowImages: message.allowImages === true,
          reason: String(message.reason || "not_needed") });
        decision(true, message.allowImages === true);
        if (!assessed) { environment.motion?.clearText(); for (const job of jobs.values()) clearTimeout(job.deadline); jobs.clear(); queue = []; notify("skipped", String(message.reason || "not_needed")); }
        else pump();
        return true;
      }
      if (message.type !== "pageAutoTranslations") return false;
      const job = jobs.get(message.requestId); const changed = new Set();
      const updates = message.updates ?? (message.finished === true ? [] : null);
      if (!Array.isArray(updates) || updates.length > policy.maximumBatchBlocks) return false;
      for (const update of updates) {
        if (!job.ids.has(update?.id) || (update.failed !== true && typeof update.text !== "string") || update.text?.length > 6000) continue;
        const block = blocks.get(update.id);
        if (!block || block.record.node.isConnected === false || block.record.node.nodeValue !== block.record.lastWritten || block.piece.complete) continue;
        block.piece.failed = update.failed === true;
        block.piece.value = block.piece.failed ? block.piece.source : block.piece.prefix + update.text + block.piece.suffix;
        block.piece.complete = block.piece.failed || update.complete === true;
        block.piece.cached = update.fromCache === true;
        for (const record of block.atomicRecords || []) if (record !== block.record) {
          for (const piece of record.pieces) { piece.value = block.piece.failed ? piece.source : ""; piece.complete = !block.piece.failed; piece.cached = block.piece.cached; }
          changed.add(record);
        }
        changed.add(block.record);
      }
      for (const record of changed) {
        const cached = record.pieces.every(piece => !piece.id || piece.cached);
        write(record, true, cached);
      }
      for (const element of new Set([...changed].map(record => record.groupElement))) {
        const peers = [...records.values()].filter(record => record.groupElement === element);
        if (peers.every(record => record.pieces.every(piece => !piece.id || piece.complete))) environment.motion?.textEnd(element,
          peers.every(record => !record.pieces.some(piece => piece.failed)), peers.every(record => record.pieces.every(piece => !piece.id || piece.cached)));
      }
      if ([...changed].some(record => record.pieces.every(piece => !piece.id || piece.complete)))
        cacheCompletedGroups(new Set([...changed].map(record => record.groupElement)));
      renderBilingual(new Set([...changed].map(record => record.groupElement)));
      if (message.finished === true) {
        clearTimeout(job.deadline); jobs.delete(message.requestId);
        if (message.error && retryPreparation(message.error)) return true;
        failUnfinished(job);
        if (message.error && assessed !== null) {
          queue = []; manualRunning = false; notify("failed", message.error); return true;
        }
        if (assessed === null) {
          // Admission/probing can finish before an assessment packet. The old
          // pump left firstRequestId set and waited forever for that packet.
          assessed = false; queue = []; firstRequestId = null;
          decision(); notify("failed", message.error || "page_unavailable");
        } else if (scanAgain && active()) { scanAgain = false; queue = []; scheduleScan(); }
        else pump();
      }
      else notify("translating");
      return true;
    }
    function configure(value) {
      const next = policy.normalizeConfig(value); if (!next || next.generation < config.generation) return false;
      routeKey ??= pageRouteKey(environment.getLocation?.() || document.location?.href);
      if (JSON.stringify(next) === JSON.stringify(config)) {
        // An enabled config may arrive at document_start before <body> exists.
        // DOMContentLoaded's identical query must still start the first scan.
        if (active() && assessed === null && !firstRequestId) scheduleScan();
        return true;
      }
      const activeOnly = next.enabled === config.enabled && next.generation === config.generation
        && JSON.stringify({ ...next, active: config.active }) === JSON.stringify(config);
      if (activeOnly) {
        config = next; if (!readable()) pickingParagraph = false; viewChanged();
        if (!active()) {
          if (canRender()) { for (const record of records.values()) write(record); renderBilingual(); decision(); notify(manualMode && assessed ? "completed" : "manual_ready"); }
          else { knownOpenRoots.clear(); environment.onOpenRoots?.(knownOpenRoots); decision(); notify("paused"); }
        }
        else {
          for (const record of records.values()) write(record); renderBilingual();
          const remembered = decisionMemo.get(snapshotKey); decision(assessed !== null, remembered?.allowImages === true);
          if (active()) scheduleScan();
        }
        return true;
      }
      const thresholdChanged = next.chineseProseShareThresholdPercent !== config.chineseProseShareThresholdPercent;
      const flagsOnly = next.generation === config.generation && (config.manualAvailable && next.manualAvailable || thresholdChanged)
        && JSON.stringify({ ...next, enabled: config.enabled, active: config.active, siteMode: config.siteMode, concurrency: config.concurrency,
          chineseProseShareThresholdPercent: config.chineseProseShareThresholdPercent }) === JSON.stringify(config);
      if (flagsOnly) {
        const ruleChanged = next.siteMode !== config.siteMode;
        const admissionChanged = ruleChanged || thresholdChanged;
        if (!manualRunning && (!next.enabled || admissionChanged)) cancel(ruleChanged ? "site_rule_changed" : thresholdChanged ? "threshold_changed" : "automatic_disabled", false, true);
        if (admissionChanged) { decisionMemo.clear(); decisionCharacters = 0; snapshotKey = null; }
        config = next; if (!readable()) pickingParagraph = false;
        if (!manualRunning && next.enabled) { manualMode = null; selectedRanges = null; selectionSources = null; }
        viewChanged(); for (const record of records.values()) write(record); renderBilingual(); decision();
        if (active()) { if (jobs.size && (!admissionChanged || manualRunning)) pump(); else scheduleScan(); }
        else notify(manualMode && assessed ? "completed" : "manual_ready");
        return true;
      }
      const retainCached = !!next.cacheScope && next.cacheScope === config.cacheScope && next.cacheEpoch === config.cacheEpoch
        && next.targetLanguage === config.targetLanguage;
      const preserveCompleted = retainCached && (config.enabled || config.manualAvailable) && (next.enabled || next.manualAvailable);
      const sameJudgement = next.judgementMode === config.judgementMode && !thresholdChanged;
      cancel("configuration_changed", !preserveCompleted, preserveCompleted); config = next; paused = false;
      preparationRetries = 0;
      manualMode = null; manualRunning = false; selectedRanges = null; selectionSources = null; selectionPending = false; pickingParagraph = false; manualImageRequest = null;
      if (!retainCached) { completedGroups.clear(); completedCharacters = 0; }
      if (!retainCached || !sameJudgement) { decisionMemo.clear(); decisionCharacters = 0; snapshotKey = null; }
      if (preserveCompleted) for (const record of records.values()) {
        // An epoch-stable generation change can reuse successful finals, never
        // an unfinished prefix or a failure from a retired provider request.
        for (const piece of record.pieces) if (!piece.complete || piece.failed) {
          piece.value = piece.source; piece.complete = false; piece.failed = false;
        }
        write(record);
      }
      if (!retainCached) viewMode = "translation";
      viewChanged();
      if (!active()) { knownOpenRoots.clear(); environment.onOpenRoots?.(knownOpenRoots); }
      decision();
      if (active()) scheduleScan(); else notify(config.manualAvailable ? "manual_ready" : "disabled"); return true;
    }
    function authoredSelection() {
      const visible = environment.getSelectedRanges?.() || selectedTextSlices(document,
        environment.getSelection?.() || document.getSelection?.() || root.getSelection?.());
      const selected = new Map();
      for (const [node, intervals] of visible) {
        if (!permitted(node, environment.getStyle, environment.fragmentGroups) || environment.isRenderedText && !environment.isRenderedText(node)) continue;
        const before = records.get(node);
        if (!before || node.nodeValue !== before.lastWritten || node.nodeValue === before.source) { selected.set(node, intervals); continue; }
        const translated = []; let sourceAt = 0, visibleAt = 0;
        for (const piece of before.pieces) {
          if (piece.value === piece.source) for (const interval of intervals) {
            const start = Math.max(interval.start, visibleAt), end = Math.min(interval.end, visibleAt + piece.value.length);
            if (end > start) translated.push({ start: sourceAt + start - visibleAt, end: sourceAt + end - visibleAt });
          }
          sourceAt += piece.source.length; visibleAt += piece.value.length;
        }
        if (translated.length) selected.set(node, translated);
      }
      return selected;
    }
    function startManual(mode, pickedRanges = null) {
      if (!config.manualAvailable || !readable() || !["selection", "page"].includes(mode)) return false;
      pickingParagraph = false;
      const selected = mode === "selection" ? pickedRanges || authoredSelection() : null;
      if (mode === "selection" && !selected.size) {
        const selection = environment.getSelection?.() || document.getSelection?.() || root.getSelection?.();
        selectionPending = !selection || selection.isCollapsed || !selection.rangeCount;
        notify(selectionPending ? "selection_pending" : "selection_not_needed"); return true;
      }
      cancel("manual_requested", false, true);
      for (const record of records.values()) {
        for (const piece of record.pieces) if (!piece.complete || piece.failed) { piece.value = piece.source; piece.complete = false; piece.failed = false; }
        write(record);
      }
      manualMode = mode; manualRunning = true; selectionPending = false; selectedRanges = selected; assessed = true;
      selectionSources = selected ? new Map([...selected.keys()].map(node => {
        const before = records.get(node); return [node, before && node.nodeValue === before.lastWritten ? before.source : node.nodeValue];
      })) : null;
      paused = false;
      if (viewMode === "original") viewMode = "translation";
      viewChanged(); notify("assessing"); scheduleScan(); return true;
    }
    function pickParagraph(x, y) {
      if (!pickingParagraph || !config.manualAvailable || !readable()) return false;
      const element = paragraphAtPoint(environment, x, y), selected = new Map();
      if (element) {
        let visited = 0;
        for (const node of walkAccessibleNodes(element)) {
          if (++visited > 2048) break;
          if (node.nodeType !== 3 || groupElement(node) !== element || !permitted(node, environment.getStyle, environment.fragmentGroups)) continue;
          const before = records.get(node);
          if (!renderedRecord(before || { node }, environment.isRenderedText)) continue;
          const source = before && node.nodeValue === before.lastWritten ? before.source : node.nodeValue;
          if (typeof source === "string" && source.length) selected.set(node, [{ start: 0, end: source.length }]);
        }
      }
      if (!selected.size) { notify("paragraph_not_found"); return false; }
      return startManual("selection", selected);
    }
    function pickContent(x, y) {
      if (!pickingParagraph || !config.manualAvailable || !readable()) return false;
      const image = environment.pickImageAtPoint?.(x, y);
      if (!image || image.documentToken !== token || typeof image.imageId !== "string") return pickParagraph(x, y);
      pickingParagraph = false; selectionPending = false;
      const requestId = environment.makeId(); manualImageRequest = requestId; notify("image_translating");
      Promise.resolve(send({ type: "manualImageCandidate", ...identity(requestId), imageId: image.imageId })).then(reply => {
        if (manualImageRequest !== requestId) return;
        if (reply?.accepted !== true) { manualImageRequest = null; notify("image_failed", reply?.reason || "image_unavailable"); }
      }, () => { if (manualImageRequest === requestId) { manualImageRequest = null; notify("image_failed", "desktop_unavailable"); } });
      return true;
    }
    function control(action) {
      if (action === "manual-pick" || action === "cancel-pick") {
        if (!config.manualAvailable || !readable()) return false;
        pickingParagraph = action === "manual-pick" && !pickingParagraph; selectionPending = false;
        notify(pickingParagraph ? "paragraph_pending" : jobs.size || queue.length ? "translating" : assessed ? "completed" : "manual_ready");
        return true;
      }
      if (action === "manual-selection" || action === "manual-page") return startManual(action.slice(7));
      if (action === "original" || ["view-original", "view-translation", "view-bilingual"].includes(action)) {
        viewMode = action === "original" ? viewMode === "original" ? "translation" : "original" : action.slice(5);
        if (viewMode === "translation") for (const record of records.values()) write(record);
        else restore();
        renderBilingual(); viewChanged();
        notify(paused ? "paused" : assessed === false ? "skipped" : jobs.size || queue.length ? "translating" : "completed");
        // Display-only controls are local. They neither cancel the desktop job
        // nor submit a new candidate; image overlays read the isolated view state.
        return true;
      } else if (action === "pause") { pickingParagraph = false; cancel("paused"); manualRunning = false; manualMode = null; selectedRanges = null; selectionSources = null; paused = true; knownOpenRoots.clear(); environment.onOpenRoots?.(knownOpenRoots); decision(); notify("paused"); }
      else if (action === "resume") { paused = false; manualRunning = false; manualMode = null; selectedRanges = null; selectionSources = null; cancel("resumed"); if (active()) scheduleScan(); else notify(config.manualAvailable ? "manual_ready" : "disabled"); }
      else return false;
      void send({ type: "pageAutoControl", action, ...identity() }); return true;
    }
    function mutated(mutations) {
      navigationChanged();
      if (!canRender()) return;
      let invalidated = false;
      function transferSelection(previous, next) {
        if (selectedRanges?.has(previous)) { selectedRanges.set(next, selectedRanges.get(previous)); selectedRanges.delete(previous); }
        if (selectionSources?.has(previous)) { selectionSources.set(next, selectionSources.get(previous)); selectionSources.delete(previous); }
      }
      function touchesManualSelection(mutation) {
        if (!manualRunning || manualMode !== "selection") return true;
        for (const node of selectedRanges?.keys() || []) {
          if (mutation.target === node || groupElement(mutation.target) === groupElement(node)) return true;
          for (let parent = composedParent(node); parent; parent = composedParent(parent)) if (parent === mutation.target
              && mutation.type === "attributes") return true;
          if (mutation.type === "childList") for (const removed of mutation.removedNodes || [])
            for (const descendant of walkAccessibleNodes(removed)) if (descendant === node) return true;
        }
        return false;
      }
      function refreshUntranslated(record, source) {
        const oldIds = new Set(record.pieces.map(piece => piece.id).filter(Boolean));
        for (const id of oldIds) blocks.delete(id);
        queue = queue.filter(block => !oldIds.has(block.id));
        for (const job of jobs.values()) for (const id of oldIds) job.ids.delete(id);
        record.source = source; record.lastWritten = source; record.pieces = [{ source, value: source }];
      }
      function invalidateGroup(element) {
        if (!element) return;
        for (const record of records.values()) if (record.groupElement === element) {
          const ids = record.pieces.map(piece => piece.id).filter(Boolean);
          invalidated ||= ids.length > 0 || !environment.fragmentGroups.has(element);
          for (const id of ids) {
            blocks.delete(id); queue = queue.filter(block => block.id !== id);
            for (const job of jobs.values()) job.ids.delete(id);
          }
          delete record.groupId;
          for (const piece of record.pieces) { delete piece.id; piece.value = piece.source; piece.complete = false; piece.failed = false; }
          // A reassigned slot's old fallback is no longer rendered, but it must
          // still lose the retired group's translation before a later reveal.
          if (record.node.isConnected !== false && record.node.nodeValue === record.lastWritten) {
            if (record.node.nodeValue !== record.source) record.node.nodeValue = record.source;
            record.lastWritten = record.source;
          }
        }
        removeAnnotation(element);
      }
      function rebindRegroupedCompleted() {
        const previousGroups = new Map();
        for (const record of records.values()) {
          if (!previousGroups.has(record.groupElement)) previousGroups.set(record.groupElement, []);
          previousGroups.get(record.groupElement).push(record);
        }
        for (const [beforeElement, previous] of previousGroups) {
          previous.sort((left, right) => left.groupOrder - right.groupOrder);
          const element = groupElement(previous[0].node);
          if (!element || element === beforeElement || previous.some(record => record.node.isConnected === false
              || groupElement(record.node) !== element)) continue;
          const kind = /^H[1-6]$/u.test(element.tagName) ? "heading" : isProseGroup(element) ? "body" : "navigation";
          const source = previous.map(record => record.source).join("");
          if (previous.some(record => record.groupKind !== kind || record.groupText !== source
              || record.pieces.some(piece => piece.id && (!piece.complete || piece.failed)))
              || !previous.some(record => record.pieces.some(piece => piece.id && piece.value !== piece.source))) continue;
          const nodes = [...walkAccessibleNodes(element)].filter(node => node.nodeType === 3 && groupElement(node) === element
            && permitted(node, environment.getStyle, environment.fragmentGroups)
            && (String(node.nodeValue || "").length || previous.some(record => record.atomicOwner))
            && renderedRecord(records.get(node) || { node }, environment.isRenderedText));
          // Reparent only the entire proven group, in the same order. This must
          // never salvage an unchanged fragment from a changed prose paragraph
          // or carry an old translation across slot reassignment.
          if (nodes.length !== previous.length || !nodes.every((node, index) => {
            const record = previous[index];
            if (node !== record.node || node.nodeValue !== record.source && node.nodeValue !== record.lastWritten) return false;
            const slots = new Set();
            for (let ancestor = composedParent(node); ancestor; ancestor = composedParent(ancestor)) if (ancestor.tagName === "SLOT") slots.add(ancestor);
            return slots.size === record.renderSlots?.size && [...slots].every(slot => record.renderSlots.has(slot));
          })) continue;
          removeAnnotation(beforeElement); removeClonedAnnotations(element);
          for (const record of previous) { record.groupElement = element; record.lastWritten = record.node.nodeValue; write(record); }
          renderBilingual(new Set([element]));
        }
      }
      function rebindUnchangedGroup(element) {
        if (!element) return false;
        const previous = [...records.values()].filter(record => record.groupElement === element)
          .sort((left, right) => left.groupOrder - right.groupOrder);
        if (!previous.length) return false;
        const nodes = [...walkAccessibleNodes(element)].filter(node => node.nodeType === 3
          && groupElement(node) === element && permitted(node, environment.getStyle, environment.fragmentGroups) && (String(node.nodeValue || "").length || previous.some(record => record.atomicOwner))
          && (!environment.isRenderedText || environment.isRenderedText(node) || node.nodeValue === "" && previous.some(record => record.atomicOwner)));
        if (nodes.length !== previous.length) return false;
        // Framework textContent/clone replacements can replace every Text node
        // without changing the paragraph. Rebind the actual job records before
        // observer invalidation, including an already visible stream prefix.
        if (!nodes.every((node, index) => node.nodeValue === previous[index].source
            || node.nodeValue === previous[index].lastWritten)) return false;
        if (previous.some(record => record.groupText !== previous.map(part => part.source).join(""))) return false;
        removeClonedAnnotations(element);
        for (const [index, node] of nodes.entries()) {
          const record = previous[index]; transferSelection(record.node, node); records.delete(record.node); record.node = node;
          record.renderSlots = new Set();
          for (let ancestor = composedParent(node); ancestor; ancestor = composedParent(ancestor)) if (ancestor.tagName === "SLOT") record.renderSlots.add(ancestor);
          record.lastWritten = node.nodeValue; records.set(node, record); write(record);
        }
        for (const record of previous) if (record.atomicOwner) write(record);
        renderBilingual(new Set([element])); return true;
      }
      function replayAddedParagraphs(addedNodes) {
        const elements = new Set();
        for (const added of addedNodes || []) for (const node of walkAccessibleNodes(added)) {
          if (node.nodeType === 3 && permitted(node, environment.getStyle, environment.fragmentGroups)
              && (!environment.isRenderedText || environment.isRenderedText(node))) elements.add(groupElement(node));
        }
        for (const element of elements) {
          if (!element) continue;
          const nodes = [...walkAccessibleNodes(element)].filter(node => node.nodeType === 3 && groupElement(node) === element
            && permitted(node, environment.getStyle, environment.fragmentGroups) && String(node.nodeValue || "").length
            && (!environment.isRenderedText || environment.isRenderedText(node)));
          if (!nodes.length || nodes.some(node => records.has(node))) continue;
          const prose = isProseGroup(element), kind = /^H[1-6]$/u.test(element.tagName) ? "heading" : !prose ? "navigation" : "body";
          const groupRecords = nodes.map((node, groupOrder) => ({ node, source: node.nodeValue, lastWritten: node.nodeValue,
            groupElement: element, groupOrder, groupKind: kind, renderSlots: new Set(),
            pieces: policy.splitText(node.nodeValue).map(source => ({ source, value: source })) }));
          const source = groupRecords.map(record => record.source).join("");
          // Standalone Han headings depend on other paragraphs' Japanese
          // evidence; leave those to the complete source scan.
          if (policy.alreadyTargetText(source, config.targetLanguage)) continue;
          const entry = completedGroups.get(groupKey(groupRecords)); if (!entry) continue;
          if (entry.selectionScoped && groupRecords.every((record, index) => entry.values[index]?.map(piece => piece.source).join("") === record.source)) {
            for (const [index, record] of groupRecords.entries()) {
              record.pieces = entry.values[index].map(piece => ({ source: piece.source, value: piece.source, selected: piece.eligible }));
              record.selectionScoped = true;
            }
          }
          const hasKana = policy.languageCounts(source).kanaCharacters > 0;
          const compatible = groupRecords.every((record, index) => record.pieces.length === entry.values[index]?.length
            && record.pieces.every((piece, at) => {
              const eligible = policy.hasLanguageText(piece.source) && (hasKana || !policy.alreadyTargetText(piece.source, config.targetLanguage));
              return piece.source === entry.values[index][at].source && (entry.selectionScoped || entry.values[index][at].eligible === eligible);
            }));
          if (!compatible) continue;
          removeClonedAnnotations(element);
          for (const [index, record] of groupRecords.entries()) {
            record.groupText = source;
            for (let ancestor = composedParent(record.node); ancestor; ancestor = composedParent(ancestor)) if (ancestor.tagName === "SLOT") record.renderSlots.add(ancestor);
            for (const [at, piece] of record.pieces.entries()) if (entry.values[index][at].eligible) {
              piece.id = environment.makeId(); piece.value = entry.values[index][at].value; piece.complete = true; piece.failed = false;
            }
            records.set(record.node, record); write(record);
          }
        }
      }
      function rebindReplacedParagraphs(addedNodes, removedNodes) {
        const removed = new Set();
        for (const node of removedNodes || []) for (const descendant of walkAccessibleNodes(node)) removed.add(descendant);
        const previousGroups = new Map();
        for (const record of records.values()) if (removed.has(record.node)) {
          if (!previousGroups.has(record.groupElement)) previousGroups.set(record.groupElement, []);
          previousGroups.get(record.groupElement).push(record);
        }
        const replacements = new Map();
        for (const added of addedNodes || []) for (const node of walkAccessibleNodes(added)) {
          if (node.nodeType !== 3 || !permitted(node, environment.getStyle, environment.fragmentGroups)) continue;
          const element = groupElement(node);
          const emptyCalendarPeer = node.nodeValue === "" && [...previousGroups.values()].some(group => group.some(record => record.atomicOwner));
          if ((!String(node.nodeValue || "").length || environment.isRenderedText && !environment.isRenderedText(node)) && !emptyCalendarPeer) continue;
          if (!replacements.has(element)) replacements.set(element, []); replacements.get(element).push(node);
        }
        const reboundRemoved = new Set();
        for (const [element, nodes] of replacements) {
          if (!element || nodes.some(node => records.has(node))) continue;
          const prose = isProseGroup(element), kind = /^H[1-6]$/u.test(element.tagName) ? "heading" : !prose ? "navigation" : "body";
          const matches = [...previousGroups.values()].map(group => group.sort((left, right) => left.groupOrder - right.groupOrder))
            .filter(group => group.length === nodes.length && group[0].groupKind === kind
              && group.some(record => record.pieces.some(piece => piece.id && !piece.failed && piece.value !== piece.source))
              && group.every(record => record.pieces.every(piece => !piece.failed))
              && group.every((record, index) => nodes[index].nodeValue === record.source || nodes[index].nodeValue === record.lastWritten)
              && group.every(record => record.groupText === group.map(part => part.source).join("")));
          // Only a uniquely identified replacement from this mutation may
          // recover the authored source from copied rendered Chinese text.
          if (matches.length !== 1) continue;
          const previous = matches[0]; previousGroups.delete(previous[0].groupElement);
          removeAnnotation(previous[0].groupElement);
          removeClonedAnnotations(element);
          for (const [index, node] of nodes.entries()) {
            const record = previous[index]; transferSelection(record.node, node); reboundRemoved.add(record.node); records.delete(record.node); record.node = node; record.groupElement = element;
            record.renderSlots = new Set();
            for (let ancestor = composedParent(node); ancestor; ancestor = composedParent(ancestor)) if (ancestor.tagName === "SLOT") record.renderSlots.add(ancestor);
            record.lastWritten = node.nodeValue; records.set(node, record); write(record);
          }
          for (const record of previous) if (record.atomicOwner) write(record);
          renderBilingual(new Set([element]));
        }
        return reboundRemoved;
      }
      function hasUnchangedGroupSource(element, expected) {
        let text = "";
        for (const node of walkAccessibleNodes(element)) {
          if (node.nodeType !== 3 || groupElement(node) !== element || !permitted(node, environment.getStyle, environment.fragmentGroups)) continue;
          const before = records.get(node);
          text += before && node.nodeValue === before.lastWritten ? before.source : node.nodeValue;
        }
        return text === expected;
      }
      function rebindCoalescedGroup(element) {
        if (!element) return false;
        const previous = [...records.values()].filter(record => record.groupElement === element)
          .sort((left, right) => left.groupOrder - right.groupOrder);
        if (previous.length < 2) return false;
        const nodes = [...walkAccessibleNodes(element)].filter(node => node.nodeType === 3
          && groupElement(node) === element && permitted(node, environment.getStyle, environment.fragmentGroups) && (String(node.nodeValue || "").length || previous.some(record => record.atomicOwner))
          && (!environment.isRenderedText || environment.isRenderedText(node) || node.nodeValue === "" && previous.some(record => record.atomicOwner)));
        if (!nodes.length || nodes.length >= previous.length) return false;
        const authored = previous.map(record => record.source).join("");
        if (previous.some(record => record.groupText !== authored)) return false;
        const matches = [];
        function match(nodeIndex, oldIndex, segments) {
          if (matches.length > 1) return;
          if (nodeIndex === nodes.length) { if (oldIndex === previous.length) matches.push(segments); return; }
          let source = "", rendered = "";
          for (let end = oldIndex; end < previous.length - (nodes.length - nodeIndex - 1); end++) {
            source += previous[end].source; rendered += previous[end].lastWritten;
            if (nodes[nodeIndex].nodeValue === source || nodes[nodeIndex].nodeValue === rendered)
              match(nodeIndex + 1, end + 1, [...segments, previous.slice(oldIndex, end + 1)]);
          }
        }
        match(0, 0, []);
        // Only exact, uniquely identified fine-to-coarse concatenation is safe.
        // A new fine inline structure must still be translated with its own IDs.
        if (matches.length !== 1) return false;
        const replacements = [];
        for (const [index, segment] of matches[0].entries()) {
          const node = nodes[index], source = segment.map(record => record.source).join(""), intervals = []; let offset = 0;
          for (const record of segment) {
            for (const interval of selectedRanges?.get(record.node) || []) intervals.push({ start: offset + interval.start, end: offset + interval.end });
            offset += record.source.length;
          }
          const record = { ...segment[0], node, source, lastWritten: node.nodeValue, groupOrder: index,
            pieces: segment.flatMap(item => item.pieces), selectionScoped: segment.some(item => item.selectionScoped), renderSlots: new Set() };
          for (let ancestor = composedParent(node); ancestor; ancestor = composedParent(ancestor)) if (ancestor.tagName === "SLOT") record.renderSlots.add(ancestor);
          replacements.push({ record, segment, intervals });
        }
        for (const { segment } of replacements) for (const before of segment) {
          records.delete(before.node); selectedRanges?.delete(before.node); selectionSources?.delete(before.node);
        }
        const replacementByPrevious = new Map();
        for (const { record, segment } of replacements) for (const before of segment) replacementByPrevious.set(before, record);
        for (const { record } of replacements) if (record.atomicOwner) record.atomicOwner = replacementByPrevious.get(record.atomicOwner) || record.atomicOwner;
        for (const block of blocks.values()) if (block.atomicRecords) block.atomicRecords = [...new Set(block.atomicRecords.map(record => replacementByPrevious.get(record) || record))];
        for (const { record, segment, intervals } of replacements) {
          records.set(record.node, record);
          if (intervals.length) { selectedRanges?.set(record.node, intervals); selectionSources?.set(record.node, record.source); }
          const retired = new Set(segment);
          for (const block of blocks.values()) if (retired.has(block.record)) block.record = record;
          write(record);
        }
        removeClonedAnnotations(element); renderBilingual(new Set([element])); cacheCompletedGroups(new Set([element]));
        return true;
      }
      function readableChange(node) {
        if (node.nodeType === 3) return policy.hasLanguageText(node.nodeValue) && permitted(node, environment.getStyle, environment.fragmentGroups);
        if (node.nodeType !== 1 || !permitted({ assignedSlot: node }, environment.getStyle, environment.fragmentGroups)) return false;
        for (const text of walkAccessibleNodes(node)) if (text.nodeType === 3 && policy.hasLanguageText(text.nodeValue) && permitted(text, environment.getStyle, environment.fragmentGroups)) return true;
        return false;
      }
      function eligibilityChanged(element) {
        let changed = false;
        for (const text of walkAccessibleNodes(element)) {
          if (text.nodeType !== 3) continue;
          const record = records.get(text);
          if (!policy.hasLanguageText(record?.source || text.nodeValue)) continue;
          const allowed = permitted(text, environment.getStyle, environment.fragmentGroups) && renderedRecord(record || { node: text }, environment.isRenderedText);
          if (scanning) {
            // An unseen node will be checked by the continuing traversal. A CSS
            // animation on already checked text only retires work if readability
            // actually changes, rather than aborting every animation frame.
            if (scanEligibility?.has(text)) changed ||= scanEligibility.get(text) !== allowed;
            else if (record) changed ||= (record.renderEligible !== false) !== allowed;
            if (record) record.renderEligible = allowed;
            continue;
          }
          if (record) {
            if ((record.renderEligible !== false) !== allowed) changed = true;
            record.renderEligible = allowed;
          } else if (allowed) changed = true;
        }
        return changed;
      }
      let changed = false;
      if (mutations.some(mutation => mutation.type === "childList" && !ownedNodes.has(mutation.target)
          && touchesManualSelection(mutation))) rebindRegroupedCompleted();
      // normalize() reports characterData before childList. Rebind against the
      // final DOM before either event can discard the proven old fine pieces.
      const coalesced = new Set();
      for (const mutation of mutations) {
        if (ownedNodes.has(mutation.target) || !touchesManualSelection(mutation)
            || !["characterData", "childList"].includes(mutation.type)) continue;
        if (mutation.type === "characterData" && records.get(mutation.target)?.lastWritten === mutation.target.nodeValue) continue;
        const element = groupElement(mutation.target.nodeType === 3 ? mutation.target : { assignedSlot: mutation.target });
        if (!coalesced.has(element)) { coalesced.add(element); rebindCoalescedGroup(element); }
      }
      for (const mutation of mutations) {
        if (ownedNodes.has(mutation.target)) continue;
        if (!touchesManualSelection(mutation)) continue;
        if (mutation.type === "characterData") {
          const record = records.get(mutation.target);
          if (record && mutation.target.nodeValue === record.lastWritten) continue;
          if (!permitted(mutation.target, environment.getStyle, environment.fragmentGroups)) continue;
          const nextSource = String(mutation.target.nodeValue || "");
          if (record && nextSource === record.source && record.pieces.some(piece => !piece.failed && piece.value !== piece.source)
              && groupElement(record.node) === record.groupElement && hasUnchangedGroupSource(record.groupElement, record.groupText)) {
            // Some virtualised readers rewrite their unchanged authored string
            // while scrolling. Reapply the retained prefix synchronously instead
            // of treating the framework's repaint as a new translation request.
            record.lastWritten = nextSource; write(record); continue;
          }
          if (!policy.hasLanguageText(nextSource)) {
            const hadLanguage = record && policy.hasLanguageText(record.source);
            const changedParagraphFact = record && [...records.values()].some(peer => peer !== record
              && peer.groupElement === record.groupElement && peer.pieces.some(piece => piece.id));
            if (changedParagraphFact) { invalidateGroup(record.groupElement); changed = true; }
            if (record) refreshUntranslated(record, nextSource);
            // Remove the changed node from outstanding work. A visible numeric
            // clock never restarts other paragraphs, while a formerly decisive
            // language block disappearing can unblock a previously skipped page.
            if (hadLanguage && assessed !== true) changed = true;
            continue;
          }
          if (record && record.groupKind === "navigation" && policy.alreadyTargetText(nextSource, config.targetLanguage)
              && !record.pieces.some(piece => piece.id)) { refreshUntranslated(record, nextSource); continue; }
          if (record) invalidateGroup(record.groupElement);
          changed = true;
        } else if (mutation.type === "childList") {
          if (!permitted({ assignedSlot: mutation.target }, environment.getStyle, environment.fragmentGroups)) continue;
          const replaced = rebindReplacedParagraphs(mutation.addedNodes, mutation.removedNodes);
          const retiredElements = new Set();
          for (const removed of mutation.removedNodes || []) for (const text of walkAccessibleNodes(removed))
            if (records.has(text) && !replaced.has(text) && records.get(text).pieces.some(piece => piece.id)
                && (text.isConnected === false || groupElement(text) !== records.get(text).groupElement)) retiredElements.add(records.get(text).groupElement);
          const affected = [...(mutation.addedNodes || []), ...(mutation.removedNodes || [])];
          const element = groupElement({ assignedSlot: mutation.target });
          const changedParagraphFact = [...records.values()].some(record => record.groupElement === element
            && record.pieces.some(piece => piece.id) && !hasUnchangedGroupSource(element, record.groupText));
          if (changedParagraphFact || affected.some(node => !ownedNodes.has(node) && readableChange(node))) {
            if (rebindUnchangedGroup(element)) continue;
            if (!changedParagraphFact && replaced.size && [...(mutation.addedNodes || [])].every(node => {
              for (const text of walkAccessibleNodes(node)) if (text.nodeType === 3 && policy.hasLanguageText(text.nodeValue)
                  && permitted(text, environment.getStyle, environment.fragmentGroups) && !records.has(text)) return false;
              return true;
            }) && [...(mutation.removedNodes || [])].every(node => {
              for (const text of walkAccessibleNodes(node)) if (text.nodeType === 3 && policy.hasLanguageText(text.nodeValue)
                  && !replaced.has(text) && !ownedNodes.has(text)) return false;
              return true;
            })) continue;
            for (const retired of retiredElements) invalidateGroup(retired);
            replayAddedParagraphs(mutation.addedNodes);
            invalidateGroup(element); changed = true;
          } else if (retiredElements.size) { for (const retired of retiredElements) invalidateGroup(retired); changed = true; }
        } else if (mutation.type === "attributes" && !mutation.target.hasAttribute?.("data-screen-translator")) {
          resetScanLayout?.();
          if (eligibilityChanged(mutation.target)) { changed = true; if (scanning) invalidated = true; }
        }
        else if (mutation.type === "slotchange" && permitted({ assignedSlot: mutation.target }, environment.getStyle, environment.fragmentGroups)
            && (readableChange(mutation.target) || [...records.values()].some(record => record.renderSlots?.has(mutation.target) && policy.hasLanguageText(record.source)))) {
          for (const record of records.values()) if (record.renderSlots?.has(mutation.target)) invalidateGroup(record.groupElement);
          changed = true;
        }
      }
      if (changed) {
        const selectionChanged = manualRunning && manualMode === "selection";
        if (!manualMode && assessed === false && localRejection && !jobs.size) {
          // No native work needs cancellation. Keep the last valid local verdict
          // during collection, then reevaluate all current body evidence. Even
          // invalidated carousel groups must not publish an unknown decision on
          // every frame or restart an otherwise continuing extraction walk.
          if (scanning) scanAgain = true; else scheduleScan();
        } else if (!selectionChanged && jobs.size && [...jobs.values()].some(job => job.ids.size)
            || !invalidated && !selectionChanged && (scanning || jobs.size)) {
          // Lazy additions and visibility changes must not repeatedly abort a
          // preparation walk or unrelated streams on a loading/animated page.
          // A ticker/carousel retires only its own paragraph IDs. Keep other
          // paragraphs in the admitted batch flowing, then scan the new source.
          scanAgain = true;
        } else {
          cancel(selectionChanged ? "selection_changed" : "page_changed", false, true);
          if (selectionChanged) {
            manualRunning = false; selectedRanges = null; selectionSources = null; selectionPending = true; notify("selection_changed");
          } else scheduleScan();
        }
      }
      renderBilingual();
    }
    function visibilityChanged() {
      if (document.visibilityState === "hidden") { pickingParagraph = false; knownOpenRoots.clear(); environment.onOpenRoots?.(knownOpenRoots); decision(); notify("paused"); }
      else if (canRender()) {
        for (const record of records.values()) write(record); renderBilingual();
        const remembered = decisionMemo.get(snapshotKey); decision(assessed !== null, remembered?.allowImages === true);
        if (active()) scheduleScan(); else notify(manualMode && assessed ? "completed" : config.manualAvailable ? "manual_ready" : "disabled");
      }
    }
    function layoutChanged() {
      if (!active() || manualMode === "selection") return;
      resetScanLayout?.();
      // Scroll, responsive layouts and content-visibility can reveal source
      // without mutating the DOM. Never replace IDs of a live stream to rescan.
      if (jobs.size || scanning) { scanAgain = true; return; }
      scheduleScan(120);
    }
    async function discoverOpenRoots() {
      if (!active() || !document.body || scanning) return;
      const found = new Set(); let count = 0; const atGeneration = config.generation;
      for (const _node of walkAccessibleNodes(document.body, found, environment.onRootFound)) {
        if (++count % 200 === 0) await (environment.yieldWork?.() || Promise.resolve());
        if (!active() || config.generation !== atGeneration) return;
      }
      if (found.size !== knownOpenRoots.size || [...found].some(tree => !knownOpenRoots.has(tree))) {
        knownOpenRoots = found; environment.onOpenRoots?.(found);
        cancel("shadow_roots_changed", false, true); scheduleScan();
      }
    }
    function navigationChanged() {
      if (disposed) return false;
      const next = pageRouteKey(environment.getLocation?.() || document.location?.href);
      if (!next || next === routeKey) return false;
      routeKey = next;
      cacheCompletedGroups();
      cancel("page_navigated", false, true);
      // Manual page/selection controls apply to their current route. A SPA
      // keeps its document token, so end that scope before scanning a new list.
      manualMode = null; manualRunning = false; selectedRanges = null; selectionSources = null; selectionPending = false; pickingParagraph = false; manualImageRequest = null;
      preparationRetries = 0;
      for (const record of records.values()) {
        for (const piece of record.pieces) if (!piece.complete || piece.failed) {
          piece.value = piece.source; piece.complete = false; piece.failed = false;
        }
        write(record);
      }
      renderBilingual(); decision();
      if (scanTimer !== null) { clearSchedule(scanTimer); scanTimer = null; }
      if (active()) { notify("assessing"); scheduleScan(80); }
      else notify(config.manualAvailable && !paused ? "manual_ready" : paused ? "paused" : "disabled");
      return true;
    }
    function suspend() { pickingParagraph = false; suspended = true; cancel("document_hidden", false, true); knownOpenRoots.clear(); environment.onOpenRoots?.(knownOpenRoots); notify("paused"); }
    function resumeDocument() { if (disposed) return; suspended = false; decision(); if (active()) scheduleScan(); }
    function dispose() { disposed = true; manualImageRequest = null; cancel("document_unloaded"); knownOpenRoots.clear(); environment.onOpenRoots?.(knownOpenRoots); if (scanTimer !== null) clearSchedule(scanTimer); }
    function selectionChanged() {
      if (!selectionPending || !config.manualAvailable || !readable()) return;
      const selection = environment.getSelection?.() || document.getSelection?.() || root.getSelection?.();
      notify(selection && !selection.isCollapsed && selection.rangeCount ? "selection_ready" : "selection_pending");
    }
    return { configure, receive, control, pickParagraph, pickContent, mutated, visibilityChanged, layoutChanged, navigationChanged, discoverOpenRoots, suspend, resumeDocument, dispose, selectionChanged,
      inspect: () => ({ revision, config, assessed, jobs: jobs.size, pending: queue.length, records, blocks, paused,
        manualMode, manualRunning, selectionPending, pickingParagraph, observing: canRender() && (config.enabled || records.size > 0),
        originalView: viewMode === "original", viewMode, annotations, active: active(),
        cachedGroups: completedGroups.size, cachedCharacters: completedCharacters, rememberedDecisions: decisionMemo.size, scanning }) };
  }
  function createOpenRootObserver({ document, createObserver, onMutations }) {
    const observer = createObserver(onMutations), roots = new Set(); let enabled = false;
    const slotChanged = event => onMutations([{ type: "slotchange", target: event.target }]);
    const seenEvents = new WeakSet();
    const disclosureChanged = event => {
      if (seenEvents.has(event)) return;
      if (event.type === "toggle" && event.target?.tagName !== "DETAILS") return;
      if (event.type === "transitionend" && !["height", "max-height", "width", "max-width", "grid-template-rows", "opacity", "visibility"].includes(event.propertyName)) return;
      seenEvents.add(event);
      onMutations([{ type: "attributes", target: event.target, attributeName: "style" }]);
    };
    const options = { subtree: true, childList: true, characterData: true, attributes: true,
      attributeFilter: ["hidden", "aria-hidden", "open", "aria-expanded", "contenteditable", "style", "class", "slot", "name"] };
    function attachRoot(tree) {
      observer.observe(tree, options); tree.addEventListener?.("slotchange", slotChanged, true);
      for (const type of ["toggle", "transitionend", "animationend"]) tree.addEventListener?.(type, disclosureChanged, true);
    }
    function detachRoots() {
      for (const tree of [document, ...roots]) {
        tree.removeEventListener?.("slotchange", slotChanged, true);
        for (const type of ["toggle", "transitionend", "animationend"]) tree.removeEventListener?.(type, disclosureChanged, true);
      }
    }
    function attach() { attachRoot(document); for (const tree of roots) attachRoot(tree); }
    function configure(value) {
      if (value === enabled) return;
      enabled = value; observer.disconnect(); detachRoots();
      if (enabled) attach(); else roots.clear();
    }
    function found(tree) { if (!enabled || !tree || tree.mode === "closed" || roots.has(tree)) return; roots.add(tree); attachRoot(tree); }
    function replace(next) {
      if (!enabled) { roots.clear(); return; }
      if ([...roots].some(tree => !next.has(tree))) { observer.disconnect(); detachRoots(); roots.clear(); for (const tree of next) roots.add(tree); attach(); }
      else for (const tree of next) found(tree);
    }
    return { configure, found, replace, dispose: () => configure(false), inspect: () => ({ enabled, rootCount: roots.size }) };
  }
  function statusIndicator(status, images = {}) {
    const state = status?.state;
    if (["translating", "image_translating"].includes(state) || images.translating > 0) return { phase: "translating", color: "#2585db", busy: true };
    if (["assessing", "retrying"].includes(state) || images.preparing > 0) return { phase: "preparing", color: "#8b68ce", busy: true };
    if (["failed", "image_failed"].includes(state)) return { phase: "failed", color: "#cf6070", busy: false };
    if (["partial", "waiting_for_page", "selection_changed"].includes(state)) return { phase: "attention", color: "#c89131", busy: false };
    if (status?.pickingParagraph || ["paragraph_pending", "paragraph_not_found", "selection_pending", "selection_ready"].includes(state))
      return { phase: "picking", color: "#8b68ce", busy: false };
    if (["completed", "image_completed"].includes(state) || images.completed > 0) return { phase: "complete", color: "#329a76", busy: false };
    return { phase: "idle", color: "#8494ab", busy: false };
  }
  function createFloatingStatusController({ host, panel, button, view, state = { collapsed: false, x: null, y: null }, onToggle = () => {} }) {
    const margin = 12, size = 30;
    let drag = null, ignoreClick = false, frame = 0, disposed = false;
    const requestFrame = callback => view.requestAnimationFrame(callback);
    function layout() {
      frame = 0; if (disposed || !host.isConnected) return;
      const width = view.innerWidth, height = view.innerHeight;
      const clamp = (value, extent) => Math.max(Math.min(margin, extent / 2), Math.min(value, Math.max(margin, extent - size - margin)));
      const x = clamp(state.x ?? width - size - 16, width), y = clamp(state.y ?? height - size - 16, height);
      if (state.x !== null) { state.x = x; state.y = y; }
      host.style.left = `${x}px`; host.style.top = `${y}px`;
      if (!state.collapsed) {
        panel.style.maxHeight = "";
        let bounds = panel.getBoundingClientRect(), px, py;
        const leftFits = x - bounds.width - 8 >= margin, rightFits = x + size + 8 + bounds.width <= width - margin;
        if (!leftFits && !rightFits) {
          // Narrow windows open above/below the dot; never cover the only collapse handle.
          const below = height - y - size - 8 - margin, above = y - 8 - margin;
          const useBelow = below >= above;
          panel.style.maxHeight = `${Math.max(1, useBelow ? below : above)}px`;
          bounds = panel.getBoundingClientRect();
          px = Math.max(margin, Math.min(x, width - bounds.width - margin));
          py = useBelow ? y + size + 8 : y - bounds.height - 8;
        } else {
          px = leftFits ? x - bounds.width - 8 : x + size + 8;
          const preferredY = y + size - bounds.height >= margin ? y + size - bounds.height : y;
          py = Math.max(margin, Math.min(preferredY, height - bounds.height - margin));
        }
        panel.style.left = `${px - x}px`; panel.style.top = `${py - y}px`;
      }
    }
    function schedule() { if (!frame && !disposed) frame = requestFrame(layout); }
    function render() {
      panel.hidden = state.collapsed; button.setAttribute("aria-expanded", String(!state.collapsed));
      host.setAttribute("data-collapsed", String(state.collapsed)); onToggle(state.collapsed); schedule();
    }
    function down(event) {
      if (event.button !== 0 || event.isPrimary === false || drag) return;
      const bounds = button.getBoundingClientRect();
      drag = { id: event.pointerId, x: event.clientX, y: event.clientY, left: bounds.left, top: bounds.top, moved: false };
      ignoreClick = false;
      try { button.setPointerCapture(event.pointerId); } catch { drag = null; return; }
      event.preventDefault(); event.stopPropagation();
    }
    function move(event) {
      if (!drag || drag.id !== event.pointerId) return;
      const dx = event.clientX - drag.x, dy = event.clientY - drag.y;
      if (!drag.moved && dx * dx + dy * dy < 25) return;
      drag.moved = true; state.x = drag.left + dx; state.y = drag.top + dy;
      button.setAttribute("data-dragging", "true"); schedule(); event.preventDefault(); event.stopPropagation();
    }
    function end(event, cancelled = false) {
      if (!drag || drag.id !== event.pointerId) return;
      ignoreClick = drag.moved && !cancelled; const id = drag.id; drag = null;
      button.removeAttribute("data-dragging");
      try { if (button.hasPointerCapture(id)) button.releasePointerCapture(id); } catch { }
      schedule();
    }
    function click(event) {
      event.stopPropagation();
      if (ignoreClick && event.detail !== 0) { ignoreClick = false; event.preventDefault(); return; }
      ignoreClick = false; state.collapsed = !state.collapsed; render();
    }
    const cancel = event => end(event, true);
    const handlers = [["pointerdown", down], ["pointermove", move], ["pointerup", end], ["pointercancel", cancel],
      ["lostpointercapture", cancel], ["click", click]];
    for (const [type, handler] of handlers) button.addEventListener(type, handler);
    view.addEventListener("resize", schedule);
    render();
    return { layout: schedule, inspect: () => ({ ...state, dragging: !!drag }), dispose() {
      if (drag) end({ pointerId: drag.id }, true);
      disposed = true; if (frame) view.cancelAnimationFrame(frame); frame = 0;
      for (const [type, handler] of handlers) button.removeEventListener(type, handler);
      view.removeEventListener("resize", schedule);
    } };
  }
  function createFullscreenStatusVisibility({ document, getHost }) {
    const savedDisplays = new WeakMap();
    let nativeVideoFullscreen = false;
    function isFullscreen() {
      // The fullscreen element can be an iframe/player wrapper, not the video.
      return Boolean(document.fullscreenElement || document.webkitFullscreenElement || document.webkitIsFullScreen || nativeVideoFullscreen);
    }
    function sync() {
      const host = getHost();
      if (!host) return;
      if (isFullscreen()) {
        if (!savedDisplays.has(host)) savedDisplays.set(host, {
          value: host.style.getPropertyValue("display"), priority: host.style.getPropertyPriority("display")
        });
        host.style.setProperty("display", "none", "important");
      } else if (savedDisplays.has(host)) {
        const saved = savedDisplays.get(host); savedDisplays.delete(host);
        if (saved.value) host.style.setProperty("display", saved.value, saved.priority);
        else host.style.removeProperty("display");
      }
    }
    const enterNative = event => { if (event.target?.tagName === "VIDEO") { nativeVideoFullscreen = true; sync(); } };
    const exitNative = event => { if (event.target?.tagName === "VIDEO") { nativeVideoFullscreen = false; sync(); } };
    const listeners = [["fullscreenchange", sync], ["webkitfullscreenchange", sync],
      ["webkitbeginfullscreen", enterNative], ["webkitendfullscreen", exitNative]];
    for (const [type, listener] of listeners) document.addEventListener(type, listener, true);
    sync();
    return { sync, isFullscreen, dispose() {
      for (const [type, listener] of listeners) document.removeEventListener(type, listener, true);
    } };
  }
  const api = { permitted, composedParent, walkAccessibleNodes, paragraphAtPoint, createRenderedTextTester, createScanLayout, yieldToBrowser, selectedTextSlices, canonicalSource, randomIdentifier, pageRouteKey, extractPageSnapshot, createPageAutoSession, createOpenRootObserver, createFullscreenStatusVisibility, statusIndicator, createFloatingStatusController };
  root.ScreenTranslatorPageAutoContent = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (!policy || !root.document || !root.chrome?.runtime || root.window !== root.top) return;
  const statusLabels = { assessing: "正在识别网页…", retrying: "正在等待页面就绪…", waiting_for_page: "等待网页加载或完成站点验证", translating: "正在翻译网页文字", completed: "网页文字翻译完成", partial: "部分段落保留原文", skipped: "网页文字无需翻译", paused: "页面翻译已暂停", failed: "页面翻译暂不可用",
    manual_ready: "点击选段或翻译整页", paragraph_pending: "点击图片或段落 · Esc 退出", paragraph_not_found: "请点击图片或段落文字附近 · Esc 退出",
    image_translating: "正在读取并翻译所选图片…", image_completed: "所选图片翻译完成", image_failed: "图片翻译未完成，可重新选择或检查桌面连接",
    selection_pending: "点击“翻译选中”后，再点击段落", selection_ready: "点击“翻译选中”后，再点击段落",
    selection_not_needed: "所选文字已翻译或无需翻译", selection_changed: "所选内容已变化，请重新选择后翻译" };
  let statusHost = null, statusText, modeButtons, modeGroup, manualActions, activityDot, lastStatus, floatingController;
  let quickPanel, quickArrow, quickMessage, quickSiteLabel, quickRuleLabel, quickInputs, quickOpen = false, quickBusy = false;
  let quickValues = { connected: false, host: "", siteMode: "default", effectiveMode: "default", matchedDomain: "", autoPage: false, autoImages: false, targetLanguage: "zh-CN", concurrency: 10 };
  const floatingState = { collapsed: false, x: null, y: null };
  const motion = root.ScreenTranslatorMotion;
  const fullscreenVisibility = createFullscreenStatusVisibility({ document, getHost: () => statusHost });
  function showStatus(status) {
    lastStatus = status;
    if (status.state === "disabled") { floatingController?.dispose(); floatingController = null; statusHost?.remove(); statusHost = null; return; }
    if (!document.body) return;
    if (!statusHost) {
      statusHost = document.createElement("div"); statusHost.setAttribute("data-screen-translator", "page-auto-status");
      statusHost.style.left = `${Math.max(12, root.innerWidth - 46)}px`; statusHost.style.top = `${Math.max(12, root.innerHeight - 46)}px`;
      const shadow = statusHost.attachShadow({ mode: "closed" });
      const style = document.createElement("style");
      style.textContent = ":host{all:initial;position:fixed;width:30px;height:30px;z-index:2147483647;font:13px/1.4 system-ui,sans-serif;color:#17233b;pointer-events:none}.panel{position:absolute;display:flex;gap:9px;align-items:center;flex-wrap:wrap;width:max-content;max-width:calc(100vw - 24px);max-height:calc(100vh - 24px);overflow:auto;box-sizing:border-box;padding:9px 12px;border:1px solid #dce3ee;border-radius:12px;background:#fffffff0;box-shadow:0 3px 18px #20304a24;pointer-events:auto}.panel[hidden]{display:none}.modes{display:flex;gap:2px;padding:2px;background:#edf2f7;border-radius:8px}button{font:inherit;border:1px solid #dce3ee;background:white;color:#245a91;border-radius:6px;padding:4px 8px;cursor:pointer}button[aria-pressed=true]{color:#fff;background:#176ba8;border-color:#176ba8}button:focus-visible{outline:2px solid #176ba8;outline-offset:2px}.modes button{border-color:transparent}.activity-dot{--state-color:#8494ab;position:relative;width:30px;height:30px;box-sizing:border-box;padding:0;border:1px solid #dce3ee;border-radius:50%;background:#fffffff5;box-shadow:0 2px 10px #20304a22;touch-action:none;user-select:none;pointer-events:auto;cursor:grab;color:var(--state-color);transition:box-shadow .18s ease}.activity-dot:hover{box-shadow:0 2px 14px #20304a36}.activity-dot[data-dragging=true]{cursor:grabbing}.activity-dot .core{position:absolute;left:9px;top:9px;width:10px;height:10px;background:currentColor;border-radius:50%;transition:background-color .18s ease}.activity-dot::before{content:'';position:absolute;inset:4px;border:1.5px solid currentColor;border-top-color:transparent;border-radius:50%;opacity:0}.activity-dot[data-phase=translating]::before{opacity:.8}.activity-dot[data-motion=true][data-phase=translating]::before{animation:st-dot-spin 1s linear infinite}.activity-dot[data-motion=true][data-phase=preparing] .core,.activity-dot[data-motion=true][data-phase=picking] .core{animation:st-dot-breathe 1.8s ease-in-out infinite}.activity-dot[data-motion=true][data-phase=complete] .core{animation:st-dot-settle .3s ease-out}@keyframes st-dot-spin{to{transform:rotate(360deg)}}@keyframes st-dot-breathe{50%{opacity:.5;transform:scale(.82)}}@keyframes st-dot-settle{50%{transform:scale(1.18)}}@media(prefers-reduced-motion:reduce){.activity-dot,.activity-dot .core{transition:none}.activity-dot::before,.activity-dot .core{animation:none!important}}";
      const row = document.createElement("section"); row.className = "panel"; row.id = "translation-panel";
      style.textContent += ".activity-dot[data-motion=false],.activity-dot[data-motion=false] .core{transition:none}";
      style.textContent += ".panel{display:block}.toolbar{display:flex;gap:9px;align-items:center;flex-wrap:wrap}.more-toggle{width:27px;padding:4px 0;color:#718099;border-color:transparent}.quick-settings{margin-top:11px;padding-top:11px;border-top:1px solid #e6ebf1;min-width:min(280px,calc(100vw - 50px))}.quick-settings[hidden]{display:none}.quick-settings label{display:grid;grid-template-columns:minmax(85px,1fr) minmax(95px,150px);gap:16px;align-items:center;margin:9px 0}.quick-settings input{justify-self:end;accent-color:#176ba8;width:16px;height:16px}.quick-settings select{font:inherit;padding:5px 7px;border:1px solid #dce3ee;border-radius:7px;min-width:0;max-width:100%;background:#fff;color:#273a52}.quick-settings :disabled{opacity:.55;cursor:default}.site-label{font-size:12px;color:#586b86;overflow-wrap:anywhere;margin:13px 0 5px}.quick-message,.site-rule{font-size:12px;color:#6c7e95;line-height:1.55}.quick-message{margin-top:9px}.quick-message[data-error=true]{color:#b45762}";
      style.textContent += ".panel[data-settings-open=true]{background:#fff}";
      const toolbar = document.createElement("div"); toolbar.className = "toolbar";
      statusText = document.createElement("span");
      activityDot = document.createElement("button"); activityDot.type = "button"; activityDot.className = "activity-dot";
      activityDot.setAttribute("data-action", "toggle-collapse"); activityDot.setAttribute("aria-controls", row.id);
      const dotCore = document.createElement("span"); dotCore.className = "core"; dotCore.setAttribute("aria-hidden", "true"); activityDot.append(dotCore);
      toolbar.addEventListener("pointerdown", event => { if (event.button === 0) event.preventDefault(); });
      const modes = document.createElement("span"); modes.className = "modes"; modes.setAttribute("role", "group"); modes.setAttribute("aria-label", "页面显示"); modeButtons = new Map();
      modeGroup = modes;
      for (const [mode, label] of [["original", "原文"], ["translation", "译文"], ["bilingual", "双语"]]) {
        const button = document.createElement("button"); button.type = "button"; button.textContent = label;
        button.setAttribute("aria-label", `显示${label}`); button.addEventListener("click", () => session.control(`view-${mode}`)); modes.append(button); modeButtons.set(mode, button);
      }
      manualActions = document.createElement("span"); manualActions.setAttribute("data-manual-actions", "true");
      for (const [action, label, accessible] of [["manual-pick", "翻译选中", "点击选择图片或附近段落翻译"], ["manual-page", "整页翻译", "翻译整页文字"]]) {
        const button = document.createElement("button"); button.type = "button"; button.textContent = label;
        button.setAttribute("aria-label", accessible); button.setAttribute("data-action", action);
        button.addEventListener("click", () => session.control(action)); manualActions.append(button);
      }
      quickArrow = document.createElement("button"); quickArrow.type = "button"; quickArrow.className = "more-toggle";
      quickArrow.textContent = "▾"; quickArrow.setAttribute("data-action", "toggle-settings");
      quickArrow.setAttribute("aria-label", "展开常用设置"); quickArrow.setAttribute("aria-expanded", "false"); quickArrow.setAttribute("aria-controls", "translation-quick-settings");
      quickArrow.addEventListener("click", () => setQuickOpen(!quickOpen));
      toolbar.append(statusText, modes, manualActions, quickArrow);
      quickPanel = document.createElement("div"); quickPanel.className = "quick-settings"; quickPanel.id = "translation-quick-settings";
      quickPanel.hidden = !quickOpen; quickInputs = new Map();
      function setting(key, label, options) {
        const row = document.createElement("label"), text = document.createElement("span"); text.textContent = label;
        const input = document.createElement(options ? "select" : "input");
        if (!options) input.type = "checkbox";
        else for (const [value, caption] of options) { const option = document.createElement("option"); option.value = value; option.textContent = caption; input.append(option); }
        input.setAttribute("data-setting", key); input.setAttribute("aria-label", label);
        input.addEventListener("change", () => saveQuickSetting(key, options ? key === "concurrency" ? Number(input.value) : input.value : input.checked));
        row.append(text, input); quickPanel.append(row); quickInputs.set(key, input);
      }
      setting("autoPage", "自动网页翻译");
      setting("autoImages", "自动图片", [["false", "不自动翻译"], ["true", "仅大图 / 放大后"]]);
      setting("targetLanguage", "目标语言", [["zh-CN", "简体中文"], ["zh-TW", "繁体中文"], ["en", "英语"], ["ja", "日语"], ["ko", "韩语"]]);
      setting("concurrency", "翻译并发", Array.from({ length: 10 }, (_, index) => [String(index + 1), String(index + 1)]));
      quickSiteLabel = document.createElement("div"); quickSiteLabel.className = "site-label"; quickPanel.append(quickSiteLabel);
      setting("siteMode", "当前网站规则", [["default", "遵循默认"], ["allow", "白名单 · 自动翻译"], ["block", "黑名单 · 禁止自动"]]);
      quickRuleLabel = document.createElement("div"); quickRuleLabel.className = "site-rule"; quickPanel.append(quickRuleLabel);
      quickMessage = document.createElement("div"); quickMessage.className = "quick-message"; quickMessage.setAttribute("role", "status"); quickPanel.append(quickMessage);
      row.append(toolbar, quickPanel);
      shadow.append(style, row, activityDot); document.body.append(statusHost);
      floatingController = createFloatingStatusController({ host: statusHost, panel: row, button: activityDot, view: root,
        state: floatingState, onToggle: syncDotHint });
    }
    statusHost.setAttribute("data-view-mode", status.viewMode);
    const failureLabels = { api_key_error: "请检查当前服务商的 API Key", api_configuration: "请检查模型和接口协议", desktop_unavailable: "桌面程序未连接",
      desktop_busy: "翻译任务繁忙，可稍后重试", page_busy: "当前页面仍有翻译任务", document_changed: "页面已更新，可重新翻译", tab_inactive: "切回当前标签后继续",
      rate_limit: "服务商请求受限，请稍后重试", translation_unavailable: "翻译服务连接失败，可重试", translation_format: "翻译响应格式异常，可重试",
      invalid_request: "页面请求未通过检查，可手动翻译", automatic_disabled: "自动翻译已关闭", page_paused: "页面翻译已暂停", site_blocked: "当前网站已禁止自动翻译" };
    statusText.textContent = ((status.state === "failed" ? failureLabels[status.detail] : null) || statusLabels[status.state] || "页面翻译")
      + (status.total && ["translating", "partial"].includes(status.state) ? ` ${status.completed}/${status.total}` : "");
    for (const [mode, button] of modeButtons) button.setAttribute("aria-pressed", String(status.viewMode === mode));
    const hasText = status.hasTranslation === true, hasImage = root.ScreenTranslatorHasTranslatedImages === true;
    const indicator = statusIndicator(status, root.ScreenTranslatorImageTranslationActivity);
    const working = indicator.busy;
    if (!["assessing", "retrying", "translating"].includes(status.state) && indicator.busy && !status.pickingParagraph)
      statusText.textContent = indicator.phase === "translating" ? "正在翻译图片" : "正在准备图片翻译";
    modeGroup.style.display = hasText || hasImage ? "flex" : "none";
    modeButtons.get("bilingual").style.display = hasText ? "" : "none";
    statusText.style.display = working || hasText || ["image_failed", "image_completed", "paragraph_pending", "paragraph_not_found", "selection_pending", "selection_ready", "selection_changed", "waiting_for_page", "failed"].includes(status.state) ? "" : "none";
    manualActions.querySelector('[data-action="manual-pick"]').setAttribute("aria-pressed", String(status.pickingParagraph === true));
    activityDot.setAttribute("data-phase", indicator.phase);
    activityDot.style.setProperty("--state-color", indicator.color);
    activityDot.setAttribute("data-motion", String(!motion || motion.getEnabled()));
    syncDotHint();
    manualActions.style.display = status.manualAvailable ? "inline-flex" : "none";
    manualActions.style.gap = "4px";
    renderQuickSettings();
    floatingController.layout();
    fullscreenVisibility.sync();
  }
  function syncDotHint() {
    if (!activityDot || !statusText) return;
    const hint = `${statusText.textContent || "网页翻译"} · 点击${floatingState.collapsed ? "展开" : "折叠"} · 按住拖动`;
    activityDot.title = hint; activityDot.setAttribute("aria-label", hint);
    if (floatingState.collapsed && quickOpen) setQuickOpen(false);
  }
  function setQuickOpen(value) {
    quickOpen = value; if (!quickPanel) return;
    quickPanel.parentElement?.setAttribute("data-settings-open", String(value));
    quickPanel.hidden = !value; quickArrow.textContent = value ? "▴" : "▾";
    quickArrow.setAttribute("aria-expanded", String(value)); quickArrow.setAttribute("aria-label", value ? "收起常用设置" : "展开常用设置");
    renderQuickSettings(); floatingController?.layout();
  }
  function updateQuickSettings(value) {
    if (!value || typeof value !== "object") return;
    quickValues = { connected: value.connected === true, host: typeof value.host === "string" ? value.host.slice(0, 253) : "",
      siteMode: ["allow", "block"].includes(value.siteMode) ? value.siteMode : "default",
      effectiveMode: ["allow", "block"].includes(value.effectiveMode) ? value.effectiveMode : "default",
      matchedDomain: typeof value.matchedDomain === "string" ? value.matchedDomain.slice(0, 253) : "",
      autoPage: value.autoPage === true, autoImages: value.autoImages === true,
      targetLanguage: ["zh-CN", "zh-TW", "en", "ja", "ko"].includes(value.targetLanguage) ? value.targetLanguage : "zh-CN",
      concurrency: Number.isInteger(value.concurrency) ? Math.max(1, Math.min(10, value.concurrency)) : 10 };
    renderQuickSettings();
  }
  function renderQuickSettings() {
    if (!quickPanel) return;
    for (const [key, input] of quickInputs) {
      input.disabled = quickBusy || !quickValues.connected || key === "siteMode" && !quickValues.host;
      if (!quickBusy) {
        if (input.type === "checkbox") input.checked = quickValues[key]; else input.value = String(quickValues[key]);
      }
    }
    quickSiteLabel.textContent = quickValues.host ? `当前网站 · ${quickValues.host}` : "当前页面没有可设置的网站域名";
    const caption = { default: "遵循自动语言判断", allow: "白名单：自动模式下翻译外语内容", block: "黑名单：停止文字和图片自动翻译" }[quickValues.effectiveMode];
    quickRuleLabel.textContent = caption + (quickValues.matchedDomain && quickValues.matchedDomain !== quickValues.host ? `（继承 ${quickValues.matchedDomain}）` : "");
    if (!quickValues.connected && !quickBusy) quickMessage.textContent = "请连接支持快捷设置的桌面程序";
  }
  async function saveQuickSetting(key, rawValue) {
    if (quickBusy || !quickValues.connected) { renderQuickSettings(); return; }
    const value = key === "autoImages" ? rawValue === "true" : rawValue;
    quickBusy = true; quickMessage.textContent = "正在保存…"; quickMessage.setAttribute("data-error", "false"); renderQuickSettings();
    const result = await new Promise(resolve => {
      let settled = false; const finish = value => { if (settled) return; settled = true; clearTimeout(timer); resolve(value); };
      const timer = setTimeout(() => finish({ ok: false, reason: "settings_timeout" }), 12000);
      try { chrome.runtime.sendMessage({ type: "browserQuickSettings", requestId: randomIdentifier(), documentToken,
        generation: session.inspect().config.generation, changes: { [key]: value } }, reply => {
        void chrome.runtime.lastError; finish(reply || { ok: false, reason: "desktop_unavailable" });
      }); } catch { finish({ ok: false, reason: "desktop_unavailable" }); }
    });
    quickBusy = false; if (result.settings) updateQuickSettings(result.settings);
    renderQuickSettings();
    const errors = { desktop_unavailable: "桌面程序未连接", document_changed: "页面已变化，请重试", settings_timeout: "保存超时，请检查桌面连接",
      site_unavailable: "此页面没有可设置的域名", invalid_request: "设置未通过检查，请重试", settings_unavailable: "设置保存失败，请重试" };
    quickMessage.textContent = result.ok ? key === "siteMode" ? "当前网站规则已保存" : "已保存，应用于所有网页" : errors[result.reason] || "设置保存失败，请重试";
    quickMessage.setAttribute("data-error", String(!result.ok)); floatingController?.layout();
  }
  function syncMotionStatus() {
    if (activityDot) activityDot.setAttribute("data-motion", String(!motion || motion.getEnabled()));
  }
  root.addEventListener("ScreenTranslatorMotionChanged", syncMotionStatus);
  root.addEventListener("ScreenTranslatorImageTranslationChanged", () => { if (lastStatus) showStatus(lastStatus); });
  const documentToken = root.ScreenTranslatorDocumentToken || randomIdentifier();
  const session = createPageAutoSession({ document, token: documentToken,
    getLocation: () => root.location.href,
    motion,
    getStyle: element => getComputedStyle(element), makeId: () => randomIdentifier(), viewportHeight: innerHeight,
    getSelection: () => root.getSelection(),
    pickImageAtPoint: (x, y) => root.ScreenTranslatorManualImages?.pick(x, y),
    hashSourceGroup: text => new Promise(resolve => {
      try { chrome.runtime.sendMessage({ type: "hashPageSource", documentToken, text, generation: session.inspect().config.generation }, reply => {
        void chrome.runtime.lastError; resolve(reply?.hash || null);
      }); } catch { resolve(null); }
    }),
    isRenderedText: createRenderedTextTester(document),
    yieldWork: yieldToBrowser,
    send: packet => new Promise(resolve => {
      try { chrome.runtime.sendMessage(packet, response => { void chrome.runtime.lastError; resolve(response || { accepted: false, reason: "desktop_unavailable" }); }); }
      catch { resolve({ accepted: false, reason: "desktop_unavailable" }); }
    }), onStatus: status => { showStatus(status); updateObservation(); }, onView: state => {
      root.ScreenTranslatorPageViewState = Object.freeze(state);
      document.dispatchEvent(new Event("ScreenTranslatorPageViewChanged"));
    },
    onRootFound: tree => openRootObserver.found(tree), onOpenRoots: trees => openRootObserver.replace(trees), onDecision: decision => {
      root.ScreenTranslatorPageAutoDecision = Object.freeze(decision);
      document.dispatchEvent(new Event("ScreenTranslatorPageAssessmentChanged"));
    } });
  const openRootObserver = createOpenRootObserver({ document, createObserver: callback => new MutationObserver(callback),
    onMutations: records => session.mutated(records) });
  let rootProbeTimer = 0, probingRoots = false;
  function updateObservation() {
    const enabled = session.inspect().observing; openRootObserver.configure(enabled);
    if (!enabled) { if (rootProbeTimer) clearTimeout(rootProbeTimer); rootProbeTimer = 0; return; }
    if (!rootProbeTimer) rootProbeTimer = setTimeout(async () => {
      rootProbeTimer = 0; if (probingRoots || !session.inspect().active) return;
      probingRoots = true;
      try { await session.discoverOpenRoots(); } catch { /* A detached component can disappear during the idle probe. */ }
      finally { probingRoots = false; updateObservation(); }
    }, 1500);
  }
  chrome.runtime.onMessage.addListener((message, _sender, respond) => {
    if (message?.type === "pageAutoNavigation") { respond({ found: session.navigationChanged() }); return false; }
    if (!["manualImageStatus", "pageAutoConfig", "pageAutoAssessment", "pageAutoTranslations"].includes(message?.type)) return false;
    if (message.type === "pageAutoConfig") updateQuickSettings(message.quickSettings);
    const found = session.receive(message); if (message.type === "pageAutoConfig") updateObservation();
    respond({ found }); return false;
  });
  function requestConfig() {
    try { chrome.runtime.sendMessage({ type: "queryPageAutoConfig" }, config => {
      void chrome.runtime.lastError; updateQuickSettings(config?.quickSettings); if (session.configure(config)) updateObservation();
    }); } catch { /* The native app pushes the next opt-in configuration. */ }
  }
  document.addEventListener("DOMContentLoaded", requestConfig, { once: true });
  document.addEventListener("visibilitychange", () => { session.visibilityChanged(); if (document.visibilityState !== "hidden") requestConfig(); });
  document.addEventListener("selectionchange", () => session.selectionChanged());
  document.addEventListener("click", event => {
    if (event.button !== 0 || !session.inspect().pickingParagraph
        || event.composedPath().some(node => node.hasAttribute?.("data-screen-translator"))) return;
    if (session.pickContent(event.clientX, event.clientY)) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && session.inspect().pickingParagraph) {
      session.control("cancel-pick"); event.preventDefault(); event.stopImmediatePropagation();
    }
  }, true);
  document.addEventListener("scroll", () => session.layoutChanged(), { capture: true, passive: true });
  root.addEventListener("resize", () => session.layoutChanged(), { passive: true });
  // Navigation API reaches pushState/replaceState in Chrome's isolated world.
  // tabs.onUpdated supplies a fallback without patching a site's history API.
  root.navigation?.addEventListener("currententrychange", () => session.navigationChanged());
  root.addEventListener("popstate", () => session.navigationChanged());
  root.addEventListener("hashchange", () => session.navigationChanged());
  root.addEventListener("pagehide", () => { session.suspend(); updateObservation(); });
  root.addEventListener("pageshow", event => { if (event.persisted) { session.resumeDocument(); updateObservation(); requestConfig(); } });
  requestConfig();
})(globalThis);
