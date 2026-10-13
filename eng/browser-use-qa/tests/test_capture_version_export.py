"""Offline reference export proof checks; no real site or quality certification."""
import copy
import json
import shutil
import subprocess
import unittest

import test_capture as support
import test_capture_bounded_observations as bounded

capture = support.capture


class VersionExportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        node = shutil.which("node")
        if not node:
            raise RuntimeError("Node is required for actual adapter version fixtures")
        result = subprocess.run([node, str(support.HERE / "adapter-check.js"),
                                 "--version-export-fixture", "--bounded-fixture"],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.fixtures = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                      if line.startswith("VERSION_EXPORT_FIXTURE=")))
        cls.legacy = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                    if line.startswith("BOUNDED_FIXTURE=")))

    record = bounded.BoundedObservationTests.record
    predictions = bounded.BoundedObservationTests.predictions
    assert_rejected_atomically = bounded.BoundedObservationTests.assert_rejected_atomically

    def test_1844_reflow_retains_exact_required_views_and_full_current_source_facts(self):
        value = self.fixtures["reflow"]
        saved = copy.deepcopy(value)
        self.assertEqual(len(value["sourceNodes"]), 1844)
        self.assertEqual(len(value["producerObservations"]), 1844)
        self.assertEqual(len(value["independentSourceObservations"]), 1844)
        self.assertEqual(value["independentSourceObservationDropped"], 0)
        self.assertEqual(value["producerObservationDropped"], 0)
        self.assertLessEqual(len(capture.encode(value["independentSourceObservations"])), 2000000)
        old = {(v["source"]["id"], v["sequence"]): v
               for v in self.fixtures["complete"]["independentSourceObservations"]}
        self.assertEqual({(v["source"]["id"], v["sequence"]): v
                          for v in value["independentSourceObservations"]}, old)
        self.assertTrue(all(row["phase"] == "unassessed-full-reflow" for row in value["sourceNodes"]))
        observations, history = self.record(value)
        self.assertEqual(value, saved)
        self.assertEqual(len(observations), 1844)
        self.assertEqual(len(history), 740)
        self.assertTrue(all(row["eligible"] is True and row["idle"] for row in observations.values()))
        predictions, issues = self.predictions(value, observations, history)
        self.assertEqual(issues, [])
        self.assertTrue(all(row["completed"] for row in predictions))

        # Newly rendered admission-reset content has no current producer view.
        # Retained older completed pieces cannot qualify the new visible rows.
        visible = self.fixtures["visibleReflow"]
        observations, history = self.record(visible)
        predictions, issues = self.predictions(visible, observations, history)
        self.assertTrue(all(row["eligible"] is None for row in predictions))
        self.assertEqual(sum(issue.startswith("unknown_prediction:") for issue in issues), 1844)

    def test_reference_export_preserves_atomic_owner_geometry_membership_and_time(self):
        value = self.fixtures["reflow"]
        indexed = capture._independent_source_versions(value,
            {row["id"]: row for row in value["sourceNodes"]}, capture._observation_time(value["at"]))
        rows = capture._expand_producer_transport(value, indexed)
        owner = rows[0]
        peer = next(row for row in rows if row["representedBy"] == owner["sourceId"])
        current = next(row for row in value["sourceNodes"] if row["id"] == owner["sourceId"])
        self.assertNotEqual(owner["source"]["locator"], current["locator"])
        self.assertNotEqual(owner["source"]["rects"], current["rects"])
        self.assertEqual(peer["representedSource"], owner["source"])
        self.assertEqual(peer["representedSourceObservation"], owner["sourceObservation"])
        self.assertEqual(peer["pieces"], owner["pieces"])
        observations, _ = self.record(value)
        self.assertEqual(observations[peer["sourceId"]]["at"], peer["at"])
        self.assertEqual(observations[peer["sourceId"]]["sequence"], peer["sequence"])

    def test_character_pressure_prunes_redundancy_even_below_the_row_limit(self):
        value = self.fixtures["characterPressure"]
        self.assertEqual(len(value["sourceNodes"]), 1000)
        self.assertEqual(len(value["independentSourceObservations"]), 1000)
        self.assertLessEqual(len(capture.encode(value["independentSourceObservations"])), 2000000)
        # The unpruned union has only 2000 rows but exceeds the original budget.
        self.assertGreater(len(capture.encode(value["independentSourceObservations"])) +
                           len(capture.encode(value["sourceNodes"])), 2000000)
        self.assertEqual(value["independentSourceObservationDropped"], 0)
        observations, history = self.record(value)
        predictions, issues = self.predictions(value, observations, history)
        self.assertEqual(issues, [])
        self.assertEqual(len(observations), 1000)
        self.assertTrue(all(row["completed"] for row in predictions))

    def test_unreferenced_membership_time_witnesses_are_required_not_redundant(self):
        value = self.fixtures["membership"]
        self.assertEqual(len(value["sourceNodes"]), 1844)
        self.assertEqual(len(value["producerObservations"]), 1835)
        self.assertEqual(len(value["independentSourceObservations"]), 1844)
        observed_ids = {row["sourceId"] for row in value["producerObservations"]}
        extra = [version for version in value["independentSourceObservations"]
                 if version["source"]["id"] not in observed_ids]
        self.assertEqual(len(extra), 9)
        observations, history = self.record(value)
        predictions, issues = self.predictions(value, observations, history)
        self.assertEqual(sum(row["completed"] for row in predictions), 1835)
        self.assertEqual(sum(issue.startswith("unknown_prediction:") for issue in issues), 9)
        # Loss of one membership witness cannot be inferred from source firstSeen
        # or current negative eligibility. It leaves the entire atomic view unknown.
        value = copy.deepcopy(value)
        lost_id = extra[0]["source"]["id"]
        value["independentSourceObservations"] = [version for version in value["independentSourceObservations"]
            if version["source"]["id"] != lost_id]
        observations, history = self.record(value)
        peer = next(row for row in value["producerObservations"] if row["sourceId"] == "source-2")
        self.assertIsNone(observations[peer["sourceId"]]["eligible"])
        predictions, issues = self.predictions(value, observations, history)
        self.assertIn("unknown_prediction:source-2", issues)

    def test_small_and_original_1901_version_exports_keep_legacy_validation(self):
        value = self.legacy["historical"]
        self.assertEqual(len(value["independentSourceObservations"]), 1901)
        self.assertNotIn("independentSourceObservationExport", value)
        self.assertNotIn("independentSourceObservationExport", self.fixtures["complete"])
        self.record(value)
        # No scope marker means the old current-version guard still applies.
        value = copy.deepcopy(value)
        value["independentSourceObservations"][0]["source"]["locator"] = "0/99999/0"
        self.assert_rejected_atomically(value)

    def test_export_scope_is_explicit_strict_and_does_not_excuse_missing_proof(self):
        changes = [
            lambda v: v.pop("independentSourceObservationExport"),
            lambda v: v.pop("independentSourceObservations"),
            lambda v: v.update(independentSourceObservationExport=None),
            lambda v: v["independentSourceObservationExport"].update(schemaVersion=True),
            lambda v: v["independentSourceObservationExport"].update(schemaVersion=2),
            lambda v: v["independentSourceObservationExport"].update(scope="current"),
            lambda v: v["independentSourceObservationExport"].update(extra=True),
            lambda v: v.update(independentSourceObservationDropped=1),
        ]
        for change in changes:
            with self.subTest(change=change):
                value = copy.deepcopy(self.fixtures["reflow"])
                change(value)
                self.assert_rejected_atomically(value)

    def test_forged_malformed_missing_and_future_versions_are_rejected_atomically(self):
        changes = [
            lambda v: v["producerObservations"][0]["sourceObservation"].update(sequence=999999),
            lambda v: v["producerObservations"][0]["sourceObservation"].update(sequence=True),
            lambda v: v["producerObservations"][0]["sourceObservation"].update(at="2099-01-01T00:00:00Z"),
            lambda v: v["independentSourceObservations"].pop(0),
            lambda v: v["independentSourceObservations"][0]["source"].update(sourceText="Forged factual content"),
            lambda v: v["independentSourceObservations"][0]["source"].update(firstSeen="2099-01-01T00:00:00Z"),
            lambda v: v["independentSourceObservations"][0]["source"].update(locator="0/../0"),
            lambda v: v["independentSourceObservations"][0]["source"]["rects"][0].update(width=-1),
            lambda v: v["independentSourceObservations"][0].update(sequence=True),
            lambda v: v["independentSourceObservations"].append(copy.deepcopy(v["independentSourceObservations"][0])),
            lambda v: v.update(producerObservationTransport=None),
            lambda v: v["producerObservationTransport"].update(values=[]),
        ]
        for change in changes:
            with self.subTest(change=change):
                value = copy.deepcopy(self.fixtures["reflow"])
                change(value)
                self.assert_rejected_atomically(value)

    def test_wrong_owner_and_membership_cannot_import_partial_evidence(self):
        changes = [
            lambda v, peer: peer.update(representedBy="unobserved-owner"),
            lambda v, peer: peer["representedSourceObservation"].update(sequence=1),
            lambda v, peer: peer["representedSourceObservation"].update(at="2099-01-01T00:00:00Z"),
            lambda v, peer: v["producerObservationTransport"]["values"][str(peer["pieces"][0])]
                .update(sourceIds=[peer["representedBy"]]),
            lambda v, peer: peer.update(rendered=True),
        ]
        for change in changes:
            with self.subTest(change=change):
                value = copy.deepcopy(self.fixtures["reflow"])
                peer = next(row for row in value["producerObservations"] if row["representedBy"])
                change(value, peer)
                self.assert_rejected_atomically(value)

    def test_stale_piece_flags_stay_unknown_in_reference_export(self):
        value = self.fixtures["reflow"]
        history = {piece["id"]: {**piece, "completed": False}
                   for piece in value["producerObservationTransport"]["values"].values() if "sourceIds" in piece}
        observations, history = self.record(value, history=history)
        self.assertTrue(all(row["eligible"] is None and row["pieces"] == [] for row in observations.values()))
        predictions, issues = self.predictions(value, observations, history)
        self.assertTrue(all(row["eligible"] is None and not row["completed"] for row in predictions))
        self.assertEqual(sum(issue.startswith("unknown_prediction:") for issue in issues), 1844)

    def test_missing_owner_row_stays_unknown_despite_retained_peer_references(self):
        value = copy.deepcopy(self.fixtures["reflow"])
        lost = value["producerObservations"].pop(0)
        observations, history = self.record(value)
        self.assertNotIn(lost["sourceId"], observations)
        predictions, issues = self.predictions(value, observations, history)
        prediction = next(row for row in predictions if row["nodeId"] == lost["sourceId"])
        self.assertIsNone(prediction["eligible"])
        self.assertFalse(prediction["completed"])
        self.assertIn("unknown_prediction:" + lost["sourceId"], issues)

    def test_necessary_row_and_character_witness_overflow_still_fails(self):
        for name in ("requiredOverflow", "characterOverflow"):
            value = self.fixtures[name]
            with self.subTest(name=name):
                self.assertEqual(value["producerObservationDropped"], 0)
                self.assertGreater(value["independentSourceObservationDropped"], 0)
                self.assertLessEqual(len(value["independentSourceObservations"]), 2048)
                self.assertLessEqual(len(capture.encode(value["independentSourceObservations"])), 2000000 + 4096)
                with self.assertRaisesRegex(capture.CaptureError, "independent_source_observation_overflow"):
                    self.record(value)
                self.assert_rejected_atomically(value)
        self.assertLess(len(self.fixtures["characterOverflow"]["independentSourceObservations"]), 2048)


if __name__ == "__main__":
    unittest.main()
