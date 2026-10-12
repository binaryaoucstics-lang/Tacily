"""Offline coverage fixtures only; these are not real website quality results."""

from contextlib import redirect_stderr, redirect_stdout
from copy import deepcopy
import io
import json
import unittest

from evaluate import _score_case
from normalize import main, normalize_case, NormalizationError
import test_normalize as baseline


class CoverageScopeTests(unittest.TestCase):
    # Reuse the frozen original evidence fixture, without inheriting or editing
    # its tests. All generated evidence stays under the checkout's .parallel/.
    setUp = baseline.NormalizationTests.setUp
    source = baseline.NormalizationTests.source
    piece = baseline.NormalizationTests.piece
    save = baseline.NormalizationTests.save

    def skipped(self, *, native=True, eligible=True):
        self.case.update(expectedPageTranslation=False, pageDecision=False,
                         liveTranslation=False, requestIds=[], terminals=[])
        self.case["predictions"][0].update(eligible=eligible, completed=False)
        self.case["productionEligibility"][0]["eligible"] = eligible
        self.review["pageAnnotation"]["needTranslation"] = False
        if native:
            self.case["requestIds"] = ["local-assessment"]
            self.case["terminals"] = [{"type": "terminal", "id": "local-assessment",
                "caseId": self.case["id"], "success": True, "failedCount": 0,
                "cacheHits": 0, "requestCount": 0}]

    def run_scope(self, mode="automatic-page", name=None):
        self.save()
        output = self.root / (name or mode + ".json")
        result = normalize_case(self.case_path, self.review_path, output, self.root,
                                coverage_scope=mode)
        self.assertEqual(result, json.loads(output.read_text(encoding="utf-8")))
        self.assertEqual(self.case, result["provenance"]["rawCapture"])
        self.assertEqual(self.review, result["provenance"]["rawReview"])
        self.assertEqual({"schemaVersion": 1, "mode": mode}, result["coverageScope"])
        return result

    def counts(self, result, positive, translated, skipped, omissions):
        expected = {"annotatedPositiveNodes": positive, "translatedPositiveNodes": translated,
                    "unTranslatedPositiveNodes": positive - translated,
                    "policySkippedPositiveNodes": skipped, "confirmedOmissions": omissions}
        for key, value in expected.items():
            self.assertIs(type(result[key]), int, key)
            self.assertEqual(value, result[key], key)
        self.assertEqual(translated / positive if positive else None, result["translationCoverage"])

    def blocked(self, result, issue, *, omissions=1):
        self.assertFalse(result["completed"])
        self.assertIn(issue, result["normalizationIssues"])
        self.counts(result, 1, 0, 0, omissions)

    def test_default_and_explicit_strict_preserve_original_scored_omission(self):
        self.skipped()
        self.save()
        default = normalize_case(self.case_path, self.review_path, self.output, self.root)
        strict = self.run_scope("strict")
        self.assertEqual(default, strict)
        self.blocked(strict, "confirmed_omission:s1")
        self.assertEqual({"schemaVersion": 1, "mode": "strict"}, default["coverageScope"])
        stats, accepted, reasons = _score_case(
            default, {**baseline.SITE, "expectedPageTranslation": False}, self.root)
        self.assertFalse(accepted)
        self.assertIn("confirmed_omission:s1", reasons)
        self.assertEqual(1, stats["TP"])
        self.assertEqual(1, stats["confirmedOmissions"])
        self.assertEqual(0, stats["translatedPositiveNodes"])
        automatic = self.run_scope()
        self.assertTrue(automatic["completed"])
        self.counts(automatic, 1, 0, 1, 0)
        self.assertEqual(strict["annotations"], automatic["annotations"])
        self.assertEqual(strict["predictions"], automatic["predictions"])
        # The independent evaluator worker is absent: automatic output is only
        # inspected here, never scored using the baseline's strict evaluator.

    def test_skips_preserve_both_recognized_and_missed_positive_labels(self):
        for eligible in (False, True):
            with self.subTest(eligible=eligible):
                self.skipped(eligible=eligible)
                result = self.run_scope(name=f"skip-{eligible}.json")
                self.assertTrue(result["completed"], result["blockedReason"])
                self.assertEqual([], result["normalizationIssues"])
                self.counts(result, 1, 0, 1, 0)
                self.assertIs(result["annotations"][0]["needTranslation"], True)
                self.assertEqual(eligible, result["predictions"][0]["eligible"])
                self.assertFalse(result["predictions"][0]["completed"])
                self.assertEqual(0, result["requestCount"])
                self.assertEqual(self.case["terminals"], result["hostEvents"][1:])

    def test_live_capabilities_without_terminal_retain_omission_and_failure(self):
        for eligible in (False, True):
            with self.subTest(eligible=eligible):
                self.skipped(native=False, eligible=eligible)
                result = self.run_scope(name=f"no-terminal-{eligible}.json")
                self.blocked(result, "missing_local_terminal_evidence")
                self.assertIn("confirmed_omission:s1", result["normalizationIssues"])
                self.assertEqual([self.case["capabilities"]], result["hostEvents"])
                self.assertEqual([], result["requestIds"])
                self.assertEqual(0, result["requestCount"])
                self.assertIs(result["annotations"][0]["needTranslation"], True)
                self.assertEqual(eligible, result["predictions"][0]["eligible"])

    def test_successful_zero_call_terminal_requires_live_capabilities(self):
        case = deepcopy(self.case)
        for i, capabilities in enumerate((None, False, 0, "true")):
            with self.subTest(live=capabilities):
                self.case = deepcopy(case)
                self.skipped()
                if capabilities is None:
                    self.case.pop("capabilities")
                else:
                    self.case["capabilities"]["live"] = capabilities
                result = self.run_scope(name=f"no-live-capabilities-{i}.json")
                self.blocked(result, "missing_live_capabilities_evidence")
                self.assertIn("confirmed_omission:s1", result["normalizationIssues"])
                self.assertEqual(0, result["requestCount"])
                self.assertEqual(self.case["terminals"][-1], result["hostEvents"][-1])

    def test_missing_local_terminal_blocks_even_without_positive_nodes(self):
        self.skipped(native=False)
        self.review["annotations"][0]["needTranslation"] = False
        result = self.run_scope()
        self.assertFalse(result["completed"])
        self.assertIn("missing_local_terminal_evidence", result["normalizationIssues"])
        self.counts(result, 0, 0, 0, 0)

    def test_public_scale_negative_fixture_keeps_nine_of_412_positive_nodes(self):
        self.skipped()
        self.case["sourceNodes"] = [self.source(f"s{i}") for i in range(412)]
        self.case["sourceNodes"][9]["sourceText"] = "FOLLOW"
        self.case["predictions"] = [self.piece(f"p{i}", f"s{i}", eligible=i < 5,
                                               completed=False) for i in range(412)]
        self.case["productionEligibility"] = [
            {"sourceId": f"s{i}", "eligible": i < 5} for i in range(412)]
        self.review["annotations"] = [
            {"sourceId": f"s{i}", "needTranslation": i < 9} for i in range(412)]
        result = self.run_scope()
        self.assertTrue(result["completed"], result["blockedReason"])
        self.counts(result, 9, 0, 9, 0)
        self.assertEqual(412, len(result["sourceNodes"]))
        self.assertEqual(412, len(result["annotations"]))
        self.assertEqual(412, len(result["predictions"]))
        self.assertEqual(9, sum(r["needTranslation"] for r in result["annotations"]))
        self.assertEqual(5, sum(r["eligible"] for r in result["predictions"][:9]))
        strict = self.run_scope("strict")
        self.counts(strict, 9, 0, 0, 9)
        stats, accepted, _ = _score_case(
            strict, {**baseline.SITE, "expectedPageTranslation": False}, self.root)
        self.assertFalse(accepted)
        self.assertEqual((5, 4, 403, 0), tuple(stats[k] for k in ("TP", "FN", "TN", "FP")))

    def test_positive_live_completion_roundtrips_strict_and_stays_translated(self):
        strict = self.run_scope("strict")
        automatic = self.run_scope()
        for result in (strict, automatic):
            self.assertTrue(result["completed"])
            self.counts(result, 1, 1, 0, 0)
        stats, accepted, reasons = _score_case(strict, baseline.SITE, self.root)
        self.assertTrue(accepted, reasons)
        self.assertEqual(1, stats["translatedPositiveNodes"])

    def test_positive_pages_keep_pending_and_unrecognized_omissions(self):
        for eligible in (False, True):
            with self.subTest(eligible=eligible):
                self.case["predictions"][0].update(eligible=eligible, completed=False)
                self.case["productionEligibility"][0]["eligible"] = eligible
                result = self.run_scope(name=f"positive-{eligible}.json")
                self.blocked(result, "confirmed_omission:s1")
                if eligible:
                    self.assertIn("node_not_completed:s1", result["normalizationIssues"])

    def test_translation_counts_require_successful_identified_request_evidence(self):
        case = deepcopy(self.case)
        for kind, issue in (("wrong_case", "host_event_identity_mismatch"),
                ("wrong_id", "request_terminal_identity_mismatch"),
                ("duplicate", "duplicate_or_invalid_terminal_id"),
                ("failed", "failed_or_invalid_terminal")):
            with self.subTest(kind=kind):
                self.case = deepcopy(case)
                terminal = self.case["terminals"][0]
                if kind == "wrong_case":
                    terminal["caseId"] = "unknown"
                elif kind == "wrong_id":
                    terminal["id"] = "unknown"
                elif kind == "duplicate":
                    self.case["terminals"].append(deepcopy(terminal))
                else:
                    terminal.update(success=False, failedCount=1)
                result = self.run_scope(name=f"completion-{kind}.json")
                self.blocked(result, issue)
                self.assertIn("contradictory_node_completion:s1", result["normalizationIssues"])

    def test_atomic_and_split_pieces_count_source_nodes_once(self):
        self.skipped()
        self.case["sourceNodes"].append(self.source("peer"))
        self.review["annotations"].append({"sourceId": "peer", "needTranslation": True})
        self.case["productionEligibility"].append({"sourceId": "peer", "eligible": True})
        self.case["predictions"][0]["sourceIds"] = ["s1", "peer"]
        self.case["predictions"].append(self.piece("split", "s1", completed=False))
        result = self.run_scope()
        self.assertTrue(result["completed"])
        self.counts(result, 2, 0, 2, 0)
        self.assertEqual(["p1", "split"], result["predictions"][0]["pieceIds"])
        self.assertEqual(["p1"], result["predictions"][1]["pieceIds"])

    def test_wrongly_skipped_positive_page_keeps_omission(self):
        self.skipped()
        self.case["expectedPageTranslation"] = True
        self.review["pageAnnotation"]["needTranslation"] = True
        result = self.run_scope()
        self.blocked(result, "confirmed_omission:s1")
        self.assertIn("missing_live_translation_evidence", result["normalizationIssues"])

    def test_negative_page_actually_selected_cannot_defer_coverage(self):
        self.skipped()
        self.case["pageDecision"] = True
        result = self.run_scope()
        self.blocked(result, "confirmed_omission:s1")
        self.assertIn("node_not_completed:s1", result["normalizationIssues"])

    def test_independent_negative_page_label_is_required(self):
        self.skipped()
        case, review = deepcopy(self.case), deepcopy(self.review)
        for i, page_label in enumerate((None, {"needTranslation": True},
                {"needTranslation": "unknown"}, {"needTranslation": False, "reviewed": False},
                {"needTranslation": False, "independent": False})):
            with self.subTest(page_label=page_label):
                self.case, self.review = deepcopy(case), deepcopy(review)
                if page_label is None:
                    del self.review["pageAnnotation"]
                else:
                    self.review["pageAnnotation"] = page_label
                result = self.run_scope(name=f"page-label-{i}.json")
                self.blocked(result, "confirmed_omission:s1")
                if page_label is None or page_label.get("needTranslation") is not True:
                    self.assertIn("unknown_page_label", result["normalizationIssues"])

    def test_actual_nonzero_calls_override_forged_zero_summary(self):
        self.skipped()
        self.case["requestCount"] = 0
        self.case["terminals"][0]["requestCount"] = 3
        self.case["terminals"].append({**self.case["terminals"][0], "id": "other", "requestCount": 2})
        self.case["requestIds"].append("other")
        result = self.run_scope()
        self.assertEqual(5, result["requestCount"])
        self.blocked(result, "unnecessary_translation_request")
        self.assertIn("confirmed_omission:s1", result["normalizationIssues"])

    def test_negative_terminal_count_corruption_cannot_defer_coverage(self):
        self.skipped()
        for i, value in enumerate((-1, True, 0.0, "0", None)):
            with self.subTest(count=value):
                self.case["terminals"][0]["requestCount"] = value
                result = self.run_scope(name=f"count-{i}.json")
                self.blocked(result, "invalid_terminal_request_count")
                self.assertIsNone(result["requestCount"])
        del self.case["terminals"][0]["requestCount"]
        self.blocked(self.run_scope(), "invalid_terminal_request_count")

    def test_host_event_and_request_corruption_blocks_policy_skip(self):
        self.skipped()
        case, review = deepcopy(self.case), deepcopy(self.review)
        for kind, issue in (("missing", "request_terminal_identity_mismatch"),
                ("extra", "request_terminal_identity_mismatch"),
                ("duplicate", "duplicate_or_invalid_terminal_id"),
                ("wrong_id", "request_terminal_identity_mismatch"),
                ("wrong_case", "host_event_identity_mismatch"),
                ("invalid_id", "duplicate_or_invalid_terminal_id"),
                ("wrong_type", "host_event_type_mismatch"),
                ("failed", "failed_or_invalid_terminal"),
                ("boolean_failed_count", "failed_or_invalid_terminal"),
                ("invalid_cache_count", "failed_or_invalid_terminal"),
                ("duplicate_request", "invalid_request_identities"),
                ("invalid_request", "invalid_request_identities"),
                ("wrong_cap_case", "host_event_identity_mismatch"),
                ("wrong_cap_type", "host_event_type_mismatch"),
                ("non_live", "non_live_or_simulated_capture")):
            with self.subTest(kind=kind):
                self.case, self.review = deepcopy(case), deepcopy(review)
                terminal = self.case["terminals"][0]
                if kind == "missing":
                    self.case["terminals"] = []
                elif kind in ("extra", "duplicate"):
                    self.case["terminals"].append({**terminal, "id": "extra" if kind == "extra" else terminal["id"]})
                elif kind == "wrong_id":
                    terminal["id"] = "unknown"
                elif kind == "wrong_case":
                    terminal["caseId"] = "unknown"
                elif kind == "invalid_id":
                    terminal["id"] = ["bad-id"]
                elif kind == "wrong_type":
                    terminal["type"] = "packet"
                elif kind == "failed":
                    terminal.update(success=False, failedCount=1)
                elif kind == "boolean_failed_count":
                    terminal["failedCount"] = False
                elif kind == "invalid_cache_count":
                    terminal["cacheHits"] = True
                elif kind == "duplicate_request":
                    self.case["requestIds"] *= 2
                elif kind == "invalid_request":
                    self.case["requestIds"] = [False]
                elif kind == "wrong_cap_case":
                    self.case["capabilities"]["caseId"] = "unknown"
                elif kind == "wrong_cap_type":
                    self.case["capabilities"]["type"] = "terminal"
                else:
                    self.case["capabilities"]["live"] = False
                self.blocked(self.run_scope(name=f"events-{kind}.json"), issue)

    def test_missing_host_evidence_is_not_proof_of_zero_calls(self):
        self.skipped(native=False)
        self.case["capabilities"] = None
        result = self.run_scope()
        self.blocked(result, "confirmed_omission:s1")
        self.assertEqual([], result["hostEvents"])

    def test_concurrent_zero_call_terminals_preserve_bijection_and_order(self):
        self.skipped()
        self.case["requestIds"] = ["r1", "r2"]
        self.case["terminals"] = [{**self.case["terminals"][0], "id": identity}
                                  for identity in ("r2", "r1")]
        result = self.run_scope()
        self.assertTrue(result["completed"])
        self.counts(result, 1, 0, 1, 0)
        self.assertEqual(["r1", "r2"], result["requestIds"])
        self.assertEqual(["r2", "r1"], [e["id"] for e in result["hostEvents"][1:]])

    def test_unknown_failed_and_contradictory_predictions_remain_blocking(self):
        self.skipped()
        case, review = deepcopy(self.case), deepcopy(self.review)
        for kind, issue in (("unknown_flags", "invalid_prediction_flags:s1"),
                ("unknown_prediction", "unknown_prediction:s1"),
                ("failed", "node_not_completed:s1"),
                ("zero_call_completion", "contradictory_node_completion:s1"),
                ("ineligible_completion", "contradictory_piece_completion:s1"),
                ("eligibility_disagreement", "contradictory_production_eligibility:s1"),
                ("reverse_disagreement", "contradictory_production_eligibility:s1"),
                ("duplicate_piece", "missing_or_duplicate_prediction_id"),
                ("unmatched", "unmatched_prediction_source:missing"),
                ("source_audit", "prediction_source_audit_issue:s1")):
            with self.subTest(kind=kind):
                self.case, self.review = deepcopy(case), deepcopy(review)
                piece = self.case["predictions"][0]
                if kind == "unknown_flags":
                    del piece["eligible"]
                elif kind == "unknown_prediction":
                    self.case["predictions"] = []
                    self.case["productionEligibility"] = []
                elif kind == "failed":
                    piece["failed"] = True
                elif kind == "zero_call_completion":
                    piece["completed"] = True
                elif kind == "ineligible_completion":
                    piece.update(eligible=False, completed=True)
                elif kind == "eligibility_disagreement":
                    self.case["productionEligibility"][0]["eligible"] = False
                elif kind == "reverse_disagreement":
                    piece["eligible"] = False
                elif kind == "duplicate_piece":
                    self.case["predictions"].append(deepcopy(piece))
                elif kind == "unmatched":
                    piece["sourceId"] = "missing"
                else:
                    piece["auditIssue"] = "source_not_independently_visible"
                self.blocked(self.run_scope(name=f"prediction-{kind}.json"), issue)

    def test_explicit_finished_rejection_can_skip_but_retired_unknown_cannot(self):
        self.skipped(eligible=False)
        self.case["predictions"] = []
        result = self.run_scope(name="rejected.json")
        self.assertTrue(result["completed"])
        self.counts(result, 1, 0, 1, 0)
        self.case["sourceNodes"][0]["currentlyRendered"] = False
        self.case["sourceObservations"] = [{"sourceId": "s1", "eligible": None,
            "sourceText": "Independent source text", "rendered": True, "pieces": []}]
        self.blocked(self.run_scope(name="retired.json"), "unknown_retired_source:s1")

    def test_capture_review_and_retraction_failures_still_block(self):
        self.skipped()
        case, review = deepcopy(self.case), deepcopy(self.review)
        for kind, issue in (("errors", "capture_errors"),
                ("settled", "capture_not_successfully_finished"),
                ("captureFinished", "capture_not_successfully_finished"),
                ("evidenceReady", "capture_not_successfully_finished"),
                ("visitSuccess", "capture_not_successfully_finished"),
                ("domainVerified", "capture_not_successfully_finished"),
                ("blockedReason", "original_failure"),
                ("completed", "capture_completion_not_established"),
                ("simulation", "non_live_or_simulated_capture"),
                ("screenshots", "missing_or_rejected_screenshot_reviews"),
                ("geometry", "invalid_source_node:s1"),
                ("retraction", "confirmed_retraction:0")):
            with self.subTest(kind=kind):
                self.case, self.review = deepcopy(case), deepcopy(review)
                if kind == "errors":
                    self.case["errors"] = [{"code": "original_failure"}]
                elif kind == "blockedReason":
                    self.case["blockedReason"] = "original_failure;screenshot_review_pending"
                elif kind == "completed":
                    self.case["blockedReason"] = ""
                elif kind == "simulation":
                    self.case["simulation"] = True
                elif kind == "screenshots":
                    self.review["screenshots"]["before"] = False
                elif kind == "geometry":
                    self.case["sourceNodes"][0]["rects"] = []
                elif kind == "retraction":
                    self.case["retractions"] = [{"sourceId": "s1", "sourceUnchanged": True}]
                    self.review["retractions"] = [{"eventIndex": 0, "confirmed": True, "sourceUnchanged": True}]
                else:
                    self.case[kind] = False
                self.blocked(self.run_scope(name=f"failure-{kind}.json"), issue)

    def test_unknown_independent_node_labels_do_not_become_policy_skips(self):
        self.skipped()
        for i, patch in enumerate(({"needTranslation": "unknown"},
                {"needTranslation": True, "independent": False},
                {"needTranslation": True, "reviewed": False})):
            with self.subTest(label=patch):
                self.review["annotations"] = [{"sourceId": "s1", **patch}]
                result = self.run_scope(name=f"unknown-label-{i}.json")
                self.assertFalse(result["completed"])
                self.assertIn("unknown_node_label:s1", result["normalizationIssues"])
                self.counts(result, 0, 0, 0, 0)

    def test_negative_nodes_do_not_receive_translation_or_skip_credit(self):
        self.skipped()
        self.review["annotations"][0]["needTranslation"] = False
        result = self.run_scope()
        self.assertTrue(result["completed"])
        self.counts(result, 0, 0, 0, 0)

    def test_page_label_disagreement_blocks_even_without_positive_nodes(self):
        self.skipped()
        self.review["annotations"][0]["needTranslation"] = False
        self.review["pageAnnotation"]["needTranslation"] = True
        result = self.run_scope()
        self.assertFalse(result["completed"])
        self.assertIn("initial_page_label_disagrees_with_review", result["normalizationIssues"])
        self.counts(result, 0, 0, 0, 0)

    def test_all_coverage_summaries_and_mode_are_recomputed(self):
        self.skipped()
        self.case.update(coverageScope={"schemaVersion": 900, "mode": "forged"},
            annotatedPositiveNodes=0, translatedPositiveNodes=100, translationCoverage=1.0,
            unTranslatedPositiveNodes=-3, policySkippedPositiveNodes=999, confirmedOmissions=0,
            requestCount=123, hostEvents=[{"type": "error"}])
        result = self.run_scope()
        self.assertTrue(result["completed"])
        self.counts(result, 1, 0, 1, 0)
        self.assertEqual(0, result["requestCount"])
        self.assertEqual([self.case["capabilities"], *self.case["terminals"]], result["hostEvents"])
        strict = self.run_scope("strict")
        self.blocked(strict, "confirmed_omission:s1")

    def test_invalid_modes_rejected_before_any_output_or_evidence_access(self):
        class EqualToStrict:
            def __eq__(self, other):
                return other == "strict"

        for mode in (None, True, False, 0, 1.0, "", "automatic", "STRICT", {}, [],
                     ("strict",), EqualToStrict()):
            with self.subTest(mode=mode), self.assertRaisesRegex(NormalizationError, "invalid_coverage_scope"):
                normalize_case(self.case_path, self.review_path, self.output, self.root, coverage_scope=mode)
        self.assertFalse(self.output.exists())

    def test_cli_versioned_scope_default_and_explicit_and_invalid_mode(self):
        self.skipped()
        self.save()
        argv = ["--case", str(self.case_path), "--review", str(self.review_path),
                "--evidence-root", str(self.root)]
        for mode in (None, "strict", "automatic-page"):
            with self.subTest(mode=mode):
                output = self.root / f"cli-{mode}.json"
                stdout = io.StringIO()
                args = argv + ["--output", str(output)]
                if mode is not None:
                    args += ["--coverage-scope", mode]
                with redirect_stdout(stdout), redirect_stderr(io.StringIO()):
                    code = main(args)
                self.assertEqual(0 if mode == "automatic-page" else 1, code)
                metadata = {"schemaVersion": 1, "mode": mode or "strict"}
                self.assertEqual(metadata, json.loads(stdout.getvalue())["coverageScope"])
                self.assertEqual(metadata, json.loads(output.read_text(encoding="utf-8"))["coverageScope"])
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            main(argv + ["--output", str(self.output), "--coverage-scope", "invalid"])
        self.assertEqual(2, error.exception.code)
        self.assertFalse(self.output.exists())

    def test_scopes_require_distinct_new_outputs_and_preserve_original_bytes(self):
        self.skipped()
        self.save()
        originals = {p: p.read_bytes() for p in [self.case_path, self.review_path,
            *(self.attempt / s["path"] for s in self.case["screenshots"])]}
        strict = normalize_case(self.case_path, self.review_path, self.output, self.root)
        output_bytes = self.output.read_bytes()
        with self.assertRaisesRegex(NormalizationError, "output_must_be_new_file"):
            normalize_case(self.case_path, self.review_path, self.output, self.root,
                           coverage_scope="automatic-page")
        self.assertEqual(output_bytes, self.output.read_bytes())
        automatic = normalize_case(self.case_path, self.review_path, self.root / "automatic.json",
                                   self.root, coverage_scope="automatic-page")
        self.assertFalse(strict["completed"])
        self.assertTrue(automatic["completed"])
        for path, content in originals.items():
            self.assertEqual(content, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
