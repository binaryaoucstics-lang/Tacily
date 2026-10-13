"""Offline assessment regressions; these fixtures are not real quality results.

The host event follows the public GMW development witness. Source text and
review labels are authored separately from production eligibility predictions.
The independent evaluator implementation is integrated by the controller.
"""

from contextlib import redirect_stderr, redirect_stdout
from copy import deepcopy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from evaluate import _finish, _score_case
from normalize import main, normalize_case, NormalizationError, REQUIRED_PHASES
from test_normalize import PNG


ROOT = Path(__file__).resolve().parents[2]
ASSESSMENT = {
    "schemaVersion": 1, "id": "site-095-assess", "caseId": "site-095",
    "type": "assessment", "live": True, "needsTranslation": False,
    "reason": "mixed-target-body", "confidence": 0.98, "detectedLanguage": "zh",
    "isMixedTargetLanguage": True,
}
ENGLISH_TEXTS = (
    "News from around the world", "Science and technology", "Arts and culture",
    "Education and research", "Travel and discovery", "Community voices",
    "Latest reports", "About this publication", "FOLLOW",
)


class AssessmentProofTests(unittest.TestCase):
    def setUp(self):
        evidence = ROOT / ".parallel" / "development-evidence"
        evidence.mkdir(parents=True, exist_ok=True)
        temp = tempfile.TemporaryDirectory(prefix="normalize-assessment-", dir=evidence)
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.case_path = self.root / "case.json"
        self.review_path = self.root / "review.json"
        self.sequence = 0
        self.case = {
            "id": "site-095", "registeredDomain": "gmw.cn",
            "url": "https://www.gmw.cn/", "finalUrl": "https://www.gmw.cn/",
            "category": "news", "language": "zh", "split": "development",
            "expectedPageTranslation": False, "schemaVersion": 1,
            "sourceHead": "3c3ec34b7d8a50d91eea2639e9ea46c972634547",
            "sourceHashes": {"page-adapter.js": "a" * 64}, "manifestHash": "b" * 64,
            "title": "Offline Chinese-dominant fixture", "capturedAt": "2026-10-12T23:36:41.607Z",
            "viewport": {"width": 1280, "height": 720},
            "visitSuccess": True, "domainVerified": True, "captureFinished": True,
            "evidenceReady": True, "settled": True, "completed": False,
            "blockedReason": "screenshot_review_pending", "errors": [],
            "simulation": False, "liveTranslation": False, "pageDecision": False,
            "localAssessment": deepcopy(ASSESSMENT), "requestIds": [], "terminals": [],
            "capabilities": {"schemaVersion": 1, "type": "capabilities", "id": "cap-095",
                             "caseId": "site-095", "live": True, "targetLanguage": "zh-Hans"},
            "sourceNodes": [], "productionEligibility": [], "predictions": [],
            "screenshots": [], "retractions": [],
        }
        self.review = {
            "pageAnnotation": {"needTranslation": False, "reviewed": True, "independent": True},
            "annotations": [], "screenshots": dict.fromkeys(REQUIRED_PHASES, True), "retractions": [],
        }
        # Nine independently positive English nodes, 403 Chinese nodes. The
        # producer recognizes only five English nodes and one Chinese node.
        # Labels are authored from source content, never from the producer flags.
        for index in range(412):
            source_id = f"source-{index:03d}"
            text = ENGLISH_TEXTS[index] if index < len(ENGLISH_TEXTS) else f"中文新闻正文第{index}条"
            self.case["sourceNodes"].append({
                "id": source_id, "sourceText": text, "displayedText": text,
                "tag": "A" if index < 9 else "P", "role": "text", "rendered": True,
                "currentlyRendered": True, "ancestorsVisible": True, "phase": "stable",
                "rects": [{"x": 10, "y": 30 + index * 24, "width": 240, "height": 20}],
            })
            self.review["annotations"].append({"sourceId": source_id, "needTranslation": index < 9})
            eligible = index in (0, 1, 2, 3, 4, 10)
            self.case["productionEligibility"].append({"sourceId": source_id, "eligible": eligible})
            self.case["predictions"].append({
                "id": f"piece-{index:03d}", "sourceId": source_id, "sourceText": text,
                "sourceEvidence": "matched", "auditIssue": None, "text": text,
                "eligible": eligible, "completed": False, "failed": False,
            })
        for phase in sorted(REQUIRED_PHASES):
            image = self.root / (phase + ".png")
            image.write_bytes(PNG)
            self.case["screenshots"].append({"phase": phase, "path": image.name,
                                             "sha256": hashlib.sha256(PNG).hexdigest()})

    def save(self):
        self.case_path.write_text(json.dumps(self.case, ensure_ascii=False), encoding="utf-8")
        self.review["caseSha256"] = hashlib.sha256(self.case_path.read_bytes()).hexdigest()
        self.review_path.write_text(json.dumps(self.review), encoding="utf-8")

    def run_scope(self, mode="automatic-page"):
        self.save()
        self.sequence += 1
        output = self.root / f"normalized-{self.sequence}.json"
        original_paths = [self.case_path, self.review_path,
                          *(self.root / shot["path"] for shot in self.case["screenshots"])]
        originals = {path: path.read_bytes() for path in original_paths}
        result = normalize_case(self.case_path, self.review_path, output, self.root, coverage_scope=mode)
        self.assertEqual(result, json.loads(output.read_text(encoding="utf-8")))
        self.assertEqual(self.case, result["provenance"]["rawCapture"])
        self.assertEqual(self.review, result["provenance"]["rawReview"])
        self.assertEqual(hashlib.sha256(originals[self.case_path]).hexdigest(),
                         result["provenance"]["caseSha256"])
        self.assertEqual(hashlib.sha256(originals[self.review_path]).hexdigest(),
                         result["provenance"]["reviewSha256"])
        for path, original in originals.items():
            self.assertEqual(original, path.read_bytes())
        return result

    def counts(self, result, *, skipped=9, omissions=0, positive=9):
        for key, expected in {
            "annotatedPositiveNodes": positive, "translatedPositiveNodes": 0,
            "unTranslatedPositiveNodes": positive, "policySkippedPositiveNodes": skipped,
            "confirmedOmissions": omissions,
        }.items():
            self.assertIs(type(result[key]), int, key)
            self.assertEqual(expected, result[key], key)
        self.assertEqual(0.0 if positive else None, result["translationCoverage"])

    def blocked(self, result, issue, *, positive=9):
        self.assertFalse(result["completed"])
        self.assertIn(issue, result["normalizationIssues"])
        self.counts(result, skipped=0, omissions=positive, positive=positive)

    def test_real_shape_assessment_preserved_without_synthetic_terminal(self):
        result = self.run_scope()
        self.assertTrue(result["completed"], result["blockedReason"])
        self.assertEqual([], result["normalizationIssues"])
        self.assertEqual({"schemaVersion": 1, "mode": "automatic-page"}, result["coverageScope"])
        self.assertEqual([self.case["capabilities"], ASSESSMENT], result["hostEvents"])
        self.assertEqual(ASSESSMENT, result["localAssessment"])
        self.assertEqual([], result["requestIds"])
        self.assertEqual([], result["terminals"])
        self.assertEqual(0, result["requestCount"])
        self.assertFalse(result["liveTranslation"])
        self.assertEqual(self.case["sourceHead"], result["sourceHead"])
        self.assertEqual(self.case["sourceHashes"], result["sourceHashes"])
        self.assertEqual(self.case["manifestHash"], result["manifestHash"])
        self.counts(result)
        self.assertEqual(412, len(result["sourceNodes"]))
        self.assertEqual(ENGLISH_TEXTS, tuple(n["text"] for n in result["sourceNodes"][:9]))
        self.assertEqual(9, sum(label["needTranslation"] for label in result["annotations"]))
        self.assertEqual(6, sum(prediction["eligible"] for prediction in result["predictions"]))
        self.assertTrue(all(not p["completed"] and not p["failed"] for p in result["predictions"]))
        # Recognition metrics remain unchanged in the baseline strict scorer;
        # automatic assessment scoring belongs to the separate evaluator task.
        strict = self.run_scope("strict")
        self.assertEqual(result["predictions"], strict["predictions"])
        self.assertEqual(result["annotations"], strict["annotations"])
        site = {key: self.case[key] for key in ("id", "registeredDomain", "url", "category",
                                               "language", "expectedPageTranslation", "split")}
        stats, accepted, _ = _score_case(strict, site, self.root)
        stats = _finish(stats)
        self.assertFalse(accepted)
        self.assertEqual((5, 4, 402, 1), tuple(stats[key] for key in ("TP", "FN", "TN", "FP")))
        self.assertEqual(407 / 412, stats["accuracy"])
        self.assertEqual(5 / 6, stats["precision"])
        self.assertEqual(5 / 9, stats["recall"])
        self.counts(strict, skipped=0, omissions=9)

    def test_default_strict_ignores_assessment_as_before(self):
        self.save()
        default = normalize_case(self.case_path, self.review_path, self.root / "default.json", self.root)
        strict = self.run_scope("strict")
        self.assertEqual(default, strict)
        self.blocked(default, "confirmed_omission:source-000")
        self.assertEqual([self.case["capabilities"]], default["hostEvents"])
        del self.case["localAssessment"]
        legacy = self.run_scope("strict")
        for key in ("hostEvents", "normalizationIssues", "completed", "requestCount",
                    "predictions", "annotations", "confirmedOmissions", "policySkippedPositiveNodes"):
            self.assertEqual(default[key], legacy[key], key)

    def test_missing_assessment_keeps_old_capabilities_only_failure(self):
        del self.case["localAssessment"]
        result = self.run_scope()
        self.blocked(result, "missing_local_terminal_evidence")
        self.assertEqual([self.case["capabilities"]], result["hostEvents"])

    def test_assessment_fields_require_actual_version_identity_live_negative_bool(self):
        case = deepcopy(self.case)
        patches = [(key, value) for key, values in {
            "schemaVersion": (None, True, False, 0, 2, 1.0, "1"),
            "id": (None, "", "   ", 1, [], {}),
            "caseId": (None, "other", "", 1),
            "type": (None, "terminal", "packet", "unknown"),
            "live": (None, False, 1, "true"),
            "needsTranslation": (None, True, 0, "false", "unknown"),
        }.items() for value in values]
        patches += [(key, "REMOVE") for key in ASSESSMENT if key in
                    ("schemaVersion", "id", "caseId", "type", "live", "needsTranslation")]
        for key, value in patches:
            with self.subTest(key=key, value=value):
                self.case = deepcopy(case)
                if value == "REMOVE":
                    del self.case["localAssessment"][key]
                else:
                    self.case["localAssessment"][key] = value
                result = self.run_scope()
                self.blocked(result, "invalid_local_assessment")
                self.assertEqual(self.case["localAssessment"], result["hostEvents"][-1])
                self.assertEqual([], result["terminals"])

    def test_malformed_assessment_cannot_write_output(self):
        for value in (None, False, 0, "assessment", [], [ASSESSMENT]):
            with self.subTest(value=value):
                self.case["localAssessment"] = value
                self.save()
                output = self.root / "malformed.json"
                with self.assertRaisesRegex(NormalizationError, "invalid_local_assessment"):
                    normalize_case(self.case_path, self.review_path, output, self.root,
                                   coverage_scope="automatic-page")
                self.assertFalse(output.exists())

    def test_request_list_corruption_and_issued_work_cannot_use_assessment_exemption(self):
        case = deepcopy(self.case)
        for value in (None, False, 0, "", {}, [None], [False], [[]], [""], ["r1", "r1"], ["r1"]):
            with self.subTest(requestIds=value):
                self.case = deepcopy(case)
                self.case["requestIds"] = value
                result = self.run_scope()
                issue = "request_terminal_identity_mismatch" if value == ["r1"] else "invalid_request_identities"
                self.blocked(result, issue)
                self.assertEqual([], result["terminals"])
        self.case = deepcopy(case)
        del self.case["requestIds"]
        self.blocked(self.run_scope(), "invalid_request_identities")
        self.case = deepcopy(case)
        del self.case["terminals"]
        self.blocked(self.run_scope(), "missing_local_terminal_evidence")

    def terminal(self, identity="r1", **patch):
        return {"type": "terminal", "caseId": self.case["id"], "id": identity,
                "success": True, "failedCount": 0, "cacheHits": 0, "requestCount": 0, **patch}

    def test_assessment_cannot_override_corrupt_failed_or_nonzero_terminals(self):
        case = deepcopy(self.case)
        variants = (
            ({"requestCount": 1}, "unnecessary_translation_request"),
            ({"requestCount": None}, "invalid_terminal_request_count"),
            ({"requestCount": False}, "invalid_terminal_request_count"),
            ({"requestCount": -1}, "invalid_terminal_request_count"),
            ({"requestCount": 0.0}, "invalid_terminal_request_count"),
            ({"success": False, "failedCount": 1}, "failed_or_invalid_terminal"),
            ({"success": True, "failedCount": 1}, "failed_or_invalid_terminal"),
            ({"failedCount": False}, "failed_or_invalid_terminal"),
            ({"cacheHits": True}, "failed_or_invalid_terminal"),
            ({"type": "assessment"}, "host_event_type_mismatch"),
            ({"id": "other"}, "request_terminal_identity_mismatch"),
            ({"id": []}, "duplicate_or_invalid_terminal_id"),
            ({"caseId": "other"}, "host_event_identity_mismatch"),
        )
        for patch, issue in variants:
            with self.subTest(patch=patch):
                self.case = deepcopy(case)
                self.case["requestIds"] = ["r1"]
                self.case["terminals"] = [self.terminal(**patch)]
                result = self.run_scope()
                self.blocked(result, issue)
                self.assertEqual([self.case["capabilities"], *self.case["terminals"], ASSESSMENT],
                                 result["hostEvents"])
        for requests, identities in (([], ["r1"]), (["r1"], ["r1", "r1"]),
                                     (["r1", "r2"], ["r1"]), (["r1"], ["r1", "r2"])):
            with self.subTest(requests=requests, identities=identities):
                self.case = deepcopy(case)
                self.case["requestIds"] = requests
                self.case["terminals"] = [self.terminal(identity) for identity in identities]
                self.blocked(self.run_scope(), "request_terminal_identity_mismatch")

    def test_malformed_terminal_list_is_not_empty_proof(self):
        for value in (None, False, {}, "", [None], [[]]):
            with self.subTest(value=value):
                self.case["terminals"] = value
                self.save()
                output = self.root / "malformed-terminals.json"
                with self.assertRaisesRegex(NormalizationError, "invalid_terminals"):
                    normalize_case(self.case_path, self.review_path, output, self.root,
                                   coverage_scope="automatic-page")
                self.assertFalse(output.exists())

    def test_complete_zero_call_terminal_bijection_still_works_and_retains_order(self):
        self.case["requestIds"] = ["r1", "r2"]
        self.case["terminals"] = [self.terminal("r2"), self.terminal("r1")]
        result = self.run_scope()
        self.assertTrue(result["completed"], result["blockedReason"])
        self.counts(result)
        self.assertEqual([self.case["capabilities"], *self.case["terminals"], ASSESSMENT], result["hostEvents"])
        self.assertEqual(["r1", "r2"], result["requestIds"])
        self.case["localAssessment"]["needsTranslation"] = True
        self.blocked(self.run_scope(), "invalid_local_assessment")

    def test_capture_and_independent_label_guards_remain_required(self):
        case, review = deepcopy(self.case), deepcopy(self.review)
        variants = (
            ("case", "expectedPageTranslation", True, "initial_page_label_disagrees_with_review"),
            ("case", "pageDecision", True, "node_not_completed:source-000"),
            ("case", "pageDecision", None, "invalid_page_decision"),
            ("case", "captureFinished", False, "capture_not_successfully_finished"),
            ("case", "evidenceReady", False, "capture_not_successfully_finished"),
            ("case", "settled", False, "capture_not_successfully_finished"),
            ("case", "visitSuccess", False, "capture_not_successfully_finished"),
            ("case", "domainVerified", False, "capture_not_successfully_finished"),
            ("case", "completed", None, "capture_completion_not_established"),
            ("case", "errors", [{"code": "timeout"}], "capture_errors"),
            ("case", "blockedReason", "unknown_history", "unknown_history"),
            ("case", "simulation", True, "non_live_or_simulated_capture"),
            ("review", "pageAnnotation", {"needTranslation": True}, "initial_page_label_disagrees_with_review"),
            ("review", "pageAnnotation", {"needTranslation": "unknown"}, "unknown_page_label"),
            ("review", "pageAnnotation", {"needTranslation": False, "reviewed": False}, "unknown_page_label"),
            ("review", "pageAnnotation", {"needTranslation": False, "independent": False}, "unknown_page_label"),
        )
        for target, key, value, issue in variants:
            with self.subTest(target=target, key=key, value=value):
                self.case, self.review = deepcopy(case), deepcopy(review)
                (self.case if target == "case" else self.review)[key] = value
                self.blocked(self.run_scope(), issue)
        self.case, self.review = deepcopy(case), deepcopy(review)
        self.review["screenshots"]["after"] = False
        self.blocked(self.run_scope(), "missing_or_rejected_screenshot_reviews")
        self.case, self.review = deepcopy(case), deepcopy(review)
        self.review["annotations"][0]["needTranslation"] = "unknown"
        self.blocked(self.run_scope(), "unknown_node_label:source-000", positive=8)

    def test_live_matching_capabilities_remain_required(self):
        case = deepcopy(self.case)
        for patch, issue in ((None, "missing_live_capabilities_evidence"),
                             ({"live": False}, "missing_live_capabilities_evidence"),
                             ({"live": 1}, "missing_live_capabilities_evidence"),
                             ({"caseId": "other"}, "host_event_identity_mismatch"),
                             ({"type": "unknown"}, "host_event_type_mismatch"),
                             ({"id": ""}, "host_event_identity_mismatch")):
            with self.subTest(patch=patch):
                self.case = deepcopy(case)
                if patch is None:
                    del self.case["capabilities"]
                else:
                    self.case["capabilities"].update(patch)
                self.blocked(self.run_scope(), issue)

    def test_unknown_failed_source_audit_and_completion_claims_stay_blocked(self):
        case = deepcopy(self.case)
        for patch, issue in (({"eligible": None}, "unknown_prediction:source-000"),
                             ({"failed": True}, "node_not_completed:source-000"),
                             ({"completed": True}, "contradictory_node_completion:source-000"),
                             ({"sourceEvidence": "unmatched"}, "prediction_source_audit_issue:source-000"),
                             ({"auditIssue": "stale_source_version"}, "prediction_source_audit_issue:source-000")):
            with self.subTest(patch=patch):
                self.case = deepcopy(case)
                self.case["predictions"][0].update(patch)
                self.blocked(self.run_scope(), issue)
        self.case = deepcopy(case)
        self.case["sourceNodes"][0]["currentlyRendered"] = False
        self.case["sourceObservations"] = [{"sourceId": "source-000", "eligible": None,
                                            "sourceText": ENGLISH_TEXTS[0], "pieces": []}]
        self.blocked(self.run_scope(), "unknown_retired_source:source-000")
        self.case = deepcopy(case)
        self.case["retractions"] = [{"sourceId": "source-000", "phase": "return-after"}]
        self.blocked(self.run_scope(), "unreviewed_retraction:0")
        self.review["retractions"] = [{"eventIndex": 0, "confirmed": True, "sourceUnchanged": True}]
        self.blocked(self.run_scope(), "confirmed_retraction:0")

    def test_assessment_only_negative_page_without_positive_labels_still_needs_proof(self):
        for label in self.review["annotations"]:
            label["needTranslation"] = False
        valid = self.run_scope()
        self.assertTrue(valid["completed"], valid["blockedReason"])
        self.counts(valid, skipped=0, omissions=0, positive=0)
        self.case["pageDecision"] = True
        self.blocked(self.run_scope(), "missing_local_terminal_evidence", positive=0)
        self.case["pageDecision"] = False
        del self.case["localAssessment"]
        self.blocked(self.run_scope(), "missing_local_terminal_evidence", positive=0)

    def test_original_event_extensions_and_zero_summary_are_not_rewritten(self):
        self.case["localAssessment"]["producerDetails"] = {"observations": ["中文正文", "FOLLOW"],
                                                         "version": "public-fixture"}
        self.case["requestCount"] = 999
        result = self.run_scope()
        self.assertTrue(result["completed"], result["blockedReason"])
        self.counts(result)
        self.assertEqual(0, result["requestCount"])
        self.assertEqual(999, result["provenance"]["rawCapture"]["requestCount"])
        self.assertEqual(self.case["localAssessment"], result["hostEvents"][-1])
        self.assertEqual(self.case["localAssessment"], result["localAssessment"])

    def test_cli_requires_explicit_automatic_scope_and_exclusive_output(self):
        self.save()
        output = self.root / "cli.json"
        argv = ["--case", str(self.case_path), "--review", str(self.review_path),
                "--output", str(output), "--evidence-root", str(self.root)]
        with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            self.assertEqual(1, main(argv))
            automatic_output = self.root / "cli-automatic.json"
            argv[argv.index(str(output))] = str(automatic_output)
            self.assertEqual(0, main([*argv, "--coverage-scope", "automatic-page"]))
            original = automatic_output.read_bytes()
            self.assertEqual(2, main([*argv, "--coverage-scope", "automatic-page"]))
            self.assertEqual(original, automatic_output.read_bytes())


if __name__ == "__main__":
    unittest.main()
