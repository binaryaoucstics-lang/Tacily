# Chinese prose share setting and fixed QA baseline

The desktop setting `BrowserChineseProseShareThresholdPercent` controls automatic
page admission when the target language is Chinese. It is a percentage from 10
to 90, defaults to 50, and is clamped during settings migration. Missing values
in older settings therefore keep the 50 percent default.

The share is calculated from readable prose letters across the page:

```text
Chinese share = identified Chinese prose letters
                / (identified Chinese prose letters + non-Chinese prose letters)
```

At or above the configured threshold, automatic page translation is skipped.
Below it, normal language judgement continues. Individual Chinese text blocks
remain eligible for the existing per-block target-language filter, so a page
that passes the page-level threshold does not translate its already-readable
Chinese passages. The setting applies only when Chinese is the target language.

The native `pageAutoConfig` message carries
`chineseProseShareThresholdPercent` (10 through 90, default 50). Candidate
`pageSummary` data may carry `nonChineseProseCharacters`; older extensions that
omit it continue to work from the existing Latin and kana statistics and the
bounded source samples.

## Evaluation rule

Official website judgement and accuracy runs, including BrowserPageQa and
PageRulesBench, always use
`PageTranslationDecision.DefaultChineseProseShareThresholdPercent` (50),
independent of the installed user's setting. The BrowserPageQa harness passes
that fixed value explicitly. Do not load or apply a user's personalized
threshold while measuring website coverage, node precision, or page accuracy.
Unit tests for the setting may exercise other values to verify the control, but
the benchmark fixture and acceptance criteria remain fixed at 50 percent.
