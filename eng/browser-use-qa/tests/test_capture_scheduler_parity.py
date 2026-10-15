"""Offline scheduler parity; no browser, provider or quality attainment claim."""
import json
import shutil
import subprocess
import unittest
from unittest import mock

import test_capture as support

capture = support.capture


class SchedulerAdapterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        node = shutil.which("node")
        if not node:
            raise RuntimeError("Node is required for actual adapter scheduler checks")
        result = subprocess.run([node, str(support.HERE / "adapter-check.js"),
                                 "--scheduler-lifecycle-fixture"], capture_output=True,
                                text=True, encoding="utf-8", timeout=20)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.fixture = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                                      if line.startswith("SCHEDULER_LIFECYCLE_FIXTURE=")))

    def test_requested_nonzero_and_default_delays_reach_native_timer(self):
        self.assertEqual(self.fixture["requestedDelays"], [80, 120, 240, 0, 0, 500])
        self.assertEqual(self.fixture["callbacks"], 3)

    def test_delayed_work_is_unknown_until_cancelled_or_executed(self):
        views = self.fixture["snapshots"]
        for phase in ("delayed", "beforeDeadline", "followup"):
            value = views[phase]
            self.assertEqual(value["scheduledScans"], 1)
            self.assertEqual(value["producerObservations"][0]["work"]["scheduledScans"], 1)
            self.assertIsNone(value["producerObservations"][0]["eligible"])
        self.assertTrue(views["running"]["scanning"])
        for phase in ("initial", "cancelled", "finished"):
            self.assertEqual(views[phase]["scheduledScans"], 0)
            self.assertIs(views[phase]["producerObservations"][0]["eligible"], False)

    def test_disposal_and_cancelled_timers_leave_no_phantom_work(self):
        self.assertEqual(self.fixture["disposed"]["scheduledScans"], 0)
        self.assertEqual(self.fixture["disposed"]["events"], [])

    def test_actual_adapter_boundaries_preserve_screenshot_time_withdrawal(self):
        start, end = self.fixture["start"], self.fixture["end"]
        self.assertEqual(end["sampleSequence"], start["sampleSequence"] + 2)
        self.assertEqual(end["eventCount"], start["eventCount"] + 1)
        event = end["events"][0]
        self.assertEqual(event["type"], "retraction")
        self.assertTrue(event["sourceUnchanged"])
        self.assertEqual(event["phase"], "mutation")
        later = self.fixture["snapshots"]["later-scroll"]
        self.assertEqual(later["events"][start["eventCount"]:], end["events"])
        self.assertNotEqual(later["nodes"][0]["displayedText"], event["displayedText"])


class CaptureScheduledIdleTests(support.Fixture):
    browser = support.WorkflowTests.browser
    scripted_case = support.WorkflowTests.scripted_case

    def run_scheduled(self, clear_after=None):
        browser = self.browser(skipped=True, probe_states=[{
            "id": "probe-1", "stage": "completed", "result": {"samples": [], "statistics": {}}}])
        original = browser.qa
        clock, samples = [1.0], [0]

        def qa(method, *args):
            value = original(method, *args)
            if method == "sample":
                if browser.configured:
                    samples[0] += 1
                value["scheduledScans"] = int(browser.configured and
                    (clear_after is None or samples[0] <= clear_after))
            return value

        browser.qa = qa
        events = lambda request, _: [{"type": "assessment", "id": request["id"],
                                      "caseId": request["caseId"], "needsTranslation": False}]
        with mock.patch.object(capture, "time", support.local_capture_time(
                monotonic=lambda: clock[0], sleep=lambda seconds: clock.__setitem__(0, clock[0] + seconds))):
            return self.scripted_case(events, browser=browser, case_timeout=.12,
                stable_seconds=.004, site={**support.SITE, "expectedPageTranslation": False})

    def test_remaining_delay_blocks_settlement_and_completion_gate(self):
        result, _, host, rows = self.run_scheduled()
        self.assertFalse(result["settled"])
        self.assertEqual(result["finalState"]["scheduledScans"], 1)
        self.assertIn("production_work_unresolved", result["blockedReason"])
        self.assertIn("case_timeout_without_settlement", [e["code"] for e in result["errors"]])
        self.assertEqual(host.requests, [])
        observations = [row for row in rows if row["kind"] == "stability"]
        self.assertTrue(observations)
        self.assertTrue(all(not row["idle"] and not row["semanticStable"] for row in observations))

    def test_quiet_period_starts_only_after_delay_is_cleared(self):
        result, _, _, rows = self.run_scheduled(clear_after=3)
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        after = [row for row in rows if row["kind"] == "stability" and row["phase"] == "after"]
        self.assertTrue(all(not row["idle"] and row["stableForSeconds"] == 0 for row in after[:3]))
        self.assertTrue(after[-1]["semanticStable"])
        self.assertEqual(result["finalState"]["scheduledScans"], 0)
        self.assertFalse(result["completed"] or result["evidenceReady"])


if __name__ == "__main__":
    unittest.main()
