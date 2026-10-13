"""Independent offline fixtures only; no measured website or quality claims."""

import base64
from copy import deepcopy
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from evaluate import evaluate, _score_case, main


ROOT = Path(__file__).resolve().parents[2]
PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")


def site_fixture(index=1, positive=False, split="development"):
    return {"id": f"coverage-{index}", "registeredDomain": f"coverage{index}.test",
            "url": f"https://coverage{index}.test/", "category": "news",
            "language": "en" if positive else "zh-Hans",
            "expectedPageTranslation": positive, "split": split}


def case_fixture(site, node_count=1, positive_count=None, scope="automatic-page"):
    """Build the normalized evidence interface independently of normalize.py."""
    positive = site["expectedPageTranslation"]
    positive_count = int(positive) if positive_count is None else positive_count
    result = deepcopy(site)
    result.update(
        title="Offline coverage fixture", finalUrl=site["url"],
        viewport={"width": 1280, "height": 720}, sourceHead="6" * 40,
        capturedAt="2026-10-13T00:00:00Z", visitSuccess=True,
        completed=True, pageDecision=positive, liveTranslation=positive,
        requestCount=int(positive), errors=[], blockedReason="",
        pageAnnotation={"needTranslation": positive, "reviewed": True, "independent": True},
        sourceNodes=[], annotations=[], predictions=[], retractions=[],
        screenshots={phase: {"path": "fixture.png", "reviewed": True}
                     for phase in ("before", "after", "stable")},
        hostEvents=[{"id": site["id"] + "-terminal", "caseId": site["id"],
                     "type": "terminal", "success": True, "requestCount": int(positive),
                     "cacheHits": 0, "failedCount": 0}])
    result["screenshots"]["checks"] = dict.fromkeys(
        ("firstViewport", "mainContent", "afterScroll", "layout", "dynamicReturn"), True)
    if positive:
        result["hostEvents"].insert(0, {"id": site["id"] + "-capabilities",
            "caseId": site["id"], "type": "capabilities", "live": True})
    else:
        result["hostEvents"].append({"id": site["id"] + "-capabilities",
            "caseId": site["id"], "type": "capabilities", "live": True})
    if scope is not None:
        result["coverageScope"] = {"schemaVersion": 1, "mode": scope}
    for index in range(node_count):
        node_id = f"n{index}"
        need = index < positive_count
        result["sourceNodes"].append({"id": node_id, "text": "FOLLOW" if index == 8 else
            ("English headline" if need else "中文正文"), "tag": "p", "role": "paragraph",
            "position": {"x": 5, "y": index * 24, "width": 200, "height": 20}})
        result["annotations"].append({"nodeId": node_id, "needTranslation": need,
                                     "reviewed": True, "independent": True})
        result["predictions"].append({"nodeId": node_id, "eligible": need,
                                      "completed": positive and need, "failed": False})
    return result


class CoverageScopeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="coverage-scope-", dir=ROOT / ".parallel")
        self.addCleanup(self.temp.cleanup)
        self.evidence = Path(self.temp.name)
        (self.evidence / "fixture.png").write_bytes(PNG)
        self.site = site_fixture()
        self.case = case_fixture(self.site, node_count=412, positive_count=9)
        self.manifest = {"schemaVersion": 1, "sites": [self.site]}

    def report(self, case=None, scope="automatic-page"):
        return evaluate(self.manifest, [self.case if case is None else case], self.evidence,
                        coverage_scope=scope)

    def test_gmw_like_nine_of_412_remain_true_and_untranslated(self):
        original = deepcopy(self.case)
        automatic = self.report()
        strict_case = deepcopy(self.case)
        strict_case["coverageScope"]["mode"] = "strict"
        strict = self.report(strict_case, "strict")
        auto_counts = automatic["cases"][0]["counts"]
        strict_counts = strict["cases"][0]["counts"]
        self.assertTrue(automatic["cases"][0]["completed"])
        self.assertFalse(strict["cases"][0]["completed"])
        for key, value in {"TP": 9, "TN": 403, "FP": 0, "FN": 0,
                           "annotatedPositiveNodes": 9, "translatedPositiveNodes": 0,
                           "unTranslatedPositiveNodes": 9}.items():
            self.assertEqual(value, auto_counts[key])
            self.assertEqual(value, strict_counts[key])
        self.assertEqual(9, auto_counts["policySkippedPositiveNodes"])
        self.assertEqual(0, auto_counts["confirmedOmissions"])
        self.assertEqual(0, strict_counts["policySkippedPositiveNodes"])
        self.assertEqual(9, strict_counts["confirmedOmissions"])
        self.assertEqual(original, self.case)
        self.assertEqual(9, sum(a["needTranslation"] for a in self.case["annotations"]))
        for name in ("overall", "development"):
            group = automatic["groups"][name]
            self.assertEqual(9, group["unTranslatedPositiveNodes"])
            self.assertEqual(9, group["policySkippedPositiveNodes"])
            self.assertEqual(0, group["translationCoverage"])
            for key in ("accuracy", "precision", "recall", "translationCoverage"):
                self.assertEqual(strict["groups"][name][key], group[key])
        self.assertEqual(0, automatic["groups"]["holdout"]["policySkippedPositiveNodes"])
        self.assertFalse(automatic["achieved"])  # One fixture cannot meet corpus gates.

    def test_skip_never_invents_recognition_credit(self):
        self.case["predictions"][0]["eligible"] = False
        self.case["predictions"][9]["eligible"] = True
        report = self.report()
        counts = report["cases"][0]["counts"]
        self.assertEqual((8, 402, 1, 1), tuple(counts[k] for k in ("TP", "TN", "FP", "FN")))
        self.assertEqual(9, counts["policySkippedPositiveNodes"])
        self.assertEqual(0, counts["translatedPositiveNodes"])

    def test_default_strict_and_versioned_strict_preserve_legacy(self):
        self.case.pop("coverageScope")
        default = evaluate(self.manifest, [self.case], self.evidence)
        explicit = self.report(self.case, "strict")
        self.assertEqual(default, explicit)
        self.assertEqual({"schemaVersion": 1, "mode": "strict"}, default["coverageScope"])
        self.assertEqual(9, default["cases"][0]["counts"]["confirmedOmissions"])
        self.case["coverageScope"] = {"schemaVersion": 1, "mode": "strict"}
        self.assertEqual(default, self.report(self.case, "strict"))

    def test_automatic_requires_matching_case_metadata(self):
        for metadata in (None, {}, "automatic-page", {"schemaVersion": 2, "mode": "automatic-page"},
                         {"schemaVersion": True, "mode": "automatic-page"},
                         {"schemaVersion": 1.0, "mode": "automatic-page"},
                         {"schemaVersion": 1, "mode": "strict"},
                         {"schemaVersion": 1, "mode": "unknown"}):
            with self.subTest(metadata=metadata):
                case = deepcopy(self.case)
                if metadata is None:
                    case.pop("coverageScope")
                else:
                    case["coverageScope"] = metadata
                record = self.report(case)["cases"][0]
                self.assertFalse(record["evidenceAccepted"])
                self.assertIn("coverage_scope_metadata_mismatch", record["reasons"])
        record = self.report(scope="strict")["cases"][0]
        self.assertIn("coverage_scope_metadata_mismatch", record["reasons"])
        self.assertFalse(record["completed"])

    def test_invalid_api_modes_are_rejected_before_scoring(self):
        for mode in (None, [], {}, True, "automatic", "STRICT"):
            with self.subTest(mode=mode):
                with self.assertRaisesRegex(ValueError, "coverage_scope"):
                    self.report(scope=mode)
                with self.assertRaisesRegex(ValueError, "coverage_scope"):
                    _score_case(self.case, self.site, self.evidence, coverage_scope=mode)

    def test_wrong_skip_positive_page_retains_all_required_omissions(self):
        site = site_fixture(2, positive=True)
        case = case_fixture(site, node_count=9, positive_count=9)
        case["pageDecision"] = False
        case["requestCount"] = case["hostEvents"][1]["requestCount"] = 0
        for prediction in case["predictions"]:
            prediction["completed"] = False
        report = evaluate({"schemaVersion": 1, "sites": [site]}, [case], self.evidence,
                          coverage_scope="automatic-page")
        counts = report["cases"][0]["counts"]
        self.assertEqual(9, counts["confirmedOmissions"])
        self.assertEqual(9, counts["unTranslatedPositiveNodes"])
        self.assertEqual(0, counts["policySkippedPositiveNodes"])
        self.assertEqual(9, counts["TP"])
        self.assertFalse(report["cases"][0]["completed"])

    def test_actual_translate_decision_on_negative_page_cannot_exempt(self):
        self.case["pageDecision"] = True
        record = self.report()["cases"][0]
        self.assertEqual(9, record["counts"]["confirmedOmissions"])
        self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
        self.assertFalse(record["completed"])

    def test_page_truth_is_independent_and_must_agree_with_manifest(self):
        for annotation in (None, {"needTranslation": False, "reviewed": False, "independent": True},
                           {"needTranslation": False, "reviewed": True, "independent": False},
                           {"needTranslation": "unknown", "reviewed": True, "independent": True},
                           {"needTranslation": True, "reviewed": True, "independent": True}):
            with self.subTest(annotation=annotation):
                case = deepcopy(self.case)
                case["pageAnnotation"] = annotation
                record = self.report(case)["cases"][0]
                self.assertFalse(record["completed"])
                self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
        case = deepcopy(self.case)
        case["expectedPageTranslation"] = True
        self.assertIn("manifest_mismatch", self.report(case)["cases"][0]["reasons"])

    def test_nonzero_calls_and_invalid_counts_cannot_exempt(self):
        for count in (1, -1, True, 0.0, "0", None):
            with self.subTest(count=count):
                case = deepcopy(self.case)
                case["requestCount"] = case["hostEvents"][0]["requestCount"] = count
                record = self.report(case)["cases"][0]
                self.assertFalse(record["completed"])
                self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
                if count == 1 and type(count) is int:
                    self.assertEqual(9, record["counts"]["confirmedOmissions"])
                    self.assertEqual(1, record["counts"]["unnecessaryRequests"])

    def test_missing_failed_or_malformed_host_evidence_cannot_exempt(self):
        changes = [("success", False), ("failedCount", 1), ("failedCount", False),
                   ("cacheHits", -1), ("cacheHits", True), ("requestCount", 1),
                   ("requestCount", None), ("type", "unknown"), ("id", ""),
                   ("caseId", "another-case")]
        for key, value in changes:
            with self.subTest(key=key, value=value):
                case = deepcopy(self.case)
                case["hostEvents"][0][key] = value
                record = self.report(case)["cases"][0]
                self.assertFalse(record["completed"])
                self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
        for events in ([], None, [None], [self.case["hostEvents"][0]] * 2):
            with self.subTest(events=events):
                case = deepcopy(self.case)
                case["hostEvents"] = events
                record = self.report(case)["cases"][0]
                self.assertFalse(record["completed"])
                self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])

    def test_terminal_without_live_capabilities_reports_required_omissions(self):
        case = deepcopy(self.case)
        case["hostEvents"] = [case["hostEvents"][0]]
        report = self.report(case)
        record = report["cases"][0]
        self.assertFalse(record["completed"])
        self.assertIn("missing_live_capabilities", record["reasons"])
        self.assertFalse(report["gates"]["overall"]["noConfirmedOmissions"])
        for counts in (record["counts"], report["groups"]["overall"],
                       report["groups"]["development"]):
            self.assertEqual((9, 403, 0, 0), tuple(counts[k] for k in ("TP", "TN", "FP", "FN")))
            self.assertEqual(9, counts["unTranslatedPositiveNodes"])
            self.assertEqual(9, counts["confirmedOmissions"])
            self.assertEqual(0, counts["policySkippedPositiveNodes"])
            self.assertEqual(0, counts["translatedPositiveNodes"])
        # Legacy strict negatives retain their original host requirements.
        case["coverageScope"]["mode"] = "strict"
        strict = self.report(case, "strict")["cases"][0]
        self.assertNotIn("missing_live_capabilities", strict["reasons"])
        self.assertEqual(9, strict["counts"]["confirmedOmissions"])

    def test_non_live_or_invalid_capabilities_remain_untrusted(self):
        for live in (False, None, 0, 1, "true"):
            with self.subTest(live=live):
                case = deepcopy(self.case)
                case["hostEvents"][1]["live"] = live
                record = self.report(case)["cases"][0]
                self.assertFalse(record["completed"])
                self.assertFalse(record["evidenceAccepted"])
                self.assertIn("non_live_model", record["reasons"])
                self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
                self.assertEqual(0, record["counts"]["TP"])
        for field, value in (("caseId", "another-case"), ("id", ""), ("type", "unknown")):
            with self.subTest(field=field):
                case = deepcopy(self.case)
                case["hostEvents"][1][field] = value
                record = self.report(case)["cases"][0]
                self.assertFalse(record["completed"])
                self.assertFalse(record["evidenceAccepted"])
                self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])

    def test_live_capabilities_without_successful_terminal_cannot_exempt(self):
        for mutation in ("missing", "failed"):
            with self.subTest(mutation=mutation):
                case = deepcopy(self.case)
                if mutation == "missing":
                    case["hostEvents"] = [case["hostEvents"][1]]
                else:
                    case["hostEvents"][0]["success"] = False
                record = self.report(case)["cases"][0]
                self.assertFalse(record["completed"])
                self.assertIn("missing_or_invalid_zero_call_terminal", record["reasons"])
                self.assertEqual(9, record["counts"]["confirmedOmissions"])
                self.assertEqual(9, record["counts"]["unTranslatedPositiveNodes"])
                self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])

    def test_missing_failed_contradictory_predictions_still_require_coverage(self):
        for mutation in ("missing", "unknown", "failed", "contradictory"):
            with self.subTest(mutation=mutation):
                case = deepcopy(self.case)
                if mutation == "missing":
                    case["predictions"].pop(0)
                if mutation == "unknown":
                    case["predictions"][0]["eligible"] = None
                if mutation == "failed":
                    case["predictions"][0]["failed"] = True
                if mutation == "contradictory":
                    case["predictions"][0]["completed"] = True
                report = self.report(case)
                record = report["cases"][0]
                self.assertFalse(record["completed"])
                self.assertEqual(1, record["counts"]["confirmedOmissions"])
                self.assertEqual(8, record["counts"]["policySkippedPositiveNodes"])
                self.assertEqual(9, record["counts"]["unTranslatedPositiveNodes"])
                self.assertFalse(report["gates"]["overall"]["noConfirmedOmissions"])
                if mutation != "failed":
                    self.assertEqual(1, record["counts"]["unknownPredictions"])
                    self.assertFalse(report["gates"]["overall"]["noUnknownPredictions"])

    def test_unknown_nodes_and_pages_still_fail_the_original_gates(self):
        for key in ("annotations", "pageAnnotation"):
            case = deepcopy(self.case)
            label = case[key][0] if key == "annotations" else case[key]
            label["needTranslation"] = "unknown"
            report = self.report(case)
            self.assertFalse(report["cases"][0]["completed"])
            self.assertFalse(report["gates"]["overall"]["noUnknownLabels"])
            self.assertEqual(1, report["groups"]["overall"][
                "unknownNodes" if key == "annotations" else "unknownPages"])

    def test_errors_incomplete_capture_and_retractions_remain_blocking(self):
        for key, value in (("errors", ["capture_failure"]), ("errors", None),
                           ("blockedReason", "unsettled"), ("completed", False),
                           ("retractions", None), ("retractions", [{"nodeId": "n0",
                            "reviewed": True, "confirmed": True, "sourceUnchanged": True}]),
                           ("retractions", [{"nodeId": "n0", "reviewed": False,
                            "confirmed": False, "sourceUnchanged": True}])):
            with self.subTest(key=key, value=value):
                case = deepcopy(self.case)
                case[key] = value
                report = self.report(case)
                self.assertFalse(report["cases"][0]["completed"])
                if key == "retractions" and isinstance(value, list) and value[0]["reviewed"]:
                    self.assertEqual(1, report["groups"]["overall"]["confirmedRetractions"])
                    self.assertFalse(report["gates"]["overall"]["noConfirmedRetractions"])

    def test_caller_summary_is_ignored_and_counts_are_evidence_based(self):
        self.case.update(unTranslatedPositiveNodes=0, policySkippedPositiveNodes=412,
                         confirmedOmissions=0, translatedPositiveNodes=412,
                         counts={"TP": 412, "policySkippedPositiveNodes": 412})
        counts = self.report()["cases"][0]["counts"]
        self.assertEqual(9, counts["TP"])
        self.assertEqual(9, counts["unTranslatedPositiveNodes"])
        self.assertEqual(9, counts["policySkippedPositiveNodes"])
        self.assertEqual(0, counts["translatedPositiveNodes"])

    def test_all_cases_and_groups_expose_integer_counts_even_if_rejected(self):
        extra = deepcopy(self.case)
        extra["id"] = "outside-manifest"
        report = evaluate(self.manifest, [self.case, self.case, extra], self.evidence,
                          coverage_scope="automatic-page")
        for stats in [r["counts"] for r in report["cases"]] + list(report["groups"].values()):
            for key in ("unTranslatedPositiveNodes", "policySkippedPositiveNodes"):
                self.assertIs(type(stats[key]), int)
                self.assertEqual(0, stats[key])

    def cli(self, cases, *options):
        manifest_path, cases_path = self.evidence / "sites.json", self.evidence / "cases.json"
        manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        cases_path.write_text(json.dumps(cases), encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = main(["--sites", str(manifest_path), "--cases", str(cases_path),
                         "--evidence-root", str(self.evidence), *options])
        return code, json.loads(output.getvalue())

    def test_cli_metadata_and_distinct_exclusive_outputs(self):
        auto_path, strict_path = self.evidence / "automatic.json", self.evidence / "strict.json"
        code, auto = self.cli([self.case], "--coverage-scope", "automatic-page",
                              "--output", str(auto_path))
        self.assertEqual(1, code)
        self.assertEqual(auto, json.loads(auto_path.read_text(encoding="utf-8")))
        original_auto = auto_path.read_bytes()
        strict_case = deepcopy(self.case)
        strict_case.pop("coverageScope")
        code, strict = self.cli([strict_case], "--output", str(strict_path))
        self.assertEqual(1, code)
        self.assertEqual(strict, json.loads(strict_path.read_text(encoding="utf-8")))
        self.assertEqual("strict", strict["coverageScope"]["mode"])
        self.assertEqual("automatic-page", auto["coverageScope"]["mode"])
        code, failure = self.cli([strict_case], "--output", str(auto_path))
        self.assertEqual(2, code)
        self.assertIn("error", failure)
        self.assertEqual(original_auto, auto_path.read_bytes())
        code, failure = self.cli([strict_case], "--output", str(self.evidence / "cases.json"))
        self.assertEqual(2, code)
        self.assertEqual([strict_case], json.loads((self.evidence / "cases.json").read_text()))

    def test_cli_checks_optional_envelope_metadata_and_each_case(self):
        for metadata in ({"schemaVersion": 2, "mode": "automatic-page"},
                         {"schemaVersion": 1, "mode": "strict"}):
            code, report = self.cli({"schemaVersion": 1, "coverageScope": metadata,
                                     "cases": [self.case]}, "--coverage-scope", "automatic-page")
            self.assertEqual(2, code)
            self.assertIn("metadata_mismatch", report["error"])
        envelope = {"schemaVersion": 1, "coverageScope": self.case["coverageScope"],
                    "cases": [self.case]}
        code, report = self.cli(envelope, "--coverage-scope", "automatic-page")
        self.assertEqual(1, code)
        self.assertTrue(report["cases"][0]["completed"])
        legacy = deepcopy(self.case)
        legacy.pop("coverageScope")
        envelope["cases"] = [legacy]
        code, report = self.cli(envelope, "--coverage-scope", "automatic-page")
        self.assertEqual(1, code)
        self.assertFalse(report["cases"][0]["completed"])
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            main(["--cases", "unused", "--evidence-root", "unused", "--coverage-scope", "bogus"])
        self.assertEqual(2, caught.exception.code)


class AutomaticQualityGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="coverage-gates-", dir=ROOT / ".parallel")
        self.addCleanup(self.temp.cleanup)
        self.evidence = Path(self.temp.name)
        (self.evidence / "fixture.png").write_bytes(PNG)
        self.manifest = {"schemaVersion": 1, "sites": [site_fixture(
            index, positive=index % 2 == 0, split="holdout" if index < 40 else "development")
            for index in range(120)]}
        self.cases = [case_fixture(site) for site in self.manifest["sites"]]

    def report(self, cases=None):
        return evaluate(self.manifest, self.cases if cases is None else cases, self.evidence,
                        coverage_scope="automatic-page")

    def test_node_accuracy_boundary_counts_skipped_positives_as_fn(self):
        # 50 genuine translated positives + 50 negative pages. Five independently
        # positive nodes on skipped pages remain FN: exactly 95/100 recognition.
        for case in self.cases[41:51:2]:
            case["annotations"][0]["needTranslation"] = True
        report = self.report(self.cases[:100])
        self.assertEqual(.95, report["groups"]["overall"]["accuracy"])
        self.assertTrue(report["gates"]["overall"]["nodeAccuracy"])
        self.assertEqual(5, report["groups"]["overall"]["policySkippedPositiveNodes"])
        self.assertEqual(50 / 55, report["groups"]["overall"]["translationCoverage"])
        self.cases[51]["annotations"][0]["needTranslation"] = True
        report = self.report(self.cases[:100])
        self.assertEqual(.94, report["groups"]["overall"]["accuracy"])
        self.assertFalse(report["gates"]["overall"]["nodeAccuracy"])

    def test_node_precision_exact_95_and_below_are_unchanged(self):
        # Eligible negative nodes on skipped pages remain false positives.
        for case in self.cases:
            if case["expectedPageTranslation"]:
                expanded = case_fixture(case, node_count=19, positive_count=19)
                case.update({key: expanded[key] for key in ("sourceNodes", "annotations", "predictions")})
            else:
                case["predictions"][0]["eligible"] = True
        report = self.report()
        self.assertEqual(.95, report["groups"]["overall"]["precision"])
        self.assertTrue(report["gates"]["overall"]["nodePrecision"])
        case = self.cases[1]
        for key in ("sourceNodes", "annotations", "predictions"):
            row = deepcopy(case[key][0])
            row["id" if key == "sourceNodes" else "nodeId"] = "extra-fp"
            case[key].append(row)
        report = self.report()
        self.assertLess(report["groups"]["overall"]["precision"], .95)
        self.assertFalse(report["gates"]["overall"]["nodePrecision"])
        self.assertFalse(report["gates"]["holdout"]["nodePrecision"])

    def test_page_97_domain_100_and_holdout_30_boundaries_remain(self):
        for index in (41, 43, 45):
            self.cases[index]["pageDecision"] = True
        report = self.report(self.cases[:100])
        self.assertEqual(.97, report["groups"]["overall"]["pageAccuracy"])
        self.assertTrue(report["gates"]["overall"]["pageAccuracy"])
        self.assertTrue(report["gates"]["independentDomains"])
        self.assertFalse(self.report(self.cases[:99])["gates"]["independentDomains"])
        self.cases[47]["pageDecision"] = True
        self.assertFalse(self.report(self.cases[:100])["gates"]["overall"]["pageAccuracy"])
        self.assertTrue(self.report(self.cases[:30] + self.cases[40:])["gates"]["completedHoldoutDomains"])
        self.assertFalse(self.report(self.cases[:29] + self.cases[40:])["gates"]["completedHoldoutDomains"])
        self.cases[1]["pageDecision"] = self.cases[3]["pageDecision"] = True
        self.assertFalse(self.report()["gates"]["holdout"]["pageAccuracy"])


if __name__ == "__main__":
    unittest.main()
