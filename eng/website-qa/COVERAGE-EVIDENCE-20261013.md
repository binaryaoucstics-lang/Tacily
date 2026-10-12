# Coverage and bounded evidence acceptance

Formal browser judgment remains fixed at 50 percent. Product settings remain
configurable. Frozen corpus, independent labels, original failures, all evidence,
95/97 quality gates, domain/holdout requirements and budgets remain unchanged.
This development run addresses public development cases only.

## Explicit coverage interface

Preserve the default `strict` behavior and all original tests unchanged.
Add keyword `coverage_scope="strict"` to normalize_case, evaluate and _score_case;
existing positional callers remain valid. Add CLI --coverage-scope choices strict
and automatic-page. Reject invalid modes. Results record coverageScope as
{"schemaVersion":1,"mode":"strict" or "automatic-page"}. Automatic evaluation
requires matching versioned case metadata; it must not silently reinterpret a
strict/legacy case. Default strict evaluation rejects automatic cases. Legacy
cases without metadata remain admissible only under strict.

Automatic-page changes coverage obligations only for independently confirmed
negative pages correctly skipped with zero model calls: expectedPageTranslation
false, independent/reviewed pageAnnotation false, actual pageDecision false,
valid zero requestCount and valid host terminal/event evidence. Normalization
must compute this after request accounting. Existing capture, screenshot,
settlement, provenance, errors, unknown, prediction and retraction validation
continues to block completion. No failed, contradictory or unknown prediction
can qualify a node for policy skip. Positive/wrongly skipped pages retain all
omissions. Nonzero calls, invalid counts/events or page label disagreement cannot
qualify. Never infer labels from predictions or manifest alone.

Keep every positive node label, TP/TN/FP/FN, accuracy/precision/recall and existing
annotatedPositiveNodes/translatedPositiveNodes/translationCoverage formulas.
Add integer unTranslatedPositiveNodes for all known positive nodes lacking proven
complete translation, and policySkippedPositiveNodes for the subset correctly
skipped under automatic-page. confirmedOmissions counts required omissions;
strict mode retains its old meaning. Expose new counts in case and aggregate
reports; do not remove original quality or unknown gates. No translated/TP credit
is invented for policy skips. Recompute counts from evidence, never trust a
caller-supplied summary. Save strict and automatic results in distinct new files.

GMW public case has 412 independently reviewed nodes, nine true English headlines
and FOLLOW, Chinese-dominant page label false, actual skip and zero model calls.
Those nine must remain labelled true, retain their recognition confusion-matrix
entries, and be visibly reported as untranslated/policy skipped when justified.

## Bounded historical producer evidence

Fresh Wiki capture at main 3c3ec34 failed producer_observation_overflow. Current
adapter retains redundant per-source pieces and all-source versions; 2048 rows
and 2,000,000 characters apply. Fix transport/retention accounting within those
limits, not by increasing limits, suppressing the error or treating lost evidence
as false eligibility/completion. Preserve source version/geometry/member owner,
actual scan idle, bounded timestamped producer observations and stale-piece
unknown safeguards. Demonstrate a realistic >1800 source page within original
bounds, movement/atomic memberships, true loss remaining unknown or explicit
failure, and forged/malformed/stale evidence rejection. Any necessary schema
change outside ownership must return blocked. Existing tests remain unchanged;
append adapter checks or add exclusively owned new test files.

## Tasks and acceptance

Four independent codex exec workers: bounded transport, normalization coverage,
evaluation coverage and documentation. No worker commits/merges/pushes, other
checkouts, real browser/model/network, settings/credential/corpus modification.
Main stays fixed while fresh capture runs. Controller reviews actual diffs and
reruns original plus added suites, approves task commits, merges sequentially,
then full repository gates before main promotion. Offline tests are not real
website results. No hundred-site or quality achievement claim without real
capture, frozen source labels, screenshot inspection and complete score report.
