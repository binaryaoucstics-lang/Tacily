# Fixed-50 development repair contract

Baseline is the promoted live-repair release. Formal judgment stays at explicit
50%; custom product ratio, fixed corpus and holdout labels are unchanged.
No new public wire API is required. Sources and sourceFacts remain untrusted data.

## Verified development evidence

- NASA secondary navigation is visibly rendered in screenshots and Browser Use
  state. An ancestor has HTML hidden=true while computed display=block and positive
  geometry. The production extractor unconditionally rejects hidden ancestors.
  Fix the HTML/CSS rendering rule generically; preserve true nonrendering hidden
  content, hidden-until-found, closed details, editable/code and other exclusions.
- Independent QA geometry currently regards offscreen carousel ranges as rendered,
  ignoring overflow clipping. Source inspection found a range at x=-2439 within
  a 500px overflow:hidden ancestor at x=2060.5. Screen-reader-only/sr-only 1px
  clipped labels also enter the corpus. Correct independent visual source geometry
  without consulting production eligibility or predictions. Preserve below-fold
  prose, actual partly visible text, shadow/slot traversal, retraction detection,
  history of genuinely observed source nodes and original failed evidence.
- Live Wikipedia now passes ninth-century but rejects the later decade/group.
  A direct current-production provider replay returns:
  source `1990s and early 2000s`, translated `20世纪90年代至21世纪初`.
  Guard rejects because the explicit decade and phase become a broader century.
  Source ` translates roughly enough text to fill 1 million books in one day.`
  translates to `一天内翻译的文本量大约足以填满100万本书。` and already passes.
  Do not weaken factual validation or invent successful output. Improve bounded
  source-fact guidance and test the original production request/retry/cache path.
- CCTV negative page makes zero cloud calls, but pending extraction candidates
  contain channel identifiers, Chinese IPC/PAC/CR450 prose and ICP metadata. Improve
  per-node eligibility conservatively and generically, preserving meaningful
  foreign commands, proper names in prose, Japanese context and explicit selection.
  Do not broadly reject title-case names, all abbreviations or short English.
- Normalizer still calls any native candidate on a negative page an unnecessary
  model request. Capture now correctly uses complete original terminal requestCount.
  Align normalization with that evidence, retaining failure on missing/invalid/
  failed terminal, real model request, retraction, error and unresolved work.

## Acceptance

Each task changes only owned paths in its checkout, no commit/merge/push, live
browser/model/network, credentials/settings, other workspace or sandbox change.
Original deadlines, bounded retries, guards, fixed labels/corpus and completion
gates remain. Offline fixtures cannot prove real quality. Supervisor must inspect
diffs, rerun acceptance and full repository gates; fresh Browser Use replay follows.
Keep all old captures and frozen labels. Do not retroactively repair metrics by
changing old source labels or deleting failures; new geometry needs a fresh run.
