"""Offline transport regressions, never real-site or quality achievement evidence."""
import copy
import json
import shutil
import subprocess
import unittest

import test_capture as support
import test_capture_transient_observations as transient

capture = support.capture


class BoundedObservationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        node = shutil.which("node")
        if not node:
            raise RuntimeError("Node is required for actual adapter transport fixtures")
        result = subprocess.run([node, str(support.HERE / "adapter-check.js"), "--bounded-fixture"],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.fixtures = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                      if line.startswith("BOUNDED_FIXTURE=")))

    def record(self, value, observations=None, history=None):
        observations = {} if observations is None else observations
        history = {} if history is None else history
        sources = {row["id"]: row for row in value.get("sourceNodes", [])}
        capture.record_producer_observations(observations, value, sources, history)
        return observations, history

    def predictions(self, value, observations, history, finished=True):
        case = {"predictions": [], "predictionHistory": list(history.values()),
                "sourceObservations": list(observations.values()),
                "productionEligibility": value["productionEligibility"], "pageDecision": True,
                "captureFinished": finished, "settled": finished, "evidenceReady": finished,
                "visitSuccess": True, "domainVerified": True, "errors": [], "completed": False,
                "blockedReason": "screenshot_review_pending"}
        nodes = [{**row, "text": row["sourceText"]} for row in value["sourceNodes"]]
        issues = []
        return transient.normalize._predictions(case, nodes, issues), issues

    def test_1900_independent_sources_keep_all_historical_atomic_pieces_with_original_bounds(self):
        value = self.fixtures["historical"]
        self.assertEqual(len(value["sourceNodes"]), 1900)
        self.assertEqual(len(value["producerObservations"]), 1900)
        self.assertLessEqual(self.fixtures["wireCharacters"], 2000000)
        self.assertLessEqual(len(capture.encode(value["producerObservations"])) +
                             len(capture.encode(value["producerObservationTransport"])), 2000000)
        self.assertLessEqual(len(capture.encode(value["independentSourceObservations"])), 2000000)
        self.assertLessEqual(len(value["independentSourceObservations"]), 2048)
        saved = copy.deepcopy(value)
        observations, history = self.record(value)
        self.assertEqual(value, saved)
        self.assertEqual(len(observations), 1900)
        self.assertEqual(len(history), 760)
        self.assertTrue(all(row["eligible"] is True and row["idle"] for row in observations.values()))
        self.assertTrue(all(len(row["pieces"]) == 4 for row in observations.values()))
        predictions, issues = self.predictions(value, observations, history)
        self.assertEqual(issues, [])
        self.assertEqual(len(predictions), 1900)
        self.assertTrue(all(row["completed"] for row in predictions))
        # Expanded duplicate rows really exceed the bound; references solve
        # redundancy, rather than shortening or silently discarding pieces.
        indexed = capture._independent_source_versions(value,
            {row["id"]: row for row in value["sourceNodes"]}, capture._observation_time(value["at"]))
        expanded = capture._expand_producer_transport(value, indexed)
        self.assertGreater(len(capture.encode(expanded)), 2000000)
        self.assertEqual(sum(len(row["pieces"]) for row in expanded), 7600)

    def test_compact_movement_preserves_original_atomic_owner_and_version(self):
        value = self.fixtures["historical"]
        indexed = capture._independent_source_versions(value,
            {row["id"]: row for row in value["sourceNodes"]}, capture._observation_time(value["at"]))
        rows = capture._expand_producer_transport(value, indexed)
        owner = rows[0]
        latest = next(source for source in value["sourceNodes"] if source["id"] == owner["sourceId"])
        self.assertNotEqual(owner["source"]["locator"], latest["locator"])
        self.assertNotEqual(owner["source"]["rects"], latest["rects"])
        peer = next(row for row in rows if row["representedBy"] == owner["sourceId"])
        self.assertEqual(peer["representedSource"], owner["source"])
        self.assertEqual(peer["representedSourceObservation"], owner["sourceObservation"])
        observations, _ = self.record(value)
        self.assertEqual(observations[peer["sourceId"]]["at"], owner["at"])
        self.assertEqual(observations[peer["sourceId"]]["representedBy"], owner["sourceId"])

    def test_partial_and_unfinished_work_never_borrow_completion(self):
        value = self.fixtures["partial"]
        observations, history = self.record(value)
        self.assertTrue(all(row["eligible"] is True and not row["idle"] for row in observations.values()))
        self.assertTrue(all(not piece["completed"] for piece in history.values()))
        predictions, issues = self.predictions(value, observations, history, finished=False)
        self.assertTrue(all(not row["completed"] for row in predictions))
        self.assertTrue(issues)
        # A source with no pieces during scanning must remain unknown, never TN.
        value = copy.deepcopy(value)
        work = next(item for item in value["producerObservationTransport"]["values"].values()
                    if "scanning" in item)
        self.assertTrue(work["scanning"])
        row = value["producerObservations"][0]
        row["pieces"] = []; row["eligible"] = None
        observations, history = self.record(value)
        self.assertIsNone(observations[row["sourceId"]]["eligible"])
        predictions, issues = self.predictions(value, observations, history)
        prediction = next(item for item in predictions if item["nodeId"] == row["sourceId"])
        self.assertIsNone(prediction["eligible"])
        self.assertTrue(issues)

    def test_newer_hidden_piece_versions_remain_unknown(self):
        value = self.fixtures["partial"]
        observations, history = self.record(value)
        for piece in history.values():
            piece["completed"] = True
        self.record(value, observations, history)
        self.assertTrue(all(row["eligible"] is None and row["pieces"] == [] for row in observations.values()))
        predictions, issues = self.predictions(value, observations, history)
        self.assertTrue(all(row["eligible"] is None for row in predictions))
        self.assertEqual(sum(issue.startswith("unknown_prediction:") for issue in issues), 1900)

    def assert_rejected_atomically(self, value):
        observations, history = {"existing": {"at": "kept"}}, {"existing": {"kept": True}}
        saved = copy.deepcopy((observations, history))
        with self.assertRaises(capture.CaptureError):
            self.record(value, observations, history)
        self.assertEqual((observations, history), saved)

    def test_missing_forged_future_malformed_and_conflicting_references_fail_atomically(self):
        original = self.fixtures["historical"]
        changes = [
            lambda v: v["producerObservationTransport"].update(schemaVersion=2),
            lambda v: v["producerObservationTransport"].update(schemaVersion=True),
            lambda v: v["producerObservations"][0].update(work=True),
            lambda v: v["producerObservations"][0].update(pieces=[999999]),
            lambda v: v["producerObservations"][0]["sourceObservation"].update(sequence=999999),
            lambda v: v["producerObservations"][0].update(source={"id": "forged"}),
            lambda v: v["producerObservations"][1].update(representedBy="unobserved"),
            lambda v: next(row for row in v["producerObservations"] if row["representedBy"])
                ["representedSourceObservation"].update(at="2099-01-01T00:00:00Z"),
            lambda v: v["independentSourceObservations"][0]["source"].update(sourceText="Forged facts"),
            lambda v: v["independentSourceObservations"][0]["source"].update(locator="0/99999/0"),
            lambda v: v["independentSourceObservations"].append(copy.deepcopy(v["independentSourceObservations"][0])),
            lambda v: v.pop("independentSourceObservations"),
            lambda v: v["producerObservationTransport"]["values"].pop(str(v["producerObservations"][0]["work"])),
        ]
        for change in changes:
            value = copy.deepcopy(original); change(value)
            with self.subTest(change=change):
                self.assert_rejected_atomically(value)
        value = copy.deepcopy(original)
        definitions = value["producerObservationTransport"]["values"]
        piece_id = value["producerObservations"][0]["pieces"][0]
        definitions["999999"] = {**definitions[str(piece_id)], "completed": False}
        next(row for row in value["producerObservations"]
             if row["representedBy"] == value["producerObservations"][0]["sourceId"])["pieces"][0] = 999999
        self.assert_rejected_atomically(value)

    def test_real_row_and_character_overflow_still_fail(self):
        for name in ("rowOverflow", "characterOverflow", "pieceOverflow"):
            value = self.fixtures[name]
            self.assertGreater(value["producerObservationDropped"] + value["independentSourceObservationDropped"], 0)
            self.assert_rejected_atomically(value)
        # Definitions themselves count toward the original character budget.
        value = copy.deepcopy(self.fixtures["historical"])
        definitions = value["producerObservationTransport"]["values"]
        reference = str(value["producerObservations"][0]["pieces"][0])
        definitions[reference]["text"] = "x" * 2000001
        self.assert_rejected_atomically(value)

    def test_missing_row_remains_unknown_even_when_current_eligibility_is_false(self):
        value = copy.deepcopy(self.fixtures["historical"])
        lost = value["producerObservations"].pop()
        observations, history = self.record(value)
        self.assertNotIn(lost["sourceId"], observations)
        predictions, issues = self.predictions(value, observations, history)
        row = next(item for item in predictions if item["nodeId"] == lost["sourceId"])
        self.assertIsNone(row["eligible"])
        self.assertFalse(row["completed"])
        self.assertIn("unknown_prediction:" + lost["sourceId"], issues)

    def test_case_capture_journals_compact_transport_and_exports_only_public_v1_rows(self):
        helper = support.WorkflowTests()
        helper.setUp(); self.addCleanup(helper.tearDown)
        browser = helper.browser(skipped=True, probe_states=[{
            "id": "probe-1", "stage": "completed", "result": {"samples": [], "statistics": {}}}])
        original_qa = browser.qa
        fixture = self.fixtures["historical"]
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "sample" and browser.configured:
                value["at"] = fixture["at"]
                value["sourceNodes"].extend(copy.deepcopy(fixture["sourceNodes"]))
                for key in ("producerObservations", "producerObservationDropped", "producerObservationTransport",
                            "independentSourceObservations", "independentSourceObservationDropped"):
                    value[key] = copy.deepcopy(fixture[key])
            return value
        browser.qa = qa
        def events(request, requests):
            return [{"type": "assessment", "id": request["id"], "caseId": request["caseId"],
                     "needsTranslation": False, "allowImages": False}]
        result, _, _, journal = helper.scripted_case(events, browser=browser,
            run_id="bounded-transport-journal", site={**support.SITE, "expectedPageTranslation": False})
        self.assertEqual(result["errors"], [])
        self.assertEqual(len(result["sourceObservations"]), 1900)
        self.assertEqual(len(result["predictionHistory"]), 760)
        self.assertNotIn("producerObservationTransport", result)
        self.assertTrue(all(isinstance(piece, dict) for row in result["sourceObservations"] for piece in row["pieces"]))
        self.assertTrue(any(event["kind"] == "sample" and event["value"].get("producerObservationTransport")
                            == fixture["producerObservationTransport"] for event in journal))
        self.assertFalse(result["completed"])
        self.assertTrue(result["simulation"])


if __name__ == "__main__":
    unittest.main()
