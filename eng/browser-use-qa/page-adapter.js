"use strict";
// Transport/evidence only. The production policy, extractor and renderer are unmodified.
(function install(root) {
  const api = root.ScreenTranslatorPageAutoContent;
  if (!api || root.__websiteQa) throw new Error("qa_adapter_installation_invalid");
  const outbound = [], pending = new Map(), events = [], sources = new Map();
  const identities = new WeakMap(), locators = new Map(), previous = new Map();
  const sourceGroups = new WeakSet();
  // Pending exact commits from the session's motion writer. The existing
  // evidence bounds prevent an unobserved/offline writer from
  // accumulating unbounded history. Exhausted proof is never guessed.
  const productWrites = new Map();
  const uncertainProductWrites = new WeakSet();
  let productWriteCharacters = 0, productWriteCount = 0;
  function releaseProductWrites(node) {
    const entry = productWrites.get(node);
    if (entry) { productWriteCharacters -= entry.characters; productWriteCount -= entry.steps.length; }
    productWrites.delete(node); return entry;
  }
  // Latest actual producer view per independent source, including views between
  // controller polls. Bounds limit additional evidence, never production work.
  const producerObservations = new Map();
  // Intern immutable work/piece values across atomic members and source rows.
  // Source snapshots already live in the independent version registry; retain
  // references to those exact versions instead of a second copy in every row.
  const observationValues = new Map(), observationValueKeys = new Map();
  let observationValueSerial = 0;
  // Latest full rows already belong to the independent sources registry. Store
  // only their exact observation metadata here, rather than duplicating every
  // source/display/geometry string. Replacing a sources row never mutates it:
  // retained producer versions keep the old immutable row and its actual time.
  // The full needed version union is still charged at export under both bounds.
  const sourceVersions = new Map();
  let sourceVersionCharacters = 0, sourceVersionDropped = 0;
  const scheduledScans = new Set();
  const observationLimit = 2048, observationCharacterLimit = 2000000;
  let observationCharacters = 64, observationDropped = 0, sampleSequence = 0;
  let serial = 0, session, observer, probeTimer, status = null, probeJob = null, disposed = false;
  const document = root.document;
  const timestamp = () => new Date().toISOString();
  function sourceVersion(id) {
    const reference = sourceVersions.get(id), source = sources.get(id);
    return reference && source ? { at: reference.at, sequence: reference.sequence, source } : null;
  }
  function internObservationValue(value) {
    const serialized = JSON.stringify(value);
    let entry = observationValueKeys.get(serialized);
    if (!entry) {
      const id = ++observationValueSerial;
      entry = { id, serialized, size: serialized.length + String(id).length + 4, references: 0 };
      observationValueKeys.set(serialized, entry); observationValues.set(id, entry);
      observationCharacters += entry.size;
    }
    entry.references++;
    return entry.id;
  }
  function releaseObservation(entry) {
    observationCharacters -= entry.size;
    for (const id of entry.references) {
      const value = observationValues.get(id);
      if (--value.references === 0) {
        observationCharacters -= value.size;
        observationValues.delete(id); observationValueKeys.delete(value.serialized);
      }
    }
  }
  function expandedObservation(entry) {
    const row = JSON.parse(entry.serialized);
    const [sourceVersion, ownerVersion] = entry.versions;
    row.source = sourceVersion.source;
    row.sourceText = row.source.sourceText;
    row.representedSource = ownerVersion?.source || null;
    row.work = JSON.parse(observationValues.get(row.work).serialized);
    row.pieces = row.pieces.map(id => JSON.parse(observationValues.get(id).serialized));
    return JSON.parse(JSON.stringify(row));
  }
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
    // Detached records still refer to their original evidence. A connected
    // return after an observed authored change starts a new lifetime.
    if (old && old.source === source && (!old.retired || node.isConnected === false)) return old.id;
    const path = locator(node), key = JSON.stringify([path, source]);
    if (old && old.source !== source && locators.get(old.key) === old.id) locators.delete(old.key);
    const id = old && old.source !== source ? `source-${++serial}` : locators.get(key) || `source-${++serial}`;
    // Identity allocation is independent of evidence. Hidden nodes and production
    // predictions may have identities without ever becoming visible source rows.
    locators.set(key, id); identities.set(node, { id, source, key });
    return id;
  }
  function sourceTransitions(mutations, records) {
    if (!mutations.some(mutation => ["characterData", "childList"].includes(mutation.type))) return;
    const characterValues = new Map(), characterSteps = new Map(), chains = new Map(), displays = new Map();
    const unknownDisplays = new Map();
    for (let index = mutations.length - 1; index >= 0; index--) {
      const mutation = mutations[index];
      if (mutation.type !== "characterData") continue;
      const after = characterValues.has(mutation.target) ? characterValues.get(mutation.target)
        : String(mutation.target.nodeValue || "");
      const step = { before: mutation.oldValue, after };
      characterSteps.set(mutation, step);
      if (!chains.has(mutation.target)) chains.set(mutation.target, []);
      chains.get(mutation.target).push(step);
      if (typeof mutation.oldValue === "string") characterValues.set(mutation.target, mutation.oldValue);
    }
    for (const [node, reverseChain] of chains) {
      const record = records.get(node), old = identities.get(node), prior = old && previous.get(old.id);
      const writes = releaseProductWrites(node);
      const uncertain = uncertainProductWrites.has(node); uncertainProductWrites.delete(node);
      if (!record || !old || old.retired || old.source !== record.source) continue;
      const known = new Set([record.source, record.lastWritten, prior?.value]);
      const chain = reverseChain.reverse();
      // A retained source and terminal lastWritten establish monotonic stream
      // tails even when several animation commits arrived between samples.
      // Arbitrary intermediate corrections need actual writer commit proof.
      const terminal = chain.at(-1).after;
      if (terminal === record.lastWritten && terminal !== record.source && known.has(chain[0].before)
          && chain.every((step, index) => typeof step.before === "string" && step.after.length > 0
            && terminal.startsWith(step.after) && (!index || step.after.startsWith(chain[index - 1].after))))
        for (const step of chain) known.add(step.after);
      if (writes?.source === record.source && writes.sourceId === old.id) {
        let cursor = 0;
        for (const step of chain) {
          const write = writes.steps[cursor];
          if (write && write.before === step.before && write.after === step.after) {
            known.add(write.before); known.add(write.after); cursor++;
          }
        }
      }
      displays.set(node, known);
      // If commit provenance hit a bound, an unproved intermediate may be a
      // product correction. It cannot certify authored text. The existing
      // dropped-evidence gate also keeps this capture from claiming completion.
      if (uncertain) unknownDisplays.set(node, new Set(chain.flatMap(step => [step.before, step.after])
        .filter(value => !known.has(value))));
    }
    // All source/display aliases normalize to the same retained source. Let
    // the usual sample still observe completion and unchanged withdrawals;
    // reconstruction needs no DOM traversal for these own-write-only batches.
    if (mutations.every(mutation => mutation.type !== "childList" && (mutation.type !== "characterData"
        || displays.has(mutation.target) && displays.get(mutation.target).has(characterSteps.get(mutation).before)
        && displays.get(mutation.target).has(characterSteps.get(mutation).after)))) return;
    const children = new Map(), values = new Map(), aliases = new Map(), groups = new Set();
    const visit = node => {
      if (!node || children.has(node)) return;
      if (sourceGroups.has(node)) groups.add(node);
      children.set(node, Array.from(node.childNodes || []));
      if (node.nodeType === 3) values.set(node, String(node.nodeValue || ""));
      for (const child of children.get(node)) visit(child);
    };
    for (const record of records.values()) {
      if (record.groupElement) sourceGroups.add(record.groupElement);
      const old = identities.get(record.node), prior = old && previous.get(old.id);
      aliases.set(record.node, { source: record.source,
        unknown: unknownDisplays.get(record.node),
        displays: displays.get(record.node)
          || new Set([record.source, record.lastWritten, ...(prior?.translated ? [prior.value] : [])]) });
    }
    for (const mutation of mutations) {
      visit(mutation.target);
      for (let ancestor = composedParent(mutation.target); ancestor; ancestor = composedParent(ancestor))
        if (sourceGroups.has(ancestor)) { groups.add(ancestor); visit(ancestor); }
      for (const node of [...mutation.addedNodes || [], ...mutation.removedNodes || []]) visit(node);
    }
    const leafCache = new Map();
    const leaves = node => {
      if (!node || node.nodeType === 1 && (node.hasAttribute?.("data-screen-translator")
          || ["SCRIPT", "STYLE"].includes(node.tagName))) return [];
      if (node.nodeType === 3) return [node];
      if (!leafCache.has(node)) leafCache.set(node, (children.get(node) || []).flatMap(leaves));
      return leafCache.get(node);
    };
    const text = nodes => {
      const parts = [];
      for (const node of nodes) {
        const value = values.get(node), alias = aliases.get(node);
        if (alias?.unknown?.has(value)) return null;
        parts.push(alias?.displays.has(value) ? alias.source : value);
      }
      return parts.join("");
    };
    // Undo the actual MutationRecords, then replay them in order. Live DOM
    // alone loses a short intermediate replacement, including one removed
    // before this observer callback. Missing anchors/old values stay unknown.
    const steps = [];
    for (let index = mutations.length - 1; index >= 0; index--) {
      const mutation = mutations[index];
      if (mutation.type === "characterData") {
        if (typeof mutation.oldValue !== "string") continue;
        steps.push({ mutation, index, after: values.get(mutation.target) });
        values.set(mutation.target, mutation.oldValue);
      } else if (mutation.type === "childList") {
        const list = children.get(mutation.target), added = Array.from(mutation.addedNodes || []);
        const removed = Array.from(mutation.removedNodes || []);
        if (!list || !added.length && !removed.length) continue;
        let position = added.length ? list.indexOf(added[0]) : -1;
        if (added.length && (position < 0 || !added.every((node, offset) => list[position + offset] === node))) continue;
        if (!added.length) {
          if (mutation.nextSibling && list.includes(mutation.nextSibling)) position = list.indexOf(mutation.nextSibling);
          else if (mutation.previousSibling && list.includes(mutation.previousSibling)) position = list.indexOf(mutation.previousSibling) + 1;
          else if (mutation.nextSibling === null && mutation.previousSibling === null && !list.length) position = 0;
          else continue;
        }
        list.splice(position, added.length, ...removed);
        steps.push({ mutation, index, position, added, removed });
      }
    }
    const changes = new Map(), members = new Map(Array.from(groups, group => [group, new Set(leaves(group))]));
    for (const step of steps.reverse()) {
      const before = new Map(Array.from(groups, group => [group, text(leaves(group))]));
      for (const group of groups) for (const node of leaves(group)) members.get(group).add(node);
      let replacement = true;
      if (step.added) {
        const oldNodes = step.removed.flatMap(leaves), newNodes = step.added.flatMap(leaves);
        replacement = oldNodes.some(node => String(values.get(node)).trim())
          && newNodes.some(node => String(values.get(node)).trim())
          && !oldNodes.every(node => newNodes.includes(node))
          && !(oldNodes.every(node => node.isConnected !== false) && newNodes.every(node => identities.has(node)));
        // Exact source/display copies transfer their identity, even if a whole
        // paragraph is replaced or regrouped at a different locator.
        if (oldNodes.length === newNodes.length) for (let i = 0; i < oldNodes.length; i++) {
          const old = oldNodes[i], next = newNodes[i], alias = aliases.get(old);
          const expected = alias?.source ?? values.get(old), value = values.get(next);
          if (value === expected || alias?.displays.has(value)) {
            aliases.set(next, alias || { source: expected, displays: new Set([expected]) });
            const id = identities.get(old);
            if (id) identities.set(next, { ...id });
          }
        }
        children.get(step.mutation.target).splice(step.position, step.removed.length, ...step.added);
        leafCache.clear();
        // A replacement of the ancestor itself leaves the detached old subtree
        // intact. Compare its exact members to the replacement at this same
        // mutation position, rather than mistaking that intact subtree for the
        // final displayed group. Pure movement still has the same members.
        if (replacement) for (const group of groups) {
          const priorMembers = leaves(group);
          if (!step.removed.includes(group) || priorMembers.length !== oldNodes.length
              || !priorMembers.every((node, index) => node === oldNodes[index])) continue;
          const prior = text(oldNodes), after = text(newNodes);
          if (prior?.trim() && after?.trim() && prior !== after) {
            if (!changes.has(group)) changes.set(group, []);
            changes.get(group).push({ mutationIndex: step.index, mutationType: "childList",
              beforeSourceText: prior, afterSourceText: after });
            for (const node of newNodes) members.get(group).add(node);
          }
          if (step.added.length === 1 && step.added[0].nodeType === 1) sourceGroups.add(step.added[0]);
        }
      } else {
        replacement = String(values.get(step.mutation.target)).trim().length > 0
          && String(step.after).trim().length > 0;
        values.set(step.mutation.target, step.after);
      }
      for (const group of groups) {
        for (const node of leaves(group)) members.get(group).add(node);
        const after = text(leaves(group)), prior = before.get(group);
        // A removed group/member or empty display is not proof of a new source.
        // Require explicit, nonempty source text on both sides of the change.
        if (replacement && prior?.trim() && after?.trim() && prior !== after) {
          if (!changes.has(group)) changes.set(group, []);
          changes.get(group).push({ mutationIndex: step.index, mutationType: step.mutation.type,
            beforeSourceText: prior, afterSourceText: after });
        }
      }
    }
    // Missing oldValue can still have an exact retained record and a changed
    // final DOM value. Do not infer anything from detach/layout alone.
    for (const mutation of mutations) if (mutation.type === "characterData" && mutation.oldValue == null) {
      const record = records.get(mutation.target), value = String(mutation.target.nodeValue || "");
      if (record?.groupElement && value.trim() && value !== record.source && value !== record.lastWritten) {
        if (!members.has(record.groupElement)) members.set(record.groupElement, new Set([record.node]));
        if (!changes.has(record.groupElement)) changes.set(record.groupElement, []);
        changes.get(record.groupElement).push({ mutationIndex: mutations.indexOf(mutation), mutationType: "characterData",
          beforeSourceText: record.source, afterSourceText: value });
      }
    }
    for (const [group, witnesses] of changes) {
      const affected = new Set([...members.get(group), ...Array.from(records.values())
        .filter(record => record.groupElement === group).map(record => record.node)]);
      const retired = new Set();
      for (const node of affected) {
        const old = identities.get(node);
        if (!old || old.retired) continue;
        // Preserve history and withdrawal candidates. Retire only the mapping
        // that could otherwise resurrect this lifetime on a later same-text return.
        if (locators.get(old.key) === old.id) locators.delete(old.key);
        identities.set(node, { ...old, retired: true });
        if (!retired.has(old.id)) events.push({ type: "source-change", schemaVersion: 1, sourceId: old.id,
          at: timestamp(), witnesses });
        retired.add(old.id);
      }
    }
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
  // This is independent source geometry, not the production rendered-text test.
  // Zero extents are known empty geometry; absent, contradictory or non-finite
  // coordinates are unknown and must not authorize retained blank evidence.
  function normalizeRect(rect) {
    if (!rect) return null;
    function axis(origin, start, end, extent) {
      const values = [origin, start, end, extent];
      if (values.some(value => value !== undefined && !Number.isFinite(value))) return null;
      const position = start ?? origin, size = extent ?? (end - position);
      if (!Number.isFinite(position) || !Number.isFinite(size) || size < 0
          || !Number.isFinite(position + size)) return null;
      const agrees = (a, b) => Math.abs(a - b) <= Number.EPSILON * 8 * Math.max(1, Math.abs(a), Math.abs(b));
      if (origin !== undefined && !agrees(origin, position)
          || end !== undefined && !agrees(end, position + size)) return null;
      return [position, size];
    }
    const horizontal = axis(rect.x, rect.left, rect.right, rect.width);
    const vertical = axis(rect.y, rect.top, rect.bottom, rect.height);
    return horizontal && vertical ? { x: horizontal[0], y: vertical[0], width: horizontal[1], height: vertical[1] } : null;
  }
  function intersect(rect, bounds) {
    const x = Math.max(rect.x, bounds.left), y = Math.max(rect.y, bounds.top);
    const right = Math.min(rect.x + rect.width, bounds.right), bottom = Math.min(rect.y + rect.height, bounds.bottom);
    return { x, y, width: Math.max(0, right - x), height: Math.max(0, bottom - y) };
  }
  function length(value, size) {
    if (value === "0") return 0;
    if (!/^-?(?:\d+(?:\.\d*)?|\.\d+)(px|%)$/.test(value)) return null;
    const result = parseFloat(value) * (value.endsWith("%") ? size / 100 : 1);
    return Number.isFinite(result) ? result : null;
  }
  function clippingBounds(element, style) {
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
    const clip = ["absolute", "fixed"].includes(style.position) && /^(?:rect)\((.*)\)$/.exec(style.clip || "");
    const inset = /^inset\((.*?)\)(?:\s+border-box)?$/.exec(style.clipPath || "");
    if (!clipX && !clipY && !clip && !inset) return bounds;
    const box = normalizeRect(element.getBoundingClientRect?.());
    if (!box) return null;
    const left = box.x, top = box.y;
    if (clipX || clipY) {
      // Client geometry excludes borders and scrollbar gutters. Scale client
      // dimensions into the same coordinates as transformed Range rectangles.
      for (const key of ["offsetWidth", "offsetHeight", "clientLeft", "clientTop", "clientWidth", "clientHeight"])
        if (element[key] !== undefined && (!Number.isFinite(element[key]) || element[key] < 0)) return null;
      const sx = element.offsetWidth > 0 ? box.width / element.offsetWidth : 1;
      const sy = element.offsetHeight > 0 ? box.height / element.offsetHeight : 1;
      const x = left + (element.clientLeft || 0) * sx, y = top + (element.clientTop || 0) * sy;
      const width = Number.isFinite(element.clientWidth) ? element.clientWidth * sx : box.width;
      const height = Number.isFinite(element.clientHeight) ? element.clientHeight * sy : box.height;
      if (clipX && ![x, width, x + width].every(Number.isFinite)
          || clipY && ![y, height, y + height].every(Number.isFinite)) return null;
      bounds.push({ left: clipX ? x : -Infinity, right: clipX ? x + width : Infinity,
        top: clipY ? y : -Infinity, bottom: clipY ? y + height : Infinity });
    }
    // Legacy accessibility clipping applies only to positioned elements.
    if (clip) {
      const values = clip[1].trim().split(/[\s,]+/);
      const offsets = values.map((value, i) => value === "auto" ? [0, box.width, box.height, 0][i]
        : length(value, i % 2 ? box.width : box.height));
      if (offsets.length !== 4 || offsets.some(value => value === null)) return null;
      bounds.push({ left: left + offsets[3], top: top + offsets[0], right: left + offsets[1], bottom: top + offsets[2] });
    }
    if (inset) {
      const values = inset[1].split(/\s+round\s+/)[0].trim().split(/\s+/);
      if (values.length >= 1 && values.length <= 4) {
        const expanded = [values[0], values[1] || values[0], values[2] || values[0], values[3] || values[1] || values[0]];
        const offsets = expanded.map((value, i) => length(value, i % 2 ? box.width : box.height));
        if (offsets.some(value => value === null)) return null;
        bounds.push({ left: left + offsets[3], top: top + offsets[0],
          right: left + box.width - offsets[1], bottom: top + box.height - offsets[2] });
      } else return null;
    }
    // Infinite edges are intentional only on the unconstrained overflow axes.
    for (const bound of bounds.slice(clipX || clipY ? 1 : 0))
      if (![bound.left, bound.top, bound.right, bound.bottom].every(Number.isFinite)) return null;
    return bounds;
  }
  function geometry(node, retainedRects) {
    const range = document.createRange(); range.selectNodeContents(node);
    let canRetain = Number.isFinite(root.scrollX) && Number.isFinite(root.scrollY);
    let rects = retainedRects ? retainedRects.map(r => Number.isFinite(r.documentX) && Number.isFinite(r.documentY)
      ? normalizeRect({ x: r.documentX - root.scrollX, y: r.documentY - root.scrollY, width: r.width, height: r.height }) : null)
      : Array.from(range.getClientRects(), normalizeRect);
    if (rects.some(rect => !rect)) canRetain = false;
    rects = rects.filter(Boolean);
    const parent = composedParent(node), bounds = [];
    let visible = !!parent && Number.isFinite(root.scrollX) && Number.isFinite(root.scrollY);
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
      const clipping = clippingBounds(element, style);
      if (!clipping) { visible = false; canRetain = false; break; }
      bounds.push(...clipping);
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
      clipped = normalizeRect(clipped);
      const documentX = clipped?.x + root.scrollX, documentY = clipped?.y + root.scrollY;
      if (!clipped || !Number.isFinite(documentX) || !Number.isFinite(documentY)) { canRetain = false; return []; }
      return [{ ...clipped, documentX, documentY }];
    });
    const rendered = visible && rects.some(r => r.width > 0 && r.height > 0);
    return { canRetain, rects, ancestorsVisible: visible, rendered: !!rendered, inViewport: !!rendered && rects.some(r =>
      r.x < root.innerWidth && r.y < root.innerHeight && r.x + r.width > 0 && r.y + r.height > 0) };
  }
  function sample(phase) {
    const at = timestamp(), sequence = ++sampleSequence;
    const state = session?.inspect(), records = state?.records || new Map(), nodes = [];
    for (const record of records.values()) if (record.groupElement) sourceGroups.add(record.groupElement);
    for (const node of textNodes(document.body)) {
      const record = records.get(node);
      const source = record && (node.nodeValue === record.source || node.nodeValue === record.lastWritten)
        ? record.source : node.nodeValue;
      if (!String(source || "").trim()) continue;
      const id = identity(node, source), { canRetain, ...layout } = geometry(node), before = previous.get(id);
      // Empty Text ranges have no geometry. Retain the previous visible position
      // when an unchanged, still connected translated node disappears to blank.
      let disappeared = !String(node.nodeValue || "").trim() && before?.translated
        && sources.has(id) && canRetain && layout.ancestorsVisible && node.isConnected !== false;
      if (disappeared && !layout.rects.length) {
        const retained = geometry(node, before.rects);
        disappeared = retained.rendered;
        if (disappeared) { layout.rects = retained.rects; layout.inViewport = retained.inViewport; }
      }
      if (!layout.rendered && !disappeared) continue;
      const parent = node.parentElement || node.getRootNode?.().host;
      const value = String(node.nodeValue || "");
      const row = { id, sourceText: source, locator: locator(node),
        firstSeen: sources.get(id)?.firstSeen || at,
        tag: parent?.tagName || "", role: parent?.getAttribute?.("role") || "",
        ...layout, displayedText: value, phase };
      // Only full independently observed rows enter history. If a node later
      // hides or detaches, the last complete row remains available for review.
      sources.set(id, JSON.parse(JSON.stringify(row))); nodes.push(row);
      const reference = { at, sequence };
      // Charge the actual supplemental representation (including its source
      // key). Full source rows are independently retained exactly once above;
      // historical producer snapshots and exported proof are never shortened.
      const size = JSON.stringify({ sourceId: id, ...reference }).length, oldVersion = sourceVersions.get(id);
      if (oldVersion) { sourceVersionCharacters -= oldVersion.size; sourceVersions.delete(id); }
      if (size <= observationCharacterLimit) {
        while (sourceVersions.size >= observationLimit || sourceVersionCharacters + size > observationCharacterLimit) {
          const oldest = sourceVersions.keys().next().value;
          sourceVersionCharacters -= sourceVersions.get(oldest).size;
          sourceVersions.delete(oldest); sourceVersionDropped++;
        }
        sourceVersions.set(id, { ...reference, size }); sourceVersionCharacters += size;
      } else sourceVersionDropped++;
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
    const work = { jobs: state?.jobs ?? null, pending: state?.pending ?? null,
      scanning: state?.scanning ?? null, rpcPending: pending.size,
      scheduledScans: scheduledScans.size, active: state?.active ?? null,
      paused: state?.paused ?? null, originalView: state?.originalView ?? null,
      automatic: state?.manualMode === null, documentVisible: document.visibilityState !== "hidden" };
    const idle = Number.isInteger(work.jobs) && work.jobs === 0
      && Number.isInteger(work.pending) && work.pending === 0
      && work.scanning === false && work.rpcPending === 0 && work.scheduledScans === 0
      && work.active === true && work.paused === false && work.originalView === false
      && work.automatic && work.documentVisible;
    if (typeof state?.assessed === "boolean") {
      const represented = new Map(), visibleRows = new Map(nodes.map(row => [row.id, row])), piecesBySource = new Map();
      for (const prediction of predictions) for (const member of prediction.sourceIds) {
        if (!piecesBySource.has(member)) piecesBySource.set(member, []);
        piecesBySource.get(member).push(prediction);
      }
      for (const prediction of predictions) if (currentSources.has(prediction.sourceId)
          && visibleRows.get(prediction.sourceId)?.rendered === true)
        for (const member of prediction.sourceIds) if (member !== prediction.sourceId && sources.has(member))
          represented.set(member, prediction.sourceId);
      for (const id of new Set([...currentSources, ...represented.keys()])) {
        const pieces = piecesBySource.get(id) || [];
        const observedVersion = sourceVersion(id);
        const ownerVersion = sourceVersion(represented.get(id));
        const reference = version => version ? { at: version.at, sequence: version.sequence } : null;
        const value = { sourceId: id, phase, at, sequence,
          sourceObservation: reference(observedVersion),
          representedSourceObservation: reference(ownerVersion),
          rendered: visibleRows.get(id)?.rendered === true,
          representedBy: represented.get(id) || null,
          pageDecision: state.assessed,
          work, eligible: idle || pieces.length ? eligibleSources.has(id) : null, pieces };
        // Detach snapshots from mutable DOM/session state and returned samples.
        const old = producerObservations.get(id);
        if (old) { releaseObservation(old); producerObservations.delete(id); }
        if (!observedVersion || represented.has(id) && !ownerVersion) { observationDropped++; continue; }
        value.work = internObservationValue(work);
        value.pieces = pieces.map(internObservationValue);
        const serialized = JSON.stringify(value), size = serialized.length + 1;
        const entry = { serialized, size, references: [value.work, ...value.pieces],
          versions: [observedVersion, ownerVersion].filter(Boolean) };
        observationCharacters += size;
        producerObservations.set(id, entry);
        while (producerObservations.size > observationLimit || observationCharacters > observationCharacterLimit) {
          const oldest = producerObservations.keys().next().value;
          releaseObservation(producerObservations.get(oldest));
          producerObservations.delete(oldest); observationDropped++;
        }
      }
    }
    const versions = new Map();
    for (const id of sourceVersions.keys()) {
      const version = sourceVersion(id);
      versions.set(JSON.stringify([version.source.id, version.sequence]), version);
    }
    for (const { versions: retained } of producerObservations.values()) for (const version of retained)
      versions.set(JSON.stringify([version.source.id, version.sequence]), version);
    // Keep the legacy export unless its union exceeds a bound. At pressure only
    // exact producer/owner references and membership-time witnesses are needed;
    // the complete latest traversal still lives, unchanged, in sourceNodes.
    const versionSize = version => JSON.stringify(version).length;
    const referencesOnly = versions.size > observationLimit ||
      Array.from(versions.values()).reduce((size, version) => size + versionSize(version), 0) > observationCharacterLimit;
    if (referencesOnly) {
      const required = new Map(), earliest = new Map();
      const key = version => JSON.stringify([version.source.id, version.sequence]);
      const before = (left, right) => left.at < right.at || left.at === right.at && left.sequence <= right.sequence;
      for (const version of versions.values()) {
        const prior = earliest.get(version.source.id);
        if (!prior || before(version, prior)) earliest.set(version.source.id, version);
      }
      for (const { versions: retained } of producerObservations.values()) for (const version of retained)
        required.set(key(version), version);
      const witnessed = new Map();
      for (const version of required.values()) {
        const prior = witnessed.get(version.source.id);
        if (!prior || before(version, prior)) witnessed.set(version.source.id, version);
      }
      for (const entry of producerObservations.values()) {
        const row = JSON.parse(entry.serialized);
        for (const id of row.pieces) for (const member of JSON.parse(observationValues.get(id).serialized).sourceIds) {
          const prior = witnessed.get(member), candidate = earliest.get(member);
          // Never invent an earlier witness when none was retained. That
          // membership remains unknown in capture, just as in the full export.
          if ((!prior || !before(prior, row)) && candidate && before(candidate, row)) {
            required.set(key(candidate), candidate); witnessed.set(member, candidate);
          }
        }
      }
      versions.clear();
      for (const [id, version] of required) versions.set(id, version);
    }
    const independentSourceObservations = []; let versionCharacters = 0, versionOverflow = 0;
    for (const version of versions.values()) {
      const size = versionSize(version);
      if (independentSourceObservations.length >= observationLimit || versionCharacters + size > observationCharacterLimit) {
        versionOverflow++; continue;
      }
      independentSourceObservations.push(JSON.parse(JSON.stringify(version))); versionCharacters += size;
    }
    // Small/legacy consumers still see full rows. Large captures use the same
    // observations with explicit version and value references. Count the value
    // table as retained characters too; interning never hides actual loss.
    let producerRows = [], expandedCharacters = 0, compact = false;
    for (const entry of producerObservations.values()) {
      const row = expandedObservation(entry);
      expandedCharacters += JSON.stringify(row).length;
      if (expandedCharacters > observationCharacterLimit) { compact = true; break; }
      producerRows.push(row);
    }
    const transport = compact ? { schemaVersion: 1, values: Object.fromEntries(
      Array.from(observationValues.values(), entry => [entry.id, JSON.parse(entry.serialized)])) } : null;
    if (compact) producerRows = Array.from(producerObservations.values(), entry => JSON.parse(entry.serialized));
    return { at, finalUrl: root.location.href, title: document.title,
      viewport: { width: root.innerWidth, height: root.innerHeight, scrollX: root.scrollX, scrollY: root.scrollY },
      nodes, sourceNodes: JSON.parse(JSON.stringify(Array.from(sources.values(), value => ({ ...value,
        currentlyRendered: currentSources.has(value.id) })))), predictions, productionEligibility, status,
      producerObservations: producerRows,
      ...(transport ? { producerObservationTransport: transport } : {}),
      producerObservationDropped: observationDropped,
      independentSourceObservations,
      ...(referencesOnly ? { independentSourceObservationExport: { schemaVersion: 1, scope: "references" } } : {}),
      independentSourceObservationDropped: sourceVersionDropped + versionOverflow,
      pageDecision: typeof state?.assessed === "boolean" ? state.assessed : null,
      jobs: work.jobs, pending: work.pending, scanning: work.scanning,
      rpcPending: pending.size, scheduledScans: scheduledScans.size, events: events.slice() };
  }
  // Two lightweight boundaries per screenshot, never a DOM walk or a poll.
  // Event offsets refer to the immutable, append-only raw history; exporting a
  // delta does not consume it. A boundary is an observation, not paint proof.
  function screenshotBoundary(eventOffset) {
    if (!Number.isInteger(eventOffset) || eventOffset < 0 || eventOffset > events.length)
      throw new Error("qa_screenshot_event_offset_invalid");
    return { at: timestamp(), sampleSequence, eventOffset, eventCount: events.length,
      scheduledScans: scheduledScans.size,
      viewport: { width: root.innerWidth, height: root.innerHeight, scrollX: root.scrollX, scrollY: root.scrollY },
      events: JSON.parse(JSON.stringify(events.slice(eventOffset))) };
  }
  function observation() { if (observer && session) observer.configure(session.inspect().observing); }
  function recordStatus(value) { status = value; events.push({ type: "status", value, at: timestamp() }); observation(); }
  const motion = root.ScreenTranslatorMotion || root.ScreenTranslatorMotionFactory.createTranslationMotion({ document, window: root });
  // Pass through the real writer and its real commit result, including delayed
  // animation frames. Record only successful DOM writes under the exact source
  // identity; inbound provider strings alone are not write provenance. The
  // shared production motion object/writer and scheduling remain untouched.
  const evidenceMotion = Object.create(motion);
  if (motion.writer?.write) {
    evidenceMotion.writer = Object.create(motion.writer);
    evidenceMotion.writer.write = (node, value, options) => motion.writer.write(node, value, { ...options,
      commit: next => {
        const record = session?.inspect().records.get(node), source = record?.source;
        const sourceId = identities.get(node)?.id, before = node.nodeValue;
        const result = options.commit(next);
        if (result && record && record.source === source && record.lastWritten === next
            && node.nodeValue === next && before !== next) {
          let entry = productWrites.get(node);
          if (!entry || entry.source !== source || entry.sourceId !== sourceId) {
            releaseProductWrites(node);
            entry = { source, sourceId, steps: [], characters: 0 };
          }
          const size = String(before).length + String(next).length;
          if (productWriteCount < observationLimit && productWriteCharacters + size <= observationCharacterLimit) {
            entry.steps.push({ before, after: next }); entry.characters += size;
            productWriteCount++; productWriteCharacters += size;
            productWrites.set(node, entry);
          } else {
            uncertainProductWrites.add(node); observationDropped++;
          }
        }
        return result;
      }
    });
  }
  session = api.createPageAutoSession({ document, token: api.randomIdentifier(),
    // Pass through the production session's requested delay. Track real
    // scheduled work and sample after its Promise settles, without adding polls,
    // changing delays, or declaring idle from a status callback during scanning.
    schedule: (callback, delay = 0) => {
      const id = root.setTimeout(() => {
        scheduledScans.delete(id);
        const result = callback();
        if (result && typeof result.then === "function") result.then(() => {
          if (!disposed && session.inspect().active) sample("producer-idle");
        });
      }, delay);
      scheduledScans.add(id); return id;
    },
    clearSchedule: id => { scheduledScans.delete(id); root.clearTimeout(id); },
    getLocation: () => root.location.href, motion: evidenceMotion,
    getStyle: element => root.getComputedStyle(element), makeId: () => api.randomIdentifier(), viewportHeight: root.innerHeight,
    getSelection: () => root.getSelection(), isRenderedText: api.createRenderedTextTester(document), yieldWork: api.yieldToBrowser,
    send: message => new Promise(resolve => {
      const rpcId = `rpc-${++serial}`; pending.set(rpcId, resolve);
      outbound.push({ rpcId, message }); events.push({ type: "outbound", rpcId, message, at: timestamp() });
    }), onStatus: recordStatus, onView: value => events.push({ type: "view", value, at: timestamp() }),
    onDecision: value => events.push({ type: "decision", value, at: timestamp() }),
    onRootFound: tree => observer?.found(tree), onOpenRoots: trees => observer?.replace(trees) });
  observer = api.createOpenRootObserver({ document, createObserver: callback => {
    const native = new root.MutationObserver(callback);
    // QA evidence needs exact characterData history; production scheduling and
    // mutation delivery are unchanged. No additional browser poll is introduced.
    return { observe: (tree, options) => native.observe(tree, { ...options, characterDataOldValue: true }),
      disconnect: () => native.disconnect() };
  },
    onMutations: mutations => {
      sourceTransitions(mutations, session.inspect().records);
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
    sample, screenshotBoundary, startProbe, probeStatus: id => probeJob?.id === id ? { ...probeJob } : null,
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
      disposed = true; probeJob = null;
      root.clearInterval(probeTimer); session.dispose(); observer.dispose(); motion.dispose();
      for (const id of scheduledScans) root.clearTimeout(id);
      scheduledScans.clear();
      productWrites.clear(); productWriteCount = productWriteCharacters = 0;
      for (const [target, type, fn, options] of listeners) target?.removeEventListener(type, fn, options);
      for (const resolve of pending.values()) resolve({ accepted: false, reason: "qa_disposed" }); pending.clear();
    }
  };
})(globalThis);
