"use strict";

(function (root) {
  const maximumPreviewCharacters = 262144;
  function randomIdentifier(implementation = root.crypto) {
    if (typeof implementation.randomUUID === "function") return implementation.randomUUID();
    const bytes = new Uint8Array(16); implementation.getRandomValues(bytes);
    bytes[6] = bytes[6] & 0x0f | 0x40; bytes[8] = bytes[8] & 0x3f | 0x80;
    const hex = [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  function validGeneration(value) {
    return Number.isSafeInteger(value) && value >= 0;
  }
  function normalizeConfig(value) {
    if (!value || !validGeneration(value.generation)) return null;
    return { enabled: value.enabled === true, active: value.active !== false,
      targetLanguage: typeof value.targetLanguage === "string" ? value.targetLanguage.slice(0, 64) : "简体中文",
      generation: value.generation, requirePageJudgement: value.requirePageJudgement === true };
  }
  function pageAllowsImages(config, decision) {
    if (config?.requirePageJudgement !== true) return true;
    return decision?.enabled === true && decision.assessed === true && decision.allowImages === true
      && validGeneration(decision.generation) && decision.generation === config.generation;
  }
  function eligibleImage({ natural, display, clip, viewportSize, complete = true, connected = true }) {
    return complete && connected && [natural?.width, natural?.height, display?.width, display?.height,
      clip?.width, clip?.height, viewportSize?.width, viewportSize?.height].every(Number.isFinite)
      && Math.min(natural.width, natural.height) >= 120
      && Math.max(natural.width, natural.height) >= 240
      && natural.width * natural.height <= 24000000
      && viewportSize.width > 0 && viewportSize.height > 0
      && Math.min(display.width, display.height) >= 240 && Math.max(display.width, display.height) >= 360
      && display.width * display.height >= 120000
      // Cap at the viewport: a tall comic still qualifies when only part is visible.
      // An absolute-size alternative keeps full-sized readers eligible on wide monitors.
      && (Math.min(display.width, viewportSize.width) * Math.min(display.height, viewportSize.height)
          / (viewportSize.width * viewportSize.height) >= .18
        || Math.min(display.width, display.height) >= 400 && Math.max(display.width, display.height) >= 600)
      && clip.width >= 64 && clip.height >= 64
      && clip.width * clip.height >= Math.max(120000, viewportSize.width * viewportSize.height * .02);
  }
  function previewSize(width, height, edge = 1280) {
    const scale = Math.min(1, edge / Math.max(width, height));
    return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
  }

  // One image read and one outstanding local assessment. Weak history does not retain page images.
  class AutoImageQueue {
    constructor({ read, send, isCurrent, isSameSource = isCurrent, maximumPending = 16, onSettled = () => {} }) {
      this.read = read; this.send = send; this.isCurrent = isCurrent; this.maximumPending = maximumPending;
      this.isSameSource = isSameSource;
      this.onSettled = onSettled; this.config = { enabled: false, active: true, generation: 0, targetLanguage: "简体中文", requirePageJudgement: false };
      this.history = new WeakMap(); this.completed = new WeakMap(); this.pending = []; this.running = false; this.epoch = 0;
    }
    configure(value) {
      const next = normalizeConfig(value);
      if (!next || next.generation < this.config.generation) return false;
      if (next.enabled === this.config.enabled && next.generation === this.config.generation
          && next.targetLanguage === this.config.targetLanguage && next.requirePageJudgement === this.config.requirePageJudgement) {
        if (next.active === this.config.active) return false;
        this.config = next;
        // Visibility is an admission gate, never a new model/config identity.
        // Keep the in-flight source reserved and all completed decisions.
        for (const item of this.pending) this.history.delete(item.image);
        this.pending = [];
        return true;
      }
      this.config = next; this.epoch++; this.pending = []; this.history = new WeakMap(); this.completed = new WeakMap();
      return true;
    }
    suspendPending() {
      // A page decision changes admission, not completed image/overlay caches.
      this.epoch++; this.pending = []; this.history = new WeakMap();
    }
    offer(image, version) {
      if (!this.config.enabled || !this.config.active || this.pending.length >= this.maximumPending) return false;
      if (this.completed.get(image) === version) return false;
      const saved = this.history.get(image);
      if (saved?.version === version && saved.epoch === this.epoch) return false;
      if (!this.isCurrent(image, version)) return false;
      this.history.set(image, { version, epoch: this.epoch });
      this.pending.push({ image, version, epoch: this.epoch, generation: this.config.generation });
      void this.pump();
      return true;
    }
    retry(image, version) {
      const saved = this.history.get(image);
      if (this.completed.get(image) !== version && (!saved || saved.version !== version || saved.epoch !== this.epoch)) return false;
      this.history.delete(image);
      this.completed.delete(image);
      return true;
    }
    releaseHistory(item) {
      const saved = this.history.get(item.image);
      if (saved?.version === item.version && saved.epoch === item.epoch) this.history.delete(item.image);
    }
    async pump() {
      if (this.running) return;
      this.running = true;
      try {
        while (this.config.enabled && this.config.active && this.pending.length) {
          const item = this.pending.shift();
          if (!this.isCurrent(item.image, item.version)) {
            this.releaseHistory(item); continue;
          }
          let sent = false, retryable = false;
          try {
            const candidate = await this.read(item.image, item.version);
            if (item.epoch !== this.epoch || !this.config.enabled || !this.config.active || !this.isCurrent(item.image, item.version)) continue;
            sent = true;
            const result = await this.send({ ...candidate, generation: item.generation });
            retryable = ["image_changed", "document_changed", "configuration_changed", "bridge_disconnected",
              "automatic_translation_unavailable", "assessment_timeout", "image_unavailable"].includes(result?.reason);
          } catch {
            // An unavailable image is attempted once for this source/config, never in a retry loop.
          } finally {
            if (item.epoch === this.epoch) {
              if (!this.isSameSource(item.image, item.version)
                  || !sent && (!this.config.active || !this.isCurrent(item.image, item.version))) this.releaseHistory(item);
              else if (!retryable) this.completed.set(item.image, item.version);
            }
            this.onSettled();
          }
        }
      } finally { this.running = false; }
    }
  }
  const api = { normalizeConfig, pageAllowsImages, eligibleImage, randomIdentifier, previewSize, maximumPreviewCharacters, AutoImageQueue };
  if (typeof module !== "undefined") module.exports = api;
  root.ScreenTranslatorImageAutoPolicy = api;
})(globalThis);
