"""Independently authored normalized fixtures; no browser/corpus quality claims.

No normalizer imports: original captures, source reviews and scorer records are
constructed here to exercise the evaluator's independent trust boundary.
"""

import base64
from contextlib import redirect_stdout
from copy import deepcopy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from evaluate import evaluate, _score_case, main, THRESHOLDS


ROOT = Path(__file__).resolve().parents[2]
IMAGE = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")
PHASES = ("before", "before-full", "after", "after-full", "stable", "stable-full",
          "scroll-before", "scroll-after", "reflow-after", "return-after")
RECOGNITION = ("TP", "TN", "FP", "FN", "accuracy", "precision", "recall", "pageAccuracy")


def digest(value):
    return hashlib.sha256(value).hexdigest()


class VisualProofTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="independent-visual-", dir=ROOT / ".parallel")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.site = {"id": "rendered-example", "registeredDomain": "rendered-example.test",
                     "url": "https://rendered-example.test/report", "category": "knowledge",
                     "language": "en", "expectedPageTranslation": True, "split": "development"}
        self.manifest = {"schemaVersion": 1, "sites": [self.site]}
        self.case = self.fixture()

    def fixture(self, positive=True, scope="strict", assessment=False):
        site = {**self.site, "expectedPageTranslation": positive}
        nodes = [
            {"id": "source-A", "sourceText": "Revenue rose 12% in 2025.", "tag": "P",
             "role": "paragraph", "rendered": True,
             "rects": [{"x": 3, "y": 12, "width": 300, "height": 24}]},
            {"id": "source-B", "sourceText": "中文说明", "tag": "SPAN",
             "role": "metadata", "rendered": True,
             "rects": [{"x": 3, "y": 50, "width": 100, "height": 20}]}]
        capability = {"id": "cap-actual", "caseId": site["id"], "type": "capabilities", "live": True}
        terminal = {"id": "native-actual", "caseId": site["id"], "type": "terminal",
                    "success": True, "failedCount": 0, "cacheHits": 0, "requestCount": int(positive)}
        raw = {**site, "finalUrl": site["url"], "title": "Independent offline render fixture",
               "viewport": {"width": 1280, "height": 720}, "sourceHead": "d" * 40,
               "capturedAt": "2026-10-14T00:00:00Z", "sourceHashes": {"adapter.js": "a" * 64},
               "manifestHash": "b" * 64, "visitSuccess": True, "domainVerified": True,
               "captureFinished": True, "evidenceReady": True, "settled": True,
               "completed": False, "blockedReason": "screenshot_review_pending", "errors": [],
               "pageDecision": positive, "liveTranslation": positive, "sourceNodes": nodes,
               "predictions": [{"id": "piece-A", "sourceId": "source-A", "sourceEvidence": "matched",
                                "eligible": True, "completed": positive, "failed": False}],
               "productionEligibility": [{"sourceId": "source-A", "eligible": True},
                                         {"sourceId": "source-B", "eligible": False}],
               "retractions": [], "capabilities": capability,
               "requestIds": [] if assessment else [terminal["id"]],
               "terminals": [] if assessment else [terminal], "screenshots": []}
        if assessment:
            raw["localAssessment"] = {"schemaVersion": 1, "id": "assessment-actual",
                "caseId": site["id"], "type": "assessment", "live": True, "needsTranslation": False}
        shots = {}
        for phase in PHASES:
            filename = phase + ".png"
            (self.root / filename).write_bytes(IMAGE)
            shot = {"phase": phase, "path": filename, "sha256": digest(IMAGE)}
            raw["screenshots"].append(shot)
            shots[phase] = {**shot, "reviewed": True}
        shots["checks"] = dict.fromkeys(
            ("firstViewport", "mainContent", "afterScroll", "layout", "dynamicReturn"), True)
        raw_bytes = json.dumps(raw, ensure_ascii=False).encode("utf-8")
        (self.root / "capture.json").write_bytes(raw_bytes)
        review = {"caseSha256": digest(raw_bytes), "sourceReviewSha256": "f" * 64,
                  "pageAnnotation": {"needTranslation": positive, "reviewed": True, "independent": True},
                  "annotations": [{"sourceId": "source-A", "needTranslation": True,
                                   "reviewed": True, "independent": True},
                                  {"sourceId": "source-B", "needTranslation": False,
                                   "reviewed": True, "independent": True}],
                  "screenshots": dict.fromkeys(PHASES, True), "retractions": []}
        visual = {"schemaVersion": 1, "caseSha256": review["caseSha256"],
                  "sourceReviewSha256": review["sourceReviewSha256"],
                  "reviewed": True, "independent": True, "nodes": [
                      {"sourceId": n["id"], "sourceTextSha256": digest(n["sourceText"].encode("utf-8")),
                       "outcome": "correct_translation" if positive and i == 0 else "unchanged",
                       "reviewed": True, "independent": True,
                       "evidence": [{"phase": "stable-full", "note": "Offline authored vote for inspected text."}]}
                      for i, n in enumerate(nodes)]}
        review["visualReview"] = deepcopy(visual)
        result = deepcopy(raw)
        result.update(completed=True, blockedReason="", requestCount=int(positive), screenshots=shots,
                      coverageScope={"schemaVersion": 1, "mode": scope}, visualReview=visual,
                      pageAnnotation=deepcopy(review["pageAnnotation"]),
                      annotations=[{**r, "nodeId": r["sourceId"]} for r in review["annotations"]],
                      sourceNodes=[{**n, "text": n["sourceText"], "position": deepcopy(n["rects"][0])}
                                   for n in nodes],
                      predictions=[{"nodeId": "source-A", "eligible": True,
                                    "completed": positive, "failed": False, "pieceIds": ["piece-A"]},
                                   {"nodeId": "source-B", "eligible": False,
                                    "completed": False, "failed": False, "pieceIds": []}],
                      hostEvents=[capability, *raw["terminals"]] +
                                 ([raw["localAssessment"]] if assessment else []),
                      provenance={"casePath": str(self.root / "capture.json"), "caseSha256": digest(raw_bytes),
                                  "reviewSha256": digest(json.dumps(review).encode("utf-8")),
                                  "rawCapture": deepcopy(raw), "rawReview": deepcopy(review)})
        return result

    def report(self, case=None, required=True, scope="strict"):
        case = self.case if case is None else case
        site = {**self.site, "expectedPageTranslation": case["expectedPageTranslation"]}
        return evaluate({"schemaVersion": 1, "sites": [site]}, [case], self.root,
                        coverage_scope=scope, require_visual_review=required)

    def vote(self, case, outcome, index=0):
        case["visualReview"]["nodes"][index]["outcome"] = outcome
        self.freeze_visual(case)

    def freeze_visual(self, case):
        case["provenance"]["rawReview"]["visualReview"] = deepcopy(case["visualReview"])

    def false_positive_fixture(self, false_text="中文说明", true_count=1):
        """Author actual original pieces for correct true nodes and one FP.

        These records only test offline scoring. They are not real rendered
        evidence or results from any website or reserved evaluation dataset.
        """
        case = deepcopy(self.case)
        raw = case["provenance"]["rawCapture"]
        review = case["provenance"]["rawReview"]
        for index in range(1, true_count):
            identity = "source-true-" + str(index)
            text = "Independent authored paragraph number " + str(index) + "."
            source = deepcopy(raw["sourceNodes"][0])
            source.update(id=identity, sourceText=text)
            raw["sourceNodes"].append(source)
            normalized = deepcopy(case["sourceNodes"][0])
            normalized.update(id=identity, sourceText=text, text=text)
            case["sourceNodes"].append(normalized)
            label = {"sourceId": identity, "needTranslation": True,
                     "reviewed": True, "independent": True}
            review["annotations"].append(label)
            case["annotations"].append({**label, "nodeId": identity})
            piece_id = "piece-true-" + str(index)
            raw["predictions"].append({**raw["predictions"][0], "id": piece_id, "sourceId": identity})
            raw["productionEligibility"].append({"sourceId": identity, "eligible": True})
            case["predictions"].append({**case["predictions"][0], "nodeId": identity,
                                        "pieceIds": [piece_id]})
            case["visualReview"]["nodes"].append({**deepcopy(case["visualReview"]["nodes"][0]),
                "sourceId": identity, "sourceTextSha256": digest(text.encode("utf-8"))})
        raw["sourceNodes"][1]["sourceText"] = false_text
        case["sourceNodes"][1].update(sourceText=false_text, text=false_text)
        case["visualReview"]["nodes"][1].update(sourceTextSha256=digest(false_text.encode("utf-8")),
                                                   outcome="unnecessary_translation")
        raw["productionEligibility"][1]["eligible"] = True
        raw["predictions"].append({**raw["predictions"][0], "id": "piece-false", "sourceId": "source-B"})
        case["predictions"][1].update(eligible=True, completed=True, pieceIds=["piece-false"])
        self.freeze_visual(case)
        return case

    def assert_blocked(self, case, reason=None, scope="strict"):
        report = self.report(case, scope=scope)
        self.assertFalse(report["cases"][0]["completed"])
        self.assertFalse(report["achieved"])
        if reason:
            self.assertTrue(any(reason in r for r in report["cases"][0]["reasons"]),
                            report["cases"][0]["reasons"])
        return report

    def test_correct_live_true_and_unchanged_false_succeed_without_mutation(self):
        original = deepcopy(self.case)
        before = {p: p.read_bytes() for p in self.root.iterdir()}
        report = self.report()
        counts = report["cases"][0]["counts"]
        self.assertTrue(report["cases"][0]["completed"])
        self.assertEqual(1, counts["translatedPositiveNodes"])
        self.assertEqual((1, 1, 0, 0), tuple(counts[k] for k in ("TP", "TN", "FP", "FN")))
        self.assertEqual({"schemaVersion": 1, "required": True}, report["visualCoverage"])
        self.assertFalse(report["achieved"])  # Two authored nodes cannot meet domain gates.
        self.assertEqual(original, self.case)
        self.assertEqual(before, {p: p.read_bytes() for p in self.root.iterdir()})
        self.assertEqual(THRESHOLDS, report["thresholds"])
        self.assertTrue(report["gates"]["overall"]["noUnknownVisualNodes"])

    def test_legacy_shape_and_default_remain_unchanged(self):
        baseline = self.report(required=False)
        self.case.pop("visualReview")
        self.case.pop("provenance")
        self.assertEqual(baseline, self.report(required=False))
        self.assertNotIn("visualCoverage", baseline)
        self.assertNotIn("unknownVisualNodes", baseline["groups"]["overall"])
        self.assertNotIn("noUnknownVisualNodes", baseline["gates"]["overall"])
        self.assertEqual(baseline, evaluate(self.manifest, [self.case], self.root))
        self.assert_blocked(self.case, "missing_visual_provenance")

    def test_live_terminal_with_visible_untranslated_text_is_omission(self):
        legacy = self.report(required=False)
        self.vote(self.case, "unchanged")
        report = self.assert_blocked(self.case, "confirmed_omission:source-A")
        counts = report["cases"][0]["counts"]
        self.assertEqual(0, counts["translatedPositiveNodes"])
        self.assertEqual(1, counts["unTranslatedPositiveNodes"])
        self.assertEqual(1, counts["confirmedOmissions"])
        self.assertTrue(self.case["predictions"][0]["completed"])
        for key in RECOGNITION:
            self.assertEqual(legacy["groups"]["overall"][key], report["groups"]["overall"][key])

    def test_partial_english_and_changed_facts_never_get_credit(self):
        for outcome, note in (("partial_translation", "English fragment remains after Chinese prefix."),
                              ("incorrect_translation", "Revenue changed from 12% to 21%.")):
            with self.subTest(outcome=outcome):
                case = deepcopy(self.case)
                self.vote(case, outcome)
                case["visualReview"]["nodes"][0]["evidence"][0]["note"] = note
                self.freeze_visual(case)
                report = self.assert_blocked(case, "incorrect_visual_translation")
                self.assertEqual(1, report["cases"][0]["counts"]["incorrectVisualTranslations"])
                self.assertEqual(0, report["cases"][0]["counts"]["translatedPositiveNodes"])
                self.assertFalse(report["gates"]["overall"]["noIncorrectVisualTranslations"])

    def test_unknown_and_invisible_are_explicit_unresolved_nodes(self):
        for outcome in ("unknown", "not_visible"):
            for index in (0, 1):
                with self.subTest(outcome=outcome, index=index):
                    case = deepcopy(self.case)
                    self.vote(case, outcome, index)
                    report = self.assert_blocked(case, "unresolved_visual_node")
                    self.assertEqual(1, report["cases"][0]["counts"]["unknownVisualNodes"])
                    self.assertFalse(report["gates"]["overall"]["noUnknownVisualNodes"])

    def test_unnecessary_translation_does_not_rewrite_recognition(self):
        for text in ("中文说明", "2025-10-14 / 12%", "ACME", "© 示例公司"):
            with self.subTest(false_text=text):
                case = self.false_positive_fixture(text)
                original = deepcopy(case)
                legacy = self.report(case, required=False)
                report = self.report(case)
                record = report["cases"][0]
                self.assertTrue(record["completed"], record["reasons"])
                self.assertEqual([], record["reasons"])
                self.assertEqual(1, record["counts"]["unnecessaryVisualTranslations"])
                self.assertEqual((1, 0, 1, 0), tuple(record["counts"][k] for k in ("TP", "TN", "FP", "FN")))
                self.assertEqual(1, record["counts"]["translatedPositiveNodes"])
                self.assertNotIn("noUnnecessaryVisualTranslations", report["gates"]["overall"])
                self.assertNotIn("noUnnecessaryVisualTranslations", report["gates"]["holdout"])
                self.assertFalse(report["gates"]["overall"]["nodeAccuracy"])
                self.assertFalse(report["gates"]["overall"]["nodePrecision"])
                for key in RECOGNITION:
                    self.assertEqual(legacy["groups"]["overall"][key], report["groups"]["overall"][key])
                stats, completed, reasons = _score_case(case, self.site, self.root, require_visual_review=True)
                self.assertTrue(completed, reasons)
                self.assertEqual(1, stats["unnecessaryVisualTranslations"])
                self.assertEqual(original, case)

    def test_measured_false_positives_allow_exact_95_percent_aggregate(self):
        cases = [self.false_positive_fixture(text, true_count=19)
                 for text in ("中文说明", "12% / 2025")]
        sites = []
        for index, case in enumerate(cases):
            site = {**self.site, "id": "authored-" + str(index),
                    "registeredDomain": "authored-" + str(index) + ".test",
                    "url": "https://authored-" + str(index) + ".test/report"}
            sites.append(site)
            for item in (case, case["provenance"]["rawCapture"]):
                item.update(site, finalUrl=site["url"])
                if "capabilities" in item:
                    item["capabilities"]["caseId"] = site["id"]
                for event in item["terminals"]:
                    event["caseId"] = site["id"]
            for event in case["hostEvents"]:
                event["caseId"] = site["id"]
        manifest = {"schemaVersion": 1, "sites": sites}
        report = evaluate(manifest, cases, self.root, require_visual_review=True)
        legacy = evaluate(manifest, cases, self.root)
        stats = report["groups"]["overall"]
        self.assertTrue(all(c["completed"] for c in report["cases"]), report["cases"])
        self.assertEqual((38, 0, 2, 0), tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))
        self.assertEqual(2, stats["unnecessaryVisualTranslations"])
        self.assertEqual(.95, stats["accuracy"])
        self.assertEqual(.95, stats["precision"])
        self.assertTrue(report["gates"]["overall"]["nodeAccuracy"])
        self.assertTrue(report["gates"]["overall"]["nodePrecision"])
        for key in RECOGNITION:
            self.assertEqual(legacy["groups"]["overall"][key], stats[key])
        self.assertFalse(report["achieved"])  # Offline fixtures do not satisfy domain requirements.
        below = self.report(self.false_positive_fixture(true_count=18))
        self.assertTrue(below["cases"][0]["completed"])
        self.assertFalse(below["gates"]["overall"]["nodeAccuracy"])
        self.assertFalse(below["gates"]["overall"]["nodePrecision"])

    def test_false_positive_output_keeps_invalid_native_source_and_old_gates(self):
        for mutate in (
            lambda c: c["provenance"]["rawCapture"]["predictions"][-1].update(completed=1),
            lambda c: c["provenance"]["rawCapture"]["predictions"][-1].update(sourceId="forged-source"),
            lambda c: c["provenance"]["rawCapture"]["productionEligibility"][1].update(eligible=False),
            lambda c: c["hostEvents"][1].update(success=False),
            lambda c: c.update(errors=["native_error"]),
            lambda c: self.vote(c, "partial_translation"),
            lambda c: self.vote(c, "not_visible"),
            lambda c: self.vote(c, "unchanged")):
            case = self.false_positive_fixture()
            mutate(case)
            self.assert_blocked(case)

    def test_source_review_digest_accepts_uppercase_hex_with_exact_original_binding(self):
        for value in ("ABCDEF0123456789" * 4, "aBcDeF0123456789" * 4):
            with self.subTest(digest=value):
                case = deepcopy(self.case)
                case["provenance"]["rawReview"]["sourceReviewSha256"] = value
                case["visualReview"]["sourceReviewSha256"] = value
                self.freeze_visual(case)
                self.assertTrue(self.report(case)["cases"][0]["completed"])
                stats, completed, reasons = _score_case(case, self.site, self.root, require_visual_review=True)
                self.assertTrue(completed, reasons)
                self.assertEqual(1, stats["translatedPositiveNodes"])
                case["visualReview"]["sourceReviewSha256"] = value.lower()
                self.freeze_visual(case)
                self.assert_blocked(case, "visual_source_review_hash_mismatch")
        for value in (None, True, 1, [], {}, "", " " * 64, "G" * 64,
                      "A" * 63, "A" * 65, " " + "A" * 64, "A" * 64 + "\n"):
            with self.subTest(invalid=value):
                case = deepcopy(self.case)
                case["provenance"]["rawReview"]["sourceReviewSha256"] = value
                case["visualReview"]["sourceReviewSha256"] = value
                self.freeze_visual(case)
                self.assert_blocked(case, "visual_source_review_hash_mismatch")

    def test_contradictory_label_outcome_pairs_block(self):
        for index, outcome in ((0, "unnecessary_translation"), (1, "correct_translation"),
                               (1, "partial_translation"), (1, "incorrect_translation")):
            with self.subTest(index=index, outcome=outcome):
                case = deepcopy(self.case)
                self.vote(case, outcome, index)
                report = self.assert_blocked(case, "contradictory_visual_label_outcome")
                self.assertEqual(1, report["cases"][0]["counts"]["unknownVisualNodes"])

    def test_strict_types_and_missing_duplicate_unmatched_rows_fail_closed(self):
        mutations = [
            lambda v: v.update(schemaVersion=True), lambda v: v.update(schemaVersion=1.0),
            lambda v: v.update(schemaVersion=2), lambda v: v.update(reviewed=1),
            lambda v: v.update(independent="true"), lambda v: v.update(nodes={}),
            lambda v: v["nodes"].pop(), lambda v: v["nodes"].append(deepcopy(v["nodes"][0])),
            lambda v: v["nodes"][0].update(sourceId="current-substitute"),
            lambda v: v["nodes"][0].update(reviewed=1),
            lambda v: v["nodes"][0].update(independent=False),
            lambda v: v["nodes"][0].update(outcome=["correct_translation"]),
            lambda v: v["nodes"][0].update(outcome="CORRECT_TRANSLATION"),
            lambda v: v["nodes"][0].update(evidence=[]),
            lambda v: v["nodes"][0].update(evidence=[None]),
            lambda v: v["nodes"][0]["evidence"][0].update(note=" \n "),
            lambda v: v["nodes"][0]["evidence"][0].update(phase=["stable"])]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                case = deepcopy(self.case)
                mutate(case["visualReview"])
                self.freeze_visual(case)
                report = self.assert_blocked(case)
                self.assertEqual(0, report["cases"][0]["counts"]["translatedPositiveNodes"])
                self.assertGreater(report["cases"][0]["counts"]["unknownVisualNodes"], 0)

    def test_exact_hashes_original_text_and_frozen_labels_are_independent(self):
        mutations = [
            lambda c: c["visualReview"].update(caseSha256="0" * 64),
            lambda c: c["visualReview"].update(sourceReviewSha256="1" * 64),
            lambda c: c["provenance"]["rawReview"].update(sourceReviewSha256=" "),
            lambda c: c["provenance"].update(caseSha256=True),
            lambda c: c["visualReview"]["nodes"][0].update(sourceTextSha256="a" * 64),
            lambda c: c["sourceNodes"][0].update(text="Revenue rose 21% in 2025."),
            lambda c: c["sourceNodes"][0].update(text=c["sourceNodes"][0]["text"].lower()),
            lambda c: c["sourceNodes"][0].update(text=c["sourceNodes"][0]["text"] + " "),
            lambda c: c["provenance"]["rawCapture"]["sourceNodes"].pop(),
            lambda c: c["annotations"][0].update(needTranslation=False),
            lambda c: c["provenance"]["rawReview"]["annotations"][0].update(needTranslation=False),
            lambda c: c.update(sourceHead="e" * 40),
            lambda c: c.update(capturedAt="2026-10-14T01:00:00Z"),
            lambda c: c["sourceHashes"].update({"adapter.js": "c" * 64})]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                case = deepcopy(self.case)
                mutate(case)
                self.freeze_visual(case)
                report = self.assert_blocked(case)
                self.assertEqual(0, report["cases"][0]["counts"]["translatedPositiveNodes"])

    def test_positive_outcome_requires_reviewed_original_output_phase(self):
        for phase in ("before", "before-full", "crop-supplement", "invented-after"):
            with self.subTest(phase=phase):
                case = deepcopy(self.case)
                case["visualReview"]["nodes"][0]["evidence"][0]["phase"] = phase
                self.freeze_visual(case)
                self.assert_blocked(case, "invalid_visual_node")
        for mutate in (
            lambda c: c["provenance"]["rawReview"]["screenshots"].update({"stable-full": False}),
            lambda c: c["screenshots"]["stable-full"].update(reviewed=False),
            lambda c: c["screenshots"]["stable-full"].update(path="after.png"),
            lambda c: c["provenance"]["rawCapture"]["screenshots"][-1].update(phase="checks"),
            lambda c: c["provenance"]["rawCapture"]["screenshots"][-1].update(path="../outside.png")):
            case = deepcopy(self.case)
            mutate(case)
            self.assert_blocked(case)
        (self.root / "stable-full.png").write_bytes(IMAGE + b"changed")
        self.assert_blocked(self.case, "visual_original_screenshot_mismatch")

    def add_reviewed_original_phase(self, case, phase):
        """Author a phase in all three retained screenshot representations."""
        filename = phase + ".png"
        (self.root / filename).write_bytes(IMAGE)
        shot = {"phase": phase, "path": filename, "sha256": digest(IMAGE)}
        case["provenance"]["rawCapture"]["screenshots"].append(shot)
        case["provenance"]["rawReview"]["screenshots"][phase] = True
        case["screenshots"][phase] = {**shot, "reviewed": True}

    def test_direct_unchanged_before_only_cannot_lend_negative_policy_credit(self):
        for assessment in (False, True):
            for index in (0, 1):
                for phases in (("before",), ("before-full",), ("before", "before-full")):
                    with self.subTest(assessment=assessment, index=index, phases=phases):
                        case = self.fixture(False, "automatic-page", assessment)
                        case["visualReview"]["nodes"][index]["evidence"] = [
                            {"phase": p, "note": "Only inspected input; no output inspection."}
                            for p in phases]
                        self.freeze_visual(case)
                        original = deepcopy(case)
                        self.assertEqual(case["visualReview"], case["provenance"]["rawReview"]["visualReview"])
                        stats, completed, reasons = _score_case(
                            case, {**self.site, "expectedPageTranslation": False}, self.root,
                            coverage_scope="automatic-page", require_visual_review=True)
                        self.assertFalse(completed)
                        self.assertIn("invalid_visual_node:" + case["sourceNodes"][index]["id"], reasons)
                        self.assertEqual(0, stats["policySkippedPositiveNodes"])
                        self.assertEqual(1, stats["confirmedOmissions"])
                        report = self.assert_blocked(case, "invalid_visual_node", scope="automatic-page")
                        self.assertFalse(report["gates"]["overall"]["noUnknownVisualNodes"])
                        self.assertEqual(original, case)

    def test_direct_unchanged_inspected_output_preserves_negative_policy_credit(self):
        for assessment in (False, True):
            for phase in ("after", "after-full", "stable", "stable-full",
                          "scroll-after", "return-after", "reflow-after", "disclosure-open",
                          "disclosure-restored", "stable-refresh-1", "stable-full-refresh-3"):
                with self.subTest(assessment=assessment, phase=phase):
                    case = self.fixture(False, "automatic-page", assessment)
                    if phase not in case["screenshots"]:
                        self.add_reviewed_original_phase(case, phase)
                    for row in case["visualReview"]["nodes"]:
                        row["evidence"] = [{"phase": "before", "note": "Inspected input."},
                                           {"phase": phase, "note": "Inspected unchanged output."}]
                    self.freeze_visual(case)
                    original = deepcopy(case)
                    stats, completed, reasons = _score_case(
                        case, {**self.site, "expectedPageTranslation": False}, self.root,
                        coverage_scope="automatic-page", require_visual_review=True)
                    self.assertTrue(completed, reasons)
                    self.assertEqual((1, 0, 1, 0), tuple(stats[k] for k in
                        ("annotatedPositiveNodes", "translatedPositiveNodes",
                         "policySkippedPositiveNodes", "confirmedOmissions")))
                    self.assertTrue(self.report(case, scope="automatic-page")["cases"][0]["completed"])
                    self.assertEqual(original, case)

    def test_every_visible_outcome_requires_recognized_output_not_arbitrary_reviewed_phase(self):
        for outcome in ("correct_translation", "partial_translation", "incorrect_translation",
                        "unchanged", "unnecessary_translation"):
            for phase in ("before", "before-full", "scroll-before", "crop-supplement", "invented-after",
                          "stable-refresh-0", "stable-full-refresh-01", "stable-refresh--1",
                          "stable-refresh-1-extra", "after-refresh-1", "STABLE-refresh-1"):
                with self.subTest(outcome=outcome, phase=phase):
                    case = (self.false_positive_fixture() if outcome == "unnecessary_translation"
                            else deepcopy(self.case))
                    index = 1 if outcome == "unnecessary_translation" else 0
                    if phase not in case["screenshots"]:
                        self.add_reviewed_original_phase(case, phase)
                    case["visualReview"]["nodes"][index].update(
                        outcome=outcome, evidence=[{"phase": phase, "note": "Reviewed original phase."}])
                    self.freeze_visual(case)
                    original = deepcopy(case)
                    stats, completed, reasons = _score_case(
                        case, self.site, self.root, require_visual_review=True)
                    self.assertFalse(completed)
                    self.assertIn("invalid_visual_node:" + case["sourceNodes"][index]["id"], reasons)
                    self.assertEqual(0, stats["translatedPositiveNodes"])
                    self.assertEqual(original, case)

    def test_direct_unchanged_scroll_before_cannot_lend_negative_policy_credit(self):
        for assessment in (False, True):
            for index in (0, 1):
                with self.subTest(assessment=assessment, index=index):
                    case = self.fixture(False, "automatic-page", assessment)
                    case["visualReview"]["nodes"][index]["evidence"] = [
                        {"phase": "scroll-before", "note": "Inspected scrolled input only."}]
                    self.freeze_visual(case)
                    original = deepcopy(case)
                    self.assertEqual(case["visualReview"],
                                     case["provenance"]["rawReview"]["visualReview"])
                    stats, completed, reasons = _score_case(
                        case, {**self.site, "expectedPageTranslation": False}, self.root,
                        coverage_scope="automatic-page", require_visual_review=True)
                    self.assertFalse(completed)
                    self.assertIn("invalid_visual_node:" + case["sourceNodes"][index]["id"], reasons)
                    self.assertEqual(0, stats["policySkippedPositiveNodes"])
                    self.assertEqual(0, stats["translatedPositiveNodes"])
                    self.assertEqual(1, stats["confirmedOmissions"])
                    self.assertEqual((1, 1, 0, 0), tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))
                    report = self.assert_blocked(case, "invalid_visual_node", scope="automatic-page")
                    self.assertFalse(report["gates"]["overall"]["noUnknownVisualNodes"])
                    self.assertEqual(original, case)

    def test_every_visible_outcome_accepts_reviewed_stable_refresh_syntax(self):
        for outcome in ("correct_translation", "partial_translation", "incorrect_translation",
                        "unchanged", "unnecessary_translation"):
            for phase in ("stable-refresh-1", "stable-full-refresh-2"):
                with self.subTest(outcome=outcome, phase=phase):
                    case = (self.false_positive_fixture() if outcome == "unnecessary_translation"
                            else deepcopy(self.case))
                    index = 1 if outcome == "unnecessary_translation" else 0
                    self.add_reviewed_original_phase(case, phase)
                    case["visualReview"]["nodes"][index].update(
                        outcome=outcome, evidence=[{"phase": phase, "note": "Inspected refreshed output."}])
                    self.freeze_visual(case)
                    stats, completed, reasons = _score_case(
                        case, self.site, self.root, require_visual_review=True)
                    self.assertFalse(any("invalid_visual_node" in r for r in reasons), reasons)
                    self.assertEqual(0, stats["unknownVisualNodes"])
                    self.assertEqual(outcome in ("correct_translation", "unnecessary_translation"), completed)
                    if outcome in ("partial_translation", "incorrect_translation"):
                        self.assertEqual(1, stats["incorrectVisualTranslations"])
                    if outcome == "unchanged":
                        self.assertEqual(1, stats["confirmedOmissions"])

    def test_direct_unknown_and_not_visible_before_votes_remain_unresolved(self):
        for outcome in ("unknown", "not_visible"):
            for phase in ("before", "before-full", "stable-full"):
                for index in (0, 1):
                    with self.subTest(outcome=outcome, phase=phase, index=index):
                        case = self.fixture(False, "automatic-page", True)
                        case["visualReview"]["nodes"][index].update(
                            outcome=outcome, evidence=[{"phase": phase, "note": "Unable to establish output."}])
                        self.freeze_visual(case)
                        original = deepcopy(case)
                        stats, completed, reasons = _score_case(
                            case, {**self.site, "expectedPageTranslation": False}, self.root,
                            coverage_scope="automatic-page", require_visual_review=True)
                        self.assertFalse(completed)
                        self.assertFalse(any("invalid_visual_node" in r for r in reasons), reasons)
                        self.assertIn("unresolved_visual_node:" + case["sourceNodes"][index]["id"], reasons)
                        self.assertEqual(1, stats["unknownVisualNodes"])
                        self.assertEqual(0, stats["policySkippedPositiveNodes"])
                        self.assertEqual(1, stats["confirmedOmissions"])
                        self.assertEqual(original, case)

    def test_correct_visual_vote_cannot_invent_native_completion(self):
        mutations = [
            lambda c: c["predictions"][0].update(completed=False),
            lambda c: c["hostEvents"][1].update(success=False),
            lambda c: c["provenance"]["rawCapture"]["predictions"][0].update(completed=False),
            lambda c: c["provenance"]["rawCapture"].update(predictions=[]),
            lambda c: c["provenance"]["rawCapture"]["predictions"][0].update(sourceEvidence="unmatched"),
            lambda c: c["provenance"]["rawCapture"]["predictions"][0].update(sourceId="source-B"),
            lambda c: c["provenance"]["rawCapture"].update(terminals=[]),
            lambda c: c["provenance"]["rawCapture"].update(requestIds=[])]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                case = deepcopy(self.case)
                mutate(case)
                report = self.assert_blocked(case)
                self.assertEqual(0, report["cases"][0]["counts"]["translatedPositiveNodes"])

    def test_submitted_duplicate_counters_are_ignored(self):
        self.vote(self.case, "incorrect_translation")
        self.case.update(translatedPositiveNodes=999, confirmedOmissions=0,
                         unknownVisualNodes=0, incorrectVisualTranslations=0,
                         unnecessaryVisualTranslations=0, visualCoverage={"schemaVersion": 1, "required": True})
        report = self.assert_blocked(self.case)
        self.assertEqual(0, report["cases"][0]["counts"]["translatedPositiveNodes"])
        self.assertEqual(1, report["groups"]["overall"]["incorrectVisualTranslations"])

    def test_original_native_types_eligibility_and_source_membership_are_checked(self):
        mutations = [
            lambda r: r["predictions"][0].update(completed=1),
            lambda r: r["predictions"][0].update(eligible=False),
            lambda r: r["predictions"][0].update(failed=True),
            lambda r: r["predictions"][0].update(sourceIds=["source-A", "source-A"]),
            lambda r: r["predictions"][0].update(sourceIds=["source-B"]),
            lambda r: r["productionEligibility"][0].update(eligible=False),
            lambda r: r["productionEligibility"][0].update(eligible=1),
            lambda r: r.update(sourceObservations=[None])]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                case = deepcopy(self.case)
                mutate(case["provenance"]["rawCapture"])
                report = self.assert_blocked(case)
                self.assertEqual(0, report["cases"][0]["counts"]["translatedPositiveNodes"])

    def test_retired_source_needs_real_matching_original_history(self):
        case = deepcopy(self.case)
        raw = case["provenance"]["rawCapture"]
        piece = raw["predictions"].pop()
        raw["sourceNodes"][0]["currentlyRendered"] = False
        raw["productionEligibility"][0]["eligible"] = False  # Later DOM observation.
        raw["predictionHistory"] = [deepcopy(piece)]
        record = {"sourceId": "source-A", "sourceText": raw["sourceNodes"][0]["sourceText"],
                  "eligible": True, "rendered": True, "pieces": [deepcopy(piece)]}
        raw["sourceObservations"] = [record]
        self.assertTrue(self.report(case)["cases"][0]["completed"])
        for mutate in (
            lambda r: r.update(predictionHistory=[]),
            lambda r: r["sourceObservations"][0].update(rendered=False),
            lambda r: r["sourceObservations"][0].update(sourceText="current text substitute"),
            lambda r: r["sourceObservations"].append(deepcopy(r["sourceObservations"][0]))):
            invalid = deepcopy(case)
            mutate(invalid["provenance"]["rawCapture"])
            self.assert_blocked(invalid)

    def test_atomic_piece_covers_original_members_without_duplicate_credit(self):
        case = deepcopy(self.case)
        raw = case["provenance"]["rawCapture"]
        raw["predictions"][0]["sourceIds"] = ["source-A", "source-B"]
        raw["productionEligibility"][1]["eligible"] = True
        case["predictions"][1].update(eligible=True, completed=True, pieceIds=["piece-A"])
        case["annotations"][1]["needTranslation"] = True
        case["provenance"]["rawReview"]["annotations"][1]["needTranslation"] = True
        self.vote(case, "correct_translation", 1)
        report = self.report(case)
        self.assertTrue(report["cases"][0]["completed"], report["cases"][0]["reasons"])
        self.assertEqual(2, report["cases"][0]["counts"]["translatedPositiveNodes"])

    def test_original_retraction_cannot_be_deleted_or_relabelled(self):
        case = deepcopy(self.case)
        event = {"sourceId": "source-A", "sourceUnchanged": True}
        case["provenance"]["rawCapture"]["retractions"] = [event]
        case["provenance"]["rawReview"]["retractions"] = [
            {"eventIndex": 0, "confirmed": True, "sourceUnchanged": True}]
        self.assert_blocked(case, "visual_original_retractions_mismatch")
        case["retractions"] = [{**event, "nodeId": "source-A", "eventIndex": 0,
                                "reviewed": True, "confirmed": True, "sourceUnchanged": True}]
        report = self.assert_blocked(case, "confirmed_retraction")
        self.assertEqual(1, report["cases"][0]["counts"]["confirmedRetractions"])
        case["retractions"][0]["confirmed"] = False
        self.assert_blocked(case, "visual_original_retractions_mismatch")

    def test_unknown_source_labels_remain_unknown_despite_correct_vote(self):
        self.case["annotations"][0]["needTranslation"] = "unknown"
        self.case["provenance"]["rawReview"]["annotations"][0]["needTranslation"] = "unknown"
        report = self.assert_blocked(self.case, "unknown_node_label")
        self.assertEqual(1, report["cases"][0]["counts"]["unknownNodes"])
        self.assertEqual(1, report["cases"][0]["counts"]["unknownVisualNodes"])
        self.assertEqual(0, report["cases"][0]["counts"]["TP"])

    def test_genuine_negative_zero_work_policy_preserves_true_nodes_and_strict_omissions(self):
        for assessment in (False, True):
            with self.subTest(assessment=assessment):
                case = self.fixture(False, "automatic-page", assessment)
                report = self.report(case, scope="automatic-page")
                self.assertTrue(report["cases"][0]["completed"], report["cases"][0]["reasons"])
                counts = report["cases"][0]["counts"]
                self.assertEqual((1, 0, 1, 0), tuple(counts[k] for k in
                    ("annotatedPositiveNodes", "translatedPositiveNodes", "policySkippedPositiveNodes", "confirmedOmissions")))
                case["coverageScope"]["mode"] = "strict"
                if assessment:
                    case["hostEvents"].pop()  # Strict retains no assessment events.
                strict = self.assert_blocked(case, "confirmed_omission")
                self.assertEqual(1, strict["cases"][0]["counts"]["confirmedOmissions"])
                self.assertEqual(0, strict["cases"][0]["counts"]["policySkippedPositiveNodes"])
                self.assertTrue(case["annotations"][0]["needTranslation"])

    def test_invalid_negative_proof_and_unknown_false_vote_block_policy_credit(self):
        for mutate in (
            lambda c: c["hostEvents"][0].update(live=False),
            lambda c: c["hostEvents"][-1].update(needsTranslation=True),
            lambda c: c["requestIds"].append("unaccounted-request"),
            lambda c: self.vote(c, "not_visible", 1),
            lambda c: self.vote(c, "correct_translation", 0)):
            case = self.fixture(False, "automatic-page", True)
            mutate(case)
            report = self.assert_blocked(case, scope="automatic-page")
            self.assertEqual(0, report["cases"][0]["counts"]["policySkippedPositiveNodes"])

    def test_original_errors_source_and_retraction_gates_still_apply(self):
        for mutate in (
            lambda c: c.update(errors=["model_error"]),
            lambda c: c.update(blockedReason="provider_blocked"),
            lambda c: c["provenance"]["rawCapture"].update(blockedReason="capture_failed"),
            lambda c: c["provenance"]["rawCapture"].update(settled=False),
            lambda c: c["retractions"].append({"nodeId": "source-A", "reviewed": True,
                                               "confirmed": True, "sourceUnchanged": True})):
            case = deepcopy(self.case)
            mutate(case)
            self.assert_blocked(case)
        self.case["retractions"] = [{"nodeId": "source-A", "reviewed": True,
                                     "confirmed": True, "sourceUnchanged": True}]
        self.assertEqual(1, self.report()["groups"]["overall"]["confirmedRetractions"])

    def test_option_is_strict_boolean_for_public_and_direct_api(self):
        for value in (None, 0, 1, "true", [], {}):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ValueError, "require_visual_review"):
                    self.report(required=value)
                with self.assertRaisesRegex(ValueError, "require_visual_review"):
                    _score_case(self.case, self.site, self.root, require_visual_review=value)

    def test_cli_opt_in_reports_metadata_and_new_gates(self):
        sites = self.root / "sites-authored.json"
        cases = self.root / "cases-authored.json"
        sites.write_text(json.dumps(self.manifest), encoding="utf-8")
        cases.write_text(json.dumps([self.case]), encoding="utf-8")
        args = ["--sites", str(sites), "--cases", str(cases), "--evidence-root", str(self.root)]
        stream = io.StringIO()
        with redirect_stdout(stream):
            self.assertEqual(1, main([*args, "--require-visual-review"]))
        self.assertTrue(json.loads(stream.getvalue())["visualCoverage"]["required"])
        self.assertNotIn("visualCoverage", self.report(required=False))
        cases.write_text("{}", encoding="utf-8")
        stream = io.StringIO()
        with redirect_stdout(stream):
            self.assertEqual(2, main([*args, "--require-visual-review"]))
        self.assertTrue(json.loads(stream.getvalue())["visualCoverage"]["required"])

    def test_negative_zero_call_visual_translation_cannot_lend_policy_skip(self):
        for assessment in (False, True):
            with self.subTest(assessment=assessment):
                case = self.fixture(False, "automatic-page", assessment)
                self.vote(case, "unnecessary_translation", 1)
                original = deepcopy(case)
                stats, completed, reasons = _score_case(
                    case, {**self.site, "expectedPageTranslation": False}, self.root,
                    coverage_scope="automatic-page", require_visual_review=True)
                self.assertFalse(completed)
                self.assertIn("confirmed_omission:source-A", reasons)
                self.assertEqual(1, stats["unnecessaryVisualTranslations"])
                self.assertEqual(0, stats["policySkippedPositiveNodes"])
                self.assertEqual(0, stats["translatedPositiveNodes"])
                self.assertEqual((1, 1, 0, 0), tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))
                self.assertEqual(original, case)

    def test_false_positive_visual_vote_cannot_complete_unfinished_original_piece(self):
        for normalized_claim in (False, True):
            with self.subTest(normalized_claim=normalized_claim):
                case = self.false_positive_fixture("© Example Observatory", true_count=19)
                case["provenance"]["rawCapture"]["predictions"][-1]["completed"] = False
                case["predictions"][1]["completed"] = normalized_claim
                original = deepcopy(case)
                report = self.assert_blocked(case, "visual_eligible_source_without_native_completion")
                counts = report["cases"][0]["counts"]
                self.assertEqual(19, counts["translatedPositiveNodes"])
                self.assertEqual(1, counts["unnecessaryVisualTranslations"])
                self.assertEqual((19, 0, 1, 0), tuple(counts[k] for k in ("TP", "TN", "FP", "FN")))
                self.assertEqual(original, case)


if __name__ == "__main__":
    unittest.main()
