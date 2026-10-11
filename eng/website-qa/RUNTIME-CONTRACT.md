# Browser Use real QA runtime follow-up

The existing CONTRACT.md remains the quality and evidence authority. This round
changes QA tools only; production recognition/rendering, provider selection,
user settings, credentials, and billing are unchanged.

## Exclusive ownership

1. transport: eng/browser-use-qa/capture.py,
   eng/browser-use-qa/tests/test_capture.py, and eng/browser-use-qa/README.md.
   Batch production frame delivery through the existing Browser Use CLI wrapper.
   Preserve each original host event and frame, order, identity checks, Promise
   acknowledgements, UTF-16 argument limits, and strict completion evidence.
   A transport optimization cannot substitute translation results or suppress
   failures. Do not use a different browser controller. Existing public Python
   entry points stay compatible. Bound work by the case deadline.
2. normalization: eng/website-qa/normalize.py and test_normalize.py.
   The command accepts --case, --review, --output. Review includes pageAnnotation,
   annotations with sourceId and needTranslation, screenshot phase booleans,
   and retraction reviews. Preserve raw provenance/hashes. Map sourceText/rects
   to text/position; aggregate multiple production pieces per sourceId; all
   pieces must complete. Missing predictions map to eligible=false only when
   source evidence and an actual finished capture establish that fact. Preserve
   errors, failures, unknown labels, and noncompletion. Never generate truth
   from predictions. Use original capabilities and terminal events as hostEvents.
   --output is exclusive creation. Normalization does not certify quality.
3. review-index: eng/browser-use-qa/review_index.py and
   eng/browser-use-qa/tests/test_review_index.py.
   The command accepts --case and --output-dir (new directory). Generate an
   offline HTML review index, source-only JSON/TSV, and blank review template.
   Default source review excludes production predictions to keep annotation
   independent. Include all source rows, screenshots, geometry, and provenance.
   Never automatically label, mark reviewed, or claim completion. Escape HTML
   and use local relative links without network or credential access.

## Acceptance

All workers run offline tests only in their assigned worktree, use no real
browser or model, do not commit/merge/push, and return truthful structured
results. Controller reviews actual diffs and reruns acceptance, then merges and
executes all repository gates before promoting clean main. Raw real-page
evidence remains outside the repository and is never rewritten by these tools.
