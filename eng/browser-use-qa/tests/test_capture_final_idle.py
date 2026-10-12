"""Offline post-screenshot regressions; synthetic evidence never certifies a site."""
import copy
import base64
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest import mock

import test_capture as support
import test_review_index as review_support

capture = support.capture
normalize_spec = importlib.util.spec_from_file_location(
    "final_idle_normalize", support.HERE.parent.parent / "website-qa" / "normalize.py")
normalize = importlib.util.module_from_spec(normalize_spec)
normalize_spec.loader.exec_module(normalize)
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")


class FinalIdleTests(support.Fixture):
    browser = support.WorkflowTests.browser
    scripted_case = support.WorkflowTests.scripted_case
    packet_event = staticmethod(support.WorkflowTests.packet_event)
    terminal_event = staticmethod(support.WorkflowTests.terminal_event)

    def final_browser(self, skipped=True, after_image=None, final_sample=None):
        browser = self.browser(skipped=skipped, probe_states=[{
            "id": "probe-1", "stage": "completed", "result": {"samples": [], "statistics": {}}}])
        browser.final_started = False
        browser.final_samples = 0
        browser.content = "合成原始页面" if skipped else "合成翻译"
        browser.source = "合成原始页面" if skipped else "Synthetic source"
        browser.events = []
        original_qa, original_image = browser.qa, browser.screenshot

        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "sample":
                value["at"] = f"sample-{len(browser.calls)}"
                value["nodes"] = [{"id": "s1", "sourceText": browser.source,
                                   "displayedText": browser.content, "tag": "P", "role": "article",
                                   "rects": [{"x": 0, "y": 0, "width": 240, "height": 24}]}]
                value["sourceNodes"] = [{key: value["nodes"][0][key]
                                         for key in ("id", "sourceText", "tag", "role", "rects")}]
                for prediction in value["predictions"]:
                    prediction.update(sourceText=browser.source, text=browser.content)
                value["events"] = copy.deepcopy(browser.events)
                if browser.final_started:
                    browser.final_samples += 1
                    if final_sample:
                        final_sample(browser, value, args[0])
            return value

        def screenshot(path, full=False):
            original_image(path, full=full)
            # Save different bytes when refreshed content differs, so preserving
            # historical hashes and paths is checked independently of metadata.
            Path(path).write_bytes(PNG + browser.content.encode("utf-8"))
            if Path(path).name.startswith("stable-full"):
                browser.final_started = True
            if after_image:
                after_image(browser, Path(path).name)

        browser.qa, browser.screenshot = qa, screenshot
        return browser

    def events(self, request, requests):
        if request["action"] == "assess":
            return [{"type": "assessment", "id": request["id"], "caseId": request["caseId"],
                     "needsTranslation": False}]
        return [self.packet_event(request, '{"type":"pageAutoTranslations","finished":true}'),
                self.terminal_event(request)]

    def run_final(self, browser, *, skipped=True, events=None, timeout=.4, clock=None, name="final"):
        clock = clock or [1.0]
        with mock.patch.object(capture, "time", support.local_capture_time(
                monotonic=lambda: clock[0], sleep=lambda seconds: clock.__setitem__(0, clock[0] + seconds))):
            result, browser, host, rows = self.scripted_case(
                events or self.events, browser=browser, run_id=name, case_timeout=timeout,
                stable_seconds=.01, full_screenshots=True,
                site={**support.SITE, "expectedPageTranslation": not skipped})
        self.assertTrue(result["simulation"])
        self.assertFalse(result["completed"])
        self.assertFalse(result["evidenceReady"])
        self.assertEqual(browser.deadline, None)
        attempt = next((self.root / name).glob("*/attempt-*"))
        saved = json.loads((attempt / "case.json").read_text(encoding="utf-8"))
        self.assertEqual(saved, result)
        for shot in result["screenshots"]:
            self.assertEqual(capture.hashlib.sha256((attempt / shot["path"]).read_bytes()).hexdigest(),
                             shot["sha256"])
        return result, browser, host, rows

    def test_gmw_final_scanning_is_observed_then_actually_settles_after_ten_images(self):
        def sample(browser, value, phase):
            value["scanning"] = browser.final_samples <= 3
        browser = self.final_browser(final_sample=sample)
        original = browser.qa
        def qa(method, *args):
            value = original(method, *args)
            if method == "sample" and args[0] == "stable-full":
                value["scanning"] = True  # Original final snapshot defect.
            return value
        browser.qa = qa
        result, browser, host, rows = self.run_final(browser)
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertEqual(len(result["screenshots"]), 10)
        self.assertEqual(result["requestIds"], [])
        self.assertEqual(result["terminals"], [])
        self.assertEqual([request["action"] for request in host.requests], ["assess"])
        prior = next(row["value"] for row in rows if row.get("phase") == "stable-full" and row["kind"] == "sample")
        self.assertTrue(prior["scanning"])
        final = result["finalState"]
        self.assertIs(final["pageDecision"], False)
        self.assertEqual(final["status"]["state"], "skipped")
        self.assertTrue(all(not final[key] for key in ("jobs", "pending", "scanning", "rpcPending")))
        self.assertGreater(browser.final_samples, 3)
        proof = result["stability"]["final-idle"]
        self.assertTrue(proof["semanticStable"] and proof["idle"] and proof["terminalsComplete"])
        self.assertGreaterEqual(proof["stableForSeconds"], .01)
        self.assertNotIn("production_work_unresolved", result["blockedReason"])
        # The public guard remains strict for the original observed snapshot.
        self.assertIn("production_work_unresolved", capture.completion_gate(
            {**result, "finalState": prior}, next((self.root / "final").glob("*/attempt-*")))[2])

    def test_new_final_unresolved_flags_and_decision_timeout_remain_failures(self):
        for key in ("jobs", "pending", "scanning", "rpcPending", "status", "pageDecision"):
            with self.subTest(key=key):
                def sample(browser, value, phase):
                    value[key] = {"state": "assessing"} if key == "status" else None if key == "pageDecision" else 1
                result, _, _, rows = self.run_final(self.final_browser(final_sample=sample), name=key)
                self.assertIn("case_timeout_without_settlement", [e["code"] for e in result["errors"]])
                self.assertFalse(result["settled"] or result["semanticStable"])
                self.assertEqual(len(result["screenshots"]), 10)
                self.assertIn("production_work_unresolved", result["blockedReason"])
                self.assertFalse(any(row["semanticStable"] for row in rows
                                     if row["kind"] == "stability" and row["phase"] == "final-idle"))

    @staticmethod
    def admit(browser, filename):
        if filename != "stable-full.png":
            return
        browser.outgoing.append({"rpcId": "rpc-late", "message": {
            "type": "pageAutoCandidate", "requestId": "req-late", "documentToken": "doc1",
            "generation": 1, "revision": 2, "documentLanguage": "en",
            "blocks": [{"id": "b1", "text": "Synthetic source"}], "samples": [], "pageSummary": {}}})

    def test_final_admitted_request_is_forwarded_and_terminal_proven_before_refresh(self):
        result, browser, host, rows = self.run_final(
            self.final_browser(skipped=False, after_image=self.admit), skipped=False)
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertEqual(result["requestIds"], ["site-001-rpc-1", "site-001-rpc-late"])
        self.assertEqual([event["id"] for event in result["terminals"]], result["requestIds"])
        self.assertEqual(len(result["screenshots"]), 12)
        self.assertEqual(result["finalScreenshotProof"]["paths"],
                         ["stable-refresh-1.png", "stable-full-refresh-1.png"])
        late = [row for row in rows if row["kind"] == "host_event" and row["event"]["id"].endswith("rpc-late")]
        self.assertEqual([row["event"]["type"] for row in late], ["packet", "terminal"])
        self.assertEqual(sum(call[0] == "acknowledge" and call[1] == "rpc-late" for call in browser.calls), 1)

    def test_final_packet_without_terminal_cannot_settle(self):
        def events(request, requests):
            values = self.events(request, requests)
            return values[:-1] if request["id"].endswith("rpc-late") else values
        result, _, _, _ = self.run_final(self.final_browser(skipped=False, after_image=self.admit),
                                         skipped=False, events=events)
        self.assertEqual([e["code"] for e in result["errors"]],
                         ["case_timeout_without_settlement", "cleanup_case_timeout_without_settlement"])
        self.assertEqual(len(result["terminals"]), 1)
        self.assertIn("terminal_evidence_missing", result["blockedReason"])
        self.assertFalse(result["settled"])
        self.assertTrue(result["stability"]["final-idle"]["idle"])
        self.assertFalse(result["stability"]["final-idle"]["terminalsComplete"])

    def test_final_backend_failure_preserves_error_terminal_retraction_and_history(self):
        def events(request, requests):
            if request["id"].endswith("rpc-late"):
                return [self.packet_event(request, '{"type":"pageAutoTranslations","error":"backend unavailable"}'),
                        self.terminal_event(request, success=False, failedCount=1)]
            return self.events(request, requests)
        def sample(browser, value, phase):
            value["events"] = [{"type": "retraction", "sourceId": "s1", "sourceText": browser.source}]
            value["predictions"][0].update(id="late-failed", completed=False, failed=True)
            value["status"] = {"state": "failed"}
        result, _, _, rows = self.run_final(self.final_browser(skipped=False, after_image=self.admit,
                                                               final_sample=sample), skipped=False, events=events)
        self.assertEqual([e["code"] for e in result["errors"]], ["backend_packet_error", "backend_terminal_failed"])
        self.assertEqual(len(result["terminals"]), 2)
        self.assertFalse(result["terminals"][-1]["success"])
        self.assertFalse(result["settled"] or result["semanticStable"])
        self.assertTrue(result["retractions"])
        self.assertEqual({p["id"] for p in result["predictionHistory"]}, {"b1", "late-failed"})
        self.assertIn("failureSnapshot", result)
        self.assertEqual(len(result["screenshots"]), 10)
        self.assertTrue(any(row.get("phase") == "failure-final" for row in rows))

    def test_new_content_after_images_refreshes_stable_pair_without_overwriting(self):
        for skipped in (False, True):
            with self.subTest(skipped=skipped):
                def change(browser, filename):
                    if filename == "stable-full.png":
                        browser.content = "新的合成内容"
                result, _, _, _ = self.run_final(self.final_browser(skipped=skipped, after_image=change),
                                                 skipped=skipped, name="changed-" + str(skipped))
                self.assertEqual(result["errors"], [])
                self.assertTrue(result["settled"])
                shots = [shot for shot in result["screenshots"] if shot["phase"].startswith("stable")]
                self.assertEqual([s["path"] for s in shots],
                                 ["stable.png", "stable-full.png", "stable-refresh-1.png", "stable-full-refresh-1.png"])
                self.assertEqual([s["phase"] for s in shots],
                                 ["stable", "stable-full", "stable-refresh-1", "stable-full-refresh-1"])
                self.assertEqual([s.get("basePhase") for s in shots], [None, None, "stable", "stable-full"])
                self.assertNotEqual(shots[0]["sha256"], shots[2]["sha256"])
                self.assertTrue(result["finalScreenshotProof"]["contentMatches"])
                self.assertFalse(result["finalScreenshotProof"]["dynamicSkippedContent"])

    def test_capture_refresh_roundtrip_preserves_every_phase_image_and_independent_vote(self):
        # These are explicit synthetic review labels/votes, never inferred from
        # production decisions or predictions, and cannot certify a real site.
        for skipped, label in ((True, False), (False, True)):
            with self.subTest(skipped=skipped):
                name = "roundtrip-" + str(skipped)
                def change(browser, filename):
                    if filename in ("stable-full.png", "stable-full-refresh-1.png"):
                        browser.content += "新内容"
                result, _, _, _ = self.run_final(
                    self.final_browser(skipped=skipped, after_image=change), skipped=skipped, name=name)
                self.assertEqual(result["errors"], [])
                self.assertTrue(result["settled"])
                self.assertEqual(result["finalScreenshotProof"]["refreshes"], 2)
                attempt = next((self.root / name).glob("*/attempt-*"))
                case_path = attempt / "case.json"
                shots = result["screenshots"]
                originals = {p: p.read_bytes() for p in [case_path, *(attempt / s["path"] for s in shots)]}
                original_phases = ["before", "before-full", "after", "after-full", "scroll-before",
                                   "scroll-after", "return-after", "reflow-after", "stable", "stable-full"]
                self.assertEqual([s["phase"] for s in shots[:10]], original_phases)
                for shot in shots[:10]:
                    self.assertNotIn("basePhase", shot)
                    self.assertEqual(shot["path"], shot["phase"] + ".png")
                    self.assertEqual(originals[attempt / shot["path"]],
                                     PNG + ("合成原始页面" if skipped else "合成翻译").encode("utf-8"))
                proof = result["finalScreenshotProof"]
                self.assertEqual(proof["images"], [
                    {"basePhase": base, **{key: shot[key] for key in ("phase", "path", "sha256")}}
                    for base, shot in zip(("stable", "stable-full"), shots[-2:])])
                self.assertEqual([s["basePhase"] for s in shots[10:]],
                                 ["stable", "stable-full", "stable", "stable-full"])
                self.assertTrue(proof["screenshotsCurrent"])

                # Reproduce the prior cross-module defect using a separate
                # synthetic case: appended files with duplicate canonical phases
                # must still be rejected by the unchanged normalizer.
                duplicate = copy.deepcopy(result)
                for shot in duplicate["screenshots"][10:]:
                    shot["phase"] = shot["basePhase"]
                duplicate_case = attempt / "duplicate-case.json"
                capture.atomic_json(duplicate_case, duplicate)
                duplicate_review = self.root / (name + "-duplicate-votes.json")
                capture.atomic_json(duplicate_review, {
                    "caseSha256": capture.hashlib.sha256(duplicate_case.read_bytes()).hexdigest(),
                    "pageAnnotation": {"needTranslation": label, "reviewed": True, "independent": True},
                    "annotations": [{"sourceId": "s1", "needTranslation": label,
                                     "reviewed": True, "independent": True}],
                    "screenshots": {s["phase"]: True for s in duplicate["screenshots"]}, "retractions": []})
                duplicate_output = self.root / (name + "-duplicate-normalized.json")
                diagnostic = io.StringIO()
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(diagnostic):
                    self.assertEqual(normalize.main([
                        "--case", str(duplicate_case), "--review", str(duplicate_review),
                        "--output", str(duplicate_output), "--evidence-root", str(self.root)]), 2)
                self.assertEqual(json.loads(diagnostic.getvalue())["error"], "duplicate_or_reserved_screenshot_phase")
                self.assertFalse(duplicate_output.exists())

                bundle = self.root / (name + "-review")
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(review_support.review_index.main(
                        ["--case", str(case_path), "--output-dir", str(bundle)]), 0)
                sources = json.loads((bundle / "sources.json").read_text(encoding="utf-8"))
                review = json.loads((bundle / "review-template.json").read_text(encoding="utf-8"))
                phases = [s["phase"] for s in shots]
                self.assertEqual([s["phase"] for s in sources["screenshots"]], phases)
                self.assertEqual(set(review["screenshots"]), set(phases))
                self.assertTrue(all(v is False for v in review["screenshots"].values()))
                self.assertEqual(review["pageAnnotation"]["needTranslation"], "unknown")
                for raw, exported in zip(shots, sources["screenshots"]):
                    self.assertEqual(exported["originalPath"], raw["path"])
                    self.assertEqual(exported["sha256"], raw["sha256"])
                    self.assertEqual((bundle / exported["path"]).read_bytes(), originals[attempt / raw["path"]])
                    self.assertIn('<figcaption>' + raw["phase"] + '</figcaption>',
                                  (bundle / "index.html").read_text(encoding="utf-8"))
                review["pageAnnotation"] = {"needTranslation": label, "reviewed": True, "independent": True}
                review["annotations"] = [{"sourceId": "s1", "needTranslation": label,
                                          "reviewed": True, "independent": True}]
                review["screenshots"] = dict.fromkeys(phases, True)
                review_path = self.root / (name + "-votes.json")

                def normalize_review(votes, output):
                    capture.atomic_json(review_path, votes)
                    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                        code = normalize.main(["--case", str(case_path), "--review", str(review_path),
                                               "--output", str(output), "--evidence-root", str(self.root)])
                    return code

                output = self.root / (name + "-normalized.json")
                self.assertEqual(normalize_review(review, output), 1)  # Simulation stays blocked.
                normalized = json.loads(output.read_text(encoding="utf-8"))
                self.assertTrue(normalized["synthetic"])
                self.assertFalse(normalized["completed"])
                self.assertIn("non_live_or_simulated_capture", normalized["normalizationIssues"])
                self.assertNotIn("missing_or_rejected_screenshot_reviews", normalized["normalizationIssues"])
                self.assertEqual(normalized["provenance"]["rawCapture"], result)
                self.assertEqual(normalized["provenance"]["rawReview"], review)
                self.assertEqual(set(normalized["screenshots"]) - {"checks"}, set(phases))
                self.assertEqual(normalized["finalScreenshotProof"], proof)
                for shot in shots:
                    exported = normalized["screenshots"][shot["phase"]]
                    self.assertIs(exported["reviewed"], True)
                    self.assertEqual(exported["sha256"], shot["sha256"])
                    self.assertEqual(exported.get("basePhase"), shot.get("basePhase"))
                    self.assertEqual({k: v for k, v in exported.items() if k not in ("path", "reviewed")},
                                     {k: v for k, v in shot.items() if k != "path"})
                    self.assertEqual((self.root / exported["path"]).read_bytes(), originals[attempt / shot["path"]])

                # Neither canonical images nor refresh images can be waived or
                # replaced by another phase's vote, even with final idle proved.
                for phase in ("stable", "stable-full", "stable-refresh-1", "stable-full-refresh-2"):
                    missing = copy.deepcopy(review)
                    del missing["screenshots"][phase]
                    self.assertEqual(normalize_review(missing, self.root / (name + "-missing-" + phase + ".json")), 2)
                    rejected = copy.deepcopy(review)
                    rejected["screenshots"][phase] = False
                    rejection = self.root / (name + "-rejected-" + phase + ".json")
                    self.assertEqual(normalize_review(rejected, rejection), 1)
                    self.assertIn("missing_or_rejected_screenshot_reviews",
                                  json.loads(rejection.read_text(encoding="utf-8"))["normalizationIssues"])
                for path, raw in originals.items():
                    self.assertEqual(path.read_bytes(), raw)

    def test_unending_post_image_content_changes_have_bounded_refreshes(self):
        def change(browser, filename):
            if filename.startswith("stable-full"):
                browser.content += "变化"
        result, _, _, _ = self.run_final(self.final_browser(after_image=change))
        self.assertEqual([e["code"] for e in result["errors"]], ["final_screenshot_content_unstable"])
        self.assertFalse(result["settled"])
        self.assertFalse(result["finalScreenshotProof"]["screenshotsCurrent"])
        self.assertEqual(len(result["screenshots"]), 16)
        self.assertEqual(len({shot["path"] for shot in result["screenshots"]}), 16)
        self.assertEqual(len({shot["phase"] for shot in result["screenshots"]}), 16)
        self.assertEqual(result["finalScreenshotProof"]["paths"],
                         ["stable-refresh-3.png", "stable-full-refresh-3.png"])

    def test_existing_skipped_ticker_does_not_hide_new_changes_to_static_content(self):
        browser = self.final_browser()
        browser.static_content = "稳定内容"
        original_qa, original_image = browser.qa, browser.screenshot
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "sample":
                value["nodes"][0]["displayedText"] = f"合成动态时刻-{len(browser.calls)}"
                value["nodes"].append({"id": "s2", "sourceText": "稳定内容",
                                       "displayedText": browser.static_content, "tag": "P", "role": "article"})
                value["sourceNodes"].append({"id": "s2", "sourceText": "稳定内容"})
            return value
        def image(path, full=False):
            original_image(path, full=full)
            if Path(path).name == "stable-full.png":
                browser.static_content = "新内容"
        browser.qa, browser.screenshot = qa, image
        result, _, _, rows = self.run_final(browser)
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        proof = result["finalScreenshotProof"]
        self.assertTrue(proof["dynamicSkippedContent"])
        self.assertEqual(proof["dynamicSourceIds"], ["s1"])
        self.assertFalse(proof["contentMatches"])  # Honest about the running ticker.
        self.assertTrue(proof["screenshotsCurrent"])
        self.assertEqual(proof["refreshes"], 1)  # Static content still refreshes both images.
        self.assertEqual(len(result["screenshots"]), 12)
        self.assertTrue(any(row["kind"] == "final_screenshot_proof" and not row["screenshotsCurrent"]
                            for row in rows))

    def test_new_incomplete_prediction_after_images_cannot_settle(self):
        def sample(browser, value, phase):
            value["predictions"][0]["completed"] = False
        result, _, _, _ = self.run_final(self.final_browser(skipped=False, final_sample=sample), skipped=False)
        self.assertFalse(result["settled"])
        self.assertIn("case_timeout_without_settlement", [e["code"] for e in result["errors"]])
        self.assertFalse(result["stability"]["final-idle"]["predictionsComplete"])
        self.assertIn("node_completion_missing", result["blockedReason"])

    def test_image_or_refresh_exhausting_deadline_keeps_images_and_does_not_start_more_work(self):
        for expired_image in ("stable-full.png", "stable-refresh-1.png"):
            with self.subTest(image=expired_image):
                clock = [1.0]
                def change(browser, filename):
                    if filename == "stable-full.png":
                        browser.content = "改变后的内容"
                    if filename == expired_image:
                        clock[0] = browser.deadline
                result, browser, _, rows = self.run_final(self.final_browser(after_image=change),
                    clock=clock, name="deadline-" + expired_image.removesuffix(".png"))
                self.assertEqual([e["code"] for e in result["errors"]],
                                 ["case_timeout_without_settlement", "cleanup_case_timeout_without_settlement"])
                self.assertFalse(result["settled"])
                self.assertEqual(result["screenshots"][-1]["path"], expired_image)
                self.assertFalse(any(call[0] == "screenshot" and call[1] == "stable-full-refresh-1.png"
                                     for call in browser.calls))
                self.assertEqual(clock[0], 1.4)


if __name__ == "__main__":
    unittest.main()
