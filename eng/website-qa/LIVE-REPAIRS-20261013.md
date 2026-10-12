# Live development failures: calendar words, Chinese headlines and evidence

The original 100-domain, screenshot, no-retraction and 95%/97% goal remains
unchanged. Official evaluation always uses the explicit fixed 50 percent
threshold. Personalized settings and holdout outcomes are not tuning inputs.
All real evidence is external and immutable; these small fixtures are offline
regression inputs and never count as visited or translated domains.

## Verified observations

The main build c127d8fde4ba4e7cd0cc239bcda7c954e301aa75 passed every repository
gate. Its fresh Browser Use development replay used the previously verified
official DeepSeek preset. Real successful terminals prove that credential works.

- Wikipedia's first three batches succeeded. A later terminal reported seven
  requests and fifteen failed nodes, all in the paragraph beginning “The origins
  of machine translation…”. Its source includes “a ninth-century Arabic” and
  “17th century. In 1629”. An offline production-guard probe verifies that correct
  `ninth-century` -> `9世纪` and `9th-century` -> `9世纪` are rejected. The normal
  `9th century` spelling is accepted. Wrong centuries must remain rejected.
- Chinese news portals are readable Chinese before translation. Their summaries
  classified thousands of readable Han headline letters as non-Chinese: one
  Xinhua summary had 636 Chinese prose letters and 3394 non-Chinese letters,
  although the full page had 3924 Han letters and 108 Latin letters. Lack of
  paragraph punctuation/connective words is not evidence of a foreign language.
  Preserve Japanese Han/kana distinction, body/title/navigation/code exclusions,
  the foreign-majority/small-Chinese case and the exact fixed-50 boundary.
- The recorded Xinhua message terminals had zero model requests. Native candidate
  or assessment messages are not cloud translation requests. The collector must
  distinguish actual terminal request counts, genuine pending work and dynamic
  negative-page decision stability. Do not turn unknown/missing terminals into 0.
- A backend error packet currently causes collection to stop before its failed
  terminal, final node flags and translated/error screenshots are recorded. This
  loses useful failure evidence. Keep the original error and return failure;
  collect the actual terminal and final state within the existing case deadline.

## Independent task boundaries

1. Calendar guard: PageTextSemanticGuard.cs, its existing Core tests and a new
   dedicated integration regression file. Normalize ordinary written English
   century ordinals and adjectival hyphens to the existing typed century fact.
   Correct equivalents pass; changed phase/century, dropped facts and cross-ID
   transfers remain unsafe. HasProtectedFacts must recognize these sources
   before display. Test the public inline paragraph with a deterministic provider;
   preserve factual staging, cache/retry bounds and unsafe-prefix rejection.
2. Chinese headline recognition: Core PageTranslationDecision.cs and its tests,
   extension page-auto-policy.js and its tests. Keep the same wire/statistics and
   setting interfaces. Readable Chinese main-body headlines without prose grammar
   must contribute Chinese evidence rather than inflate foreign letters. Use
   generic language/context behavior; no site-name overrides or all-Han veto.
   Desktop and extension decisions must agree, including Japanese context and
   minority Chinese on foreign-dominant pages. Do not edit the content renderer.
3. Capture evidence: capture.py, test_capture.py and its README. Preserve exact
   production packets, RPC association, source observations, completion flags,
   every error/failed terminal and original attempt. After packet failure, drain
   the actual terminal and retain a final production snapshot plus available
   screenshot phases without accepting success. Respect the unchanged deadline,
   UTF-16 limits and Browser Use wrapper. Negative zero-model assessment is
   allowed; actual cloud calls, missing terminals and unresolved work are not.

All public interfaces above already exist at the shared baseline. No task needs
another task's modifications to compile. Workers only edit their owned paths,
use no real browser/model/network or credentials, and never commit/merge/push.
Sandbox testhost denial is reported truthfully; the controller reruns the same
original commands and retains that evidence. Full repository gates precede main
promotion. Fresh live replay must verify the repaired behavior before the formal
130-domain corpus starts; source-only visits or mock tests never prove the goal.
