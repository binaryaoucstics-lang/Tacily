# Real zero-call assessment and reference-only source evidence

Baseline product judgment stays fixed50; no corpus/label/holdout/95/97/domain,
timeout, budget or sandbox changes. Main stays pinned at 8517302 while all three
formal capture processes run. This run only uses development diagnoses. Preserve
all original tests and immutable real failed evidence.

## Valid zero-call host assessment

Real development GMW capture at 3c3ec34 correctly skipped a Chinese-dominant page.
Raw localAssessment is an actual host `assessment` event: schemaVersion 1,
nonempty id, matching caseId, live true, needsTranslation false. The host's assess
action intentionally emits assessment rather than a translation terminal.
requestIds and terminals are empty; this is no model work, not a missing terminal
for an issued translation request. Latest automatic-page scorer wrongly requires
a terminal even for this real host assessment. Old missing-capability / bare
capability-without-terminal rejection tests must remain unchanged.

Extend only automatic-page normalization/scoring to accept either valid existing
successful zero-call terminals OR a real matching live negative host assessment
when translation requestIds and terminals are both empty and valid. Normalize
raw localAssessment into hostEvents without replacing or synthesizing anything.
Evaluator recognizes the assessment event as a separately validated event type;
other unknown event types stay invalid. Default strict legacy behavior remains
unchanged; explicit coverageScope metadata and new untranslated/skipped counters
remain unchanged. Invalid/missing ids, case mismatch, non-integer version,
non-live/positive/unknown assessment, missing or malformed request identity list,
nonempty requestIds without their successful terminals, failed/nonzero/malformed
terminals or contradictory page/independent labels cannot qualify.

Counts remain evidence-derived and all TP/TN/FP/FN formulas/gates remain. The
alternative proof cannot resolve unknown predictions, source ownership, failed
nodes, incomplete screenshots, unsettled work, errors, missing labels or
retractions. GMW nine English nodes stay independently true and untranslated;
old unknown history remains blocking. Synthetic cases need genuine fixture
assessment evidence to exercise the alternative; do not change earlier tests
that had only capabilities or missing terminal evidence.

Normalizer and evaluator use existing keyword/CLI coverage_scope modes. Explicit
automatic-page result metadata remains v1 because it now recognizes an existing
producer event; output files remain exclusive and original captures untouched.
Controller verifies real + synthetic roundtrip consistency after integration.

## Independent version references under original bounds

Formal Wiki site-021 is a development case. At baseline8517302, producer value
interning prevented earlier producer_observation_overflow, but after 98 actual
calls and four images the case failed independent_source_observation_overflow.
There were1844 source rows and zero observed retractions before interruption.
The adapter exports every latest source version plus versions referenced by
retained producer views. Reflow can exceed2048 even when necessary proof fits.

Optimize redundant UNREFERENCED exported versions only when needed to fit the
original2048-row/2,000,000-character limits. Preserve all required exact source
and represented-owner version references and actual membership/time evidence.
Keep original small-page export/old fixture behavior unchanged. Do not drop a
required witness, fabricate it from current positions, reset dropped counters,
increase limits, merge distinct source versions, silence overflow, infer missing
negative eligibility or borrow later hidden completion. The original full
sourceNodes history remains unchanged. If a necessary proof set itself exceeds
bounds, retain explicit overflow failure. Add large realistic movement/ref-only
regressions, atomic ownership, required-witness true overflow, forged versions,
stale pieces and unknown loss. Existing fixture1901 version count must pass.

## Four independent tasks

Version export (adapter/capture/new tests), normalizer assessment (own new tests),
evaluator assessment (own new tests) and three QA documents. No commits/merges/
pushes, other checkout modification, real model/browser/network/credentials or
holdout inspection by workers. Controller reviews diffs and reruns acceptance,
then sequential integration/full gates. Do not promote main or rebuild active
capture artifacts while current formal captures are running. Visits/offline
tests/new proof handling do not establish hundred-site or quality achievement.
