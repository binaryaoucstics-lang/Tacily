"""Independent offline assessment fixtures; no real capture or quality claims."""

import base64
from copy import deepcopy
from pathlib import Path
import tempfile
import unittest

from evaluate import evaluate, THRESHOLDS


ROOT = Path(__file__).resolve().parents[2]
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")


def assessment_fixture():
    """Author the normalized interface directly, without using normalize.py.

    Review labels are authored from source text. Production eligibility is a
    separate fixture input and deliberately disagrees in counterexample tests.
    The nine English rows are true even on this Chinese-dominant negative page.
    """
    site = {"id": "assessment-fixture", "registeredDomain": "assessment.test",
            "url": "https://assessment.test/news", "category": "news",
            "language": "zh-Hans", "expectedPageTranslation": False,
            "split": "development"}
    reviewed_english = (
        "English headline", "Science and technology", "World news",
        "About us", "Contact us", "Privacy policy", "Media centre",
        "Subscribe", "FOLLOW")
    reviewed_sources = [(text, True) for text in reviewed_english]
    reviewed_sources += [("中文新闻正文", False)] * 403
    case = deepcopy(site)
    case.update(
        coverageScope={"schemaVersion": 1, "mode": "automatic-page"},
        title="Offline Chinese-dominant assessment fixture", finalUrl=site["url"],
        viewport={"width": 1280, "height": 720}, sourceHead="b" * 40,
        capturedAt="2026-10-13T01:00:00Z", visitSuccess=True,
        liveTranslation=False, completed=True, pageDecision=False,
        requestIds=[], terminals=[], requestCount=0, errors=[], blockedReason="",
        pageAnnotation={"needTranslation": False, "reviewed": True, "independent": True},
        sourceNodes=[], annotations=[], predictions=[], retractions=[],
        hostEvents=[
            {"schemaVersion": 1, "id": "fixture-capabilities", "caseId": site["id"],
             "type": "capabilities", "live": True},
            {"schemaVersion": 1, "id": "fixture-local-assess", "caseId": site["id"],
             "type": "assessment", "live": True, "needsTranslation": False,
             "reason": "target_language_dominant", "confidence": .9,
             "detectedLanguage": "zh", "isMixedTargetLanguage": True}],
        screenshots={phase: {"path": "fixture.png", "reviewed": True}
                     for phase in ("before", "after", "stable")})
    case["screenshots"]["checks"] = dict.fromkeys(
        ("firstViewport", "mainContent", "afterScroll", "layout", "dynamicReturn"), True)
    for index, (text, reviewed_need) in enumerate(reviewed_sources):
        node_id = f"source-{index}"
        case["sourceNodes"].append({"id": node_id, "text": text, "tag": "p",
            "role": "paragraph", "position": {"x": 10, "y": index * 25,
                                                 "width": 300, "height": 20}})
        case["annotations"].append({"nodeId": node_id, "needTranslation": reviewed_need,
                                    "reviewed": True, "independent": True})
        case["predictions"].append({"nodeId": node_id, "eligible": index in range(9),
                                    "completed": False, "failed": False})
    return site, case


def terminal_fixture(case, request_id="native-request", request_count=0):
    return {"id": request_id, "caseId": case["id"], "type": "terminal",
            "success": True, "requestCount": request_count, "cacheHits": 0,
            "failedCount": 0}


class AssessmentProofTests(unittest.TestCase):
    def setUp(self):
        public = ROOT / ".parallel" / "development-evidence"
        public.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="evaluate-assessment-", dir=public)
        self.addCleanup(self.temp.cleanup)
        self.evidence = Path(self.temp.name)
        (self.evidence / "fixture.png").write_bytes(PNG)
        self.site, self.case = assessment_fixture()

    def report(self, case=None, site=None, scope="automatic-page"):
        return evaluate({"schemaVersion": 1, "sites": [self.site if site is None else site]},
                        [self.case if case is None else case], self.evidence,
                        coverage_scope=scope)

    def assert_blocked(self, case, reason=None):
        record = self.report(case)["cases"][0]
        self.assertFalse(record["completed"])
        self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
        if reason:
            self.assertIn(reason, record["reasons"])
        return record

    def test_actual_assessment_has_no_terminal_and_preserves_all_counts(self):
        original = deepcopy(self.case)
        report = self.report()
        record = report["cases"][0]
        self.assertTrue(record["completed"])
        self.assertEqual([], record["reasons"])
        for stats in (record["counts"], report["groups"]["overall"],
                      report["groups"]["development"]):
            self.assertEqual((9, 403, 0, 0), tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))
            for key, expected in {"annotatedPositiveNodes": 9, "translatedPositiveNodes": 0,
                                  "unTranslatedPositiveNodes": 9,
                                  "policySkippedPositiveNodes": 9, "confirmedOmissions": 0,
                                  "unnecessaryRequests": 0}.items():
                self.assertEqual(expected, stats[key])
        self.assertEqual(0, report["groups"]["overall"]["translationCoverage"])
        self.assertEqual(1, report["groups"]["overall"]["accuracy"])
        self.assertEqual(1, report["groups"]["overall"]["pageAccuracy"])
        self.assertEqual({"schemaVersion": 1, "mode": "automatic-page"}, report["coverageScope"])
        self.assertFalse(report["achieved"])
        self.assertFalse(report["gates"]["independentDomains"])
        self.assertFalse(report["gates"]["completedHoldoutDomains"])
        self.assertEqual({"independentDomains": 100, "nodeAccuracy": .95,
                          "nodePrecision": .95, "pageAccuracy": .97,
                          "completedHoldoutDomains": 30}, THRESHOLDS)
        self.assertEqual(original, self.case)
        self.assertEqual([], self.case["terminals"])
        self.assertFalse(any(e["type"] == "terminal" for e in self.case["hostEvents"]))

    def test_successful_terminal_and_assessment_proofs_produce_identical_report(self):
        terminal_case = deepcopy(self.case)
        terminal = terminal_fixture(terminal_case)
        terminal_case["requestIds"] = [terminal["id"]]
        terminal_case["terminals"] = [terminal]
        terminal_case["hostEvents"][1:] = [deepcopy(terminal)]
        self.assertEqual(self.report(), self.report(terminal_case))
        # A real assessment can accompany native work, but that work must retain
        # its actual complete successful terminal and the original identity.
        terminal_case["hostEvents"].append(deepcopy(self.case["hostEvents"][1]))
        self.assertEqual(self.report(), self.report(terminal_case))

    def test_default_strict_does_not_use_assessment_or_change_legacy_scoring(self):
        case = deepcopy(self.case)
        case.pop("coverageScope")
        report = evaluate({"schemaVersion": 1, "sites": [self.site]}, [case], self.evidence)
        record = report["cases"][0]
        self.assertFalse(record["completed"])
        self.assertEqual(9, record["counts"]["confirmedOmissions"])
        self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
        legacy = deepcopy(case)
        legacy["hostEvents"] = []
        legacy.pop("requestIds")
        legacy.pop("terminals")
        self.assertEqual(report, self.report(legacy, scope="strict"))
        case["coverageScope"] = {"schemaVersion": 1, "mode": "strict"}
        self.assertEqual(report, self.report(case, scope="strict"))

    def test_bare_capabilities_and_missing_capabilities_still_fail(self):
        for events in ([], [self.case["hostEvents"][0]], [self.case["hostEvents"][1]]):
            with self.subTest(events=events):
                case = deepcopy(self.case)
                case["hostEvents"] = deepcopy(events)
                record = self.assert_blocked(case)
                self.assertEqual(9, record["counts"]["confirmedOmissions"])
        for live in (False, None, 1, "true"):
            case = deepcopy(self.case)
            case["hostEvents"][0]["live"] = live
            self.assert_blocked(case, "non_live_model")
        case = deepcopy(self.case)
        case["localAssessment"] = case["hostEvents"].pop()
        self.assert_blocked(case, "missing_or_invalid_zero_call_terminal")

    def test_assessment_version_live_and_decision_are_strictly_typed(self):
        changes = {
            "schemaVersion": (None, True, 1.0, "1", 0, 2),
            "live": (None, False, 1, "true"),
            "needsTranslation": (None, True, 0, "false", "unknown")}
        for field, values in changes.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    case = deepcopy(self.case)
                    case["hostEvents"][1][field] = value
                    self.assert_blocked(case, "invalid_host_assessment")
            case = deepcopy(self.case)
            del case["hostEvents"][1][field]
            self.assert_blocked(case, "invalid_host_assessment")

    def test_assessment_identity_shape_and_unknown_events_remain_invalid(self):
        for field, values in {"id": (None, "", 1, []),
                              "caseId": (None, "other-case", 1)}.items():
            for value in values:
                with self.subTest(field=field, value=value):
                    case = deepcopy(self.case)
                    case["hostEvents"][1][field] = value
                    self.assert_blocked(case, "host_event_case_identity_mismatch")
        for event in (None, [], "assessment"):
            case = deepcopy(self.case)
            case["hostEvents"][1] = event
            self.assert_blocked(case, "invalid_host_events")
        for event_type in (None, "packet", "error", "unknown"):
            case = deepcopy(self.case)
            case["hostEvents"][1]["type"] = event_type
            self.assert_blocked(case, "invalid_host_event_type")

    def test_empty_request_and_terminal_lists_must_be_explicit_and_consistent(self):
        for field in ("requestIds", "terminals"):
            for value in (None, {}, "", [None], [""], ["orphan-request"]):
                with self.subTest(field=field, value=value):
                    case = deepcopy(self.case)
                    case[field] = value
                    self.assert_blocked(case)
            case = deepcopy(self.case)
            del case[field]
            self.assert_blocked(case)
        for requests in (["r", "r"], [1], [[]], [{"id": "r"}]):
            case = deepcopy(self.case)
            case["requestIds"] = requests
            self.assert_blocked(case, "invalid_request_identities")
        for count in (None, True, False, 0.0, "0", -1, 1):
            case = deepcopy(self.case)
            case["requestCount"] = count
            self.assert_blocked(case)

    def test_assessment_identity_requires_nonblank_id_and_matching_nonblank_case_id(self):
        for blank in ("   ", "\t\r\n", "\u3000"):
            for field in ("id", "caseId"):
                with self.subTest(field=field, blank=repr(blank)):
                    case = deepcopy(self.case)
                    case["hostEvents"][1][field] = blank
                    self.assert_blocked(case, "host_event_case_identity_mismatch")
            # Matching the manifest is insufficient when the case id is blank.
            site, case = deepcopy(self.site), deepcopy(self.case)
            site["id"] = case["id"] = blank
            for event in case["hostEvents"]:
                event["caseId"] = blank
            record = self.report(case, site)["cases"][0]
            self.assertFalse(record["completed"])
            self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])
            self.assertIn("host_event_case_identity_mismatch", record["reasons"])

    def test_duplicate_and_conflicting_assessment_identities_cannot_receive_credit(self):
        for changes in ({}, {"reason": "different-reason"}, {"needsTranslation": True},
                        {"caseId": "other-case"}, {"live": False}):
            for mixed_work in (False, True):
                with self.subTest(changes=changes, mixed_work=mixed_work):
                    case = deepcopy(self.case)
                    duplicate = deepcopy(case["hostEvents"][1])
                    duplicate.update(changes)
                    case["hostEvents"].append(duplicate)
                    if mixed_work:
                        terminal = terminal_fixture(case)
                        case["requestIds"] = [terminal["id"]]
                        case["terminals"] = [deepcopy(terminal)]
                        case["hostEvents"].append(deepcopy(terminal))
                    self.assert_blocked(case, "duplicate_assessment_event")

    def test_assessment_id_cannot_collide_with_capability_or_terminal_evidence(self):
        for event_type in ("capabilities", "terminal"):
            for reversed_events in (False, True):
                with self.subTest(event_type=event_type, reversed_events=reversed_events):
                    case = deepcopy(self.case)
                    if event_type == "capabilities":
                        case["hostEvents"][1]["id"] = case["hostEvents"][0]["id"]
                    else:
                        terminal = terminal_fixture(case, case["hostEvents"][1]["id"])
                        case["requestIds"] = [terminal["id"]]
                        case["terminals"] = [deepcopy(terminal)]
                        case["hostEvents"].append(deepcopy(terminal))
                    if reversed_events:
                        case["hostEvents"].reverse()
                    self.assert_blocked(case, "host_event_identity_collision")

    def test_assessment_identity_checks_preserve_strict_legacy_behavior(self):
        case = deepcopy(self.case)
        case["coverageScope"]["mode"] = "strict"
        legacy_report = self.report(case, scope="strict")
        for mutation in ("blank-id", "duplicate", "capability-collision", "terminal-collision"):
            with self.subTest(mutation=mutation):
                changed = deepcopy(case)
                if mutation == "blank-id":
                    changed["hostEvents"][1]["id"] = "   "
                elif mutation == "duplicate":
                    changed["hostEvents"].append(deepcopy(changed["hostEvents"][1]))
                elif mutation == "capability-collision":
                    changed["hostEvents"][1]["id"] = changed["hostEvents"][0]["id"]
                else:
                    terminal = terminal_fixture(changed, changed["hostEvents"][1]["id"])
                    changed["hostEvents"].append(terminal)
                self.assertEqual(legacy_report, self.report(changed, scope="strict"))

    def test_assessment_never_hides_missing_or_corrupt_terminal_for_issued_work(self):
        for location in ("both", "raw-only", "event-only"):
            for field, value in (("success", False), ("success", 1), ("failedCount", 1),
                                 ("failedCount", False), ("cacheHits", -1), ("cacheHits", True),
                                 ("requestCount", None), ("requestCount", True),
                                 ("requestCount", 1), ("id", ""), ("id", "other"),
                                 ("caseId", "other-case"), ("type", "unknown")):
                with self.subTest(location=location, field=field, value=value):
                    case = deepcopy(self.case)
                    case["requestIds"] = ["native-request"]
                    terminal = terminal_fixture(case)
                    terminal[field] = value
                    if location != "event-only":
                        case["terminals"] = [deepcopy(terminal)]
                    if location != "raw-only":
                        case["hostEvents"].append(deepcopy(terminal))
                    self.assert_blocked(case)
        case = deepcopy(self.case)
        case["requestIds"] = ["issued-but-no-terminal"]
        self.assert_blocked(case, "request_terminal_identity_mismatch")
        # Python dict equality alone equates False with 0, True with 1 and
        # integer counts with floats. Both normalized views must stay typed.
        for field, value in (("success", 1), ("failedCount", False),
                             ("cacheHits", False), ("requestCount", 0.0)):
            case = deepcopy(self.case)
            terminal = terminal_fixture(case)
            case["requestIds"] = [terminal["id"]]
            case["terminals"] = [deepcopy(terminal)]
            case["hostEvents"].append(deepcopy(terminal))
            case["terminals"][0][field] = value
            self.assert_blocked(case, "terminal_host_events_mismatch")

    def test_multiple_requests_need_complete_unique_bijection_not_completion_order(self):
        case = deepcopy(self.case)
        case["requestIds"] = ["r1", "r2"]
        case["terminals"] = [terminal_fixture(case, "r2"), terminal_fixture(case, "r1")]
        case["hostEvents"] += deepcopy(case["terminals"])
        self.assertTrue(self.report(case)["cases"][0]["completed"])
        for requests in (["r1"], ["r1", "unknown"], ["r1", "r1"], []):
            broken = deepcopy(case)
            broken["requestIds"] = requests
            self.assert_blocked(broken)
        broken = deepcopy(case)
        broken["terminals"][1] = deepcopy(broken["terminals"][0])
        broken["hostEvents"][-1] = deepcopy(broken["terminals"][0])
        self.assert_blocked(broken, "duplicate_terminal_event")

    def test_real_nonzero_calls_remain_unnecessary_and_cannot_receive_policy_credit(self):
        case = deepcopy(self.case)
        terminal = terminal_fixture(case, request_count=1)
        case["requestIds"] = [terminal["id"]]
        case["terminals"] = [deepcopy(terminal)]
        case["hostEvents"].append(deepcopy(terminal))
        case["requestCount"] = 1
        case["liveTranslation"] = True
        report = self.report(case)
        record = report["cases"][0]
        self.assertFalse(record["completed"])
        self.assertIn("unnecessary_translation_requests", record["reasons"])
        for stats in (record["counts"], report["groups"]["overall"],
                      report["groups"]["development"]):
            self.assertEqual((9, 403, 0, 0), tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))
            self.assertEqual(1, stats["unnecessaryRequests"])
            self.assertEqual(0, stats["policySkippedPositiveNodes"])
            self.assertEqual(9, stats["confirmedOmissions"])
        self.assertFalse(report["gates"]["overall"]["noUnnecessaryRequests"])

    def test_invalid_assessment_is_not_masked_by_valid_terminal(self):
        for field, value in (("schemaVersion", True), ("live", False),
                             ("needsTranslation", True), ("id", "")):
            case = deepcopy(self.case)
            terminal = terminal_fixture(case)
            case["requestIds"] = [terminal["id"]]
            case["terminals"] = [deepcopy(terminal)]
            case["hostEvents"].append(deepcopy(terminal))
            case["hostEvents"][1][field] = value
            self.assert_blocked(case)

    def test_independent_page_review_and_actual_decision_cannot_be_inferred(self):
        for field, value in (("pageDecision", True), ("pageDecision", 0),
                             ("pageDecision", None), ("pageAnnotation", None),
                             ("pageAnnotation", {"needTranslation": False, "reviewed": False,
                                                 "independent": True}),
                             ("pageAnnotation", {"needTranslation": False, "reviewed": True,
                                                 "independent": False}),
                             ("pageAnnotation", {"needTranslation": True, "reviewed": True,
                                                 "independent": True}),
                             ("pageAnnotation", {"needTranslation": "unknown", "reviewed": True,
                                                 "independent": True})):
            with self.subTest(field=field, value=value):
                case = deepcopy(self.case)
                case[field] = value
                self.assert_blocked(case)
        case = deepcopy(self.case)
        case["expectedPageTranslation"] = True
        self.assert_blocked(case, "manifest_mismatch")

    def test_assessment_cannot_replace_translation_work_on_positive_page(self):
        site, case = deepcopy(self.site), deepcopy(self.case)
        site["expectedPageTranslation"] = case["expectedPageTranslation"] = True
        case["pageAnnotation"]["needTranslation"] = True
        case["liveTranslation"] = True
        record = self.report(case, site)["cases"][0]
        self.assertFalse(record["completed"])
        self.assertIn("missing_live_request_or_successful_terminal", record["reasons"])
        self.assertEqual(9, record["counts"]["confirmedOmissions"])
        self.assertEqual(0, record["counts"]["policySkippedPositiveNodes"])

    def test_independent_node_truth_and_confusion_matrix_survive_policy_skip(self):
        case = deepcopy(self.case)
        case["predictions"][0]["eligible"] = False
        case["predictions"][9]["eligible"] = True
        case.update(counts={"TP": 412}, translatedPositiveNodes=412,
                    policySkippedPositiveNodes=412, unTranslatedPositiveNodes=0)
        report = self.report(case)
        self.assertTrue(report["cases"][0]["completed"])
        for stats in (report["cases"][0]["counts"], report["groups"]["overall"]):
            self.assertEqual((8, 402, 1, 1), tuple(stats[k] for k in ("TP", "TN", "FP", "FN")))
            self.assertEqual(9, stats["policySkippedPositiveNodes"])
            self.assertEqual(9, stats["unTranslatedPositiveNodes"])
            self.assertEqual(0, stats["translatedPositiveNodes"])
        self.assertEqual(9, sum(a["needTranslation"] for a in case["annotations"]))

    def test_unknown_missing_failed_and_contradictory_predictions_still_block(self):
        for mutation in ("missing", "unknown", "failed", "contradictory"):
            with self.subTest(mutation=mutation):
                case = deepcopy(self.case)
                if mutation == "missing":
                    case["predictions"].pop(0)
                elif mutation == "unknown":
                    case["predictions"][0]["eligible"] = None
                elif mutation == "failed":
                    case["predictions"][0]["failed"] = True
                else:
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
        case = deepcopy(self.case)
        case["annotations"][0]["needTranslation"] = "unknown"
        report = self.report(case)
        self.assertFalse(report["cases"][0]["completed"])
        self.assertEqual(1, report["groups"]["overall"]["unknownNodes"])
        self.assertFalse(report["gates"]["overall"]["noUnknownLabels"])

    def test_original_errors_source_screenshots_unsettled_and_retractions_stay_blocking(self):
        for field, value in (("errors", ["unknown_source_membership"]), ("errors", None),
                             ("blockedReason", "stale_source_version"),
                             ("blockedReason", "unresolved_source_ownership"),
                             ("blockedReason", "unsettled"), ("completed", False),
                             ("screenshots", {}), ("sourceNodes", []), ("retractions", None),
                             ("retractions", [{"nodeId": "source-0", "reviewed": True,
                                               "confirmed": True, "sourceUnchanged": True}])):
            with self.subTest(field=field, value=value):
                case = deepcopy(self.case)
                case[field] = value
                report = self.report(case)
                self.assertFalse(report["cases"][0]["completed"])
                self.assertFalse(report["achieved"])
                if field not in ("retractions",):
                    self.assertEqual(0, report["cases"][0]["counts"]["policySkippedPositiveNodes"])
                if isinstance(value, list) and value and isinstance(value[0], dict):
                    self.assertEqual(1, report["groups"]["overall"]["confirmedRetractions"])
                    self.assertFalse(report["gates"]["overall"]["noConfirmedRetractions"])
        case = deepcopy(self.case)
        case["screenshots"]["checks"]["dynamicReturn"] = False
        self.assert_blocked(case, "missing_screenshot_checks")


if __name__ == "__main__":
    unittest.main()
