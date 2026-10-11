"use strict";

// All coordinates here are CSS pixels in the top document's viewport.
function imageGeometry(box, natural, fit = "fill", position = "50% 50%") {
  let width = box.width, height = box.height;
  const contain = Math.min(width / natural.width, height / natural.height);
  const cover = Math.max(width / natural.width, height / natural.height);
  if (fit !== "fill") {
    const scale = fit === "cover" ? cover : fit === "none" ? 1 : fit === "scale-down" ? Math.min(1, contain) : contain;
    width = natural.width * scale; height = natural.height * scale;
  }
  const parts = position.trim().match(/calc\([^)]*\)|[^\s]+/g) || [];
  const axis = (value, remaining, vertical) => {
    const names = vertical ? { top: 0, center: .5, bottom: 1 } : { left: 0, center: .5, right: 1 };
    if (value in names) return remaining * names[value];
    if (/^-?[\d.]+%$/.test(value)) return remaining * parseFloat(value) / 100;
    if (/^-?[\d.]+px$/.test(value)) return parseFloat(value);
    const calc = /^calc\(\s*(-?[\d.]+)%\s*([+-])\s*([\d.]+)px\s*\)$/.exec(value);
    if (calc) return remaining * Number(calc[1]) / 100 + (calc[2] === "+" ? 1 : -1) * Number(calc[3]);
    throw new Error("Unsupported object-position");
  };
  let x, y;
  if (parts.length <= 2) {
    if (parts.length === 1 && ["top", "bottom"].includes(parts[0])) parts.unshift("50%");
    if (["top", "bottom"].includes(parts[0]) || ["left", "right"].includes(parts[1])) parts.reverse();
    x = axis(parts[0] || "50%", box.width - width, false);
    y = axis(parts[1] ?? "50%", box.height - height, true);
  } else {
    // Computed styles may retain the three/four-value edge-offset syntax.
    for (let i = 0; i < parts.length; i++) {
      const edge = parts[i], horizontal = ["left", "right"].includes(edge);
      if (edge === "center") {
        const horizontalEdge = parts.some(part => ["left", "right"].includes(part));
        const verticalEdge = parts.some(part => ["top", "bottom"].includes(part));
        if (horizontalEdge && !verticalEdge && y === undefined) y = (box.height - height) / 2;
        else if (verticalEdge && !horizontalEdge && x === undefined) x = (box.width - width) / 2;
        else throw new Error("Unsupported object-position");
        continue;
      }
      if (!horizontal && !["top", "bottom"].includes(edge)) throw new Error("Unsupported object-position");
      const remaining = horizontal ? box.width - width : box.height - height;
      const offset = parts[i + 1] && !["left", "right", "top", "bottom", "center"].includes(parts[i + 1])
        ? axis(parts[++i], remaining, !horizontal) : 0;
      const value = ["right", "bottom"].includes(edge) ? remaining - offset : offset;
      if (horizontal) { if (x !== undefined) throw new Error("Unsupported object-position"); x = value; }
      else { if (y !== undefined) throw new Error("Unsupported object-position"); y = value; }
    }
    if (x === undefined || y === undefined) throw new Error("Unsupported object-position");
  }
  return { left: box.left + x, top: box.top + y, width, height };
}

function intersectImageRects(a, b) {
  const left = Math.max(a.left, b.left), top = Math.max(a.top, b.top);
  return { left, top, width: Math.max(0, Math.min(a.left + a.width, b.left + b.width) - left),
    height: Math.max(0, Math.min(a.top + a.height, b.top + b.height) - top) };
}
function composedImageParent(element) {
  return element.assignedSlot || element.parentElement || element.getRootNode?.()?.host || null;
}

function requireAxisAlignedImageStyle(style, env) {
  if (style.visibility === "hidden" || style.visibility === "collapse" || Number(style.opacity) === 0)
    throw new Error("Image unavailable");
  if (style.transform && style.transform !== "none") {
    const matrix = new env.DOMMatrixReadOnly(style.transform);
    if (!matrix.is2D || Math.abs(matrix.b) > 1e-9 || Math.abs(matrix.c) > 1e-9 || matrix.a <= 0 || matrix.d <= 0)
      throw new Error("Unsupported transform");
  }
  // CSS individual transforms are not included in computed `transform`.
  if (style.rotate && style.rotate !== "none" && !/^(?:0(?:deg|rad|grad|turn)?|(?:z\s+)?0deg)$/.test(style.rotate))
    throw new Error("Unsupported transform");
  if (style.scale && style.scale !== "none") {
    const values = style.scale.trim().split(/\s+/).map(value => Number(value.replace(/%$/, "")));
    if (values.length > 2 || values.some(value => !Number.isFinite(value) || value <= 0)) throw new Error("Unsupported transform");
  }
  if (style.perspective && style.perspective !== "none") throw new Error("Unsupported transform");
}

function imageElementBox(element, style, rect) {
  const px = name => Number.parseFloat(style[name]) || 0;
  const bl = px("borderLeftWidth"), br = px("borderRightWidth"), bt = px("borderTopWidth"), bb = px("borderBottomWidth");
  const pl = px("paddingLeft"), pr = px("paddingRight"), pt = px("paddingTop"), pb = px("paddingBottom");
  let width = Number.parseFloat(style.width), height = Number.parseFloat(style.height);
  // Computed used dimensions retain subpixels, unlike integer client/offset sizes.
  if (!Number.isFinite(width)) width = element.clientWidth - pl - pr;
  else if (style.boxSizing === "border-box") width -= bl + br + pl + pr;
  if (!Number.isFinite(height)) height = element.clientHeight - pt - pb;
  else if (style.boxSizing === "border-box") height -= bt + bb + pt + pb;
  const layoutWidth = width + pl + pr + bl + br, layoutHeight = height + pt + pb + bt + bb;
  if (![rect.left, rect.top, rect.width, rect.height, layoutWidth, layoutHeight].every(Number.isFinite)
      || layoutWidth <= 0 || layoutHeight <= 0) throw new Error("Image unavailable");
  const sx = rect.width / layoutWidth, sy = rect.height / layoutHeight;
  return { width, height, sx, sy, bl, br, bt, bb, pl, pr, pt, pb };
}

function imageAncestorScales(ancestors, env) {
  const result = new Map(); let sx = 1, sy = 1;
  const scaleValue = value => value.endsWith("%") ? Number.parseFloat(value) / 100 : Number(value);
  for (const { element, style } of ancestors.slice().reverse()) {
    if (style.transform && style.transform !== "none") {
      const matrix = new env.DOMMatrixReadOnly(style.transform); sx *= matrix.a; sy *= matrix.d;
    }
    if (style.scale && style.scale !== "none") {
      const values = style.scale.trim().split(/\s+/).map(scaleValue); sx *= values[0]; sy *= values[1] ?? values[0];
    }
    if (style.zoom && style.zoom !== "normal") {
      const zoom = scaleValue(style.zoom); if (Number.isFinite(zoom) && zoom > 0) { sx *= zoom; sy *= zoom; }
    }
    result.set(element, { sx, sy });
  }
  return result;
}

// Reads geometry only. Callers batch these reads before overlay style writes.
function describeImageElement(image, env = globalThis, styleCache = new Map()) {
  if (!image.isConnected || !image.complete || image.naturalWidth <= 0 || image.naturalHeight <= 0
      || image.naturalWidth * image.naturalHeight > 24000000) throw new Error("Image unavailable");
  const readStyle = element => {
    if (!styleCache.has(element)) styleCache.set(element, env.getComputedStyle(element));
    return styleCache.get(element);
  };
  let rendered = image, background = null;
  // X's React Native image uses a visible background layer and a transparent
  // img for loading/original pixels. Authorize only an exact same-source layer.
  const sourceStyle = readStyle(image);
  let social = false;
  try { social = /^(?:www\.)?(?:x\.com|twitter\.com)$/iu.test(new URL(env.document?.location?.href).hostname); } catch { }
  if (social && Number(sourceStyle.opacity) === 0 && image.parentElement) {
    const host = image.parentElement;
    for (const layer of [host, ...Array.from(host.children || []).slice(0,8)].filter(element=>element!==image)) {
      const style = readStyle(layer), match = /^url\(["']?([^"']+?)["']?\)$/u.exec(String(style.backgroundImage || ""));
      let same = false;
      try { same = !!match && new URL(match[1], env.document?.baseURI || env.document?.location?.href).href === (image.currentSrc || image.src); } catch { }
      if (same && ["contain", "cover", "100% 100%"].includes(style.backgroundSize)
          && !["hidden", "collapse"].includes(sourceStyle.visibility) && sourceStyle.display !== "none"
          && Number(style.opacity)!==0 && !["hidden","collapse"].includes(style.visibility) && style.display!=="none") {
        rendered = layer; background = { objectFit: style.backgroundSize === "100% 100%" ? "fill" : style.backgroundSize,
          objectPosition: style.backgroundPosition || "50% 50%" };break;
      }
    }
  }
  const ancestors = [];
  for (let element = rendered; element; element = composedImageParent(element)) {
    const style = readStyle(element); requireAxisAlignedImageStyle(style, env);
    // A transformed root would also transform this extension's fixed host.
    if (element === env.document?.documentElement && ((style.transform && style.transform !== "none")
        || (style.scale && style.scale !== "none") || (style.translate && style.translate !== "none")
        || (style.zoom && !["1", "normal", "100%"].includes(style.zoom))
        || (style.filter && style.filter !== "none")
        || /(?:^|\s)(?:paint|layout|strict|content)(?:$|\s)/.test(style.contain || "")
        || /(?:transform|filter|perspective)/.test(style.willChange || "")))
      throw new Error("Unsupported transform");
    ancestors.push({ element, style });
  }
  const style = ancestors[0].style, rect = rendered.getBoundingClientRect(), box = imageElementBox(rendered, style, rect);
  const ancestorScales = imageAncestorScales(ancestors, env);
  const natural = { width: image.naturalWidth, height: image.naturalHeight };
  // object-fit is resolved in the untransformed content box before CSS scaling.
  const fitted = imageGeometry({ left: 0, top: 0, width: box.width, height: box.height }, natural,
    background?.objectFit || style.objectFit || "fill", background?.objectPosition || style.objectPosition || "50% 50%");
  const content = { left: rect.left + (box.bl + box.pl) * box.sx, top: rect.top + (box.bt + box.pt) * box.sy,
    width: box.width * box.sx, height: box.height * box.sy };
  const display = { left: content.left + fitted.left * box.sx, top: content.top + fitted.top * box.sy,
    width: fitted.width * box.sx, height: fitted.height * box.sy };
  const viewport = env.visualViewport;
  const viewportClip = viewport && viewport.scale !== 1
    ? { left: viewport.offsetLeft, top: viewport.offsetTop, width: viewport.width, height: viewport.height }
    : { left: 0, top: 0, width: Math.min(env.innerWidth, env.document?.documentElement?.clientWidth || env.innerWidth),
      height: Math.min(env.innerHeight, env.document?.documentElement?.clientHeight || env.innerHeight) };
  let clip = intersectImageRects(intersectImageRects(content, display), viewportClip);
  for (const { element, style: parentStyle } of ancestors.slice(1)) {
    if (element === env.document?.documentElement) continue;
    const paintContainment = /(?:^|\s)(?:paint|strict|content)(?:$|\s)/.test(parentStyle.contain || "");
    const overflowX = paintContainment ? "hidden" : parentStyle.overflowX || "visible";
    const overflowY = paintContainment ? "hidden" : parentStyle.overflowY || "visible";
    if (overflowX === "visible" && overflowY === "visible") continue;
    const bounds = element.getBoundingClientRect(), parentBox = imageElementBox(element, parentStyle, bounds);
    const scale = ancestorScales.get(element);
    // Overflow clips the padding/client area, excluding borders and scrollbars.
    // Chrome's content-box computed width already excludes scrollbar gutters;
    // dividing rect.width by that width mistakes the gutter for a CSS scale.
    const preciseContent = parentStyle.boxSizing !== "border-box" && Number.isFinite(Number.parseFloat(parentStyle.width));
    const preciseHeight = parentStyle.boxSizing !== "border-box" && Number.isFinite(Number.parseFloat(parentStyle.height));
    const paddingWidth = preciseContent ? parentBox.width + parentBox.pl + parentBox.pr : element.clientWidth;
    const paddingHeight = preciseHeight ? parentBox.height + parentBox.pt + parentBox.pb : element.clientHeight;
    const gutterWidth = Math.max(0, bounds.width / scale.sx - parentBox.bl - parentBox.br - paddingWidth);
    const leftScrollbar = element.clientLeft > Math.round(parentBox.bl) ? gutterWidth : 0;
    const padding = { left: bounds.left + (parentBox.bl + leftScrollbar) * scale.sx,
      top: bounds.top + parentBox.bt * scale.sy,
      width: Math.max(0, paddingWidth * scale.sx), height: Math.max(0, paddingHeight * scale.sy) };
    if (overflowX !== "visible") clip = intersectImageRects(clip, { ...padding, top: clip.top, height: clip.height });
    if (overflowY !== "visible") clip = intersectImageRects(clip, { ...padding, left: clip.left, width: clip.width });
  }
  return { natural, display, clip, devicePixelRatio: env.devicePixelRatio,
    viewportSize: { width: env.innerWidth, height: env.innerHeight } };
}

globalThis.ScreenTranslatorImageGeometry = { imageGeometry, intersectImageRects, describeImageElement };
if (typeof module !== "undefined") module.exports = globalThis.ScreenTranslatorImageGeometry;
