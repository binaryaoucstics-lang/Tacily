"""Offline carousel/version fixtures shaped like retained NASA/CCTV/GMW rows.

All images, translations and review votes here are synthetic, never site quality.
"""
import copy
import contextlib
import io
import json
from pathlib import Path

import test_capture as support
import test_capture_final_idle as final_idle
import test_review_index as review_support

capture = support.capture


class DynamicSourceVersionTests(support.Fixture):
    browser = support.WorkflowTests.browser
    scripted_case = support.WorkflowTests.scripted_case
    packet_event = staticmethod(support.WorkflowTests.packet_event)
    terminal_event = staticmethod(support.WorkflowTests.terminal_event)
    final_browser = final_idle.FinalIdleTests.final_browser
    run_final = final_idle.FinalIdleTests.run_final
    events = final_idle.FinalIdleTests.events

    def carousel_browser(self, *, skipped, corrupt_static=False, incomplete=False, unknown=False,
                         retract=False, extra_work=False):
        # The first positive source pair is retained in the public NASA evidence.
        # Further versions use explicit synthetic authored changes at that slot.
        titles = (["从一系列经济数据透视中国经济发展活力", "希望的田野上智绘丰收画卷"] if skipped else
                  ["Ames Research Center", "Goddard Space Flight Center"])
        translated = titles if skipped else ["艾姆斯研究中心", "戈达德太空飞行中心"]
        browser = self.final_browser(skipped=skipped)
        browser.version = 0
        history = {}
        original_qa, original_image = browser.qa, browser.screenshot

        def qa(method, *args):
            value = original_qa(method, *args)
            if method != "sample":
                return value
            version = browser.version
            static = value["nodes"][0]
            static.update(locator="0/1/0", rendered=True, ancestorsVisible=True, inViewport=True)
            if corrupt_static and version:
                static["displayedText"] = "unexplained display " + str(version)
            if retract and version:
                static["displayedText"] = static["sourceText"]
                value["events"].append({"type": "retraction", "sourceId": "s1", "sourceUnchanged": True,
                                        "at": value["at"], "phase": args[0]})
            sid = "source-" + str(430 + version)
            carousel = {"id": sid, "sourceText": titles[version % 2] + (f" · {version}" if version > 1 else ""),
                        "displayedText": translated[version % 2] + (f" · {version}" if version > 1 else ""),
                        "locator": "0/2/0", "tag": "H3" if not skipped else "A", "role": "",
                        "firstSeen": value["at"], "rendered": True, "ancestorsVisible": True, "inViewport": True,
                        "rects": [{"x": 0, "y": 40, "width": 300, "height": 30}], "phase": args[0]}
            value["nodes"].append(carousel)
            # Visibility changes include a previously observed, independently
            # identified carousel counter; it is not necessarily a new version.
            if version % 2 == 0:
                value["nodes"].append({"id": "source-319", "sourceText": "2", "displayedText": "2",
                                       "locator": "0/3/0", "tag": "SPAN", "role": "", "phase": args[0],
                                       "rects": [{"x": 0, "y": 75, "width": 12, "height": 16}]})
            for row in value["nodes"]:
                history[row["id"]] = copy.deepcopy(row)
            visible = {row["id"] for row in value["nodes"]}
            value["sourceNodes"] = [dict(row, currentlyRendered=sid in visible) for sid, row in history.items()]
            if unknown and version:
                value["sourceNodes"] = [r for r in value["sourceNodes"] if r["id"] != sid]
            if not skipped and browser.configured:
                value["predictions"].append({"id": "carousel-block-" + str(version), "sourceId": sid,
                    "sourceIds": [sid], "sourceText": carousel["sourceText"], "text": carousel["displayedText"],
                    "eligible": True, "completed": not (incomplete and version), "failed": False,
                    "sourceEvidence": "matched", "auditIssue": None})
            value["productionEligibility"] = [{"sourceId": r["id"], "currentlyRendered": r["id"] in visible,
                "eligible": not skipped and r["id"] in {p["sourceId"] for p in value["predictions"]}}
                for r in value["sourceNodes"]]
            if extra_work and version:
                value["jobs"] = 1
            return value

        def image(path, full=False):
            version = browser.version
            original_image(path, full=full)
            Path(path).write_bytes(final_idle.PNG + f"synthetic version {version} full={full}".encode())
            if Path(path).name.startswith("stable-full"):
                browser.version += 1  # Every image pair is followed by a real source/visibility observation.

        browser.qa, browser.screenshot = qa, image
        return browser

    def test_positive_and_negative_fresh_source_ids_and_visibility_have_truthful_final_proof(self):
        for skipped in (False, True):
            with self.subTest(skipped=skipped):
                result, browser, _, rows = self.run_final(self.carousel_browser(skipped=skipped), skipped=skipped,
                                                         name="carousel-" + str(skipped))
                self.assertEqual(result["errors"], [])
                self.assertTrue(result["settled"])
                self.assertEqual(result["finalState"]["jobs"], 0)
                self.assertEqual(len(result["screenshots"]), 10)
                proof = result["finalScreenshotProof"]
                self.assertFalse(proof["contentMatches"])
                self.assertTrue(proof["timestampedDynamicContent"])
                self.assertTrue(proof["screenshotsCurrent"])
                self.assertFalse(proof["dynamicSkippedContent"])
                self.assertEqual(proof["dynamicSourceIds"], ["source-319", "source-430", "source-431"])
                self.assertEqual(proof["paths"], ["stable.png", "stable-full.png"])
                self.assertTrue(all(a["explained"] for a in proof["authoredSourceChanges"].values()))
                self.assertTrue(all(a["unexplainedSourceIds"] == [] for a in proof["authoredSourceChanges"].values()))
                self.assertTrue(all(shot["at"] and shot["sampleAt"] and shot["contentObservation"]["nodes"]
                                    for shot in result["screenshots"]))
                source_ids = {r["id"] for r in result["sourceNodes"]}
                self.assertTrue({"source-430", "source-431", "source-319"}.issubset(source_ids))
                self.assertEqual({a["needTranslation"] for a in result["annotations"]}, {"unknown"})
                self.assertFalse(result["completed"] or result["evidenceReady"])
                self.assertTrue(any(r["kind"] == "sample" and r["phase"] == "final-idle" for r in rows))
                self.assertEqual(len({s["phase"] for s in result["screenshots"]}), 10)
                self.assertEqual(len({s["path"] for s in result["screenshots"]}), 10)

    def test_authored_versions_do_not_hide_same_source_display_corruption_or_unknown_rows(self):
        for flag in ("corrupt_static", "unknown"):
            for skipped in (False, True):
                with self.subTest(flag=flag, skipped=skipped):
                    result, _, _, _ = self.run_final(self.carousel_browser(skipped=skipped, **{flag: True}),
                        skipped=skipped, name=flag + str(skipped))
                    self.assertIn("final_screenshot_content_unstable", [e["code"] for e in result["errors"]])
                    self.assertFalse(result["settled"])
                    self.assertFalse(result["finalScreenshotProof"]["screenshotsCurrent"])
                    self.assertEqual(result["finalScreenshotProof"]["refreshes"], 3)
                    self.assertEqual(len(result["screenshots"]), 16)
                    if flag == "corrupt_static":
                        self.assertTrue(all("s1" in a["unexplainedSourceIds"]
                            for a in result["finalScreenshotProof"]["authoredSourceChanges"].values()))

    def test_rotating_fresh_ids_record_variation_and_positive_pages_still_need_semantic_settlement(self):
        for skipped in (True, False):
            with self.subTest(skipped=skipped):
                browser = self.carousel_browser(skipped=skipped)
                original = browser.qa

                def qa(method, *args):
                    if method == "sample" and args[0] in ("stable", "final-idle"):
                        browser.version += 1
                    return original(method, *args)

                browser.qa = qa
                result, _, _, _ = self.run_final(browser, skipped=skipped, name="rotating-" + str(skipped))
                stability = result["stability"]["stable"]
                self.assertTrue(stability["contentVaried"])
                self.assertTrue(stability["dynamicSourceIds"])
                self.assertEqual(stability["dynamicDisplaySourceIds"], [])
                if skipped:
                    self.assertTrue(result["settled"])
                    self.assertTrue(result["finalScreenshotProof"]["timestampedDynamicContent"])
                    self.assertFalse(result["finalScreenshotProof"]["contentMatches"])
                else:
                    self.assertFalse(result["settled"])
                    self.assertFalse(stability["semanticStable"])
                    self.assertIn("case_timeout_without_settlement", [e["code"] for e in result["errors"]])

    def test_new_uncompleted_work_and_retractions_keep_the_original_gates(self):
        for flag in ("incomplete", "extra_work", "retract"):
            with self.subTest(flag=flag):
                result, _, _, _ = self.run_final(self.carousel_browser(skipped=False, **{flag: True}),
                                                 skipped=False, name=flag)
                self.assertFalse(result["completed"] or result["evidenceReady"])
                if flag == "retract":
                    self.assertTrue(result["retractions"])
                    self.assertIn("translation_retractions", result["blockedReason"])
                else:
                    self.assertFalse(result["settled"])
                    self.assertIn("case_timeout_without_settlement", [e["code"] for e in result["errors"]])
                    if flag == "incomplete":
                        self.assertIn("node_completion_missing", result["blockedReason"])
                    else:
                        self.assertIn("production_work_unresolved", result["blockedReason"])

    def test_fresh_carousel_source_admits_actual_work_then_refreshes_for_new_request_count(self):
        browser = self.carousel_browser(skipped=False)
        browser.carousel_sent = browser.carousel_complete = False
        original_qa, original_image = browser.qa, browser.screenshot

        def qa(method, *args):
            if method == "deliver" and args[0] == "rpc-carousel":
                browser.carousel_complete = True
            value = original_qa(method, *args)
            if method == "sample" and browser.version == 1:
                if not browser.carousel_sent:
                    browser.carousel_sent = True
                    browser.outgoing.append({"rpcId": "rpc-carousel", "message": {
                        "type": "pageAutoCandidate", "requestId": "carousel-request", "documentToken": "doc1",
                        "generation": 1, "revision": 2, "documentLanguage": "en", "samples": [], "pageSummary": {},
                        "blocks": [{"id": "carousel-block-1", "text": "Goddard Space Flight Center"}]}})
                value["predictions"][-1]["completed"] = browser.carousel_complete
                value["jobs"] = int(not browser.carousel_complete)
            return value

        def image(path, full=False):
            version = browser.version
            original_image(path, full=full)
            if "refresh" in Path(path).name:
                browser.version = version  # This authored version now remains displayed.

        browser.qa, browser.screenshot = qa, image
        result, _, _, rows = self.run_final(browser, skipped=False, name="carousel-new-work")
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertEqual(result["requestIds"], ["site-001-rpc-1", "site-001-rpc-carousel"])
        self.assertEqual([e["id"] for e in result["terminals"]], result["requestIds"])
        self.assertEqual(result["finalScreenshotProof"]["refreshes"], 1)
        self.assertEqual(result["finalScreenshotProof"]["paths"],
                         ["stable-refresh-1.png", "stable-full-refresh-1.png"])
        self.assertEqual(len(result["screenshots"]), 12)
        self.assertTrue(any(r["kind"] == "stability" and r["phase"] == "final-idle" and
                            not r["predictionsComplete"] for r in rows))
        self.assertTrue(all(p["completed"] for p in result["predictions"]))

    def test_hidden_source_reappearance_and_reidentified_unchanged_source_cannot_hide_display_changes(self):
        def value(nodes, at):
            return {"at": at, "pageDecision": True, "nodes": nodes, "sourceNodes": copy.deepcopy(nodes),
                    "predictions": []}
        row = {"id": "source-430", "locator": "0/2/0", "sourceText": "Ames Research Center",
               "displayedText": "艾姆斯研究中心", "tag": "H3", "role": ""}
        before = value([row], "t1")
        hidden = value([], "t2")
        for sid in (row["id"], "source-999"):
            after = value([dict(row, id=sid, displayedText="unexplained")], "t3")
            proof = capture.authored_content_changes(before, after, [hidden])
            self.assertFalse(proof["explained"])
            self.assertEqual(proof["unexplainedSourceIds"], [sid])

    def test_review_index_and_normalizer_retain_rows_images_and_every_review_gate(self):
        result, _, _, _ = self.run_final(self.carousel_browser(skipped=False), skipped=False, name="review-carousel")
        attempt = next((self.root / "review-carousel").glob("*/attempt-*"))
        case_path = attempt / "case.json"
        original = case_path.read_bytes()
        bundle = self.root / "review-bundle"
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(review_support.review_index.main(["--case", str(case_path), "--output-dir", str(bundle)]), 0)
        template = json.loads((bundle / "review-template.json").read_text(encoding="utf-8"))
        self.assertTrue(all(v is False for v in template["screenshots"].values()))
        self.assertEqual(template["pageAnnotation"]["needTranslation"], "unknown")
        phases = {s["phase"] for s in result["screenshots"]}
        self.assertEqual(set(template["screenshots"]), phases)
        template["pageAnnotation"] = {"needTranslation": True, "reviewed": True, "independent": True}
        template["annotations"] = [{"sourceId": r["id"], "needTranslation": r["id"] != "source-319",
                                    "reviewed": True, "independent": True} for r in result["sourceNodes"]]
        template["screenshots"] = dict.fromkeys(phases, True)
        review_path = self.root / "votes.json"

        def normalize(votes, suffix):
            capture.atomic_json(review_path, votes)
            output = self.root / (suffix + ".json")
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                code = final_idle.normalize.main(["--case", str(case_path), "--review", str(review_path),
                    "--output", str(output), "--evidence-root", str(self.root)])
            return code, json.loads(output.read_text(encoding="utf-8")) if output.exists() else None

        code, normalized = normalize(template, "normalized")
        self.assertEqual(code, 1)  # Synthetic evidence remains blocked.
        self.assertEqual(normalized["provenance"]["rawCapture"], result)
        self.assertEqual(normalized["finalScreenshotProof"], result["finalScreenshotProof"])
        for shot in result["screenshots"]:
            self.assertEqual(normalized["screenshots"][shot["phase"]]["contentObservation"], shot["contentObservation"])
        for phase in phases:
            votes = copy.deepcopy(template)
            votes["screenshots"][phase] = False
            code, rejected = normalize(votes, "reject-" + phase)
            self.assertEqual(code, 1)
            self.assertIn("missing_or_rejected_screenshot_reviews", rejected["normalizationIssues"])
            del votes["screenshots"][phase]
            self.assertEqual(normalize(votes, "missing-" + phase)[0], 2)
        self.assertEqual(case_path.read_bytes(), original)
