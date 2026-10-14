# Source epochs, Chinese terms and honest failure diagnostics

The user requires at least 100 distinct real domains, source labels frozen before
output inspection, all screenshots reviewed, no unchanged-source withdrawal,
node accuracy/precision >=95% and page accuracy >=97%. Formal judgment stays at
fixed50. This round uses only development observations; no holdout tuning, corpus
replacement, relabelling, unknown suppression, widened budgets or dropped tests.
All 124 first-run capture attempts remain immutable, including every failure.

## Exact source transitions

A public MIT development diagnostic replay observed the completed `Browse`
translation, followed by real childList replacement at the nested span:
`浏览 -> Browse -> Search news`, then `Search news -> Browse`. The original Text
node became detached. Existing adapter evidence reuses its path/source identity
on the later Browse return, while its source-change detection only matches a
mutation target exactly equal to groupElement. The target is a nested SPAN and
the group is an A. Preserve real source changes, including removed-node and final
DOM evidence, before evaluating later withdrawals. Do not clear candidates for
an unchanged same-source text-node replacement or whole-group movement. A changed
inline factual neighbour invalidates the corresponding prior group. Do not infer
an actual source change from layout, a hash collision or disappearance alone.
Exact source/owner/member/time witnesses and old limits remain mandatory.

Do not retrospectively rewrite the earlier captures. All new output must have
explicit provenance. Counterexamples must prove genuine unchanged-source
withdrawals are still detected; cases with insufficient evidence remain unknown.

## Readable Chinese node boundaries

Development CCTV source inspection independently labelled all 227 source nodes
false. The four current false positives are CCTV.直播, CCTV-5+, 关于CCTV.com, and
readable Chinese news prose containing AI, OpenAI and Hilbert. Recognize ordinary
embedded terms and literal identifiers without treating meaningful English
sentences as already Chinese. Keep C# and JavaScript behavior consistent with
their existing public signatures and exact Unicode handling. Use general rules
and varied independent counterexamples, not hostname-specific branches, corpus
lookups or a growing list of site phrases. Foreign Han context, actual embedded
foreign instructions/prose, mathematical/scientific facts, explicit selection
and all old positive/negative tests must remain meaningful. Page ratios and
decision thresholds remain unchanged.

## Native failure diagnostics

Several development pages ended with translation_incomplete. The existing QA
host reports no difference between a malformed provider frame, a semantic guard
rejection/retry, exhausted retry budget or service result mismatch. Preserve
those failures, successful groups, the existing guard and all request limits.
Add bounded, opt-in QA diagnostics to stderr for actual work: fixed reason/phase
codes and integer counters, correlated to the existing host request/case identity.
Default JSONL protocol and normal runtime behavior remain unchanged. No model
response bodies, source prose, settings, endpoints, exception text or credentials
may be logged. The existing production service diagnostic Action<string> can
emit safe counts/codes to support this; preserve existing constructor signatures.

The CLI host accepts a leading --diagnostics flag with existing live preset
arguments. Its constructor may accept an optional diagnostic writer. Plain
execution and --self-test stay compatible. Tests must verify observed failure
categories, opt-in/default separation, concurrent work attribution, bounded
output and no accidental secret/source disclosure. Do not invent successful
terminal events or modify normalizer/evaluator quality gates.

## Three exclusive tasks

1. Source-change evidence: adapter/capture and new/append evidence tests.
2. Chinese boundary parity: C# and JavaScript eligibility policy plus new tests.
3. Native diagnosis: existing browser translation service diagnostic emissions,
   QA host/program/self-test and QA README. No source, truth or quality claims.

Controller reads actual differences, reruns acceptance, approves commits,
sequentially integrates and executes full repository gates before main promotion.
Workers use the repository scheduler, independent Worktrees/branches and explicit
gpt-6.1-sol/high/fast(priority), without commit/merge/push, extra write roots,
network access or sandbox/approval changes.
