# Real development-page failures: quality fixes

The original CONTRACT.md thresholds and independent review rules are unchanged.
Real development evidence: Wikipedia Machine_translation (site-021) produced a
1954 date transferred from its owning DOM text to its preceding fragment; the
production semantic guard correctly rejected seven nodes of that paragraph.
Xinhua (site-092) produced eligible candidates for readable Chinese headlines
containing AI/WTT/WSBK/APEC/R7. No holdout labels guide these changes.

Public fixture `fixtures/wikipedia-inline-dates.json` contains source/provenance
and observed unsafe translations, without user settings, keys or screenshots.

## Exclusive tasks

- semantic-retry: App BrowserPageTranslationService.cs and its new dedicated
  BrowserInlineSemanticRetryTests.cs. Preserve the fact guard and finite MaxRequests
  budget. Retry unsafe per-node translations with source-only boundary context;
  do not immediately make the paragraph permanently failed on a first unsafe
  answer. A corrected result must pass existing guards before cache/publication.
  Display fragments of an inline paragraph after its facts are verified, avoiding
  visible unverified prefixes followed by restoration. Preserve safe completed
  groups and cancellation/transport behavior. Do not mark invalid output valid.
- boundary-prompt: Core DeepSeekTranslationProvider.cs, TranslationPromptBuilder.cs
  and dedicated PageNodeBoundaryPromptTests.cs. Clarify DOM fragment ownership and
  use bounded per-ID source fact hints if helpful; dates/quantities from neighboring
  context cannot move to an ID. Retain provider/model/auth/billing/network behavior.
  Hints come only from each supplied block and are data, not instructions. No new
  dependencies or public request fields. This is independent of the retry task.
- readable-chinese: Core PageTextEligibility.cs and existing tests, JS
  page-auto-policy.js and existing tests. Skip predominantly readable Chinese
  snippets containing technical Latin abbreviations; retain genuinely foreign
  prose, Japanese/kana/hangul, and explicit foreignHanContext overrides. Keep
  frontend/backend parity and target-language boundaries. Do not change page
  activation thresholds or classify every mixed-script string as readable.

## Shared semantic assessment interface

The controller provides `PageTextSemanticGuard.Assess(source, translations)` on
the next fixed baseline. It returns the unchanged `UnsafeIds` verdict and exact
`NumericTransfers` edges (`OwnerId`, `ReceivingId`) from the existing missing-fact
loop. `FindUnsafeIds` remains compatible. Retry code must use these real edges
instead of synthesizing source-plus-answer probes; independent errors must not
be joined. Retain the original owner source/answer as an invocation-local witness
so correcting the owner cannot make a receiver's transferred fact acceptable.
Test a receiver that initially also changes Monday to Tuesday, then corrects only
the weekday while retaining a transferred quantity. No guard rule is weakened.

## Gates and controller duties

Workers use offline synthetic/public fixtures only in their own worktree; no real
browser, network, credentials, model calls, commits, merges or pushes. They report
actual tests and specific blockers. Root reviews diffs, reruns acceptance and full
repository gates, and then replays development cases and the fixed real corpus
with Browser Use CLI. Test success is not evidence of the hundred-site goal.

The QA host supports --running-preset-snapshot PATH. Root has independently
matched the live application's unique saved DeepSeek preset and frozen its
metadata outside tracked sources. This preserves the working credential while
new page prompts change the cache fingerprint. Each host startup confirms the
running application's original configuration has not changed. Workers must
never read the snapshot, user settings or protected secrets.
