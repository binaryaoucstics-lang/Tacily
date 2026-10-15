"""Independent offline proof-pressure checks; never certify real page quality."""
import collections
import copy
import json
from pathlib import Path
import shutil
import subprocess
import unittest

import test_capture as support
import test_capture_bounded_observations as bounded

capture = support.capture


def version_key(version):
    return version["sourceId"], version["at"], version["sequence"]


def pressure_audit(sample):
    """Replay every supplied cache operation; prove retained membership witnesses."""
    before, after = sample["producersBefore"], sample["producersAfter"]
    # These are exact rows, owner references and piece definitions, not counts.
    assert before == after
    required = {}
    for row in before["rows"]:
        versions = {version_key(version): version for version in row["versions"]}
        for field, source_id in (("sourceObservation", row["sourceId"]),
                                 ("representedSourceObservation", row["representedBy"])):
            reference = row[field]
            if source_id is None:
                assert reference is None
                continue
            key = source_id, reference["at"], reference["sequence"]
            assert key in versions
            assert key not in required or required[key] == versions[key]
            required[key] = versions[key]
    available = {version_key(version): version for version in sample["census"]}
    available.update(required)
    for row in before["rows"]:
        for piece_id in row["pieces"]:
            piece = before["definitions"][str(piece_id)]
            assert piece["sourceId"] in piece["sourceIds"]
            assert row["sourceId"] in piece["sourceIds"]
            for member in piece["sourceIds"]:
                witnesses = [version for key, version in available.items()
                             if key[0] == member and key[1:] <= (row["at"], row["sequence"])]
                assert witnesses, "missing membership-time proof"
                if not any(key[0] == member and key[1:] <= (row["at"], row["sequence"])
                           for key in required):
                    witness = min(witnesses, key=lambda version: version_key(version)[1:])
                    required[version_key(witness)] = witness
    assert len(required) <= 2048
    assert sum(version["size"] for version in required.values()) <= 2000000
    registry = collections.OrderedDict((version["sourceId"], version) for version in sample["census"])
    assert len(registry) == sample["startRows"]
    characters = sum(version["size"] for version in registry.values())
    assert characters == sample["startCharacters"]
    evictions = iter(sample["evictions"])
    eviction = next(evictions, None)
    retained, superseded, peak = 0, 0, 0
    # The new latest registry contains only exact source-key/time metadata. The
    # independently required full sourceNodes rows are not duplicated there.
    references = {version["sourceId"]: {key: version[key] for key in ("sourceId", "at", "sequence")}
                  for version in sample["census"]}
    for delta in sample["deltas"]:
        assert len(registry) == delta["rowsBefore"]
        assert characters == delta["charactersBefore"]
        candidate = delta["candidate"]
        previous = registry.pop(candidate["sourceId"], None)
        assert previous == delta["previous"]
        if previous:
            characters -= previous["size"]
        peak = max(peak, characters + candidate["size"])
        while eviction and eviction["candidate"] == candidate:
            assert eviction["rowsBefore"] == len(registry)
            assert eviction["charactersBefore"] == characters
            assert eviction["characterPressure"] == (characters + candidate["size"] > 2000000)
            assert eviction["rowPressure"] == (len(registry) >= 2048)
            victim = registry.popitem(last=False)[1]
            assert victim == eviction["victim"]
            if version_key(victim) in required:
                # Removal from latest cache does not remove the producer's
                # actual immutable version object. It remains indispensable.
                retained += 1
            else:
                assert any(key[0] == victim["sourceId"] and key[1:] <= version_key(victim)[1:]
                           for key in required), "unproved discarded version"
                superseded += 1
            characters -= victim["size"]
            eviction = next(evictions, None)
        registry[candidate["sourceId"]] = candidate
        characters += candidate["size"]
        references[candidate["sourceId"]] = {key: candidate[key] for key in ("sourceId", "at", "sequence")}
        assert len(references) <= 2048
        assert sum(len(capture.encode(value)) for value in references.values()) <= 2000000
    assert eviction is None
    assert len(registry) == sample["endRows"]
    assert characters == sample["endCharacters"]
    return {"sequence": sample["sequence"], "deltas": len(sample["deltas"]),
            "evictions": retained + superseded, "exactRetainedVictims": retained,
            "laterOptionalVictims": superseded, "requiredRows": len(required),
            "requiredCharacters": sum(version["size"] for version in required.values()),
            "peakAttemptCharacters": peak}


class RegistryPressureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        node = shutil.which("node")
        if not node:
            raise RuntimeError("Node is required for the actual offline adapter")
        result = subprocess.run([node, str(support.HERE / "adapter-check.js"), "--registry-pressure-fixture"],
                                capture_output=True, text=True, encoding="utf-8", timeout=45)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.fixtures = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                      if line.startswith("REGISTRY_PRESSURE_FIXTURE=")))

    record = bounded.BoundedObservationTests.record
    predictions = bounded.BoundedObservationTests.predictions
    assert_rejected_atomically = bounded.BoundedObservationTests.assert_rejected_atomically

    def test_full_current_display_and_geometry_pressure_preserve_exact_historical_proof(self):
        for mode in ("display", "geometry"):
            with self.subTest(mode=mode):
                fixture = self.fixtures[mode]
                value = fixture["historical"]
                saved = copy.deepcopy(value)
                self.assertGreater(len(capture.encode(value["sourceNodes"])), 2000000)
                self.assertEqual(value["independentSourceObservationDropped"], 0)
                self.assertEqual(value["producerObservationDropped"], 0)
                self.assertEqual(value["independentSourceObservationExport"], {"schemaVersion": 1, "scope": "references"})
                self.assertEqual({(v["source"]["id"], v["at"], v["sequence"]): v
                                  for v in value["independentSourceObservations"]},
                                 {(v["source"]["id"], v["at"], v["sequence"]): v
                                  for v in fixture["complete"]["independentSourceObservations"]})
                self.assertEqual(value["independentSourceObservations"], fixture["fresh"]["independentSourceObservations"])
                self.assertTrue(all(len(row["rects"]) == (180 if mode == "geometry" else 1)
                                    for row in value["sourceNodes"]))
                if mode == "display":
                    self.assertTrue(all(len(row["displayedText"]) > 7000 for row in value["sourceNodes"]))
                observations, history = self.record(value)
                predictions, issues = self.predictions(value, observations, history)
                self.assertEqual(issues, [])
                self.assertTrue(all(row["completed"] for row in predictions))
                self.assertEqual(value, saved)
                indexed = capture._independent_source_versions(value,
                    {row["id"]: row for row in value["sourceNodes"]}, capture._observation_time(value["at"]))
                expanded = (capture._expand_producer_transport(value, indexed)
                            if "producerObservationTransport" in value else value["producerObservations"])
                peer = next(row for row in expanded if row["representedBy"])
                owner = next(row for row in expanded if row["sourceId"] == peer["representedBy"])
                self.assertEqual(peer["representedSource"], owner["source"])
                self.assertEqual(peer["pieces"], owner["pieces"])
                latest = next(row for row in value["sourceNodes"] if row["id"] == owner["sourceId"])
                self.assertNotEqual(latest["locator"], owner["source"]["locator"])
                self.assertNotEqual(latest["rects"], owner["source"]["rects"])

    def test_missing_forged_owner_member_time_geometry_and_fact_witnesses_fail_atomically(self):
        changes = [
            lambda v: v["independentSourceObservations"].pop(0),
            lambda v: v["producerObservations"][0]["sourceObservation"].update(sequence=99999),
            lambda v: v["producerObservations"][0]["sourceObservation"].update(at="2099-01-01T00:00:00Z"),
            lambda v: v["producerObservations"][0]["sourceObservation"].update(sequence=True),
            lambda v: v["independentSourceObservations"][0]["source"].update(sourceText="Changed factual quantity 999"),
            lambda v: v["independentSourceObservations"][0]["source"].update(firstSeen="2099-01-01T00:00:00Z"),
            lambda v: v["independentSourceObservations"][0]["source"]["rects"][0].update(width=-1),
            lambda v: next(row for row in v["producerObservations"] if row["representedBy"])
                .update(representedBy="missing-independent-owner"),
            lambda v: next(row for row in v["producerObservations"] if row["representedBy"])
                ["representedSourceObservation"].update(sequence=1),
        ]
        for mode in ("display", "geometry"):
            for change in changes:
                with self.subTest(mode=mode, change=change):
                    value = copy.deepcopy(self.fixtures[mode]["historical"])
                    change(value)
                    self.assert_rejected_atomically(value)

    def test_lost_early_member_witness_cannot_be_replaced_by_first_seen_or_later_snapshot(self):
        value = copy.deepcopy(self.fixtures["display"]["historical"])
        peer = next(row for row in value["producerObservations"] if row["representedBy"])
        member = peer["sourceId"]
        # Remove the member's own view but keep the owner's exact atomic member
        # set. Missing original-time independent proof must remain unknown.
        value["producerObservations"] = [row for row in value["producerObservations"] if row["sourceId"] != member]
        value["independentSourceObservations"] = [v for v in value["independentSourceObservations"]
                                                  if v["source"]["id"] != member]
        observations, history = self.record(value)
        predictions, issues = self.predictions(value, observations, history)
        self.assertTrue(issues)
        self.assertFalse(next(row for row in predictions if row["nodeId"] == member)["completed"])
        self.assertIsNone(observations[peer["representedBy"]]["eligible"])

    def test_new_unfinished_work_and_unchanged_withdrawal_do_not_borrow_completion(self):
        for mode in ("display", "geometry"):
            value = self.fixtures[mode]["incomplete"]
            observations, history = self.record(value)
            predictions, issues = self.predictions(value, observations, history, finished=False)
            self.assertTrue(issues)
            self.assertTrue(all(not row["completed"] for row in predictions))
            self.assertTrue(all(not row["idle"] for row in observations.values()))
            withdrawal = self.fixtures[mode]["withdrawal"]
            self.assertTrue(any(row["type"] == "retraction" and row["sourceUnchanged"]
                                for row in withdrawal["events"]))
            self.assertTrue(all(not piece["completed"] for piece in withdrawal["predictions"]))

    def test_genuine_needed_row_and_character_overflow_still_reports_actual_loss(self):
        for mode in ("display", "geometry", "rows"):
            with self.subTest(mode=mode):
                value = self.fixtures[mode]["overflow"]
                self.assertGreater(value["independentSourceObservationDropped"], 0)
                self.assertLessEqual(len(value["independentSourceObservations"]), 2048)
                self.assertLessEqual(sum(len(capture.encode(v)) for v in value["independentSourceObservations"]), 2000000)
                self.assert_rejected_atomically(value)

    def test_every_prepared_development_pressure_event_has_exact_retained_proof(self):
        diagnostic = Path(__file__).resolve().parents[3] / ".parallel/development-evidence/registry-pressure-witness-20261014.json"
        if not diagnostic.exists():
            self.skipTest("Controller-prepared diagnostic is not part of tracked source")
        prepared = json.loads(diagnostic.read_text(encoding="utf-8-sig"))
        self.assertTrue(prepared["diagnosticOnly"])
        self.assertTrue(prepared["completeDiagnostic"])
        self.assertFalse(prepared["formalQualityProven"])
        self.assertEqual(prepared["witnessData"]["dropped"], 0)
        results = [pressure_audit(sample) for sample in prepared["witnessData"]["samples"]]
        self.assertEqual([row["sequence"] for row in results], [137, 138, 139])
        self.assertEqual([row["deltas"] for row in results], [1784, 1784, 1817])
        self.assertEqual([row["evictions"] for row in results], [1064, 1784, 352])
        self.assertEqual([row["exactRetainedVictims"] for row in results], [545, 0, 0])
        self.assertEqual([row["laterOptionalVictims"] for row in results], [519, 1784, 352])
        self.assertTrue(all(row["requiredRows"] == 1844 and row["requiredCharacters"] == 866132 for row in results))


if __name__ == "__main__":
    unittest.main()
