(function exposeScrollAccumulator(root, factory) {
  "use strict";

  const api = factory();
  root.ScrollAccumulator = api.ScrollAccumulator;

  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }
})(globalThis, function createScrollAccumulator() {
  "use strict";

  class ScrollAccumulator {
    constructor() {
      this._positions = new Map();
      this._pending = new Map();
      this._generation = 0;
    }

    get generation() {
      return this._generation;
    }

    observe(target, x, y, metadata) {
      if (
        typeof target !== "string" ||
        target.length === 0 ||
        !Number.isFinite(x) ||
        !Number.isFinite(y)
      ) {
        return false;
      }

      const previous = this._positions.get(target);
      this._positions.set(target, { x, y });

      if (!previous) {
        return false;
      }

      const deltaXCss = x - previous.x;
      const deltaYCss = y - previous.y;
      if (deltaXCss === 0 && deltaYCss === 0) {
        return false;
      }

      const pending = this._pending.get(target);
      if (pending) {
        pending.deltaXCss += deltaXCss;
        pending.deltaYCss += deltaYCss;
        if (metadata !== undefined) {
          pending.metadata = metadata;
        }
      } else {
        this._pending.set(target, {
          deltaXCss,
          deltaYCss,
          metadata
        });
      }

      return true;
    }

    flush() {
      const updates = [];
      for (const [target, pending] of this._pending) {
        const update = {
          target,
          deltaXCss: pending.deltaXCss,
          deltaYCss: pending.deltaYCss
        };

        if (pending.metadata !== undefined) {
          Object.assign(update, pending.metadata);
        }

        updates.push(update);
      }

      this._pending.clear();
      return updates;
    }

    invalidate() {
      this._positions.clear();
      this._pending.clear();
      this._generation += 1;
      return this._generation;
    }
  }

  return { ScrollAccumulator };
});
