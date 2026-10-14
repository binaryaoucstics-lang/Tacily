# Explicit independent screenshot outcomes

The requested goal includes actual screenshot confirmation that required text is
translated, in addition to 95% node accuracy/precision and 97% page accuracy over
at least 100 distinct real domains. Formal page judgment stays fixed50. Existing
producer completion flags cannot independently establish correct visible output.
All old tests, immutable captures/reviews, source labels and quality thresholds
remain unchanged. No holdout-based implementation tuning or label changes.

## Public opt-in interface

Normalizer and evaluator add keyword `require_visual_review=False` and CLI
`--require-visual-review`. False preserves legacy defaults and report behavior.
True requires explicit versioned visual evidence on every case and adds report
metadata `visualCoverage={schemaVersion:1,required:true}`. It never changes page
ratio, recognition labels or the definition of TP/TN/FP/FN. Final goal verification
must use this option; legacy machine-completion reports are not full visual proof.

The independent review file carries `visualReview`:

```json
{
  "schemaVersion": 1,
  "caseSha256": "the unchanged raw capture SHA256",
  "sourceReviewSha256": "the frozen source review SHA256",
  "reviewed": true,
  "independent": true,
  "nodes": [
    {
      "sourceId": "source-1",
      "sourceTextSha256": "SHA256 of exact original source UTF-8 text",
      "outcome": "correct_translation",
      "reviewed": true,
      "independent": true,
      "evidence": [{"phase": "stable-full", "note": "actual inspected text/geometry"}]
    }
  ]
}
```

Review-level sourceReviewSha256 must match the same nonblank 64-hex value in the
outer review. caseSha256 must match the actual original bytes (normalizer), then
the retained provenance hash (evaluator). Every and only every original sourceId
has one unique typed outcome row with exact text hash. Both implementations check
the full shape, schema/int/bool types, identities, hashes, phases and nonempty
notes. Evidence phases must exist among actually reviewed original screenshots;
Every visible outcome, including unchanged, needs an output phase. The accepted
output phases are after, after-full, stable, stable-full, scroll-after,
return-after, reflow-after, disclosure-open and disclosure-restored, plus
stable-refresh-N or stable-full-refresh-N where N is a nonzero decimal integer
without a leading zero. before, before-full and scroll-before alone do not
establish output review. A custom non-before phase is not output proof.
No inferred votes, missing rows or forged/current-source substitution.

Outcomes: `correct_translation`, `partial_translation`, `incorrect_translation`,
`unchanged`, `unnecessary_translation`, `not_visible`, `unknown`.

For an independently true node, correct_translation can provide visible credit
only together with existing actual live successful terminal/prediction/source
completion evidence. A vote cannot invent completed model work. Partial/incorrect
translations remain blocking and do not count as translatedPositiveNodes.
Unchanged true nodes are omissions in strict scope, and can be policy-skipped
only under the existing fully proven automatic-page negative zero-call policy.
Correct untranslated Chinese/metadata remain unchanged under their false labels.
Unnecessary_translation must correspond to a false label and is counted as an
observed false-positive outcome; it does not rewrite recognition predictions.
The user's 95% recognition accuracy/precision thresholds apply to these errors,
without adding a zero-false-positive completion requirement. not_visible/unknown outcomes remain
unresolved and blocking, never hidden completion or a relabelled true/false node.
Contradictory label/outcome combinations are invalid; unknown source labels stay
unknown and blocking regardless of a visual vote. Actual wrong translation on
true nodes, or false nodes changed by translation, cannot receive successful
visual credit. Retractions and every old error/source/screenshot/request gate
still apply independently.

Recognition confusion counts and accuracy/precision formulas stay unchanged.
Existing translated/untranslated/omission/policy-skip counts incorporate visual
confirmation only in required mode; retain original producer flags in provenance.
New counts `unknownVisualNodes`, `incorrectVisualTranslations` and
`unnecessaryVisualTranslations` make failures explicit. Zero-count gates for
unknown/incorrect required output and the existing 95% recognition gates apply;
do not add a zero-unnecessaryVisualTranslations gate. Keep normalizer and evaluator independently consistent, including
automatic zero-call policy and direct normalized-case scoring. A complete report
requires known correct visual outcomes, full original evidence and all old gates.

Original review files (including notes and crop votes) remain immutable. An
explicit new supplemental visual review is allowed only after actual image
inspection with labels preserved. The controller handles packaging crop votes
separately from the original-phase screenshot map; workers must not loosen the
existing screenshot schema for that convenience.

## Independent tasks and validation

Normalizer/own new tests, evaluator/own new tests, and website QA documentation
are separate tasks on one committed public baseline. Preserve all old fixtures,
default interfaces and thresholds. New fixtures are independently authored offline
sources/labels/visual votes: a successful machine terminal with visibly untranslated
text must fail, partial English/changed facts must fail, unknown visibility must
fail, hashes/source identity/time/labels remain independent, malformed/duplicate
votes fail, true page skip stays explicit, and correct true/false cases succeed.
Controller runs cross-component roundtrips and all full repository gates before
promotion. New artifacts and reports are exclusive; no real quality achievement
may be claimed from synthetic fixtures or partial reviewed samples.
