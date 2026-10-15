# Output stability repair contract

Base: reviewed and validated integration `c9aeb85746b1fe162122a2a03f5b3c92da1a67dc`, promoted to main.

## Observed development regressions

- A long English university paragraph was marked complete while its Chinese translation retained the ordinary final noun `offerings`. This is an example, not a vocabulary or site whitelist. Repair ordinary untranslated prose generically, with source-aware handling of names, acronyms, technical terms, code, paths, URLs and non-Chinese targets. Strengthen the page prompt and use existing bounded retries where appropriate. Validate cached and newly generated output consistently; do not bypass fact checks or claim machine completion proves visible correctness.
- Wikipedia and MIT produced 14 and 1 unchanged-source DOM retraction candidates respectively during the first full-page output screenshot. Both final full screenshots already show restored Chinese. The existing evidence does not establish whether intermediate English was painted. These are unresolved candidates, not certified visible withdrawals. The later explicit reflow cannot explain their earlier timestamps. Investigate production regrouping, source membership and writers; preserve translations only with exact unchanged authored source proof.
- The QA adapter currently implements `schedule(callback)` and discards requested scan delays. Restore parity with production `schedule(callback, delay)`. Record screenshot lifecycle sufficiently to distinguish screenshot-time changes from later scroll/reflow, preserving raw observations and honest unknowns.

## Independent file owners

1. `translation-completeness`: page prompt, page translation service, a new completeness helper and new Core/Integration regression files.
2. `runtime-retention`: production page content session and a new independent extension regression file.
3. `capture-lifecycle`: QA adapter/capture, appended adapter checks and new focused Python regressions/documentation.

No cross-owner API change is required. Existing browser protocol, settings, evidence v1 fields, source truth and ownership semantics remain authoritative. New helper APIs are internal to their owner. Report a needed cross-owner interface before changing it.

## Acceptance invariants

- No site/hostname/fixture-specific production rules. No blanket Latin-script rejection, guessed translations, silent deletion of ordinary words or extra unbounded provider calls. Preserve names, acronyms, legitimate technical/literal terms and other target languages.
- Every translation still obeys original IDs, source ranges, group/member identity, immutable original source, numeric/calendar facts, cancellation, total request limits, concurrency and deadlines. Cached candidates must satisfy current checks before publication. Do not persist invalid output.
- No restoring unchanged completed translations during layout-only scans, whole-group same-source replacement or safe regrouping. Changed authored group facts, slot reassignment, source removal, explicit original/pause and stale streams must retain their existing protections.
- QA must pass requested delays faithfully, preserve all raw retractions and capture before/after chronology. Additive diagnostics must be bounded and must not turn a DOM transition into a claim of painted output.
- Preserve all original tests unchanged. Add varied positive and negative examples. Existing 2048-row/2,000,000-character proof bounds, original 1900-node five-second regression, unknown/error handling and fixed 50% formal judgment remain.
- Test only relevant components during task development. Controller reviews actual diffs, replays affected real pages using Browser Use CLI and the previously working saved DeepSeek preset, then runs the required full Release/.NET/extension/scheduler gates once at the new combined HEAD before promotion. Prior exact-HEAD validation remains valid for prior promotion.
- Workers have no network, provider/real-browser, user settings/credential, holdout or other worktree access, and cannot commit, merge or push. Preserve prior evidence; use new ignored logs and capture attempts.
