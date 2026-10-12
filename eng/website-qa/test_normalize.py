"""Synthetic offline regression evidence; these fixtures are not website runs."""

import base64
from contextlib import redirect_stderr, redirect_stdout
from copy import deepcopy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from evaluate import _score_case
from normalize import main, normalize_case, NormalizationError, REQUIRED_PHASES


ROOT = Path(__file__).resolve().parents[2]
PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")
SITE = {"id": "fixture-001", "registeredDomain": "example.test",
        "url": "https://example.test/article", "category": "knowledge", "language": "en",
        "expectedPageTranslation": True, "split": "holdout"}


class NormalizationTests(unittest.TestCase):
    def setUp(self):
        # All temporary evidence stays in this checkout's ignored directory.
        self.temp = tempfile.TemporaryDirectory(prefix="normalize-tests-", dir=ROOT / ".parallel")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.attempt = self.root / "attempt-0001"
        self.attempt.mkdir()
        self.case_path = self.attempt / "case.json"
        self.review_path = self.root / "review.json"
        self.output = self.root / "scorer.json"
        self.case = {**SITE, "schemaVersion": 1, "sourceHead": "a" * 40,
                     "sourceHashes": {"page-adapter.js": "b" * 64}, "manifestHash": "c" * 64,
                     "capturedAt": "2026-10-11T00:00:00Z", "title": "Offline fixture",
                     "viewport": {"width": 1280, "height": 720}, "finalUrl": SITE["url"],
                     "visitSuccess": True, "domainVerified": True, "captureFinished": True,
                     "evidenceReady": True, "settled": True, "completed": False,
                     "blockedReason": "screenshot_review_pending", "errors": [],
                     "simulation": False, "liveTranslation": True, "pageDecision": True,
                     "sourceNodes": [self.source("s1")],
                     "predictions": [self.piece("p1", "s1")],
                     "productionEligibility": [{"sourceId": "s1", "eligible": True}],
                     "annotations": [{"sourceId": "s1", "needTranslation": "unknown"}],
                     "screenshots": [], "screenshotReview": {}, "retractions": [],
                     "capabilities": {"type": "capabilities", "id": "cap1", "caseId": SITE["id"],
                                      "live": True, "provider": "fixture", "targetLanguage": "zh-Hans"},
                     "requestIds": ["r1"],
                     "terminals": [{"type": "terminal", "id": "r1", "caseId": SITE["id"],
                                    "success": True, "failedCount": 0, "cacheHits": 0, "requestCount": 2}]}
        for phase in sorted(REQUIRED_PHASES):
            image = self.attempt / (phase + ".png")
            image.write_bytes(PNG)
            self.case["screenshots"].append({"phase": phase, "path": image.name,
                                             "sha256": hashlib.sha256(PNG).hexdigest()})
        self.review = {"pageAnnotation": {"needTranslation": True},
                       "annotations": [{"sourceId": "s1", "needTranslation": True}],
                       "screenshots": dict.fromkeys(REQUIRED_PHASES, True), "retractions": []}

    def source(self, identity):
        return {"id": identity, "sourceText": "Independent source text", "displayedText": "翻译文本",
                "tag": "P", "role": "paragraph", "rendered": True, "ancestorsVisible": True,
                "rects": [{"x": 0, "y": 0, "width": 0, "height": 0},
                          {"x": 10, "y": 30, "width": 240, "height": 24}], "phase": "stable"}

    def piece(self, identity, source_id, **patch):
        return {"id": identity, "sourceId": source_id, "sourceEvidence": "matched", "auditIssue": None,
                "sourceText": "Independent source text", "text": "Production piece", "eligible": True,
                "completed": True, "failed": False, **patch}

    def save(self):
        self.case_path.write_text(json.dumps(self.case, ensure_ascii=False), encoding="utf-8")
        self.review["caseSha256"] = hashlib.sha256(self.case_path.read_bytes()).hexdigest()
        self.review_path.write_text(json.dumps(self.review), encoding="utf-8")

    def normalize(self, root=None):
        self.save()
        return normalize_case(self.case_path, self.review_path, self.output, root or self.root)

    def assert_blocked(self, result, issue):
        self.assertFalse(result["completed"])
        self.assertIn(issue, result["blockedReason"])
        self.assertFalse(_score_case(result, {**SITE, "expectedPageTranslation": self.case["expectedPageTranslation"]},
                                     self.root)[1])

    def test_original_evidence_and_provenance_preserved_and_scorer_accepts(self):
        self.save()
        originals = {p: p.read_bytes() for p in [self.case_path, self.review_path,
                     *(self.attempt / s["path"] for s in self.case["screenshots"])]}
        result = normalize_case(self.case_path, self.review_path, self.output, self.root)
        self.assertTrue(result["completed"])
        self.assertEqual(self.case, result["provenance"]["rawCapture"])
        self.assertEqual(self.review, result["provenance"]["rawReview"])
        self.assertEqual(hashlib.sha256(originals[self.case_path]).hexdigest(), result["provenance"]["caseSha256"])
        self.assertEqual(hashlib.sha256(originals[self.review_path]).hexdigest(), result["provenance"]["reviewSha256"])
        self.assertEqual(self.case["sourceHead"], result["sourceHead"])
        self.assertEqual(self.case["sourceHashes"], result["sourceHashes"])
        self.assertEqual([self.case["capabilities"], *self.case["terminals"]], result["hostEvents"])
        self.assertEqual(2, result["requestCount"])
        self.assertEqual("Independent source text", result["sourceNodes"][0]["text"])
        self.assertEqual(self.case["sourceNodes"][0]["rects"][1], result["sourceNodes"][0]["position"])
        self.assertEqual("P", result["sourceNodes"][0]["tag"])
        self.assertEqual("paragraph", result["sourceNodes"][0]["role"])
        self.assertEqual("attempt-0001/before.png", result["screenshots"]["before"]["path"])
        self.assertTrue(_score_case(result, SITE, self.root)[1])
        self.assertEqual(result, json.loads(self.output.read_text(encoding="utf-8")))
        for path, content in originals.items():
            self.assertEqual(content, path.read_bytes())

    def test_default_root_is_original_case_directory(self):
        self.save()
        result = normalize_case(self.case_path, self.review_path, self.output)
        self.assertEqual("before.png", result["screenshots"]["before"]["path"])
        self.assertTrue(_score_case(result, SITE, self.attempt)[1])

    def retired_observation(self, pieces, eligible=True):
        self.case["sourceNodes"][0]["currentlyRendered"] = False
        self.case["predictions"] = []
        self.case["productionEligibility"][0]["eligible"] = False
        self.case["predictionHistory"] = pieces
        self.case["sourceObservations"] = [{"sourceId": "s1", "sourceText": "Independent source text",
            "rendered": True, "phase": "scroll-after", "at": "observed-time",
            "eligible": eligible, "pieces": pieces}]

    def test_hidden_completed_source_keeps_real_completion_and_old_pending_is_not_combined(self):
        piece = self.piece("latest", "s1")
        self.retired_observation([piece])
        self.case["predictionHistory"].append(self.piece("old", "s1", completed=False))
        self.case["sourceObservations"][0]["pieces"] = [piece]
        result = self.normalize()
        self.assertTrue(result["completed"])
        self.assertEqual(["latest"], result["predictions"][0]["pieceIds"])
        self.assertTrue(result["predictions"][0]["eligible"])

    def test_hidden_pending_last_set_does_not_borrow_old_success(self):
        pending = self.piece("latest", "s1", completed=False)
        self.retired_observation([pending])
        self.case["predictionHistory"].append(self.piece("old-success", "s1"))
        self.case["sourceObservations"][0]["pieces"] = [pending]
        self.assert_blocked(self.normalize(), "node_not_completed:s1")

    def test_hidden_source_without_production_observation_remains_unknown(self):
        self.retired_observation([])
        self.case["sourceObservations"] = []
        self.assert_blocked(self.normalize(), "unknown_prediction:s1")

    def test_retired_observation_cannot_invent_completion_or_reuse_changed_source(self):
        self.retired_observation([self.piece("latest", "s1")])
        self.case["predictionHistory"] = []
        with self.assertRaisesRegex(NormalizationError, "not_in_raw_prediction_history"):
            self.normalize()
        self.case["predictionHistory"] = [self.piece("latest", "s1")]
        self.case["sourceObservations"][0]["sourceText"] = "Changed source"
        with self.assertRaisesRegex(NormalizationError, "text_mismatch"):
            self.normalize()

    def test_atomic_piece_completes_only_explicit_independently_observed_members(self):
        self.case["sourceNodes"].append(self.source("peer"))
        self.review["annotations"].append({"sourceId": "peer", "needTranslation": True})
        self.case["predictions"][0]["sourceIds"] = ["s1", "peer"]
        self.case["productionEligibility"].append({"sourceId": "peer", "eligible": True})
        result = self.normalize()
        self.assertTrue(result["completed"])
        self.assertEqual(2, len(result["predictions"]))
        self.assertTrue(all(row["completed"] for row in result["predictions"]))

    def test_retired_atomic_peer_does_not_overwrite_current_owners_piece_set(self):
        self.case["sourceNodes"].append({**self.source("peer"), "currentlyRendered": False})
        self.review["annotations"].append({"sourceId": "peer", "needTranslation": True})
        old = self.piece("old-atomic", "s1", sourceIds=["s1", "peer"], completed=False)
        self.case["predictionHistory"] = [old, *self.case["predictions"]]
        self.case["sourceObservations"] = [{"sourceId": "peer", "sourceText": "Independent source text",
            "rendered": False, "representedBy": "s1", "eligible": True, "pieces": [old]}]
        self.case["productionEligibility"].append({"sourceId": "peer", "eligible": False})
        result = self.normalize()
        owner, peer = result["predictions"]
        self.assertTrue(owner["completed"])
        self.assertEqual(["p1"], owner["pieceIds"])
        self.assertFalse(peer["completed"])
        self.assert_blocked(result, "node_not_completed:peer")

    def test_split_pieces_any_eligible_all_eligible_must_complete(self):
        for patch, expected in [({}, True), ({"completed": False}, False), ({"failed": True}, False),
                                ({"eligible": False, "completed": False}, True)]:
            with self.subTest(patch=patch):
                self.case["predictions"] = [self.piece("p1", "s1"), self.piece("p2", "s1", **patch)]
                result = self.normalize()
                self.assertEqual(1, len(result["predictions"]))
                self.assertTrue(result["predictions"][0]["eligible"])
                self.assertEqual(expected, result["predictions"][0]["completed"])
                self.assertEqual(expected, result["completed"])
                self.assertEqual(["p1", "p2"], result["predictions"][0]["pieceIds"])
                self.output.unlink()

    def test_duplicate_piece_id_blocks_even_if_both_report_complete(self):
        self.case["predictions"].append(deepcopy(self.case["predictions"][0]))
        self.assert_blocked(self.normalize(), "duplicate_prediction_id")

    def test_each_independent_source_joins_its_own_label_and_pieces(self):
        self.case["sourceNodes"].append(self.source("s2"))
        self.case["predictions"] = [self.piece("p2", "s2", eligible=False, completed=False),
                                     self.piece("p1", "s1"), self.piece("p3", "s1")]
        self.case["productionEligibility"].append({"sourceId": "s2", "eligible": False})
        self.review["annotations"] = [{"sourceId": "s2", "needTranslation": False},
                                        {"sourceId": "s1", "needTranslation": True}]
        result = self.normalize()
        self.assertEqual([True, False], [r["needTranslation"] for r in result["annotations"]])
        self.assertEqual([["p1", "p3"], ["p2"]], [r["pieceIds"] for r in result["predictions"]])
        self.assertEqual([True, False], [r["eligible"] for r in result["predictions"]])
        self.assertTrue(_score_case(result, SITE, self.root)[1])

    def test_unmatched_prediction_does_not_create_source_truth(self):
        self.case["predictions"].append(self.piece("p2", "not-independently-observed"))
        result = self.normalize()
        self.assert_blocked(result, "unmatched_prediction_source")
        self.assertEqual(["s1"], [n["id"] for n in result["sourceNodes"]])
        self.assertEqual(2, len(result["provenance"]["rawCapture"]["predictions"]))

    def test_source_audit_issue_remains_failure(self):
        self.case["predictions"][0]["auditIssue"] = "source_not_independently_visible"
        self.case["predictions"][0]["sourceEvidence"] = "unmatched"
        self.assert_blocked(self.normalize(), "prediction_source_audit_issue")

    def test_missing_predictions_are_unknown_without_finished_explicit_rejection(self):
        self.case["predictions"] = []
        for patch in [{"productionEligibility": []}, {"captureFinished": False},
                      {"evidenceReady": False}, {"settled": False}]:
            with self.subTest(patch=patch):
                case = deepcopy(self.case)
                self.case["productionEligibility"] = [{"sourceId": "s1", "eligible": False}]
                self.case.update(patch)
                result = self.normalize()
                self.assertIsNone(result["predictions"][0]["eligible"])
                self.assert_blocked(result, "unknown_prediction")
                self.output.unlink()
                self.case = case

    def test_unknown_labels_never_use_manifest_predictions_or_raw_labels(self):
        self.review["annotations"] = []
        del self.review["pageAnnotation"]
        self.case["annotations"] = [{"sourceId": "s1", "needTranslation": True}]
        result = self.normalize()
        self.assertEqual("unknown", result["pageAnnotation"]["needTranslation"])
        self.assertEqual("unknown", result["annotations"][0]["needTranslation"])
        self.assertFalse(result["annotations"][0]["reviewed"])
        self.assert_blocked(result, "unknown_node_label")

    def test_explicit_unknown_and_unreviewed_labels_remain_unknown(self):
        for patch in [{"needTranslation": "unknown"}, {"needTranslation": True, "reviewed": False},
                      {"needTranslation": True, "independent": False}]:
            with self.subTest(patch=patch):
                self.review["annotations"] = [{"sourceId": "s1", **patch}]
                result = self.normalize()
                self.assert_blocked(result, "unknown_node_label")
                self.assertEqual(patch["needTranslation"], result["annotations"][0]["needTranslation"])
                self.output.unlink()

    def test_duplicate_unmatched_and_non_boolean_labels_rejected(self):
        for labels in [[{"sourceId": "missing", "needTranslation": True}],
                       [self.review["annotations"][0]] * 2,
                       [{"sourceId": "s1", "needTranslation": 1}]]:
            with self.subTest(labels=labels), self.assertRaises(NormalizationError):
                self.review["annotations"] = labels
                self.normalize()
        self.assertFalse(self.output.exists())

    def test_rendered_chinese_is_not_completion(self):
        del self.case["predictions"][0]["completed"]
        result = self.normalize()
        self.assertFalse(result["predictions"][0]["completed"])
        self.assert_blocked(result, "invalid_prediction_flags")

    def test_failed_terminal_retained_and_request_counts_summed(self):
        self.case["terminals"].append({**self.case["terminals"][0], "id": "r2", "requestCount": 3,
                                        "success": False, "failedCount": 1})
        self.case["requestIds"].append("r2")
        result = self.normalize()
        self.assertEqual(5, result["requestCount"])
        self.assertFalse(result["hostEvents"][-1]["success"])
        self.assert_blocked(result, "failed_or_invalid_terminal")

    def test_terminal_identity_and_counts_cannot_be_repaired(self):
        for patch, issue in [({"caseId": "other"}, "identity_mismatch"),
                             ({"id": "another-request"}, "request_terminal_identity_mismatch"),
                             ({"requestCount": -1}, "invalid_terminal_request_count"),
                             ({"requestCount": True}, "invalid_terminal_request_count")]:
            with self.subTest(patch=patch):
                terminal = deepcopy(self.case["terminals"][0])
                self.case["terminals"][0].update(patch)
                result = self.normalize()
                self.assert_blocked(result, issue)
                self.assertEqual(self.case["terminals"][0], result["hostEvents"][-1])
                self.output.unlink()
                self.case["terminals"][0] = terminal

    def test_duplicate_terminal_identity_is_not_deduplicated(self):
        self.case["terminals"] *= 2
        result = self.normalize()
        self.assertEqual(4, result["requestCount"])
        self.assertEqual(3, len(result["hostEvents"]))
        self.assert_blocked(result, "duplicate_or_invalid_terminal_id")

    def test_errors_and_capture_failure_remain_blocking(self):
        self.case["errors"] = [{"code": "quota_exhausted"}]
        self.case["blockedReason"] = "backend_terminal_failed;screenshot_review_pending"
        self.case["evidenceReady"] = False
        result = self.normalize()
        self.assertEqual(self.case["errors"], result["errors"])
        self.assert_blocked(result, "backend_terminal_failed")
        self.assertIn("capture_errors", result["normalizationIssues"])

    def test_noncompletion_without_pending_review_cannot_be_promoted(self):
        self.case["blockedReason"] = ""
        result = self.normalize()
        self.assert_blocked(result, "capture_completion_not_established")

    def test_negative_page_zero_requests_with_explicit_rejection(self):
        self.case.update(expectedPageTranslation=False, pageDecision=False, liveTranslation=False,
                         requestIds=[], terminals=[], predictions=[],
                         productionEligibility=[{"sourceId": "s1", "eligible": False}])
        self.review["pageAnnotation"]["needTranslation"] = False
        self.review["annotations"][0]["needTranslation"] = False
        result = self.normalize()
        self.assertTrue(result["completed"])
        self.assertEqual(0, result["requestCount"])
        self.assertEqual({"nodeId": "s1", "eligible": False, "completed": False,
                          "failed": False, "pieceIds": []}, result["predictions"][0])
        self.assertTrue(_score_case(result, {**SITE, "expectedPageTranslation": False}, self.root)[1])
        self.output.unlink()
        self.case.update(requestIds=["r1"], terminals=[{"type": "terminal", "id": "r1", "caseId": SITE["id"],
                         "requestCount": 1, "failedCount": 0, "cacheHits": 0, "success": True}])
        self.assert_blocked(self.normalize(), "unnecessary_translation_request")

    def skipped_candidate(self):
        self.case.update(expectedPageTranslation=False, pageDecision=False, liveTranslation=False,
                         requestIds=[], terminals=[],
                         predictions=[self.piece("p1", "s1", completed=False),
                                      self.piece("p2", "s1", completed=False)])
        self.review["pageAnnotation"]["needTranslation"] = False
        self.review["annotations"][0]["needTranslation"] = False

    def negative_native_assessment(self):
        self.skipped_candidate()
        self.case["requestIds"] = ["native-1", "native-2"]
        self.case["terminals"] = [
            {"type": "terminal", "id": identity, "caseId": SITE["id"],
             "requestCount": 0, "success": True, "failedCount": 0, "cacheHits": 0}
            for identity in self.case["requestIds"]]

    def normalize_preserving_originals(self):
        self.save()
        originals = {p: p.read_bytes() for p in [self.case_path, self.review_path,
                     *(self.attempt / s["path"] for s in self.case["screenshots"])]}
        result = normalize_case(self.case_path, self.review_path, self.output, self.root)
        self.assertEqual(result, json.loads(self.output.read_text(encoding="utf-8")))
        self.assertEqual(self.case, result["provenance"]["rawCapture"])
        self.assertEqual(self.review, result["provenance"]["rawReview"])
        self.assertEqual(hashlib.sha256(originals[self.case_path]).hexdigest(),
                         result["provenance"]["caseSha256"])
        self.assertEqual(hashlib.sha256(originals[self.review_path]).hexdigest(),
                         result["provenance"]["reviewSha256"])
        self.assertEqual([self.case["capabilities"], *self.case["terminals"]], result["hostEvents"])
        self.assertEqual(self.case["requestIds"], result["requestIds"])
        self.assertEqual(self.case["sourceHead"], result["sourceHead"])
        self.assertEqual(self.case["sourceHashes"], result["sourceHashes"])
        self.assertEqual(self.case["errors"], result["errors"])
        for path, content in originals.items():
            self.assertEqual(content, path.read_bytes())
        return result

    def test_negative_native_zero_calls_roundtrip_preserves_predictions_and_truth(self):
        self.negative_native_assessment()
        for completed, blocked in [(False, "screenshot_review_pending"), (True, "")]:
            with self.subTest(capture_completed=completed):
                self.case.update(completed=completed, blockedReason=blocked)
                result = self.normalize_preserving_originals()
                self.assertTrue(result["completed"])
                self.assertEqual([], result["normalizationIssues"])
                self.assertEqual(0, result["requestCount"])
                self.assertFalse(result["liveTranslation"])
                self.assertFalse(result["pageAnnotation"]["needTranslation"])
                self.assertFalse(result["annotations"][0]["needTranslation"])
                self.assertEqual({"nodeId": "s1", "eligible": True, "completed": False,
                                  "failed": False, "pieceIds": ["p1", "p2"]}, result["predictions"][0])
                stats, accepted, reasons = _score_case(
                    result, {**SITE, "expectedPageTranslation": False}, self.root)
                self.assertTrue(accepted, reasons)
                self.assertEqual(1, stats["FP"])
                self.assertEqual(0, stats["unnecessaryRequests"])
                self.output.unlink()

    def test_negative_native_actual_calls_roundtrip_remain_failure(self):
        self.negative_native_assessment()
        self.case["terminals"][0]["requestCount"] = 2
        self.case["terminals"][1]["requestCount"] = 3
        result = self.normalize_preserving_originals()
        self.assertEqual(5, result["requestCount"])
        self.assert_blocked(result, "unnecessary_translation_request")
        self.assertEqual(5, _score_case(
            result, {**SITE, "expectedPageTranslation": False}, self.root)[0]["unnecessaryRequests"])

    def concurrent_native_requests(self, expected):
        if not expected:
            self.skipped_candidate()
        self.case["requestIds"] = ["r1", "r2"]
        # r2 finishes before r1; counts stay attached to their original IDs.
        self.case["terminals"] = [
            {"type": "terminal", "id": identity, "caseId": SITE["id"],
             "requestCount": count, "success": True, "failedCount": 0, "cacheHits": 0}
            for identity, count in zip(("r2", "r1"), (3, 2) if expected else (0, 0))]

    def test_concurrent_out_of_order_terminals_roundtrip_accepts_both_page_types(self):
        baseline_case, baseline_review = deepcopy(self.case), deepcopy(self.review)
        for expected in (False, True):
            with self.subTest(expected=expected):
                self.case, self.review = deepcopy(baseline_case), deepcopy(baseline_review)
                self.concurrent_native_requests(expected)
                result = self.normalize_preserving_originals()
                self.assertTrue(result["completed"])
                self.assertEqual([], result["normalizationIssues"])
                self.assertEqual(["r1", "r2"], result["requestIds"])
                self.assertEqual(["r2", "r1"], [e["id"] for e in result["terminals"]])
                self.assertEqual(["r2", "r1"], [e["id"] for e in result["hostEvents"][1:]])
                self.assertEqual({"r1": 2, "r2": 3} if expected else {"r1": 0, "r2": 0},
                                 {e["id"]: e["requestCount"] for e in result["hostEvents"][1:]})
                self.assertEqual(5 if expected else 0, result["requestCount"])
                stats, accepted, reasons = _score_case(
                    result, {**SITE, "expectedPageTranslation": expected}, self.root)
                self.assertTrue(accepted, reasons)
                self.assertEqual(0, stats["unnecessaryRequests"])
                self.assertEqual(0 if expected else 1, stats["FP"])
                self.assertEqual(expected, result["pageAnnotation"]["needTranslation"])
                self.assertEqual(expected, result["annotations"][0]["needTranslation"])
                self.assertTrue(result["predictions"][0]["eligible"])
                self.assertEqual(expected, result["predictions"][0]["completed"])
                self.output.unlink()

    def test_concurrent_terminal_identity_failures_roundtrip_block_both_page_types(self):
        baseline_case, baseline_review = deepcopy(self.case), deepcopy(self.review)
        for expected in (False, True):
            for kind, issue in [("missing", "request_terminal_identity_mismatch"),
                                ("extra", "request_terminal_identity_mismatch"),
                                ("unmatched", "request_terminal_identity_mismatch"),
                                ("duplicate", "duplicate_or_invalid_terminal_id"),
                                ("invalid_id", "duplicate_or_invalid_terminal_id"),
                                ("duplicate_request", "invalid_request_identities"),
                                ("failed", "failed_or_invalid_terminal")]:
                with self.subTest(expected=expected, kind=kind):
                    self.case, self.review = deepcopy(baseline_case), deepcopy(baseline_review)
                    self.concurrent_native_requests(expected)
                    if kind == "missing":
                        self.case["terminals"].pop()
                    elif kind == "extra":
                        self.case["terminals"].append({**self.case["terminals"][0], "id": "r3"})
                    elif kind == "unmatched":
                        self.case["terminals"][0]["id"] = "r3"
                    elif kind == "duplicate":
                        self.case["terminals"][1] = deepcopy(self.case["terminals"][0])
                    elif kind == "invalid_id":
                        self.case["terminals"][0]["id"] = ["r2"]
                    elif kind == "duplicate_request":
                        self.case["requestIds"] = ["r1", "r1", "r2"]
                    else:
                        self.case["terminals"][0].update(success=False, failedCount=1)
                    result = self.normalize_preserving_originals()
                    self.assert_blocked(result, issue)
                    self.output.unlink()

    def test_negative_native_missing_duplicate_and_mismatched_terminals_roundtrip_fail(self):
        for kind, issue in [("missing", "request_terminal_identity_mismatch"),
                            ("duplicate", "duplicate_or_invalid_terminal_id"),
                            ("wrong_id", "request_terminal_identity_mismatch"),
                            ("wrong_case", "host_event_identity_mismatch"),
                            ("wrong_type", "host_event_type_mismatch"),
                            ("duplicate_request", "invalid_request_identities")]:
            with self.subTest(kind=kind):
                self.negative_native_assessment()
                if kind == "missing":
                    self.case["terminals"] = []
                elif kind == "duplicate":
                    self.case["terminals"][1] = deepcopy(self.case["terminals"][0])
                elif kind == "wrong_id":
                    self.case["terminals"][0]["id"] = "other-native"
                elif kind == "wrong_case":
                    self.case["terminals"][0]["caseId"] = "other-case"
                elif kind == "wrong_type":
                    self.case["terminals"][0]["type"] = "assessment"
                else:
                    self.case["requestIds"] *= 2
                result = self.normalize_preserving_originals()
                self.assertEqual(0, result["requestCount"])
                self.assert_blocked(result, issue)
                self.output.unlink()

    def test_negative_native_malformed_and_failed_terminals_roundtrip_fail(self):
        patches = [({"requestCount": value}, "invalid_terminal_request_count")
                   for value in (None, True, False, "0", -1, 0.0)]
        patches += [(None, "invalid_terminal_request_count"),
                    ({"success": False}, "failed_or_invalid_terminal"),
                    ({"success": 1}, "failed_or_invalid_terminal"),
                    ({"failedCount": 1}, "failed_or_invalid_terminal"),
                    ({"failedCount": False}, "failed_or_invalid_terminal"),
                    ({"cacheHits": None}, "failed_or_invalid_terminal")]
        for patch, issue in patches:
            with self.subTest(patch=patch):
                self.negative_native_assessment()
                if patch is None:
                    del self.case["terminals"][0]["requestCount"]
                else:
                    self.case["terminals"][0].update(patch)
                result = self.normalize_preserving_originals()
                if issue == "invalid_terminal_request_count":
                    self.assertIsNone(result["requestCount"])
                else:
                    self.assertEqual(0, result["requestCount"])
                self.assert_blocked(result, issue)
                self.output.unlink()

    def test_negative_native_zero_calls_do_not_clear_original_failure_evidence(self):
        self.negative_native_assessment()
        baseline_case, baseline_review = deepcopy(self.case), deepcopy(self.review)
        for kind, issue in [("error", "capture_errors"),
                            ("pending", "production_work_unresolved"),
                            ("unsettled", "capture_not_successfully_finished"),
                            ("failed_node", "node_not_completed:s1"),
                            ("unreviewed_retraction", "unreviewed_retraction"),
                            ("confirmed_retraction", "confirmed_retraction"),
                            ("unknown_label", "unknown_node_label:s1"),
                            ("omission", "confirmed_omission:s1")]:
            with self.subTest(kind=kind):
                self.case, self.review = deepcopy(baseline_case), deepcopy(baseline_review)
                if kind == "error":
                    self.case["errors"] = [{"code": "native_assessment_failed"}]
                elif kind == "pending":
                    self.case["blockedReason"] += ";production_work_unresolved"
                    self.case["evidenceReady"] = False
                    self.case["finalState"] = {"pending": 1, "rpcPending": 1}
                elif kind == "unsettled":
                    self.case["settled"] = False
                elif kind == "failed_node":
                    self.case["predictions"][0]["failed"] = True
                elif kind.endswith("retraction"):
                    self.case["retractions"] = [{"sourceId": "s1", "type": "retraction",
                                                  "sourceUnchanged": True}]
                    if kind == "confirmed_retraction":
                        self.review["retractions"] = [{"eventIndex": 0, "confirmed": True,
                                                       "sourceUnchanged": True}]
                elif kind == "unknown_label":
                    self.review["annotations"] = []
                else:
                    self.review["annotations"][0]["needTranslation"] = True
                result = self.normalize_preserving_originals()
                self.assertEqual(0, result["requestCount"])
                self.assert_blocked(result, issue)
                self.output.unlink()

    def test_positive_native_zero_calls_still_requires_live_model(self):
        self.case["terminals"][0]["requestCount"] = 0
        self.assert_blocked(self.normalize_preserving_originals(), "missing_live_translation_evidence")

    def test_negative_finished_page_preserves_untranslated_eligible_candidates(self):
        self.skipped_candidate()
        for completed, blocked in [(False, "screenshot_review_pending"), (True, "")]:
            with self.subTest(capture_completed=completed):
                self.case.update(completed=completed, blockedReason=blocked)
                result = self.normalize()
                self.assertTrue(result["completed"])
                self.assertEqual([], result["normalizationIssues"])
                self.assertEqual(0, result["requestCount"])
                self.assertFalse(result["liveTranslation"])
                self.assertEqual({"nodeId": "s1", "eligible": True, "completed": False,
                                  "failed": False, "pieceIds": ["p1", "p2"]}, result["predictions"][0])
                self.assertEqual(self.case, result["provenance"]["rawCapture"])
                stats, accepted, reasons = _score_case(
                    result, {**SITE, "expectedPageTranslation": False}, self.root)
                self.assertTrue(accepted, reasons)
                self.assertEqual(1, stats["FP"])
                self.assertEqual(0, stats["confirmedOmissions"])
                self.assertEqual(0, stats["unnecessaryRequests"])
                self.output.unlink()

    def test_skipped_independently_positive_node_remains_scored_omission(self):
        self.skipped_candidate()
        self.review["annotations"][0]["needTranslation"] = True
        for eligible in (True, False):
            with self.subTest(eligible=eligible):
                for piece in self.case["predictions"]:
                    piece["eligible"] = eligible
                self.case["productionEligibility"][0]["eligible"] = eligible
                result = self.normalize()
                self.assert_blocked(result, "confirmed_omission:s1")
                self.assertEqual(eligible, result["predictions"][0]["eligible"])
                self.assertFalse(result["predictions"][0]["completed"])
                stats, accepted, reasons = _score_case(
                    result, {**SITE, "expectedPageTranslation": False}, self.root)
                self.assertFalse(accepted)
                self.assertIn("confirmed_omission:s1", reasons)
                self.assertEqual(1, stats["confirmedOmissions"])
                self.assertEqual(1, stats["TP" if eligible else "FN"])
                self.assertEqual(0, stats["translatedPositiveNodes"])
                self.output.unlink()

    def test_skipped_page_still_blocks_failed_unmatched_unknown_and_error_evidence(self):
        self.skipped_candidate()
        baseline_case, baseline_review = deepcopy(self.case), deepcopy(self.review)
        for kind, issue in [("failed", "node_not_completed:s1"),
                            ("unmatched", "unmatched_prediction_source"),
                            ("unknown_flags", "invalid_prediction_flags:s1"),
                            ("unknown_prediction", "unknown_prediction:s1"),
                            ("unknown_truth", "unknown_node_label:s1"),
                            ("error", "capture_errors")]:
            with self.subTest(kind=kind):
                self.case, self.review = deepcopy(baseline_case), deepcopy(baseline_review)
                if kind == "failed":
                    self.case["predictions"][1]["failed"] = True
                elif kind == "unmatched":
                    self.case["predictions"].append(self.piece("p3", "missing", completed=False))
                elif kind == "unknown_flags":
                    del self.case["predictions"][0]["completed"]
                elif kind == "unknown_prediction":
                    self.case["predictions"] = []
                elif kind == "unknown_truth":
                    self.review["annotations"] = []
                else:
                    self.case["errors"] = [{"code": "capture_failed"}]
                result = self.normalize()
                self.assert_blocked(result, issue)
                self.assertEqual(self.case["errors"], result["errors"])
                self.assertEqual(self.case, result["provenance"]["rawCapture"])
                if kind == "failed":
                    self.assertTrue(result["predictions"][0]["failed"])
                    self.assertFalse(result["predictions"][0]["completed"])
                self.output.unlink()

    def test_retractions_require_explicit_review_of_each_original_event(self):
        event = {"type": "retraction", "sourceId": "s1", "sourceUnchanged": True,
                 "previousText": "译文", "displayedText": "Independent source text"}
        self.case["retractions"] = [event]
        result = self.normalize()
        self.assert_blocked(result, "unreviewed_retraction")
        self.assertIsNone(result["retractions"][0]["sourceUnchanged"])
        self.output.unlink()
        self.review["retractions"] = [{"eventIndex": 0, "confirmed": True, "sourceUnchanged": True}]
        result = self.normalize()
        self.assert_blocked(result, "confirmed_retraction")
        self.assertEqual(1, _score_case(result, SITE, self.root)[0]["confirmedRetractions"])
        self.output.unlink()
        self.review["retractions"] = [{"sourceId": "s1", "confirmed": False, "sourceUnchanged": True}]
        result = self.normalize()
        self.assertTrue(result["completed"])
        self.assertEqual(event, result["provenance"]["rawCapture"]["retractions"][0])

    def test_ambiguous_or_fabricated_retraction_reviews_rejected(self):
        event = {"type": "retraction", "sourceId": "s1"}
        for events, reviews in [([], [{"eventIndex": 0, "confirmed": False, "sourceUnchanged": True}]),
                               ([event, event], [{"sourceId": "s1", "confirmed": False, "sourceUnchanged": True}]),
                               ([event], [{"eventIndex": 0, "confirmed": "false", "sourceUnchanged": True}])]:
            with self.subTest(events=events), self.assertRaises(NormalizationError):
                self.case["retractions"], self.review["retractions"] = events, reviews
                self.normalize()

    def test_case_hash_must_match_original_bytes(self):
        self.save()
        self.case_path.write_bytes(self.case_path.read_bytes() + b"\n")
        with self.assertRaisesRegex(NormalizationError, "case_hash_mismatch"):
            normalize_case(self.case_path, self.review_path, self.output, self.root)
        self.assertFalse(self.output.exists())

    def test_screenshot_tampering_missing_file_and_invalid_image_rejected(self):
        shot = self.case["screenshots"][0]
        image = self.attempt / shot["path"]
        for mode in ("tamper", "missing", "placeholder"):
            with self.subTest(mode=mode), self.assertRaises(NormalizationError):
                if mode == "missing":
                    image.unlink()
                else:
                    image.write_bytes(b"not an image")
                    if mode == "placeholder":
                        shot["sha256"] = hashlib.sha256(image.read_bytes()).hexdigest()
                self.normalize()
                self.assertFalse(self.output.exists())
            image.write_bytes(PNG)

    def test_phase_votes_must_be_exact_booleans_including_optional_phases(self):
        for votes in [{}, {**self.review["screenshots"], "before": 1},
                      {**self.review["screenshots"], "invented-phase": True}]:
            with self.subTest(votes=votes), self.assertRaisesRegex(NormalizationError, "screenshot_votes"):
                self.review["screenshots"] = votes
                self.normalize()
        self.review["screenshots"] = dict.fromkeys(REQUIRED_PHASES, True)
        self.case["screenshots"].append({**self.case["screenshots"][0], "phase": "disclosure-open"})
        self.review["screenshots"]["disclosure-open"] = False
        self.assert_blocked(self.normalize(), "rejected_screenshot_reviews")

    def test_paths_cannot_escape_evidence_root(self):
        for name in ("../review.json", "C:/outside.png", "C:outside.png", "\\server\\share.png", "/outside.png"):
            with self.subTest(name=name), self.assertRaisesRegex(NormalizationError, "screenshot_path"):
                self.case["screenshots"][0]["path"] = name
                self.normalize()
        self.case["screenshots"][0]["path"] = self.case["screenshots"][0]["phase"] + ".png"
        other_root = self.root / "empty"
        other_root.mkdir()
        with self.assertRaisesRegex(NormalizationError, "outside_evidence_root"):
            self.normalize(other_root)

    def test_invalid_geometry_is_never_replaced_with_invented_position(self):
        self.case["sourceNodes"][0]["rects"] = [{"x": 0, "y": 0, "width": -1, "height": 20}]
        result = self.normalize()
        self.assertIsNone(result["sourceNodes"][0]["position"])
        self.assert_blocked(result, "invalid_source_node")

    def test_output_is_exclusive_and_original_aliases_rejected(self):
        self.save()
        for output in (self.case_path, self.review_path, self.attempt / "before.png"):
            with self.subTest(output=output), self.assertRaisesRegex(NormalizationError, "new_file"):
                normalize_case(self.case_path, self.review_path, output, self.root)
        self.output.write_bytes(b"keep prior evidence")
        with self.assertRaisesRegex(NormalizationError, "new_file"):
            normalize_case(self.case_path, self.review_path, self.output, self.root)
        self.assertEqual(b"keep prior evidence", self.output.read_bytes())

    def test_simulated_capture_cannot_be_promoted(self):
        self.case["simulation"] = True
        self.assert_blocked(self.normalize(), "simulated_capture")

    def test_cli_exit_codes_complete_incomplete_invalid(self):
        argv = ["--case", str(self.case_path), "--review", str(self.review_path),
                "--output", str(self.output), "--evidence-root", str(self.root)]
        self.save()
        with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            self.assertEqual(0, main(argv))
            self.output.unlink()
            self.case["errors"] = [{"code": "failed"}]
            self.save()
            self.assertEqual(1, main(argv))
            self.assertEqual(2, main(argv))


if __name__ == "__main__":
    unittest.main()
