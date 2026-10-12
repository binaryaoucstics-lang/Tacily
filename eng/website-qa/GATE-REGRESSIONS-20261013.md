# Cross-module gate regressions

This baseline includes all reviewed visibility/fact tasks. Its full repository
gate failed two existing tests; main has not been promoted from that failure.
Formal assessment remains fixed at 50%, with no personalized ratios or holdout
tuning. Original failures, captures and frozen source labels are retained.

1. `DeepSeekRequestMetricsTests.Page_Prompt_Is_Short_And_Preserves_Per_Node_Semantics`
   requires the existing `ONLY its own text`, `do not move or copy`, and
   `do not infer gender` constraints within the existing 80-120 word prompt budget.
   Preserve these constraints and the new decade/phase hints. Do not weaken tests,
   factual guards, ID ownership, cache validation, request budget or non-page prompts.
2. `page-auto-shadow.test.js` rejects all positive public text ranges whose test
   DOM provides x/y/width/height without left/right/top/bottom. The new tester removed
   the no-clipping acceptance path and compares undefined edges. Normalize ordinary
   rectangle representations conservatively, retaining positive no-clip ranges and
   valid partial clipping. Zero-area/invalid ranges and genuinely clipped or hidden
   text must remain excluded; public below-fold and closed-host readable light text
   remain admitted.
3. Independent QA geometry must handle the same ordinary rect representations using
   its own geometry implementation, never production eligibility. Emit finite x/y/
   width/height/source positions; invalid or unknown geometry cannot manufacture
   visible sources or successful predictions. Preserve clipping, composed slots,
   unknown retired observations and unchanged-source retraction evidence.

All work is offline. Each worker edits only owned paths, leaves changes uncommitted
and preserves sandbox/deadlines. Supervisor reviews real differences and reruns the
original validations and complete repository gates before main promotion. Fresh
Browser Use captures must follow the promoted source; old failed captures remain.
