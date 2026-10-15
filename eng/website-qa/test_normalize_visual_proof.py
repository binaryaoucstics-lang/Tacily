"""Independently authored offline visual votes, never real website quality proof."""

import base64
from contextlib import redirect_stderr, redirect_stdout
from copy import deepcopy
import hashlib
import inspect
import io
import json
from pathlib import Path
import tempfile
import unittest

from evaluate import _finish, _quality_gates, _score_case
from normalize import main, normalize_case, NormalizationError, REQUIRED_PHASES


ROOT = Path(__file__).resolve().parents[2]
PIXEL = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")


class VisualProofTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="visual-normalizer-", dir=ROOT / ".parallel")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.case_path = self.root / "capture.json"
        self.review_path = self.root / "review.json"
        self.output = self.root / "normalized.json"
        texts = [("article", "The observatory opened in 1842.\nTickets cost 7 dollars.", True),
                 ("chinese", "中文说明保持原样。", False), ("metadata", "ISBN 0123456789", False)]
        self.case = {
            "schemaVersion": 1, "id": "offline-visual", "sourceHead": "d" * 40,
            "sourceHashes": {"offline-adapter.js": "e" * 64}, "manifestHash": "f" * 64,
            "expectedPageTranslation": True, "pageDecision": True, "liveTranslation": True,
            "simulation": False, "completed": False, "blockedReason": "screenshot_review_pending",
            "captureFinished": True, "evidenceReady": True, "settled": True,
            "visitSuccess": True, "domainVerified": True, "errors": [], "sourceNodes": [],
            "productionEligibility": [], "predictions": [], "retractions": [], "screenshots": [],
            "capabilities": {"type": "capabilities", "id": "offline-cap", "caseId": "offline-visual",
                             "live": True},
            "requestIds": ["offline-request"],
            "terminals": [{"type": "terminal", "id": "offline-request", "caseId": "offline-visual",
                           "success": True, "requestCount": 1, "failedCount": 0, "cacheHits": 0}]}
        self.review = {"pageAnnotation": {"needTranslation": True, "reviewed": True,
                                          "independent": True, "note": "Frozen page label"},
                       "annotations": [], "retractions": [], "screenshots": {}}
        rows = []
        for index, (identity, text, positive) in enumerate(texts):
            self.case["sourceNodes"].append({
                "id": identity, "sourceText": text, "displayedText": "producer output is not a vote",
                "tag": "P", "role": "paragraph", "rendered": True, "currentlyRendered": True,
                "rects": [{"x": 12, "y": 20 + index * 40, "width": 500, "height": 32}]})
            self.case["productionEligibility"].append({"sourceId": identity, "eligible": positive})
            self.case["predictions"].append({
                "id": "piece-" + identity, "sourceId": identity, "sourceText": text,
                "sourceEvidence": "matched", "auditIssue": None,
                "eligible": positive, "completed": positive, "failed": False})
            self.review["annotations"].append({
                "sourceId": identity, "needTranslation": positive, "reviewed": True,
                "independent": True, "note": "Frozen independent source label " + identity})
            rows.append({"sourceId": identity, "sourceTextSha256": hashlib.sha256(text.encode()).hexdigest(),
                         "outcome": "correct_translation" if positive else "unchanged",
                         "reviewed": True, "independent": True,
                         "evidence": [{"phase": "stable-full", "note": "Offline authored visual outcome " + identity}]})
        for phase in sorted(REQUIRED_PHASES | {"before-full", "after-full", "stable-full"}):
            image = self.root / (phase + ".png")
            image.write_bytes(PIXEL)
            self.case["screenshots"].append({"phase": phase, "path": image.name,
                                             "sha256": hashlib.sha256(PIXEL).hexdigest()})
            self.review["screenshots"][phase] = True
        # A frozen independent source review identity; no capture-derived truth.
        source_hash = hashlib.sha256(json.dumps(self.review, ensure_ascii=False).encode()).hexdigest()
        self.review["sourceReviewSha256"] = source_hash
        self.review["visualReview"] = {"schemaVersion": 1, "sourceReviewSha256": source_hash,
                                       "reviewed": True, "independent": True, "nodes": rows}

    def save(self, mutate_review=None):
        self.case_path.write_text(json.dumps(self.case, ensure_ascii=False), encoding="utf-8")
        case_hash = hashlib.sha256(self.case_path.read_bytes()).hexdigest()
        self.review["caseSha256"] = case_hash
        if isinstance(self.review.get("visualReview"), dict):
            self.review["visualReview"]["caseSha256"] = case_hash
        if mutate_review is not None:
            mutate_review(self.review)
        self.review_path.write_text(json.dumps(self.review, ensure_ascii=False), encoding="utf-8")

    def normalize(self, *, scope="strict", required=True, mutate_review=None):
        self.save(mutate_review)
        return normalize_case(self.case_path, self.review_path, self.output, self.root,
                              coverage_scope=scope, require_visual_review=required)

    def blocked(self, result, issue):
        self.assertFalse(result["completed"])
        self.assertIn(issue, result["normalizationIssues"])

    def test_complete_true_and_false_votes_preserve_every_original_and_label(self):
        result = self.normalize()
        self.assertTrue(result["completed"], result["blockedReason"])
        self.assertEqual({"schemaVersion": 1, "required": True}, result["visualCoverage"])
        self.assertEqual(self.review["visualReview"], result["visualReview"])
        self.assertEqual(self.review["sourceReviewSha256"], result["sourceReviewSha256"])
        self.assertEqual(self.case, result["provenance"]["rawCapture"])
        self.assertEqual(self.review, result["provenance"]["rawReview"])
        for original, normalized in zip(self.review["annotations"], result["annotations"]):
            self.assertEqual(original, {k: v for k, v in normalized.items() if k != "nodeId"})
        self.assertEqual(self.review["pageAnnotation"], result["pageAnnotation"])
        self.assertEqual(1, result["translatedPositiveNodes"])
        self.assertEqual(0, result["confirmedOmissions"])
        for counter in ("unknownVisualNodes", "incorrectVisualTranslations", "unnecessaryVisualTranslations"):
            self.assertEqual(0, result[counter])
        self.assertEqual(self.case, json.loads(self.case_path.read_text(encoding="utf-8")))
        self.assertEqual(self.review, json.loads(self.review_path.read_text(encoding="utf-8")))
        before = self.output.read_bytes()
        with self.assertRaisesRegex(NormalizationError, "output_must_be_new_file"):
            normalize_case(self.case_path, self.review_path, self.output, require_visual_review=True)
        self.assertEqual(before, self.output.read_bytes())

    def test_machine_success_with_untranslated_pixels_is_an_omission_in_both_scopes(self):
        self.review["visualReview"]["nodes"][0].update(
            outcome="unchanged", evidence=[{"phase": "after-full", "note": "English remains visible"}])
        for scope in ("strict", "automatic-page"):
            with self.subTest(scope=scope):
                result = self.normalize(scope=scope)
                self.blocked(result, "confirmed_omission:article")
                self.assertTrue(result["predictions"][0]["completed"])
                self.assertEqual(0, result["translatedPositiveNodes"])
                self.assertEqual(1, result["unTranslatedPositiveNodes"])
                self.assertEqual(0, result["policySkippedPositiveNodes"])
                self.output.unlink()

    def test_partial_english_and_changed_fact_fail_with_explicit_counters(self):
        for outcome, note in [("partial_translation", "Second sentence still English"),
                              ("incorrect_translation", "Year changed from 1842 to 1942")]:
            with self.subTest(outcome=outcome):
                self.review["visualReview"]["nodes"][0].update(
                    outcome=outcome, evidence=[{"phase": "stable-full", "note": note}])
                result = self.normalize()
                self.blocked(result, "incorrect_visual_translation:article")
                self.assertEqual(1, result["incorrectVisualTranslations"])
                self.assertEqual(0, result["translatedPositiveNodes"])
                self.assertEqual(1, result["confirmedOmissions"])
                self.output.unlink()

    def test_unknown_and_not_visible_remain_unresolved_even_on_false_nodes(self):
        for index in (0, 1):
            for outcome in ("unknown", "not_visible"):
                with self.subTest(index=index, outcome=outcome):
                    original = deepcopy(self.review["visualReview"]["nodes"][index])
                    self.review["visualReview"]["nodes"][index]["outcome"] = outcome
                    result = self.normalize()
                    self.blocked(result, "unknown_visual_node:" + original["sourceId"])
                    self.assertEqual(1, result["unknownVisualNodes"])
                    self.output.unlink()
                    self.review["visualReview"]["nodes"][index] = original

    def test_unnecessary_translation_is_counted_without_changing_recognition(self):
        self.review["visualReview"]["nodes"][1]["outcome"] = "unnecessary_translation"
        result = self.normalize()
        self.assertTrue(result["completed"], result["blockedReason"])
        self.assertEqual([], result["normalizationIssues"])
        self.assertEqual(1, result["unnecessaryVisualTranslations"])
        self.assertFalse(result["annotations"][1]["needTranslation"])
        self.assertFalse(result["predictions"][1]["eligible"])
        self.assertEqual(1, result["translatedPositiveNodes"])

    def false_positive_case(self, true_count, false_count):
        """Original offline native work with varied Chinese/metadata mistakes."""
        site = {"id": self.case["id"], "registeredDomain": "offline.test",
                "url": "https://offline.test/observatory", "category": "knowledge",
                "language": "en", "expectedPageTranslation": True, "split": "development"}
        self.case.update(site, title="Offline invented observatory",
                         finalUrl=site["url"], viewport={"width": 1280, "height": 720},
                         capturedAt="2026-10-14T00:00:00Z")
        for index in range(1, true_count):
            identity = "required-" + str(index)
            text = "The observatory has " + str(index) + " rooms."
            self.case["sourceNodes"].append({**deepcopy(self.case["sourceNodes"][0]),
                                             "id": identity, "sourceText": text})
            self.case["productionEligibility"].append({"sourceId": identity, "eligible": True})
            self.case["predictions"].append({**deepcopy(self.case["predictions"][0]),
                                            "id": "piece-" + identity, "sourceId": identity,
                                            "sourceText": text})
            self.review["annotations"].append({**deepcopy(self.review["annotations"][0]),
                                                "sourceId": identity})
            self.review["visualReview"]["nodes"].append({
                **deepcopy(self.review["visualReview"]["nodes"][0]), "sourceId": identity,
                "sourceTextSha256": hashlib.sha256(text.encode()).hexdigest()})
        for index in range(1, false_count + 1):
            self.case["productionEligibility"][index]["eligible"] = True
            self.case["predictions"][index].update(eligible=True, completed=True)
            self.review["visualReview"]["nodes"][index].update(
                outcome="unnecessary_translation",
                evidence=[{"phase": "after-full" if index == 1 else "scroll-after",
                           "note": "Offline unnecessary output on " + ("Chinese" if index == 1 else "ISBN")}])
        self.case["terminals"][0]["requestCount"] = true_count + false_count
        frozen = {key: value for key, value in self.review.items()
                  if key not in ("visualReview", "caseSha256", "sourceReviewSha256")}
        digest = hashlib.sha256(json.dumps(frozen, ensure_ascii=False).encode()).hexdigest()
        self.review["sourceReviewSha256"] = digest
        self.review["visualReview"]["sourceReviewSha256"] = digest
        return site

    def test_genuine_false_positive_native_work_preserves_scores_and_95_percent_gates(self):
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        # Boundary, above-boundary and below-boundary recognition all remain
        # measured; normalization cannot replace aggregate quality judgment.
        for true_count, false_count, accuracy_passes, precision_passes in [
                (19, 1, True, True), (39, 1, True, True), (38, 2, True, True),
                (18, 1, True, False), (17, 1, False, False)]:
            for scope in ("strict", "automatic-page"):
                with self.subTest(true_count=true_count, false_count=false_count, scope=scope):
                    self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                    site = self.false_positive_case(true_count, false_count)
                    result = self.normalize(scope=scope)
                    self.assertTrue(result["completed"], result["blockedReason"])
                    self.assertEqual(false_count, result["unnecessaryVisualTranslations"])
                    self.assertEqual(true_count, result["translatedPositiveNodes"])
                    self.assertEqual(0, result["confirmedOmissions"])
                    self.assertEqual(self.case, result["provenance"]["rawCapture"])
                    self.assertEqual(self.review, result["provenance"]["rawReview"])
                    for index in range(1, false_count + 1):
                        self.assertFalse(result["annotations"][index]["needTranslation"])
                        self.assertTrue(result["predictions"][index]["eligible"])
                        self.assertTrue(result["predictions"][index]["completed"])
                    # This checkout has the unchanged baseline scorer. The
                    # controller separately verifies the required-mode roundtrip.
                    stats, completed, reasons = _score_case(result, site, self.root, coverage_scope=scope)
                    self.assertTrue(completed, reasons)
                    self.assertEqual([true_count, 2 - false_count, false_count, 0],
                                     [stats[key] for key in ("TP", "TN", "FP", "FN")])
                    _finish(stats)
                    gates = _quality_gates(stats)
                    self.assertEqual(precision_passes, gates["nodePrecision"])
                    self.assertEqual(accuracy_passes, gates["nodeAccuracy"])
                    self.assertNotIn("noUnnecessaryVisualTranslations", gates)
                    self.output.unlink()
                    legacy = self.normalize(scope=scope, required=False)
                    legacy_stats, legacy_completed, legacy_reasons = _score_case(
                        legacy, site, self.root, coverage_scope=scope)
                    self.assertTrue(legacy_completed, legacy_reasons)
                    self.assertEqual(_finish(legacy_stats), stats)
                    self.output.unlink()

    def test_false_positive_visual_vote_cannot_hide_invalid_native_or_source_work(self):
        self.false_positive_case(19, 1)
        original_case = deepcopy(self.case)
        changes = [(lambda: self.case["predictions"][1].update(completed=False), "node_not_completed:chinese"),
                   (lambda: self.case["predictions"][1].update(sourceEvidence="unmatched"), "prediction_source_audit_issue:chinese"),
                   (lambda: self.case["terminals"][0].update(success=False), "failed_or_invalid_terminal"),
                   (lambda: self.case["requestIds"].append("missing-terminal"), "request_terminal_identity_mismatch")]
        for index, (change, issue) in enumerate(changes):
            with self.subTest(index=index):
                self.case = deepcopy(original_case)
                change()
                result = self.normalize()
                self.blocked(result, issue)
                self.assertEqual(1, result["unnecessaryVisualTranslations"])
                self.output.unlink()

    def test_uppercase_frozen_source_review_digest_is_valid_and_bound_exactly(self):
        digest = self.review["sourceReviewSha256"].upper()
        self.assertNotEqual(digest, digest.lower())
        self.review["sourceReviewSha256"] = digest
        self.review["visualReview"]["sourceReviewSha256"] = digest
        result = self.normalize()
        self.assertTrue(result["completed"], result["blockedReason"])
        self.assertEqual(digest, result["sourceReviewSha256"])
        self.assertEqual(digest, result["visualReview"]["sourceReviewSha256"])
        self.assertEqual(digest, result["provenance"]["rawReview"]["sourceReviewSha256"])
        self.output.unlink()
        self.review["visualReview"]["sourceReviewSha256"] = digest.lower()
        with self.assertRaisesRegex(NormalizationError, "invalid_visual_review"):
            self.normalize()
        self.assertFalse(self.output.exists())

    def test_malformed_visual_header_fails_before_writing(self):
        original = deepcopy(self.review)
        mutations = [lambda r: r.pop("visualReview"), lambda r: r.update(visualReview=[]),
                     lambda r: r.pop("sourceReviewSha256")]
        for key, values in {
            "schemaVersion": [True, False, 1.0, "1", 0, 2, None],
            "reviewed": [False, 1, "true", None], "independent": [False, 1, "true", None],
            "caseSha256": ["0" * 64, True, " ", None],
            "sourceReviewSha256": ["0" * 64, True, " ", None],
            "nodes": [None, {}, True]}.items():
            mutations.append(lambda r, k=key: r["visualReview"].pop(k))
            mutations.extend(lambda r, k=key, v=value: r["visualReview"].update({k: v}) for value in values)
        for value in [True, " ", "z" * 64, "f" * 63, "f" * 65]:
            mutations.append(lambda r, v=value: (
                r.update(sourceReviewSha256=v), r["visualReview"].update(sourceReviewSha256=v)))
        for index, mutation in enumerate(mutations):
            with self.subTest(index=index), self.assertRaises(NormalizationError):
                self.review = deepcopy(original)
                self.normalize(mutate_review=mutation)
            self.assertFalse(self.output.exists())

    def test_missing_duplicate_extra_and_wrong_source_votes_fail_closed(self):
        original = deepcopy(self.review)
        mutations = [lambda r: r["visualReview"].update(nodes=[]),
                     lambda r: r["visualReview"]["nodes"].pop(),
                     lambda r: r["visualReview"]["nodes"].append(deepcopy(r["visualReview"]["nodes"][0])),
                     lambda r: r["visualReview"]["nodes"][0].update(sourceId="ARTICLE"),
                     lambda r: r["visualReview"]["nodes"][0].update(sourceId="new-source"),
                     lambda r: r["visualReview"]["nodes"][0].update(sourceId=True),
                     lambda r: r["visualReview"]["nodes"].append(None)]
        for index, mutation in enumerate(mutations):
            with self.subTest(index=index), self.assertRaises(NormalizationError):
                self.review = deepcopy(original)
                self.normalize(mutate_review=mutation)
            self.assertFalse(self.output.exists())

    def test_exact_original_text_hash_and_typed_node_votes_are_required(self):
        original = deepcopy(self.review)
        for key, values in {
            "sourceTextSha256": ["0" * 64, True, None, "a" * 63,
                                 hashlib.sha256(b"producer output is not a vote").hexdigest()],
            "outcome": [None, True, "Correct_translation", "correct", {}],
            "reviewed": [False, 1, "true", None], "independent": [False, 1, "true", None],
            "evidence": [None, [], {}, True]}.items():
            for value in [*values, "MISSING"]:
                with self.subTest(key=key, value=value), self.assertRaises(NormalizationError):
                    self.review = deepcopy(original)
                    row = self.review["visualReview"]["nodes"][0]
                    if value == "MISSING":
                        row.pop(key)
                    else:
                        row[key] = value
                    self.normalize()
                self.assertFalse(self.output.exists())

    def test_every_evidence_row_needs_original_reviewed_phase_and_nonblank_note(self):
        original = deepcopy(self.review)
        bad_evidence = [None, {}, True, {"phase": "stable-full"}, {"note": "observed"},
                        {"phase": "checks", "note": "observed"},
                        {"phase": "invented-after", "note": "observed"},
                        {"phase": ["after"], "note": "observed"},
                        {"phase": "after", "note": True}, {"phase": "after", "note": " \n\t"}]
        for evidence in bad_evidence:
            with self.subTest(evidence=evidence), self.assertRaises(NormalizationError):
                self.review = deepcopy(original)
                # A correct first row cannot conceal a malformed second row.
                self.review["visualReview"]["nodes"][0]["evidence"].append(evidence)
                self.normalize()
            self.assertFalse(self.output.exists())
        self.review = deepcopy(original)
        self.review["screenshots"]["stable-full"] = False
        with self.assertRaisesRegex(NormalizationError, "invalid_visual_evidence"):
            self.normalize()

    def test_visible_outcomes_cannot_use_only_before_images(self):
        original = deepcopy(self.review)
        for index, outcome in [(0, "correct_translation"), (0, "partial_translation"),
                               (0, "incorrect_translation"), (0, "unchanged"),
                               (1, "unnecessary_translation")]:
            with self.subTest(outcome=outcome), self.assertRaisesRegex(
                    NormalizationError, "requires_output_phase"):
                self.review = deepcopy(original)
                self.review["visualReview"]["nodes"][index].update(
                    outcome=outcome, evidence=[{"phase": phase, "note": "Only initial pixels"}
                                               for phase in ("before", "before-full", "scroll-before")])
                self.normalize()
            self.assertFalse(self.output.exists())

    def test_original_refresh_output_phase_is_valid_and_vote_order_is_irrelevant(self):
        phase = "stable-full-refresh-2"
        self.case["screenshots"].append({**self.case["screenshots"][0], "phase": phase})
        self.review["screenshots"][phase] = True
        self.review["visualReview"]["nodes"][0]["evidence"] = [{"phase": phase, "note": "Offline final output"}]
        self.review["visualReview"]["nodes"].reverse()
        self.assertTrue(self.normalize()["completed"])

    def test_contradictory_label_outcome_pairs_cannot_relabel_sources(self):
        original = deepcopy(self.review)
        for index, outcome in [(1, "correct_translation"), (1, "partial_translation"),
                               (1, "incorrect_translation"), (0, "unnecessary_translation")]:
            with self.subTest(index=index, outcome=outcome), self.assertRaisesRegex(
                    NormalizationError, "label_mismatch"):
                self.review = deepcopy(original)
                self.review["visualReview"]["nodes"][index]["outcome"] = outcome
                self.normalize()
            self.assertFalse(self.output.exists())

    def test_unknown_unreviewed_missing_labels_stay_unknown_despite_correct_vote(self):
        original = deepcopy(self.review)
        for patch in [{"needTranslation": "unknown"}, {"reviewed": False}, {"independent": False}, None]:
            with self.subTest(patch=patch):
                self.review = deepcopy(original)
                if patch is None:
                    self.review["annotations"].pop(0)
                else:
                    self.review["annotations"][0].update(patch)
                result = self.normalize()
                self.blocked(result, "unknown_node_label:article")
                self.assertEqual(0, result["translatedPositiveNodes"])
                self.assertEqual("correct_translation", result["visualReview"]["nodes"][0]["outcome"])
                self.output.unlink()

    def negative_page(self, assessment=False):
        self.case.update(expectedPageTranslation=False, pageDecision=False, liveTranslation=False)
        self.review["pageAnnotation"]["needTranslation"] = False
        self.case["predictions"][0]["completed"] = False
        self.review["visualReview"]["nodes"][0]["outcome"] = "unchanged"
        self.case["terminals"][0]["requestCount"] = 0
        if assessment:
            self.case.update(requestIds=[], terminals=[], localAssessment={
                "schemaVersion": 1, "type": "assessment", "id": "offline-assessment",
                "caseId": "offline-visual", "live": True, "needsTranslation": False})

    def test_genuine_zero_call_skip_remains_the_only_unchanged_positive_alternative(self):
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        for assessment in (False, True):
            with self.subTest(assessment=assessment):
                self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                self.negative_page(assessment)
                strict = self.normalize()
                self.blocked(strict, "confirmed_omission:article")
                self.output.unlink()
                automatic = self.normalize(scope="automatic-page")
                self.assertTrue(automatic["completed"], automatic["blockedReason"])
                self.assertEqual(1, automatic["policySkippedPositiveNodes"])
                self.assertEqual(0, automatic["translatedPositiveNodes"])
                self.assertEqual(1, automatic["unTranslatedPositiveNodes"])
                self.assertEqual(0, automatic["confirmedOmissions"])
                self.assertEqual(0, automatic["requestCount"])
                self.assertEqual(self.case["terminals"], [e for e in automatic["hostEvents"] if e["type"] == "terminal"])
                self.output.unlink()

    def test_zero_call_policy_cannot_hide_unknown_wrong_or_invented_completion(self):
        self.negative_page(assessment=True)
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        patches = [("unknown", "unknown_visual_node:article"),
                   ("not_visible", "unknown_visual_node:article"),
                   ("partial_translation", "incorrect_visual_translation:article"),
                   ("incorrect_translation", "incorrect_visual_translation:article"),
                   ("correct_translation", "confirmed_omission:article")]
        for outcome, issue in patches:
            with self.subTest(outcome=outcome):
                self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                self.review["visualReview"]["nodes"][0]["outcome"] = outcome
                result = self.normalize(scope="automatic-page")
                self.blocked(result, issue)
                self.assertEqual(0, result["policySkippedPositiveNodes"])
                self.assertEqual(0, result["translatedPositiveNodes"])
                self.output.unlink()

    def test_zero_call_requires_original_request_terminal_capabilities_and_page_proof(self):
        self.negative_page(assessment=True)
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        changes = [lambda: self.case["requestIds"].append("unaccounted-request"),
                   lambda: self.case.pop("localAssessment"),
                   lambda: self.case["capabilities"].update(live=False),
                   lambda: self.case["capabilities"].update(caseId="other-case"),
                   lambda: self.case.update(pageDecision=True),
                   lambda: self.review["pageAnnotation"].update(needTranslation=True),
                   lambda: self.case["predictions"][0].update(completed=True)]
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                change()
                result = self.normalize(scope="automatic-page")
                self.assertFalse(result["completed"])
                self.assertEqual(0, result["policySkippedPositiveNodes"])
                self.output.unlink()

    def test_correct_vote_cannot_resolve_current_source_or_terminal_failures(self):
        original_case = deepcopy(self.case)
        changes = [(lambda: self.case["predictions"][0].update(completed=False), "node_not_completed:article"),
                   (lambda: self.case["predictions"][0].update(sourceEvidence="unmatched"), "prediction_source_audit_issue:article"),
                   (lambda: self.case["predictions"][0].update(auditIssue="changed_facts"), "prediction_source_audit_issue:article"),
                   (lambda: self.case["terminals"][0].update(success=False), "failed_or_invalid_terminal"),
                   (lambda: self.case["terminals"][0].update(requestCount=True), "invalid_terminal_request_count"),
                   (lambda: self.case["terminals"][0].update(caseId="other"), "host_event_identity_mismatch"),
                   (lambda: self.case["requestIds"].append("unaccounted"), "request_terminal_identity_mismatch"),
                   (lambda: self.case.update(settled=False), "capture_not_successfully_finished"),
                   (lambda: self.case.update(blockedReason="source_changed"), "source_changed")]
        for index, (change, issue) in enumerate(changes):
            with self.subTest(index=index):
                self.case = deepcopy(original_case)
                change()
                result = self.normalize()
                self.blocked(result, issue)
                if index < 7:
                    self.assertEqual(0, result["translatedPositiveNodes"])
                self.assertEqual(self.case, result["provenance"]["rawCapture"])
                self.output.unlink()

    def test_retired_source_must_keep_its_actual_latest_history_and_source_text(self):
        piece = deepcopy(self.case["predictions"][0])
        self.case["sourceNodes"][0]["currentlyRendered"] = False
        self.case["predictions"].pop(0)
        self.case["productionEligibility"][0]["eligible"] = False
        self.case["predictionHistory"] = [piece]
        self.case["sourceObservations"] = [{"sourceId": "article", "sourceText": piece["sourceText"],
                                            "eligible": True, "rendered": True, "pieces": [piece]}]
        self.assertTrue(self.normalize()["completed"])
        self.output.unlink()
        self.case["sourceObservations"][0]["sourceText"] += " changed"
        with self.assertRaisesRegex(NormalizationError, "source_observation_text_mismatch"):
            self.normalize()
        self.case["sourceObservations"][0]["sourceText"] = piece["sourceText"]
        self.case["predictionHistory"] = []
        with self.assertRaisesRegex(NormalizationError, "not_in_raw_prediction_history"):
            self.normalize()

    def test_retractions_capture_errors_and_screenshot_hash_still_fail(self):
        self.case["retractions"] = [{"sourceId": "article", "phase": "return-after"}]
        self.review["retractions"] = [{"eventIndex": 0, "sourceId": "article",
                                        "confirmed": True, "sourceUnchanged": True}]
        self.blocked(self.normalize(), "confirmed_retraction:0")
        self.output.unlink()
        self.case["retractions"], self.review["retractions"] = [], []
        self.case["errors"] = [{"code": "offline-native-error"}]
        self.blocked(self.normalize(), "capture_errors")
        self.output.unlink()
        self.case["errors"] = []
        self.case["screenshots"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(NormalizationError, "review_screenshot_hash_mismatch"):
            self.normalize()

    def test_visual_counts_and_metadata_are_recomputed_from_review_not_producer(self):
        self.case.update(visualCoverage={"schemaVersion": 2, "required": False},
                         visualReview={"nodes": []}, unknownVisualNodes=55,
                         incorrectVisualTranslations=99, unnecessaryVisualTranslations=44,
                         translatedPositiveNodes=900, confirmedOmissions=0)
        self.review["visualReview"]["nodes"][0]["outcome"] = "unknown"
        result = self.normalize()
        self.blocked(result, "unknown_visual_node:article")
        self.assertEqual({"schemaVersion": 1, "required": True}, result["visualCoverage"])
        self.assertEqual(1, result["unknownVisualNodes"])
        self.assertEqual(0, result["incorrectVisualTranslations"])
        self.assertEqual(0, result["unnecessaryVisualTranslations"])
        self.assertEqual(0, result["translatedPositiveNodes"])

    def test_default_false_is_keyword_only_and_preserves_bytes_and_legacy_interface(self):
        parameter = inspect.signature(normalize_case).parameters["require_visual_review"]
        self.assertIs(parameter.default, False)
        self.assertEqual(inspect.Parameter.KEYWORD_ONLY, parameter.kind)
        # A malformed supplemental proof is ignored by the legacy path.
        self.review["visualReview"] = {"schemaVersion": True, "nodes": []}
        self.save()
        default = normalize_case(self.case_path, self.review_path, self.output, self.root)
        default_bytes = self.output.read_bytes()
        other = self.root / "explicit-false.json"
        explicit = normalize_case(self.case_path, self.review_path, other, self.root, require_visual_review=False)
        self.assertEqual(default, explicit)
        self.assertEqual(default_bytes, other.read_bytes())
        self.assertTrue(default["completed"])
        for key in ("visualCoverage", "visualReview", "sourceReviewSha256", "unknownVisualNodes",
                    "incorrectVisualTranslations", "unnecessaryVisualTranslations"):
            self.assertNotIn(key, default)
        stdout = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(io.StringIO()):
            self.assertEqual(0, main(["--case", str(self.case_path), "--review", str(self.review_path),
                                      "--output", str(self.root / "legacy-cli.json")]))
        self.assertEqual('{"completed": true, "blockedReason": "", "coverageScope": '
                         '{"schemaVersion": 1, "mode": "strict"}}\n', stdout.getvalue())

    def test_required_cli_metadata_exit_codes_and_malformed_no_output(self):
        self.save()
        args = ["--case", str(self.case_path), "--review", str(self.review_path),
                "--output", str(self.output), "--require-visual-review"]
        stdout = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(io.StringIO()):
            self.assertEqual(0, main(args))
        report = json.loads(stdout.getvalue())
        self.assertEqual({"schemaVersion": 1, "required": True}, report["visualCoverage"])
        self.assertEqual(0, report["incorrectVisualTranslations"])
        self.output.unlink()
        self.review["visualReview"]["nodes"][0]["outcome"] = "unchanged"
        self.save()
        with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            self.assertEqual(1, main(args))
        self.output.unlink()
        self.review["visualReview"]["nodes"].pop()
        self.save()
        with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            self.assertEqual(2, main(args))
        self.assertFalse(self.output.exists())

    def test_opt_in_flag_rejects_truthy_non_boolean_values_without_evidence_access(self):
        for value in (None, 0, 1, "true", [], {}):
            with self.subTest(value=value), self.assertRaisesRegex(NormalizationError, "invalid_require_visual_review"):
                normalize_case("absent-capture", "absent-review", self.output, require_visual_review=value)
        self.assertFalse(self.output.exists())

    def add_original_phase(self, phase):
        if phase not in self.review["screenshots"]:
            self.case["screenshots"].append({**self.case["screenshots"][0], "phase": phase})
            self.review["screenshots"][phase] = True

    def assert_required_roundtrip(self, site, scope="strict"):
        result = self.normalize(scope=scope)
        # Each subtest uses a new exclusive output even if an assertion fails.
        self.output.unlink()
        original = deepcopy(result)
        stats, completed, reasons = _score_case(
            result, site, self.root, coverage_scope=scope, require_visual_review=True)
        self.assertEqual(result["completed"], completed, reasons)
        for key in ("annotatedPositiveNodes", "translatedPositiveNodes", "unTranslatedPositiveNodes",
                    "policySkippedPositiveNodes", "confirmedOmissions", "unknownVisualNodes",
                    "incorrectVisualTranslations", "unnecessaryVisualTranslations"):
            self.assertEqual(result[key], stats[key], (key, reasons))
        self.assertEqual(original, result)
        self.assertEqual(self.case, result["provenance"]["rawCapture"])
        self.assertEqual(self.review, result["provenance"]["rawReview"])
        return result, stats

    def test_required_roundtrip_visual_outcomes_and_exact_uppercase_hash(self):
        site = self.false_positive_case(1, 0)
        digest = self.review["sourceReviewSha256"].upper()
        self.review["sourceReviewSha256"] = digest
        self.review["visualReview"]["sourceReviewSha256"] = digest
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        for outcome in ("correct_translation", "unchanged", "partial_translation",
                        "incorrect_translation", "unknown", "not_visible"):
            for scope in ("strict", "automatic-page"):
                with self.subTest(outcome=outcome, scope=scope):
                    self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                    self.review["visualReview"]["nodes"][0]["outcome"] = outcome
                    result, _ = self.assert_required_roundtrip(site, scope)
                    self.assertEqual(outcome == "correct_translation", result["completed"])

    def test_required_roundtrip_false_positives_preserve_original_quality_boundaries(self):
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        for true_count, false_count in ((19, 1), (38, 2), (18, 1), (17, 1)):
            for scope in ("strict", "automatic-page"):
                with self.subTest(true_count=true_count, false_count=false_count, scope=scope):
                    self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                    site = self.false_positive_case(true_count, false_count)
                    result, stats = self.assert_required_roundtrip(site, scope)
                    self.assertTrue(result["completed"])
                    self.assertEqual((true_count, 2 - false_count, false_count, 0),
                                     tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))
                    gates = _quality_gates(_finish(stats))
                    self.assertEqual(true_count >= 19 * false_count, gates["nodePrecision"])
                    self.assertEqual((true_count + 2 - false_count) * 20 >= (true_count + 2) * 19,
                                     gates["nodeAccuracy"])
                    self.assertNotIn("noUnnecessaryVisualTranslations", gates)

    def test_required_roundtrip_false_positive_vote_cannot_hide_unfinished_native_work(self):
        site = self.false_positive_case(19, 1)
        self.case["predictions"][1]["completed"] = False
        for scope in ("strict", "automatic-page"):
            with self.subTest(scope=scope):
                result, stats = self.assert_required_roundtrip(site, scope)
                self.assertFalse(result["completed"])
                self.assertEqual(19, stats["translatedPositiveNodes"])
                self.assertEqual(1, stats["unnecessaryVisualTranslations"])
                self.assertEqual((19, 1, 1, 0), tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))

    def test_required_roundtrip_negative_policy_requires_all_unchanged_output(self):
        self.false_positive_case(1, 0)
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        for assessment in (False, True):
            for index, outcome in ((0, "unchanged"), (0, "unknown"), (1, "not_visible"),
                                   (1, "unnecessary_translation")):
                for scope in ("strict", "automatic-page"):
                    with self.subTest(assessment=assessment, index=index, outcome=outcome, scope=scope):
                        self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                        self.negative_page(assessment)
                        self.review["visualReview"]["nodes"][index]["outcome"] = outcome
                        site = {k: self.case[k] for k in ("id", "registeredDomain", "url", "category",
                                                         "language", "expectedPageTranslation", "split")}
                        result, _ = self.assert_required_roundtrip(site, scope)
                        legitimate = scope == "automatic-page" and outcome == "unchanged"
                        self.assertEqual(legitimate, result["completed"])
                        self.assertEqual(int(legitimate), result["policySkippedPositiveNodes"])
                        self.assertEqual(0, result["translatedPositiveNodes"])

    def test_required_roundtrip_unknown_labels_and_unfinished_native_remain_unknown(self):
        site = self.false_positive_case(1, 0)
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        for kind in ("unknown-label", "unreviewed-label", "native-pending", "source-unmatched"):
            with self.subTest(kind=kind):
                self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                if kind == "unknown-label":
                    self.review["annotations"][0]["needTranslation"] = "unknown"
                elif kind == "unreviewed-label":
                    self.review["annotations"][0]["reviewed"] = False
                elif kind == "native-pending":
                    self.case["predictions"][0]["completed"] = False
                else:
                    self.case["predictions"][0]["sourceEvidence"] = "unmatched"
                result, _ = self.assert_required_roundtrip(site)
                self.assertFalse(result["completed"])
                self.assertEqual(1, result["unknownVisualNodes"])
                self.assertEqual(0, result["translatedPositiveNodes"])

    def test_every_visible_outcome_rejects_arbitrary_or_input_only_original_phase(self):
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        for index, outcome in ((0, "correct_translation"), (0, "partial_translation"),
                               (0, "incorrect_translation"), (0, "unchanged"),
                               (1, "unnecessary_translation")):
            for phase in ("before", "before-full", "scroll-before", "crop-supplement", "invented-after",
                          "stable-refresh-0", "stable-full-refresh-01", "stable-refresh--1",
                          "stable-refresh-1-extra", "after-refresh-1", "STABLE-refresh-1"):
                with self.subTest(outcome=outcome, phase=phase):
                    self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                    self.add_original_phase(phase)
                    self.review["visualReview"]["nodes"][index].update(
                        outcome=outcome, evidence=[{"phase": phase, "note": "Offline inspected original phase"}])
                    with self.assertRaisesRegex(NormalizationError, "requires_output_phase"):
                        self.normalize()
                    self.assertFalse(self.output.exists())

    def test_every_visible_outcome_accepts_exact_recognized_output_phases(self):
        site = self.false_positive_case(1, 1)
        original_case, original_review = deepcopy(self.case), deepcopy(self.review)
        for index, outcome in ((0, "correct_translation"), (0, "partial_translation"),
                               (0, "incorrect_translation"), (0, "unchanged"),
                               (1, "unnecessary_translation")):
            for phase in ("after", "after-full", "stable", "stable-full", "scroll-after",
                          "return-after", "reflow-after", "disclosure-open", "disclosure-restored",
                          "stable-refresh-1", "stable-full-refresh-2"):
                with self.subTest(outcome=outcome, phase=phase):
                    self.case, self.review = deepcopy(original_case), deepcopy(original_review)
                    self.add_original_phase(phase)
                    self.review["visualReview"]["nodes"][index].update(
                        outcome=outcome, evidence=[{"phase": phase, "note": "Offline inspected output"}])
                    result, _ = self.assert_required_roundtrip(site)
                    self.assertEqual(outcome in ("correct_translation", "unnecessary_translation"),
                                     result["completed"])

    def test_unknown_input_only_votes_are_preserved_without_policy_credit(self):
        self.false_positive_case(1, 0)
        self.negative_page(assessment=True)
        site = {k: self.case[k] for k in ("id", "registeredDomain", "url", "category", "language",
                                         "expectedPageTranslation", "split")}
        original_review = deepcopy(self.review)
        for index in (0, 1):
            for outcome in ("unknown", "not_visible"):
                for phase in ("before", "before-full", "scroll-before"):
                    with self.subTest(index=index, outcome=outcome, phase=phase):
                        self.review = deepcopy(original_review)
                        self.review["visualReview"]["nodes"][index].update(
                            outcome=outcome, evidence=[{"phase": phase, "note": "Cannot establish output"}])
                        result, _ = self.assert_required_roundtrip(site, "automatic-page")
                        self.assertFalse(result["completed"])
                        self.assertEqual(0, result["policySkippedPositiveNodes"])

    def test_required_roundtrip_retired_history_and_atomic_source_members(self):
        site = self.false_positive_case(1, 0)
        piece = self.case["predictions"].pop(0)
        self.case["sourceNodes"][0]["currentlyRendered"] = False
        self.case["productionEligibility"][0]["eligible"] = False
        self.case["predictionHistory"] = [deepcopy(piece)]
        self.case["sourceObservations"] = [{"sourceId": "article", "sourceText": piece["sourceText"],
                                            "eligible": True, "rendered": True, "pieces": [deepcopy(piece)]}]
        self.assertTrue(self.assert_required_roundtrip(site)[0]["completed"])
        original_case = deepcopy(self.case)
        for kind in ("missing-observation", "unobserved-source", "unfinished-piece"):
            with self.subTest(kind=kind):
                self.case = deepcopy(original_case)
                if kind == "missing-observation":
                    self.case["sourceObservations"] = []
                    self.case["predictions"].append(deepcopy(piece))
                elif kind == "unobserved-source":
                    self.case["sourceObservations"][0]["rendered"] = False
                else:
                    self.case["sourceObservations"][0]["pieces"][0]["completed"] = False
                    self.case["predictionHistory"][0]["completed"] = False
                result, _ = self.assert_required_roundtrip(site)
                self.assertFalse(result["completed"])
                self.assertEqual(0, result["translatedPositiveNodes"])
        self.case = deepcopy(original_case)
        self.case["predictionHistory"][0]["completed"] = 1
        with self.assertRaisesRegex(NormalizationError, "not_in_raw_prediction_history"):
            self.normalize()
        self.assertFalse(self.output.exists())

        # One real atomic piece may cover two independently true source labels.
        self.case = deepcopy(original_case)
        self.case["sourceNodes"][0]["currentlyRendered"] = True
        self.case["predictions"] = [deepcopy(piece), deepcopy(self.case["predictions"][1])]
        self.case["predictions"][0]["sourceIds"] = ["article", "chinese"]
        self.case["sourceObservations"], self.case["predictionHistory"] = [], []
        self.case["productionEligibility"][0]["eligible"] = True
        self.case["productionEligibility"][1]["eligible"] = True
        self.review["annotations"][1]["needTranslation"] = True
        self.review["visualReview"]["nodes"][1]["outcome"] = "correct_translation"
        result, stats = self.assert_required_roundtrip(site)
        self.assertTrue(result["completed"])
        self.assertEqual(2, stats["translatedPositiveNodes"])


if __name__ == "__main__":
    unittest.main()
