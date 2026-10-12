# Development replay: numeric equivalence and delivery races

The original CONTRACT.md quality thresholds, source-only review and screenshot
requirements remain authoritative. The clean baseline contains the previously
reviewed semantic staging, exact transfer edges and source observation lifecycle.

## Verified development evidence

- Wikipedia Machine_translation: `in the 1960s ... (1978). Beginning in the
  late 1980s` was translated using `20世纪60年代` and `20世纪80年代末`.
  These precise equivalents were incorrectly rejected. `early 2000s` was rendered
  as the less precise `21世纪初`; keep the explicit four-digit decade instead.
- Stanford: `The 7 Schools` became `七所学校` and was incorrectly rejected.
  Empty whitespace members in production atomicRecords also acquired source IDs
  despite the independent source collector correctly omitting blank text.
- NASA: a real candidate finished successfully in 667 ms, followed by a frontend
  translation_timeout cancellation after expensive CLI/DOM operations. The host
  had retired the work and returned unknown_request. This is not complete evidence.

The small public fixture numeric-equivalence.json records these source phrases
and hand-authored expected safety examples. It is a regression fixture, not a
real model test, website count, independent annotation or quality result.

## Exclusive tasks

1. numeric-guard: PageTextSemanticGuard.cs and its existing unit tests. Recognize
   exact year-decade equivalence (including Chinese century/decade notation) and
   common unambiguous Chinese classifiers such as 所. Retain wrong number,
   century, range, duration and per-ID ownership rejection. Do not accept vague
   century beginnings for explicit decades or mistake a duration such as 2000s
   for a calendar decade. HasProtectedFacts and Assess share the same parser;
   NumericTransfers must still protect the real owner and receiver. No provider,
   request, protocol, cache or eligibility changes.
2. numeric-prompt: DeepSeekTranslationProvider.cs and PageNodeBoundaryPromptTests.cs.
   Keep PageTextPrompt within the existing 80..120-word budget. Preserve every
   existing ownership, fixed boundary, source-as-data, date/quantity, gender,
   partial-hint and JSON rule. Require four-digit years for source decades so
   ambiguous shorthand cannot lose facts. No hint generator, HTTP, billing,
   auth, model, OCR or other prompt changes.
3. qa-delivery: capture.py, page-adapter.js, their tests/README, and JsonlHost.cs,
   SelfTest.cs/README. Exclude only genuinely blank atomic members from sourceIds;
   retain all nonblank members and independent visibility proof. Make cancel for
   a completed admitted message idempotent only in its original case, bounded by
   existing request admission, without emitting another success terminal. Unknown
   IDs and cross-case cancellation must remain rejected. Improve queued frame
   delivery before slow DOM/CLI work and reduce redundant wrapper round trips.
   Keep production timer durations, real failure/timeout state, raw packets,
   Promise acknowledgements, identity, order, UTF-16 limits and case deadlines.
   Never synthesize completion or hide a frontend timeout. Browser control remains
   exclusively the existing Browser Use CLI wrapper; no MCP, direct socket,
   Playwright, CDP, WebDriver or another browser API. Official --mcp was inspected
   by the controller but does not expose the required synchronous eval interface,
   so it is not an authorized replacement for this capture path.

## Validation and real replay

All workers use offline tests within their owned worktrees. No browser, model,
network, credentials, user settings, commits, merges or pushes. Public interfaces
stay compatible. The controller reruns the original acceptance commands, reviews
actual changes and runs the full repository gate before promotion. Each new real
replay uses a fresh artifact directory and run ID; all old failures remain.
The host's explicit saved-preset snapshot selects the previously verified saved
DeepSeek preset without changing settings or trying alternate credentials.
