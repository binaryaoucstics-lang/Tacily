"""Offline transient producer evidence; fixtures never certify a real website."""
import base64
import copy
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import unittest

import test_capture as support
import test_review_index as review_support

capture = support.capture
spec = importlib.util.spec_from_file_location(
    "transient_normalize", support.HERE.parent.parent / "website-qa" / "normalize.py")
normalize = importlib.util.module_from_spec(spec)
spec.loader.exec_module(normalize)
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")


class TransientObservationTests(support.Fixture):
    @classmethod
    def setUpClass(cls):
        node = shutil.which("node")
        if not node:
            raise RuntimeError("Node is required for actual adapter movement fixtures")
        result = subprocess.run([node, str(support.HERE / "adapter-check.js"), "--movement-fixture"],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.movement = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                      if line.startswith("MOVEMENT_FIXTURE=")))

    def record_adapter(self, value, observations=None, history=None):
        observations = {} if observations is None else observations
        history = {} if history is None else history
        sources = {row["id"]: copy.deepcopy(row) for row in value["sourceNodes"]}
        saved = copy.deepcopy(sources)
        capture.record_producer_observations(observations, value, sources, history)
        self.assertEqual(sources, saved)
        return observations, history

    def test_actual_adapter_reparented_rejection_keeps_original_time_and_latest_source(self):
        value = copy.deepcopy(self.movement["rejected"])
        row = value["producerObservations"][0]
        current = next(source for source in value["sourceNodes"] if source["id"] == row["sourceId"])
        self.assertEqual(row["source"]["locator"], "0/1/0")
        self.assertEqual(current["locator"], "0/2/0")
        self.assertNotEqual(current["rects"], row["source"]["rects"])
        self.assertIsNone(value["pageDecision"])
        observations, history = self.record_adapter(value)
        self.assertEqual(observations[row["sourceId"]]["at"], row["at"])
        self.assertEqual(observations[row["sourceId"]]["sequence"], row["sequence"])
        self.assertIs(observations[row["sourceId"]]["eligible"], False)
        self.assertEqual(history, {})
        predictions, issues = self.predictions(observations, history, source_ids=(row["sourceId"],),
                                              source_nodes=value["sourceNodes"])
        self.assertEqual(issues, [])
        self.assertIs(predictions[0]["eligible"], False)

    def test_actual_atomic_owner_movement_preserves_member_evidence_and_flags(self):
        value = copy.deepcopy(self.movement["atomic"])
        observations, history = self.record_adapter(value)
        peer = next(row for row in value["producerObservations"] if row["representedBy"])
        owner = next(source for source in value["sourceNodes"] if source["id"] == peer["representedBy"])
        self.assertEqual(peer["representedSource"]["locator"], "0/1/0")
        self.assertEqual(owner["locator"], "0/2/0")
        self.assertEqual(peer["representedSourceObservation"]["at"], peer["at"])
        self.assertEqual(observations[peer["sourceId"]]["representedBy"], owner["id"])
        predictions, issues = self.predictions(observations, history, source_ids=tuple(observations),
                                              source_nodes=value["sourceNodes"])
        self.assertEqual(issues, [])
        self.assertTrue(all(row["completed"] for row in predictions))
        self.assertEqual(list(history), ["moving-piece"])

    def test_unobserved_or_forged_historic_location_is_rejected_atomically(self):
        malformed = []
        original = self.movement["rejected"]
        row = original["producerObservations"][0]
        for field, change in (("locator", "0/999/0"), ("locator", "0/../0"),
                              ("firstSeen", "2026-10-12T22:17:50Z"), ("sourceText", "Forged factual source")):
            value = copy.deepcopy(original)
            value["producerObservations"][0]["source"][field] = change; malformed.append(value)
        value = copy.deepcopy(original)
        value["producerObservations"][0]["source"]["rects"][0]["x"] = 900; malformed.append(value)
        value = copy.deepcopy(original)
        value["independentSourceObservations"] = [version for version in value["independentSourceObservations"]
            if version["sequence"] != row["sequence"]]; malformed.append(value)
        value = copy.deepcopy(original)
        del value["independentSourceObservations"]; malformed.append(value)
        value = copy.deepcopy(original)
        value["producerObservations"][0]["sourceObservation"]["sequence"] += 100; malformed.append(value)
        for field, change in (("locator", "0/666/0"), ("firstSeen", "2026-10-12T22:17:50Z"),
                              ("rendered", False)):
            value = copy.deepcopy(self.movement["atomic"])
            peer = next(row for row in value["producerObservations"] if row["representedBy"])
            peer["representedSource"][field] = change; malformed.append(value)
        value = copy.deepcopy(self.movement["atomic"])
        peer = next(row for row in value["producerObservations"] if row["representedBy"])
        peer["representedSource"]["rects"][0]["height"] = 0; malformed.append(value)
        for value in malformed:
            observations, history = {"old": {"at": "unchanged"}}, {"original": {"kept": True}}
            saved = copy.deepcopy((observations, history))
            with self.subTest(value=value), self.assertRaises(capture.CaptureError):
                self.record_adapter(value, observations, history)
            self.assertEqual((observations, history), saved)

    def test_version_registry_requires_latest_row_identity_bounds_and_original_timestamps(self):
        malformed = []
        for key, change in (("independentSourceObservationDropped", 1),
                            ("independentSourceObservationDropped", True), ("independentSourceObservations", {})):
            value = copy.deepcopy(self.movement["rejected"]); value[key] = change; malformed.append(value)
        for key, change in (("at", "unobserved"), ("sequence", True), ("sequence", -1)):
            value = copy.deepcopy(self.movement["rejected"])
            value["independentSourceObservations"][0][key] = change; malformed.append(value)
        for key, change in (("locator", "0/999/0"), ("id", "unseen"), ("sourceText", "Changed facts"),
                            ("firstSeen", "2026-10-12T22:17:50Z")):
            value = copy.deepcopy(self.movement["rejected"])
            value["independentSourceObservations"][0]["source"][key] = change; malformed.append(value)
        value = copy.deepcopy(self.movement["rejected"])
        value["independentSourceObservations"].append(copy.deepcopy(value["independentSourceObservations"][0]))
        malformed.append(value)
        for value in malformed:
            with self.subTest(value=value), self.assertRaises(capture.CaptureError):
                self.record_adapter(value)

    def test_moved_partial_source_cannot_borrow_later_hidden_completion(self):
        value = copy.deepcopy(self.movement["atomic"])
        for row in value["producerObservations"]:
            row["pieces"][0]["completed"] = False
        observations, history = self.record_adapter(value)
        history["moving-piece"] = {**history["moving-piece"], "completed": True}
        self.record_adapter(value, observations, history)
        self.assertTrue(history["moving-piece"]["completed"])
        for row in observations.values():
            self.assertIsNone(row["eligible"])
            self.assertEqual(row["pieces"], [])
        predictions, issues = self.predictions(observations, history, source_ids=tuple(observations),
                                              source_nodes=value["sourceNodes"])
        self.assertTrue(all(row["eligible"] is None for row in predictions))
        self.assertTrue(all("unknown_retired_source:" + source_id in issues for source_id in observations))

    def source(self, identity="s1", text="Transient synthetic source."):
        return {"id": identity, "sourceText": text, "locator": "0/1/0", "tag": "P", "role": "",
                "firstSeen": "2026-10-12T22:17:50.107Z", "phase": "mutation",
                "rects": [{"x": 0, "y": 10, "width": 200, "height": 24,
                           "documentX": 0, "documentY": 10}],
                "rendered": True, "ancestorsVisible": True, "inViewport": True,
                "displayedText": text, "currentlyRendered": False}

    def piece(self, identity="p1", source_id="s1", **changes):
        return {"id": identity, "sourceId": source_id, "sourceIds": [source_id],
                "sourceText": self.source(source_id)["sourceText"], "text": "Actual producer piece",
                "sourceEvidence": "matched", "auditIssue": None, "eligible": True,
                "completed": True, "failed": False, "local": False, "fromCache": False, **changes}

    def row(self, pieces=None, **changes):
        pieces = pieces or []
        return {"sourceId": "s1", "sourceText": self.source()["sourceText"], "source": self.source(),
                "at": "2026-10-12T22:17:50.107Z", "phase": "mutation", "sequence": 2,
                "rendered": True, "representedBy": None, "representedSource": None,
                "pageDecision": True, "eligible": bool(pieces), "pieces": pieces,
                "work": {"jobs": 0, "pending": 0, "scanning": False, "rpcPending": 0,
                         "scheduledScans": 0, "active": True, "paused": False,
                         "originalView": False, "automatic": True, "documentVisible": True}, **changes}

    def value(self, rows):
        # Visibility at mutation time is gone by the next controller sample.
        # The later currently-hidden false eligibility is deliberately irrelevant.
        return {"at": "2026-10-12T22:17:52.939Z", "producerObservations": rows,
                "producerObservationDropped": 0, "pageDecision": True, "nodes": [],
                "sourceNodes": [self.source()], "predictions": [],
                "productionEligibility": [{"sourceId": "s1", "eligible": False, "currentlyRendered": False}]}

    def record(self, rows, observations=None, history=None, sources=None):
        observations = {} if observations is None else observations
        history = {} if history is None else history
        capture.record_producer_observations(observations, self.value(rows),
                                            sources or {"s1": self.source()}, history)
        return observations, history

    def predictions(self, observations, history, *, finished=True, source_ids=("s1",), source_nodes=None):
        case = {"predictions": [], "predictionHistory": list(history.values()),
                "sourceObservations": list(observations.values()),
                "productionEligibility": [{"sourceId": sid, "eligible": False} for sid in source_ids],
                "captureFinished": finished, "settled": finished, "evidenceReady": finished,
                "visitSuccess": True, "domainVerified": True, "errors": [], "pageDecision": True,
                "completed": False, "blockedReason": "screenshot_review_pending"}
        nodes = [{"id": sid, "text": self.source(sid)["sourceText"],
                  "rendered": True, "currentlyRendered": False} for sid in source_ids]
        if source_nodes is not None:
            nodes = [{**row, "text": row["sourceText"]} for row in source_nodes]
        issues = []
        return normalize._predictions(case, nodes, issues), issues

    def test_mutation_visible_then_hidden_rejection_retains_actual_timestamp(self):
        observations, history = self.record([self.row()])
        self.assertEqual(observations["s1"]["at"], "2026-10-12T22:17:50.107Z")
        self.assertEqual(observations["s1"]["phase"], "mutation")
        self.assertTrue(observations["s1"]["idle"])
        predictions, issues = self.predictions(observations, history)
        self.assertIs(predictions[0]["eligible"], False)
        self.assertEqual(issues, [])

    def test_transient_completed_failed_and_partial_flags_are_preserved(self):
        for complete, failed in ((True, False), (False, True), (False, False)):
            with self.subTest(complete=complete, failed=failed):
                piece = self.piece(completed=complete, failed=failed)
                observations, history = self.record([self.row([piece])])
                predictions, issues = self.predictions(observations, history)
                self.assertTrue(predictions[0]["eligible"])
                self.assertEqual(predictions[0]["completed"], complete)
                self.assertEqual(predictions[0]["failed"], failed)
                self.assertEqual(history["p1"], piece)
                self.assertEqual(bool(issues), not complete)

    def test_eligible_work_in_progress_does_not_borrow_completion(self):
        row = self.row([self.piece(completed=False)])
        row["work"]["jobs"] = 1
        observations, history = self.record([row])
        self.assertFalse(observations["s1"]["idle"])
        predictions, issues = self.predictions(observations, history)
        self.assertFalse(predictions[0]["completed"])
        self.assertIn("node_not_completed:s1", issues)

    def test_hidden_before_idle_stays_unknown_for_every_work_or_control_flag(self):
        for key, value in (("jobs", 1), ("pending", 1), ("rpcPending", 1), ("scanning", True),
                           ("scheduledScans", 1), ("active", False), ("paused", True),
                           ("originalView", True), ("automatic", False), ("documentVisible", False)):
            row = self.row(eligible=None)
            row["work"][key] = value
            with self.subTest(key=key):
                observations, history = self.record([row])
                # The next hidden idle sample must not rewrite this snapshot.
                self.record([], observations, history)
                predictions, issues = self.predictions(observations, history)
                self.assertIsNone(predictions[0]["eligible"])
                self.assertIn("unknown_retired_source:s1", issues)

    def test_missing_observation_and_partial_capture_do_not_establish_rejection(self):
        predictions, issues = self.predictions({}, {})
        self.assertIsNone(predictions[0]["eligible"])
        self.assertIn("unknown_prediction:s1", issues)
        observations, history = self.record([self.row()])
        predictions, issues = self.predictions(observations, history, finished=False)
        self.assertIsNone(predictions[0]["eligible"])
        self.assertIn("unknown_prediction:s1", issues)

    def test_later_hidden_false_does_not_replace_completed_history(self):
        observations, history = self.record([self.row([self.piece()])])
        saved = copy.deepcopy((observations, history))
        self.record([], observations, history)
        self.assertEqual((observations, history), saved)
        self.assertTrue(self.predictions(observations, history)[0][0]["completed"])

    def test_stale_snapshot_cannot_overwrite_newer_observation_or_piece_version(self):
        newer = self.row([self.piece(completed=False)], at="2026-10-12T22:17:51.100Z", sequence=3)
        observations, history = self.record([newer])
        saved = copy.deepcopy((observations, history))
        self.record([self.row([self.piece()])], observations, history)
        self.assertEqual((observations, history), saved)

    def test_new_hidden_completion_cannot_upgrade_last_visible_partial_snapshot(self):
        observations, history = self.record([self.row([self.piece(completed=False)])])
        # Current raw history advances, but no independent visible observation exists.
        history["p1"] = self.piece()
        self.record([self.row([self.piece(completed=False)])], observations, history)
        self.assertTrue(history["p1"]["completed"])
        self.assertIsNone(observations["s1"]["eligible"])
        self.assertEqual(observations["s1"]["pieces"], [])
        self.assertIn("unknown_retired_source:s1", self.predictions(observations, history)[1])

    def test_retired_distinct_piece_set_does_not_collect_old_or_unrelated_success(self):
        history = {"old": self.piece("old"), "unrelated": self.piece("unrelated", "other")}
        observations, history = self.record([self.row([self.piece("last", completed=False)])], history=history)
        predictions, issues = self.predictions(observations, history)
        self.assertEqual(predictions[0]["pieceIds"], ["last"])
        self.assertFalse(predictions[0]["completed"])
        self.assertEqual(set(history), {"old", "unrelated", "last"})

    def test_atomic_peer_requires_explicit_membership_and_independent_owner_and_peer(self):
        piece = self.piece(sourceIds=["s1", "peer"])
        peer = self.row([piece], sourceId="peer", sourceText=self.source("peer")["sourceText"],
                        source=self.source("peer"), rendered=False, representedBy="s1",
                        representedSource=self.source())
        sources = {"s1": self.source(), "peer": self.source("peer")}
        observations, history = self.record([self.row([piece]), peer], sources=sources)
        predictions, issues = self.predictions(observations, history, source_ids=("s1", "peer"))
        self.assertEqual(issues, [])
        self.assertTrue(all(row["completed"] for row in predictions))
        self.assertEqual(observations["peer"]["representedBy"], "s1")
        self.assertFalse(observations["peer"]["rendered"])
        self.assertEqual(list(history), ["p1"])
        bad = copy.deepcopy(peer); bad["representedSource"]["rendered"] = False
        with self.assertRaisesRegex(capture.CaptureError, "representation"):
            self.record([bad], sources=sources)

    def test_never_observed_atomic_member_cannot_create_independent_source_evidence(self):
        piece = self.piece(sourceIds=["s1", "never-seen"], sourceEvidence="unmatched",
                           auditIssue="source_not_independently_visible")
        observations, history = self.record([self.row([piece])])
        self.assertIsNone(observations["s1"]["eligible"])
        self.assertEqual(history, {})
        self.assertIn("unknown_retired_source:s1", self.predictions(observations, history)[1])

    def test_unrendered_without_atomic_representation_is_unknown(self):
        row = self.row(rendered=False)
        observations, history = self.record([row])
        self.assertIsNone(observations["s1"]["eligible"])
        self.assertEqual(self.predictions(observations, history)[0][0]["eligible"], None)

    def test_malformed_source_timestamps_flags_membership_and_geometry_are_atomic_failures(self):
        original = self.row([self.piece()])
        malformed = []
        for key, value in (("at", "unknown"), ("at", "2026-10-12T22:18:00Z"),
                           ("at", "2026-10-12T22:17:49Z"), ("at", "2026-10-12T22:17:50"),
                           ("sourceId", "missing"), ("sourceText", "Changed factual version"),
                           ("pageDecision", None), ("rendered", 1), ("eligible", "false"),
                           ("sequence", True), ("pieces", {})):
            row = copy.deepcopy(original); row[key] = value; malformed.append(row)
        for key, value in (("jobs", True), ("pending", -1), ("scanning", None), ("scheduledScans", None)):
            row = copy.deepcopy(original); row["work"][key] = value; malformed.append(row)
        for key, value in (("completed", 1), ("failed", None), ("eligible", False),
                           ("sourceIds", ["other"]), ("sourceIds", ["s1", "s1"]),
                           ("sourceText", "Changed source"), ("id", "")):
            row = copy.deepcopy(original); row["pieces"][0][key] = value; malformed.append(row)
        for key, value in (("locator", "different"), ("firstSeen", "2026-10-12T22:17:49Z"),
                           ("phase", "hidden"), ("rendered", False)):
            row = copy.deepcopy(original); row["source"][key] = value; malformed.append(row)
        row = copy.deepcopy(original); row["source"]["rects"][0]["width"] = float("nan"); malformed.append(row)
        row = copy.deepcopy(original); row["source"]["rects"][0]["height"] = 0; malformed.append(row)
        row = copy.deepcopy(original); row["pieces"].append(copy.deepcopy(row["pieces"][0])); malformed.append(row)
        valid = self.row(sourceId="valid", source=self.source("valid"))
        sources = {"s1": self.source(), "valid": self.source("valid")}
        for row in malformed:
            observations, history = {"old": {"at": "unchanged"}}, {"old-piece": {"original": True}}
            saved = copy.deepcopy((observations, history))
            with self.subTest(row=row), self.assertRaises(capture.CaptureError):
                self.record([valid, row], observations, history, sources)
            self.assertEqual((observations, history), saved)

    def test_duplicate_sources_overflow_and_false_idle_claims_are_rejected(self):
        for value in (self.value([self.row(), self.row()]),
                      dict(self.value([]), producerObservationDropped=1),
                      dict(self.value([]), producerObservationDropped=True),
                      dict(self.value([]), producerObservations={})):
            with self.subTest(value=value), self.assertRaises(capture.CaptureError):
                capture.record_producer_observations({}, value, {"s1": self.source()}, {})
        row = self.row(); row["work"]["jobs"] = 1
        with self.assertRaisesRegex(capture.CaptureError, "eligibility"):
            self.record([row])

    def test_new_source_version_does_not_replace_old_independent_identity(self):
        old, history = self.record([self.row()])
        new_source = self.source("new", "Changed authored facts.")
        new_row = self.row(sourceId="new", sourceText=new_source["sourceText"], source=new_source)
        sources = {"s1": self.source(), "new": new_source}
        saved = copy.deepcopy(sources)
        self.record([new_row], old, history, sources)
        self.assertEqual(set(old), {"s1", "new"})
        self.assertEqual(sources, saved)
        bad = self.row(sourceText="Changed authored facts.")
        with self.assertRaisesRegex(capture.CaptureError, "source"):
            self.record([bad], old, history, sources)

    def test_imported_observations_and_history_do_not_alias_raw_journal_rows(self):
        row = self.row([self.piece()])
        observations, history = self.record([row])
        row["pieces"][0]["failed"] = True
        self.assertFalse(history["p1"]["failed"])
        self.assertFalse(observations["s1"]["pieces"][0]["failed"])

    def test_review_index_and_normalize_roundtrip_keep_raw_evidence_and_unknown_labels(self):
        observations, history = self.record([self.row()])
        case = {**support.SITE, "schemaVersion": 1, "expectedPageTranslation": False,
                "sourceHead": "a" * 40, "sourceHashes": {"page-adapter.js": "b" * 64},
                "manifestHash": "c" * 64, "capturedAt": self.value([])["at"], "title": "Offline transient",
                "viewport": {"width": 800, "height": 600}, "finalUrl": support.SITE["url"],
                "sourceNodes": [self.source()], "predictions": [], "predictionHistory": list(history.values()),
                "sourceObservations": list(observations.values()), "pageDecision": False,
                "productionEligibility": self.value([])["productionEligibility"],
                "captureFinished": True, "evidenceReady": True, "settled": True, "completed": False,
                "visitSuccess": True, "domainVerified": True, "errors": [], "retractions": [],
                "blockedReason": "screenshot_review_pending", "simulation": True, "liveTranslation": False,
                "screenshots": [], "requestIds": [], "terminals": []}
        for phase in sorted(normalize.REQUIRED_PHASES):
            image = self.root / (phase + ".png"); image.write_bytes(PNG)
            case["screenshots"].append({"phase": phase, "path": image.name,
                                        "sha256": capture.hashlib.sha256(PNG).hexdigest()})
        case_path = self.root / "case.json"; capture.atomic_json(case_path, case)
        original = case_path.read_bytes()
        bundle = self.root / "review"
        review_support.review_index.build_review_index(case_path, bundle)
        review = json.loads((bundle / "review-template.json").read_text(encoding="utf-8"))
        self.assertEqual(review["annotations"][0]["needTranslation"], "unknown")
        self.assertEqual(review["pageAnnotation"]["needTranslation"], "unknown")
        source_export = (bundle / "sources.json").read_text(encoding="utf-8")
        self.assertNotIn("producerObservations", source_export)
        self.assertNotIn('"eligible"', source_export)
        # Explicit offline review votes; neither label is inferred from flags.
        review["pageAnnotation"] = {"needTranslation": False, "reviewed": True, "independent": True}
        review["annotations"] = [{"sourceId": "s1", "needTranslation": False,
                                  "reviewed": True, "independent": True}]
        review["screenshots"] = dict.fromkeys(normalize.REQUIRED_PHASES, True)
        review_path = self.root / "votes.json"; capture.atomic_json(review_path, review)
        result = normalize.normalize_case(case_path, review_path, self.root / "normalized.json", self.root)
        self.assertIs(result["predictions"][0]["eligible"], False)
        self.assertNotIn("unknown_prediction:s1", result["normalizationIssues"])
        self.assertFalse(result["completed"])
        self.assertIn("non_live_or_simulated_capture", result["normalizationIssues"])
        self.assertEqual(result["provenance"]["rawCapture"], case)
        self.assertEqual(case_path.read_bytes(), original)

    def test_case_capture_journals_intervening_view_and_imports_its_piece_history(self):
        helper = support.WorkflowTests()
        helper.setUp(); self.addCleanup(helper.tearDown)
        browser = helper.browser(skipped=True, probe_states=[{
            "id": "probe-1", "stage": "completed", "result": {"samples": [], "statistics": {}}}])
        original_qa = browser.qa
        transient = self.source("transient")
        piece = self.piece(source_id="transient", completed=False)
        row = self.row([piece], sourceId="transient", sourceText=transient["sourceText"], source=transient)
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "sample":
                value["at"] = self.value([])["at"]
                value["producerObservations"] = [copy.deepcopy(row)] if browser.configured else []
                value["producerObservationDropped"] = 0
                if browser.configured:
                    value["sourceNodes"].append(copy.deepcopy(transient))
                    value["productionEligibility"] = [{"sourceId": "transient", "eligible": False,
                                                       "currentlyRendered": False}]
            return value
        browser.qa = qa
        def events(request, requests):
            return [{"type": "assessment", "id": request["id"], "caseId": request["caseId"],
                     "needsTranslation": False, "allowImages": False}]
        result, _, _, journal = helper.scripted_case(events, browser=browser,
            run_id="transient-journal", site={**support.SITE, "expectedPageTranslation": False})
        self.assertEqual(result["errors"], [])
        self.assertEqual(result["sourceObservations"][0]["sourceId"], "transient")
        self.assertEqual(result["sourceObservations"][0]["at"], row["at"])
        self.assertEqual(result["predictionHistory"], [piece])
        self.assertFalse(result["sourceObservations"][0]["pieces"][0]["completed"])
        self.assertTrue(any(event["kind"] == "sample" and
                            event["value"].get("producerObservations") == [row] for event in journal))
        self.assertFalse(result["completed"])
        self.assertTrue(result["simulation"])

    def test_case_capture_imports_and_journals_actual_adapter_source_movement(self):
        helper = support.WorkflowTests()
        helper.setUp(); self.addCleanup(helper.tearDown)
        browser = helper.browser(skipped=True, probe_states=[{
            "id": "probe-1", "stage": "completed", "result": {"samples": [], "statistics": {}}}])
        original_qa = browser.qa
        fixture = self.movement["rejected"]
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "sample" and browser.configured:
                value["at"] = fixture["at"]
                value["sourceNodes"].extend(copy.deepcopy(fixture["sourceNodes"]))
                for key in ("producerObservations", "producerObservationDropped",
                            "independentSourceObservations", "independentSourceObservationDropped"):
                    value[key] = copy.deepcopy(fixture[key])
            return value
        browser.qa = qa
        def events(request, requests):
            return [{"type": "assessment", "id": request["id"], "caseId": request["caseId"],
                     "needsTranslation": False, "allowImages": False}]
        result, _, _, journal = helper.scripted_case(events, browser=browser,
            run_id="adapter-movement-journal", site={**support.SITE, "expectedPageTranslation": False})
        self.assertEqual(result["errors"], [])
        row = fixture["producerObservations"][0]
        current = next(source for source in result["sourceNodes"] if source["id"] == row["sourceId"])
        retained = next(source for source in result["sourceObservations"] if source["sourceId"] == row["sourceId"])
        self.assertEqual(current["locator"], "0/2/0")
        self.assertEqual(retained["at"], row["at"])
        self.assertIs(retained["eligible"], False)
        self.assertTrue(any(event["kind"] == "sample" and
            event["value"].get("independentSourceObservations") == fixture["independentSourceObservations"]
            for event in journal))
        self.assertFalse(result["completed"])


if __name__ == "__main__":
    unittest.main()
