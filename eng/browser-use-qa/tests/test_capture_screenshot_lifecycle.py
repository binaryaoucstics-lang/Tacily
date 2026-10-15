"""Synthetic screenshot command boundaries preserve DOM candidates, never paint proof."""
import copy
import json
from pathlib import Path
import unittest
from unittest import mock

import test_capture as support

capture = support.capture


class ScreenshotLifecycleTests(support.Fixture):
    browser = support.WorkflowTests.browser
    scripted_case = support.WorkflowTests.scripted_case
    packet_event = staticmethod(support.WorkflowTests.packet_event)
    terminal_event = staticmethod(support.WorkflowTests.terminal_event)

    def run_lifecycle(self, *, image_error=False, boundary_error=False, expire=False,
                      legacy=False, stale_after=False):
        browser = self.browser()
        original_qa, original_image = browser.qa, browser.screenshot
        browser.raw_events, browser.sequence, browser.edge_calls = [], 0, []
        browser.page_clock, clock = 0, [1.0]
        browser.withdrawals = []

        def qa(method, *args):
            browser.page_clock += 1
            if method == "screenshotBoundary" and not legacy:
                offset = args[0]
                browser.edge_calls.append((browser.page_clock, offset))
                if boundary_error and browser.withdrawals:
                    raise capture.CaptureError("synthetic_boundary_error")
                return {"at": f"page-{browser.page_clock}", "sampleSequence": browser.sequence,
                        "eventOffset": offset, "eventCount": len(browser.raw_events),
                        "events": copy.deepcopy(browser.raw_events[offset:]),
                        "scheduledScans": 0,
                        "viewport": {"width": 800, "height": 600, "scrollX": 0, "scrollY": 0}}
            value = original_qa(method, *args)
            if method == "sample":
                browser.sequence += 1
                value["at"] = f"page-{browser.page_clock}"
                value["nodes"] = [{"id": "s1", "sourceText": "Synthetic source", "tag": "P", "role": "",
                                   "displayedText": "合成译文" if browser.configured else "Synthetic source"}]
                value["events"] = copy.deepcopy([] if stale_after else browser.raw_events)
            return value

        def image(path, full=False):
            original_image(path, full=full)
            if Path(path).name == "after-full.png":
                browser.page_clock += 1
                event = {"type": "retraction", "sourceId": "s1", "sourceText": "Synthetic source",
                         "previousText": "合成译文", "displayedText": "Synthetic source",
                         "sourceUnchanged": True, "phase": "mutation", "at": f"page-{browser.page_clock}"}
                # Duplicate raw events remain two observations, not one.
                browser.withdrawals = [copy.deepcopy(event), copy.deepcopy(event)]
                browser.raw_events.extend(copy.deepcopy(browser.withdrawals))
                # The final DOM/pixels in this fixture are already restored.
                if expire:
                    clock[0] = browser.deadline
                if image_error:
                    raise capture.CaptureError("synthetic_screenshot_error")

        browser.qa, browser.screenshot = qa, image

        def events(request, _):
            return [self.packet_event(request, '{"type":"pageAutoTranslations","finished":true}'),
                    self.terminal_event(request)]

        with mock.patch.object(capture, "time", support.local_capture_time(
                monotonic=lambda: clock[0], sleep=lambda seconds: clock.__setitem__(0, clock[0] + seconds))):
            result, browser, host, rows = self.scripted_case(events, browser=browser, case_timeout=.3,
                                                           stable_seconds=.004, full_screenshots=True)
        attempt = next((self.root / "scripted").glob("*/attempt-*"))
        self.assertEqual(json.loads((attempt / "case.json").read_text(encoding="utf-8")), result)
        self.assertFalse(result["completed"] or result["evidenceReady"])
        self.assertTrue(all(row["needTranslation"] == "unknown" for row in result["annotations"]))
        for shot in result["screenshots"]:
            self.assertEqual(capture.hashlib.sha256((attempt / shot["path"]).read_bytes()).hexdigest(),
                             shot["sha256"])
        return result, browser, host, rows, attempt

    def test_boundaries_locate_candidates_during_full_screenshot_before_scroll_and_reflow(self):
        result, browser, _, rows, _ = self.run_lifecycle()
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertEqual(result["retractions"], browser.withdrawals)
        self.assertIn("translation_retractions", result["blockedReason"])
        self.assertEqual(len(result["screenshotLifecycle"]), len(result["screenshots"]))
        shot = next(shot for shot in result["screenshots"] if shot["phase"] == "after-full")
        lifecycle = result["screenshotLifecycle"][shot["lifecycleIndex"]]
        self.assertEqual(lifecycle["phase"], "after-full")
        self.assertTrue(lifecycle["fullPage"])
        self.assertEqual(lifecycle["commandState"], "returned")
        self.assertEqual(lifecycle["paintedTransition"], "unknown")
        self.assertLessEqual(lifecycle["commandStartedAt"], lifecycle["commandFinishedAt"])
        self.assertEqual(lifecycle["after"]["eventCount"] - lifecycle["before"]["eventCount"], 2)
        self.assertEqual(lifecycle["after"]["sampleSequence"], lifecycle["before"]["sampleSequence"])
        interesting = [(index, row) for index, row in enumerate(rows)
                       if row.get("phase") == "after-full" and row["kind"] in
                       ("screenshot_boundary", "screenshot_command")]
        self.assertEqual([(row["kind"], row["edge"]) for _, row in interesting], [
            ("screenshot_boundary", "before"), ("screenshot_command", "start"),
            ("screenshot_command", "end"), ("screenshot_boundary", "after")])
        self.assertEqual(interesting[-1][1]["value"]["events"], browser.withdrawals)
        later = [index for index, row in enumerate(rows) if row["kind"] == "sample"
                 and row.get("phase") in ("scroll-before", "scroll-after", "reflow-after")]
        self.assertTrue(all(index > interesting[-1][0] for index in later))
        # Boundary calls add no complete DOM samples.
        self.assertFalse(any(row["kind"] == "sample" and "screenshot-end" in row.get("phase", "") for row in rows))

    def test_screenshot_error_still_exports_end_events_and_keeps_original_failure(self):
        result, browser, _, rows, attempt = self.run_lifecycle(image_error=True)
        self.assertIn("synthetic_screenshot_error", [error["code"] for error in result["errors"]])
        self.assertFalse(result["settled"])
        lifecycle = result["screenshotLifecycle"][-1]
        self.assertEqual(lifecycle["phase"], "after-full")
        self.assertEqual(lifecycle["commandState"], "error")
        self.assertEqual(lifecycle["commandError"]["code"], "synthetic_screenshot_error")
        self.assertIn("at", lifecycle["after"])
        self.assertEqual(result["retractions"], browser.withdrawals)
        self.assertIn("translation_retractions", result["blockedReason"])
        self.assertTrue((attempt / "after-full.png").exists())
        self.assertFalse(any(shot["phase"] == "after-full" for shot in result["screenshots"]))
        self.assertTrue(any(row["kind"] == "screenshot_boundary" and row["edge"] == "after"
                            and row["phase"] == "after-full" for row in rows))

    def test_stale_later_sample_cannot_erase_raw_withdrawals(self):
        result, browser, _, _, _ = self.run_lifecycle(stale_after=True)
        self.assertEqual(result["errors"], [])
        self.assertEqual(result["retractions"], browser.withdrawals)
        self.assertIn("translation_retractions", result["blockedReason"])

    def test_boundary_error_does_not_mask_screenshot_error_or_invent_end_evidence(self):
        result, _, _, rows, _ = self.run_lifecycle(image_error=True, boundary_error=True)
        self.assertEqual([error["code"] for error in result["errors"]],
                         ["synthetic_boundary_error", "synthetic_screenshot_error"])
        lifecycle = result["screenshotLifecycle"][-1]
        self.assertEqual(lifecycle["after"]["state"], "unavailable")
        self.assertEqual(lifecycle["paintedTransition"], "unknown")
        self.assertFalse(any(row["kind"] == "screenshot_boundary" and row["edge"] == "after"
                             and row["phase"] == "after-full" for row in rows))

    def test_deadline_exhaustion_records_command_end_without_starting_boundary_work(self):
        result, browser, _, rows, _ = self.run_lifecycle(expire=True)
        lifecycle = result["screenshotLifecycle"][-1]
        self.assertEqual(lifecycle["phase"], "after-full")
        self.assertIn("commandFinishedAt", lifecycle)
        self.assertEqual(lifecycle["after"]["state"], "unavailable")
        self.assertEqual(lifecycle["after"]["error"]["code"], "case_timeout_without_settlement")
        self.assertFalse(result["settled"])
        self.assertEqual(len(browser.edge_calls), 7)  # 3 complete pairs + last before.
        self.assertFalse(any(row["kind"] == "screenshot_boundary" and row["edge"] == "after"
                             and row["phase"] == "after-full" for row in rows))

    def test_legacy_boundary_absence_remains_unknown(self):
        result, _, _, _, _ = self.run_lifecycle(legacy=True)
        self.assertEqual(result["errors"], [])
        self.assertTrue(all(row["before"] == {"state": "unavailable"}
                            and row["after"] == {"state": "unavailable"}
                            and row["paintedTransition"] == "unknown"
                            for row in result["screenshotLifecycle"]))
        self.assertIn("translation_retractions", result["blockedReason"])


if __name__ == "__main__":
    unittest.main()
