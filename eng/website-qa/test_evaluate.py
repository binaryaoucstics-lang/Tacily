"""Offline synthetic regression fixtures: these are NOT measured website runs."""

import base64
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
import contextlib
import io

from evaluate import evaluate, main, url_belongs, validate_sites, _at_least


ROOT = Path(__file__).resolve().parents[2]
PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")


def manifest_fixture():
    # Reserved .test domains and 1px images intentionally test the score logic.
    # They never appear in the fixed real-site corpus or represent real evidence.
    return {"schemaVersion": 1, "sites": [
        {"id": f"case-{i:03}", "registeredDomain": f"site{i:03}.test",
         "url": f"https://www.site{i:03}.test/article", "category": "knowledge",
         "language": "en" if i % 2 == 0 else "zh-Hans",
         "expectedPageTranslation": i % 2 == 0,
         "split": "holdout" if i < 40 else "development"} for i in range(120)]}


def case_fixture(site):
    positive = site["expectedPageTranslation"]
    result = deepcopy(site)
    result.update(finalUrl=site["url"], title="Synthetic fixture",
                  viewport={"width": 1280, "height": 720}, sourceHead="a" * 40,
                  capturedAt="2026-10-11T00:00:00Z", visitSuccess=True,
                  liveTranslation=positive, completed=True, pageDecision=positive,
                  requestCount=int(positive), errors=[], blockedReason="",
                  sourceNodes=[{"id": "n1", "text": "Fixture source", "tag": "p", "role": "paragraph",
                                "position": {"x": 0, "y": 0, "width": 100, "height": 20}}],
                  annotations=[{"nodeId": "n1", "needTranslation": positive,
                                "reviewed": True, "independent": True}],
                  predictions=[{"nodeId": "n1", "eligible": positive,
                                "completed": positive, "failed": False}],
                  pageAnnotation={"needTranslation": positive, "reviewed": True, "independent": True},
                  retractions=[],
                  screenshots={key: {"path": "fixture.png", "reviewed": True}
                               for key in ("before", "after", "stable")},
                  hostEvents=[])
    result["screenshots"]["checks"] = dict.fromkeys(
        ("firstViewport", "mainContent", "afterScroll", "layout", "dynamicReturn"), True)
    if positive:
        result["hostEvents"] = [
            {"id": site["id"] + "-cap", "caseId": site["id"], "type": "capabilities", "live": True},
            {"id": site["id"] + "-run", "caseId": site["id"], "type": "terminal",
             "success": True, "requestCount": 1, "cacheHits": 0, "failedCount": 0}]
    return result


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        # Keep all generated fixture files inside this checkout's ignored area.
        self.temp = tempfile.TemporaryDirectory(prefix="corpus-metrics-tests-", dir=ROOT / ".parallel")
        self.addCleanup(self.temp.cleanup)
        self.evidence = Path(self.temp.name)
        (self.evidence / "fixture.png").write_bytes(PNG)
        self.manifest = manifest_fixture()
        self.cases = [case_fixture(s) for s in self.manifest["sites"]]

    def report(self, cases=None):
        return evaluate(self.manifest, self.cases if cases is None else cases, self.evidence)

    def test_fixed_corpus_is_large_diverse_and_explicitly_unvisited(self):
        real = json.loads(Path(__file__).with_name("sites.json").read_text(encoding="utf-8"))
        sites, corpus = validate_sites(real)
        self.assertTrue(corpus["requirementsMet"])
        self.assertEqual(130, corpus["independentDomains"])
        self.assertEqual(40, corpus["negativePages"])
        self.assertEqual(44, corpus["holdoutDomains"])
        self.assertEqual("provisional-unvisited", real["labelStatus"])
        self.assertTrue(all(s["annotationStatus"] == "provisional-unvisited" for s in sites))
        self.assertTrue({"news", "knowledge", "public-service", "technology", "travel", "product", "multilingual", "non-prose"}
                        <= {s["category"] for s in sites})
        self.assertTrue({"zh-Hans", "zh-Hant", "en", "ja", "ko", "de", "fr", "es", "ar", "pt"}
                        <= {s["language"] for s in sites})
        self.assertTrue(all(s["language"].startswith("zh-") for s in sites if not s["expectedPageTranslation"]))
        self.assertFalse(evaluate(real, [], self.evidence)["achieved"])

    def test_complete_synthetic_fixtures_pass_logic_only(self):
        r = self.report()
        self.assertTrue(r["achieved"])
        self.assertEqual(120, r["groups"]["overall"]["completedDomains"])
        for split, total in (("overall", 120), ("holdout", 40), ("development", 80)):
            g = r["groups"][split]
            self.assertEqual(total, g["pageTotal"])
            self.assertEqual(total // 2, g["TP"])
            self.assertEqual(total // 2, g["TN"])
            self.assertEqual(1, g["accuracy"])
            self.assertEqual(1, g["translationCoverage"])

    def test_minimum_100_completed_domains(self):
        self.assertTrue(self.report(self.cases[:100])["achieved"])
        self.assertFalse(self.report(self.cases[:99])["achieved"])

    def test_holdout_must_complete_and_is_not_hidden_by_overall(self):
        selected = self.cases[:20] + self.cases[40:]
        r = self.report(selected)
        self.assertEqual(100, r["groups"]["overall"]["completedDomains"])
        self.assertFalse(r["gates"]["completedHoldoutDomains"])
        self.assertFalse(r["achieved"])

    def test_duplicate_subdomain_attempts_cannot_increase_domain_count(self):
        extra = deepcopy(self.cases[0])
        extra["finalUrl"] = "https://other.site000.test/another"
        r = self.report(self.cases + [extra])
        self.assertEqual(119, r["groups"]["overall"]["completedDomains"])
        self.assertEqual(["duplicate_registered_domain_attempts"], r["cases"][0]["reasons"])
        self.assertFalse(r["cases"][-1]["completed"])

    def test_subdomain_cannot_be_declared_as_an_independent_domain(self):
        self.manifest["sites"][1]["registeredDomain"] = "other.site000.test"
        self.manifest["sites"][1]["url"] = "https://other.site000.test/"
        with self.assertRaisesRegex(ValueError, "subdomains twice"):
            self.report()

    def test_cross_domain_redirect_is_rejected(self):
        for url in ("https://site000.test.evil.test/", "https://notsite000.test/", "https://site001.test/"):
            with self.subTest(url=url):
                self.cases[0]["finalUrl"] = url
                r = self.report()
                self.assertFalse(r["cases"][0]["completed"])
                self.assertEqual(59, r["groups"]["overall"]["TP"])

    def test_url_matching_supports_multilabel_domains_and_rejects_bad_urls(self):
        self.assertTrue(url_belongs("https://news.bbc.co.uk/x", "bbc.co.uk"))
        self.assertFalse(url_belongs("https://bbc.co.uk.evil.test/", "bbc.co.uk"))
        for url in (None, "file:///site000.test", "https://u:p@site000.test/", "https://site000.test:bad/"):
            self.assertFalse(url_belongs(url, "site000.test"))

    def test_missing_or_unreviewed_screenshot_does_not_count(self):
        for item in ({"path": "absent.png", "reviewed": True}, {"path": "fixture.png", "reviewed": False}, "fixture.png"):
            with self.subTest(item=item):
                self.cases[0]["screenshots"]["after"] = item
                r = self.report(self.cases[:100])
                self.assertFalse(r["achieved"])
                self.assertEqual(99, r["groups"]["overall"]["completedDomains"])

    def test_screenshot_placeholder_and_path_escape_are_rejected(self):
        (self.evidence / "placeholder.png").write_text("fake screenshot", encoding="utf-8")
        for path in ("placeholder.png", "../outside.png"):
            self.cases[0]["screenshots"]["before"]["path"] = path
            self.assertFalse(self.report()["cases"][0]["evidenceAccepted"])

    def test_missing_scroll_and_relayout_review_blocks_completion(self):
        self.cases[0]["screenshots"]["checks"]["dynamicReturn"] = False
        self.assertFalse(self.report()["cases"][0]["completed"])

    def test_simulated_host_cannot_supply_true_positives(self):
        self.cases[0]["hostEvents"][0]["live"] = False
        r = self.report()
        self.assertEqual(59, r["groups"]["overall"]["TP"])
        self.assertFalse(r["cases"][0]["completed"])
        self.cases[0]["hostEvents"][0]["live"] = True
        self.cases[0]["synthetic"] = True
        self.assertFalse(self.report()["cases"][0]["evidenceAccepted"])

    def test_positive_requires_real_requests_and_successful_terminal(self):
        for change in ("no_events", "no_terminal", "unsuccessful", "no_request", "not_live"):
            with self.subTest(change=change):
                c = case_fixture(self.manifest["sites"][0])
                if change == "no_events": c["hostEvents"] = []
                if change == "no_terminal": c["hostEvents"] = c["hostEvents"][:1]
                if change == "unsuccessful": c["hostEvents"][1]["success"] = False
                if change == "no_request":
                    c["requestCount"] = c["hostEvents"][1]["requestCount"] = 0
                if change == "not_live": c["liveTranslation"] = False
                r = self.report([c])
                self.assertEqual(0, r["groups"]["overall"]["completedDomains"])
                self.assertFalse(r["achieved"])

    def test_host_event_cannot_be_reused_from_another_case(self):
        self.cases[0]["hostEvents"][1]["caseId"] = "case-002"
        self.assertFalse(self.report()["cases"][0]["evidenceAccepted"])

    def test_terminal_counts_must_agree_and_cannot_be_duplicated(self):
        self.cases[0]["requestCount"] = 2
        self.assertFalse(self.report()["cases"][0]["evidenceAccepted"])
        self.cases[0]["requestCount"] = 1
        self.cases[0]["hostEvents"].append(deepcopy(self.cases[0]["hostEvents"][1]))
        self.assertFalse(self.report()["cases"][0]["evidenceAccepted"])

    def test_unknown_annotations_do_not_become_true_negatives(self):
        self.cases[1]["annotations"][0]["needTranslation"] = "unknown"
        r = self.report()
        self.assertEqual(59, r["groups"]["overall"]["TN"])
        self.assertEqual(1, r["groups"]["overall"]["unknownNodes"])
        self.assertFalse(r["achieved"])
        self.assertFalse(r["cases"][1]["completed"])

    def test_truth_must_be_independent_of_prediction(self):
        self.cases[0]["annotations"][0]["independent"] = False
        self.cases[1]["pageAnnotation"]["reviewed"] = False
        r = self.report()
        self.assertEqual(1, r["groups"]["overall"]["unknownNodes"])
        self.assertEqual(1, r["groups"]["overall"]["unknownPages"])
        self.assertFalse(r["achieved"])

    def test_manifest_labels_cannot_be_used_as_reviewed_page_truth(self):
        self.cases[0].pop("pageAnnotation")
        r = self.report([self.cases[0]])
        self.assertEqual(0, r["groups"]["overall"]["pageTotal"])
        self.assertIsNone(r["groups"]["overall"]["pageAccuracy"])
        self.assertFalse(r["achieved"])

    def test_changed_initial_label_requires_explicit_manifest_revision(self):
        self.cases[0]["pageAnnotation"]["needTranslation"] = False
        self.assertFalse(self.report()["cases"][0]["evidenceAccepted"])

    def test_negative_page_with_unnecessary_request_fails_even_when_page_decision_correct(self):
        self.cases[1]["requestCount"] = 1
        r = self.report()
        self.assertEqual(1, r["groups"]["overall"]["unnecessaryRequests"])
        self.assertEqual(1, r["groups"]["overall"]["pageAccuracy"])
        self.assertFalse(r["achieved"])

    def test_page_accuracy_exactly_97_percent_passes_and_96_fails(self):
        for i in (41, 43, 45):
            self.cases[i]["pageDecision"] = True
        r = self.report(self.cases[:100])
        self.assertEqual(.97, r["groups"]["overall"]["pageAccuracy"])
        self.assertTrue(r["achieved"])
        self.cases[47]["pageDecision"] = True
        r = self.report(self.cases[:100])
        self.assertEqual(.96, r["groups"]["overall"]["pageAccuracy"])
        self.assertFalse(r["achieved"])

    def test_holdout_quality_gate_is_independent(self):
        self.cases[1]["pageDecision"] = self.cases[3]["pageDecision"] = True
        r = self.report()
        self.assertTrue(r["gates"]["overall"]["pageAccuracy"])
        self.assertFalse(r["gates"]["holdout"]["pageAccuracy"])
        self.assertFalse(r["achieved"])

    def test_node_95_percent_exact_boundaries_and_zero_denominators(self):
        self.assertTrue(_at_least(95, 100, "0.95"))
        self.assertFalse(_at_least(94, 100, "0.95"))
        self.assertFalse(_at_least(0, 0, "0.95"))

    def test_all_skipped_pages_cannot_get_high_scores(self):
        for c in self.cases:
            c["pageDecision"] = False
            c["predictions"][0].update(eligible=False, completed=False)
        r = self.report()
        self.assertEqual(0, r["groups"]["overall"]["TP"])
        self.assertEqual(60, r["groups"]["overall"]["FN"])
        self.assertEqual(.5, r["groups"]["overall"]["accuracy"])
        self.assertIsNone(r["groups"]["overall"]["precision"])
        self.assertEqual(0, r["groups"]["overall"]["translationCoverage"])
        self.assertFalse(r["achieved"])

    def test_only_negative_pages_and_empty_run_have_undefined_precision(self):
        for cases in ([], [c for c in self.cases if not c["expectedPageTranslation"]]):
            r = self.report(cases)
            self.assertIsNone(r["groups"]["overall"]["precision"])
            self.assertFalse(r["achieved"])

    def test_positive_eligible_but_not_completed_is_an_omission(self):
        self.cases[0]["predictions"][0]["completed"] = False
        r = self.report()
        self.assertEqual(60, r["groups"]["overall"]["TP"])
        self.assertEqual(1, r["groups"]["overall"]["confirmedOmissions"])
        self.assertLess(r["groups"]["overall"]["translationCoverage"], 1)
        self.assertFalse(r["achieved"])

    def test_confirmed_retraction_still_fails_with_119_other_completed_domains(self):
        self.cases[0]["retractions"] = [dict(nodeId="n1", reviewed=True, confirmed=True, sourceUnchanged=True)]
        r = self.report()
        self.assertEqual(119, r["groups"]["overall"]["completedDomains"])
        self.assertEqual(1, r["groups"]["overall"]["confirmedRetractions"])
        self.assertFalse(r["achieved"])

    def test_retraction_on_unsuccessful_terminal_is_not_hidden(self):
        self.cases[0]["retractions"] = [dict(nodeId="n1", reviewed=True, confirmed=True, sourceUnchanged=True)]
        self.cases[0]["hostEvents"][1]["success"] = False
        r = self.report()
        self.assertEqual(1, r["groups"]["overall"]["confirmedRetractions"])
        self.assertFalse(r["achieved"])

    def test_source_change_is_not_a_confirmed_retraction_but_unreviewed_is_incomplete(self):
        self.cases[0]["retractions"] = [dict(nodeId="n1", reviewed=True, confirmed=True, sourceUnchanged=False)]
        self.assertEqual(0, self.report()["groups"]["overall"]["confirmedRetractions"])
        self.cases[0]["retractions"][0]["reviewed"] = False
        self.assertFalse(self.report()["cases"][0]["completed"])

    def test_missing_prediction_for_positive_node_is_fn_and_cannot_complete(self):
        self.cases[0]["predictions"] = []
        r = self.report()
        self.assertEqual(1, r["groups"]["overall"]["FN"])
        self.assertEqual(1, r["groups"]["overall"]["unknownPredictions"])
        self.assertFalse(r["achieved"])

    def test_invalid_boolean_prediction_does_not_create_a_true_negative(self):
        self.cases[1]["predictions"][0]["eligible"] = "false"
        r = self.report()
        self.assertEqual(59, r["groups"]["overall"]["TN"])
        self.assertFalse(r["achieved"])

    def test_node_confusion_counts_fp_fn_and_false_completions(self):
        self.cases[0]["predictions"][0].update(eligible=False, completed=False)
        self.cases[1]["predictions"][0]["eligible"] = True
        r = self.report()
        self.assertEqual((59, 59, 1, 1), tuple(r["groups"]["overall"][k] for k in ("TP", "TN", "FP", "FN")))
        self.cases[0]["predictions"][0]["completed"] = True
        self.assertIn("contradictory_node_completion:n1", self.report()["cases"][0]["reasons"])

    def test_duplicate_and_unmatched_node_records_are_rejected(self):
        for key in ("sourceNodes", "annotations", "predictions"):
            c = deepcopy(self.cases[0])
            c[key].append(deepcopy(c[key][0]))
            self.assertFalse(self.report([c])["cases"][0]["completed"])
        self.cases[0]["annotations"][0]["nodeId"] = "model-invented-node"
        self.assertFalse(self.report()["cases"][0]["completed"])

    def test_no_node_candidate_evidence_cannot_complete(self):
        self.cases[0]["sourceNodes"] = []
        self.assertFalse(self.report()["cases"][0]["completed"])

    def test_unknown_case_and_modified_split_are_rejected(self):
        extra = deepcopy(self.cases[0]); extra["id"] = "not-in-manifest"
        r = self.report([extra])
        self.assertEqual(0, r["groups"]["overall"]["completedDomains"])
        self.cases[0]["split"] = "development"
        self.assertFalse(self.report()["cases"][0]["completed"])

    def test_invalid_capture_metadata_and_reported_errors_block_completion(self):
        for key, value in (("sourceHead", "unknown"), ("capturedAt", "2026-10-11T00:00:00"),
                           ("errors", ["provider-error"]), ("blockedReason", "login"), ("completed", False)):
            c = deepcopy(self.cases[0]); c[key] = value
            self.assertFalse(self.report([c])["cases"][0]["completed"])

    def test_cli_reports_nonzero_for_unachieved_and_invalid_inputs(self):
        manifest_path = self.evidence / "sites.json"
        cases_path = self.evidence / "cases.json"
        manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        cases_path.write_text(json.dumps({"schemaVersion": 1, "cases": []}), encoding="utf-8")
        args = ["--sites", str(manifest_path), "--cases", str(cases_path), "--evidence-root", str(self.evidence)]
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(1, main(args))
        self.assertFalse(json.loads(output.getvalue())["achieved"])
        cases_path.write_text('{"schemaVersion": 2, "cases": []}', encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(2, main(args))
        self.assertFalse(json.loads(output.getvalue())["achieved"])
        cases_path.write_text(json.dumps(self.cases), encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, main(args))
        self.assertTrue(json.loads(output.getvalue())["achieved"])


if __name__ == "__main__":
    unittest.main()
