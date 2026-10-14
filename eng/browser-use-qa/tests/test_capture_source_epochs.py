"""Offline source lifetime regressions; these fixtures certify no real quality."""
import copy
import json
import shutil
import subprocess
import unittest

import test_capture as support
import test_capture_transient_observations as transient

capture = support.capture


class SourceEpochTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        node = shutil.which("node")
        if not node:
            raise RuntimeError("Node is required for actual adapter source epoch fixtures")
        result = subprocess.run([node, str(support.HERE / "adapter-check.js"), "--source-epochs-fixture"],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.fixtures = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                      if line.startswith("SOURCE_EPOCHS_FIXTURE=")))

    def import_snapshots(self, fixture):
        sources, observations, pieces = {}, {}, {}
        states = []
        for value in fixture["snapshots"]:
            original = copy.deepcopy(value)
            sources.update({row["id"]: copy.deepcopy(row) for row in value["sourceNodes"]})
            pieces.update({piece["id"]: copy.deepcopy(piece) for piece in value["predictions"]})
            capture.record_producer_observations(observations, value, sources, pieces)
            self.assertEqual(value, original)
            states.append(copy.deepcopy((sources, observations, pieces)))
        return states

    def predictions(self, sources, observations, pieces, *, finished=True):
        visible = {row["id"] for row in sources.values() if row.get("currentlyRendered")}
        current = [piece for piece in pieces.values() if piece["sourceId"] in visible]
        eligible = {member for piece in current for member in piece["sourceIds"]}
        case = {"predictions": current, "predictionHistory": list(pieces.values()),
                "sourceObservations": list(observations.values()),
                "productionEligibility": [{"sourceId": sid, "eligible": sid in eligible} for sid in sources],
                "captureFinished": finished, "settled": finished, "evidenceReady": finished,
                "visitSuccess": True, "domainVerified": True, "errors": [], "pageDecision": True,
                "completed": False, "blockedReason": "screenshot_review_pending"}
        issues = []
        nodes = [{**row, "text": row["sourceText"]} for row in sources.values()]
        return transient.normalize._predictions(case, nodes, issues), issues

    def test_exact_public_diagnostic_has_nested_replacement_and_completed_detached_text(self):
        diagnostic = support.HERE.parents[2] / ".parallel/development-evidence/mit-mutation-diagnostic-summary.json"
        # Controller evidence is optional when the regression runs elsewhere;
        # fixture assertions and every old test still run without local files.
        if not diagnostic.exists():
            self.skipTest("Controller-prepared development diagnostic is not present")
        evidence = json.loads(diagnostic.read_text(encoding="utf-8"))
        self.assertTrue(evidence["diagnosticOnly"])
        self.assertEqual(evidence["dropped"], 0)
        changed = next(event for event in evidence["events"] if event["kind"] == "mutated")
        self.assertFalse(changed["before"][0]["connected"])
        self.assertTrue(changed["before"][0]["pieces"][0]["complete"])
        self.assertTrue(changed["before"][0]["element"].startswith("<a "))
        mutations = [row for row in changed["mutations"] if row["type"] == "childList"]
        self.assertTrue(all(row["target"].startswith("<span ") for row in mutations))
        self.assertEqual([(row["removed"], row["added"]) for row in mutations],
                         [(["浏览"], ["Browse"]), (["Browse"], ["Search news"])])
        value = self.fixtures["nested"]["snapshots"][-1]
        witnesses = [witness for event in value["events"] if event["type"] == "source-change"
                     for witness in event["witnesses"]]
        self.assertIn(("Browse", "Search news"),
                      [(row["beforeSourceText"], row["afterSourceText"]) for row in witnesses])
        self.assertIn(("Search news", "Browse"),
                      [(row["beforeSourceText"], row["afterSourceText"]) for row in witnesses])

    def test_actual_adapter_new_lifetime_keeps_old_completion_and_cannot_borrow_it(self):
        for mode in ("nested", "batch", "short", "character-batch", "varied"):
            with self.subTest(mode=mode):
                fixture = self.fixtures[mode]
                states = self.import_snapshots(fixture)
                original = fixture["sourceId"]
                old = copy.deepcopy(states[0][1][original])
                self.assertTrue(old["pieces"][0]["completed"])
                for _, observations, pieces in states:
                    self.assertEqual(observations[original], old)
                    self.assertEqual(pieces["old-piece"], old["pieces"][0])
                incomplete = next((sources, observations, pieces) for sources, observations, pieces in states
                                  if "new-piece" in pieces and not pieces["new-piece"]["completed"])
                later = incomplete[2]["new-piece"]["sourceId"]
                self.assertNotEqual(later, original)
                self.assertEqual(incomplete[0][later]["sourceText"], incomplete[0][original]["sourceText"])
                self.assertFalse(incomplete[1][later]["pieces"][0]["completed"])
                predictions, issues = self.predictions(*incomplete)
                indexed = {row["nodeId"]: row for row in predictions}
                self.assertTrue(indexed[original]["completed"])
                self.assertFalse(indexed[later]["completed"])
                self.assertIn("node_not_completed:" + later, issues)
                final = fixture["snapshots"][-1]
                self.assertEqual([event["sourceId"] for event in final["events"] if event["type"] == "retraction"], [later])

    def test_unassessed_return_has_unknown_evidence_and_no_manufactured_completion(self):
        for mode in ("nested", "batch", "short", "character-batch"):
            fixture = self.fixtures[mode]
            value = next(row for row in fixture["snapshots"] if row["nodes"]
                         and row["nodes"][0]["phase"] == "new-lifetime-unassessed")
            sources = {row["id"]: row for row in value["sourceNodes"]}
            observations, pieces = {}, {}
            capture.record_producer_observations(observations, value, sources, pieces)
            later = value["nodes"][0]["id"]
            self.assertNotIn(later, observations)
            predictions, issues = self.predictions(sources, observations, pieces, finished=False)
            row = next(row for row in predictions if row["nodeId"] == later)
            self.assertIsNone(row["eligible"])
            self.assertIn("unknown_prediction:" + later, issues)

    def test_same_source_replacement_regroup_and_atomic_move_still_detect_withdrawal(self):
        for mode in ("unchanged-copy", "translated-copy", "whole-copy", "regroup", "atomic-move"):
            with self.subTest(mode=mode):
                fixture = self.fixtures[mode]
                self.import_snapshots(fixture)
                final = fixture["snapshots"][-1]
                self.assertFalse(any(event["type"] == "source-change" for event in final["events"]))
                events = [event for event in final["events"] if event["type"] == "retraction"]
                self.assertEqual(len(events), 1)
                self.assertEqual(events[0]["sourceId"], fixture["sourceId"])
                self.assertIs(events[0]["sourceUnchanged"], True)
                case = {"retractions": events, "expectedPageTranslation": True}
                self.assertIn("translation_retractions", capture.completion_gate(case, support.HERE)[2])

    def test_inline_date_retires_prior_group_but_preserves_exact_original_facts(self):
        fixture = self.fixtures["date"]
        sources, observations, pieces = self.import_snapshots(fixture)[-1]
        original = fixture["sourceId"]
        self.assertTrue(observations[original]["pieces"][0]["completed"])
        self.assertEqual(sources[original]["displayedText"], "浏览")
        self.assertEqual({row["sourceText"] for row in sources.values()}, {"Browse", "2026-10-13", "2026-10-14"})
        self.assertFalse(any(event["type"] == "retraction" for event in fixture["snapshots"][-1]["events"]))

    def test_short_unrendered_intermediate_is_a_mutation_witness_not_a_fabricated_source_row(self):
        fixture = self.fixtures["short"]
        sources, _, _ = self.import_snapshots(fixture)[-1]
        value = fixture["snapshots"][-1]
        self.assertFalse(any(row["sourceText"] == "X" for row in sources.values()))
        self.assertTrue(any(witness["afterSourceText"] == "X" for event in value["events"]
                            if event["type"] == "source-change" for witness in event["witnesses"]))
        self.assertTrue(all(event["at"] and event["schemaVersion"] == 1 for event in value["events"]
                            if event["type"] == "source-change"))

    def test_disappearance_alone_does_not_claim_source_change_or_rewrite_old_candidates(self):
        for mode in ("remove", "blank-fact", "partial-remove"):
            with self.subTest(mode=mode):
                fixture = self.fixtures[mode]
                states = self.import_snapshots(fixture)
                original = fixture["sourceId"]
                self.assertTrue(states[-1][2]["old-piece"]["completed"])
                if mode == "remove":
                    self.assertEqual(states[-1][1][original], states[0][1][original])
                else:
                    self.assertEqual(states[-1][1][original]["pieces"], states[0][1][original]["pieces"])
                self.assertFalse(any(event["type"] in ("source-change", "retraction")
                                     for event in fixture["snapshots"][-1]["events"]))


class ProductWriteEpochTests(unittest.TestCase):
    # Reuse the importer/evaluator helpers without rerunning inherited cases.
    import_snapshots = SourceEpochTests.import_snapshots
    predictions = SourceEpochTests.predictions
    @classmethod
    def setUpClass(cls):
        node = shutil.which("node")
        if not node:
            raise RuntimeError("Node is required for actual adapter product write fixtures")
        result = subprocess.run([node, str(support.HERE / "adapter-check.js"), "--source-write-fixture"],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.write_fixtures = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                            if line.startswith("SOURCE_WRITE_FIXTURE=")))

    def test_product_stream_and_correction_keep_source_and_completion_with_truthful_withdrawal(self):
        for mode in ("untracked-growth", "correction", "identity", "literal", "mixed"):
            with self.subTest(mode=mode):
                fixture = self.write_fixtures[mode]
                states = self.import_snapshots(fixture)
                original = fixture["sourceId"]
                publication = fixture["snapshots"][1]
                piece = next(row for row in publication["predictions"] if row["id"] == "stream-piece")
                self.assertEqual(piece["sourceId"], original)
                self.assertTrue(piece["completed"])
                self.assertFalse(any(event["type"] == "source-change" and event["sourceId"] == original
                                     for event in publication["events"]))
                predictions, issues = self.predictions(*states[1])
                self.assertTrue(next(row for row in predictions if row["nodeId"] == original)["completed"])
                self.assertNotIn("node_not_completed:" + original, issues)
                withdrawals = [event for event in fixture["snapshots"][-1]["events"]
                               if event["type"] == "retraction"]
                self.assertEqual(len(withdrawals), 0 if mode in ("identity", "literal") else 1)
                if withdrawals:
                    self.assertEqual(withdrawals[0]["sourceId"], original)
                    self.assertIs(withdrawals[0]["sourceUnchanged"], True)
                    case = {"retractions": withdrawals, "expectedPageTranslation": True}
                    self.assertIn("translation_retractions", capture.completion_gate(case, support.HERE)[2])

    def test_authored_edit_in_same_batch_or_same_node_keeps_exact_witness(self):
        for mode in ("mixed", "same-node-edit"):
            fixture = self.write_fixtures[mode]
            self.import_snapshots(fixture)
            events = [event for event in fixture["snapshots"][1]["events"] if event["type"] == "source-change"]
            self.assertEqual(len(events), 1)
            self.assertEqual(events[0]["sourceId"], fixture["authoredId"] if mode == "mixed" else fixture["sourceId"])
            self.assertTrue(any(witness["afterSourceText"] == "Genuinely changed authored source"
                                for witness in events[0]["witnesses"]))
            self.assertFalse(any("最初译文" in witness["beforeSourceText"] or "最终修正" in witness["beforeSourceText"]
                                 for witness in events[0]["witnesses"]))

    def test_exhausted_writer_provenance_remains_unknown_and_original_overflow_gate_rejects(self):
        fixture = self.write_fixtures["proof-overflow"]
        value = fixture["snapshots"][1]
        self.assertGreater(value["producerObservationDropped"], 0)
        self.assertFalse(any(event["type"] == "source-change" for event in value["events"]))
        sources = {row["id"]: row for row in value["sourceNodes"]}
        with self.assertRaisesRegex(capture.CaptureError, "producer_observation_overflow"):
            capture.record_producer_observations({}, value, sources, {})

    def test_unproved_short_authored_prefix_is_not_assumed_to_be_identity_output(self):
        fixture = self.write_fixtures["authored-prefix"]
        first, final = fixture["snapshots"]
        events = [event for event in final["events"] if event["type"] == "source-change"]
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]["sourceId"], fixture["sourceId"])
        self.assertEqual([(row["beforeSourceText"], row["afterSourceText"]) for row in events[0]["witnesses"]],
                         [("FOIA", "F"), ("F", "FOIA")])
        self.assertFalse(any(event["type"] == "source-change" for event in first["events"]))
        self.assertNotEqual(next(row for row in final["nodes"] if row["sourceText"] == "FOIA")["id"],
                            fixture["sourceId"])


class SharedImportValidationTests(unittest.TestCase):
    """Independent small atomic views exercise sharing without a timing mock."""
    def fixture(self):
        helper = transient.TransientObservationTests()
        sources = {sid: helper.source(sid) for sid in ("s1", "peer")}
        piece = helper.piece(sourceIds=["s1", "peer"])
        owner = helper.row([piece], source=sources["s1"])
        peer = helper.row([piece], sourceId="peer", source=sources["peer"],
                          sourceText=sources["peer"]["sourceText"], rendered=False,
                          representedBy="s1", representedSource=sources["s1"], work=owner["work"])
        return helper.value([owner, peer]), sources

    def record(self, value, sources):
        observations, history = {}, {}
        capture.record_producer_observations(observations, value, sources, history)
        return observations, history

    def assert_atomic_rejection(self, value, sources):
        observations, history = {"kept": {"at": "original"}}, {"kept": {"original": True}}
        before = copy.deepcopy((observations, history, value, sources))
        with self.assertRaises(capture.CaptureError):
            capture.record_producer_observations(observations, value, sources, history)
        self.assertEqual((observations, history, value, sources), before)

    def test_shared_pieces_still_check_each_row_membership_and_duplicate_occurrence(self):
        value, sources = self.fixture()
        extra = transient.TransientObservationTests().source("unrelated")
        sources["unrelated"] = extra
        value["producerObservations"][1].update(sourceId="unrelated", source=extra)
        self.assert_atomic_rejection(value, sources)
        value, sources = self.fixture()
        value["producerObservations"][1]["pieces"] *= 2
        self.assert_atomic_rejection(value, sources)

    def test_equal_but_distinct_malformed_piece_work_and_geometry_are_not_cached(self):
        for kind in ("piece", "work", "source", "owner"):
            with self.subTest(kind=kind):
                value, sources = self.fixture()
                peer = value["producerObservations"][1]
                if kind == "piece":
                    peer["pieces"] = copy.deepcopy(peer["pieces"])
                    peer["pieces"][0]["completed"] = 1  # equals True, wrong type
                elif kind == "work":
                    peer["work"] = copy.deepcopy(peer["work"])
                    peer["work"]["active"] = 1
                else:
                    field = "source" if kind == "source" else "representedSource"
                    peer[field] = copy.deepcopy(peer[field])
                    peer[field]["ancestorsVisible"] = 1
                self.assert_atomic_rejection(value, sources)

    def test_each_occurrence_keeps_member_time_and_conflicting_piece_proof(self):
        value, sources = self.fixture()
        sources["peer"]["firstSeen"] = "2026-10-12T22:17:51.107Z"
        value["producerObservations"][1]["at"] = sources["peer"]["firstSeen"]
        observations, history = self.record(value, sources)
        self.assertIsNone(observations["s1"]["eligible"])
        self.assertEqual(observations["s1"]["pieces"], [])
        self.assertTrue(observations["peer"]["pieces"][0]["completed"])
        self.assertEqual(set(history), {"p1"})
        value, sources = self.fixture()
        value["producerObservations"][1]["pieces"] = copy.deepcopy(value["producerObservations"][1]["pieces"])
        value["producerObservations"][1]["pieces"][0]["text"] = "Different actual output"
        self.assert_atomic_rejection(value, sources)

    def test_old_rows_are_validated_and_next_import_does_not_reuse_prior_object_checks(self):
        value, sources = self.fixture()
        observations, history = self.record(value, sources)
        saved = copy.deepcopy((observations, history))
        # Same objects, older timestamp: malformed data must fail before the
        # nonadvancing observation is ignored, without partially changing state.
        value["producerObservations"][0]["work"]["jobs"] = -1
        with self.assertRaisesRegex(capture.CaptureError, "work"):
            capture.record_producer_observations(observations, value, sources, history)
        self.assertEqual((observations, history), saved)
        value["producerObservations"][0]["work"]["jobs"] = 0
        value["producerObservations"][0]["pieces"][0]["completed"] = 1
        with self.assertRaisesRegex(capture.CaptureError, "piece"):
            capture.record_producer_observations(observations, value, sources, history)
        self.assertEqual((observations, history), saved)

    def test_public_v1_bytes_match_legacy_and_transport_and_all_outputs_are_detached(self):
        legacy, sources = self.fixture()
        compact = copy.deepcopy(legacy)
        owner = legacy["producerObservations"][0]
        reference = {"at": owner["at"], "sequence": owner["sequence"]}
        compact["independentSourceObservations"] = [
            {**reference, "source": copy.deepcopy(source)} for source in sources.values()]
        compact["independentSourceObservationDropped"] = 0
        compact["producerObservationTransport"] = {"schemaVersion": 1, "values": {
            "1": copy.deepcopy(owner["work"]), "2": copy.deepcopy(owner["pieces"][0])}}
        for row in compact["producerObservations"]:
            for field in ("source", "sourceText", "representedSource"):
                row.pop(field)
            row.update(work=1, pieces=[2], sourceObservation=copy.deepcopy(reference),
                       representedSourceObservation=copy.deepcopy(reference) if row["representedBy"] else None)
        original = copy.deepcopy((legacy, compact, sources))
        legacy_result = self.record(legacy, sources)
        compact_result = self.record(compact, sources)
        self.assertEqual(capture.encode(legacy_result), capture.encode(compact_result))
        self.assertEqual((legacy, compact, sources), original)
        observations, history = compact_result
        self.assertTrue(all(row["eligible"] and row["idle"] for row in observations.values()))
        original_piece = copy.deepcopy(history["p1"])
        observations["s1"]["pieces"][0]["sourceIds"].append("modified-observation")
        self.assertEqual(history["p1"], original_piece)
        self.assertEqual(observations["peer"]["pieces"][0], original_piece)
        history["p1"]["completed"] = False
        self.assertTrue(observations["peer"]["pieces"][0]["completed"])
        compact["producerObservationTransport"]["values"]["2"]["text"] = "mutated-input"
        self.assertEqual(observations["peer"]["pieces"][0]["text"], original_piece["text"])
        self.assertEqual(legacy_result[1]["p1"], original_piece)


if __name__ == "__main__":
    unittest.main()
