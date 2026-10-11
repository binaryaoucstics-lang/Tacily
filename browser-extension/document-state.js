(function exposeDocumentState(root, factory) {
  "use strict";

  const api = factory();
  root.ScreenTranslatorDocumentState = api;

  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }
})(globalThis, function createDocumentState() {
  "use strict";

  function hasCompleteViewportState(state) {
    const size = state?.viewportSize;
    return Boolean(
      size &&
      Number.isFinite(size.width) &&
      Number.isFinite(size.height) &&
      size.width > 0 &&
      size.height > 0
    );
  }

  async function recoverCompleteViewportState(current, queryState) {
    if (hasCompleteViewportState(current)) {
      return current;
    }

    const recovered = await queryState();
    return hasCompleteViewportState(recovered) ? recovered : null;
  }

  return {
    hasCompleteViewportState,
    recoverCompleteViewportState
  };
});
