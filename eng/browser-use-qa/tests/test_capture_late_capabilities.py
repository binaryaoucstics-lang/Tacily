"""Offline replay of the Wikipedia cancellation/Xinhua handshake boundary."""
import io
import json
import queue
from types import SimpleNamespace
from unittest import mock

import test_capture as support

capture = support.capture


class LateCapabilitiesTests(support.Fixture):
    def host(self, on_request=None):
        host = capture.JsonlHost.__new__(capture.JsonlHost)
        host.journal, host.events, host.backlog = self.journal, queue.Queue(), []
        host.sent_requests = {}
        requests = []

        class Input(io.StringIO):
            def write(self, line):
                request = json.loads(line)
                requests.append(request)
                if on_request:
                    for event in on_request(request):
                        host.events.put(event)
                return super().write(line)

        host.process = SimpleNamespace(stdin=Input())
        return host, requests

    @staticmethod
    def reply(request, **fields):
        return {"type": "capabilities", "id": request["id"], "caseId": request["caseId"],
                "live": True, "targetLanguage": "zh-CN", **fields}

    @staticmethod
    def prior_request():
        # Original public cancellation identity; no credentials/model calls.
        return {"schemaVersion": 1, "id": "site-021-rpc-2535", "caseId": "site-021", "action": "cancel"}

    def rows(self):
        return [json.loads(line) for line in self.journal.path.read_text(encoding="utf-8").splitlines()]

    def test_known_sent_cancel_error_and_terminal_are_retained_in_order(self):
        prior = self.prior_request()
        late = [{"type": "error", "id": prior["id"], "caseId": prior["caseId"],
                 "live": True, "code": "cancelled"},
                {"type": "terminal", "id": prior["id"], "caseId": prior["caseId"],
                 "live": True, "success": False, "requestCount": 0, "failedCount": 47}]
        host, requests = self.host(lambda r: [late[1], self.reply(r)] if r["action"] == "capabilities" else [])
        host.send(prior)
        host.backlog.append(late[0])
        reply = host.capabilities("site-022")
        self.assertEqual(reply, self.reply(requests[-1]))
        self.assertRegex(reply["id"], r"^capabilities-[0-9a-f]{32}$")
        self.assertEqual([host.poll(), host.poll(), host.poll()], [*late, None])
        logged = [r for r in self.rows() if r["kind"] == "host_deferred_event"]
        self.assertEqual([r["event"] for r in logged], late)
        self.assertTrue(all(r["at"] and r["waitingFor"] == requests[-1] for r in logged))

    def test_backlog_is_not_repolled_and_repeated_handshakes_use_fresh_ids(self):
        host, requests = self.host(lambda r: [self.reply(r)] if r["action"] == "capabilities" else [])
        host.send(self.prior_request())
        late = {"type": "error", "id": "site-021-rpc-2535", "caseId": "site-021", "code": "cancelled"}
        host.backlog.append(late)
        replies = [host.capabilities("site-022") for _ in range(3)]
        self.assertEqual(len({r["id"] for r in replies}), 3)
        self.assertEqual(host.backlog, [late])
        self.assertEqual(len([r for r in self.rows() if r["kind"] == "host_deferred_event"]), 3)

    def test_unknown_ownership_and_same_case_wrong_ids_still_fail(self):
        for event in (
            {"type": "error", "id": "site-021-rpc-forged", "caseId": "site-021", "code": "cancelled"},
            {"type": "terminal", "id": "site-021-rpc-2535", "caseId": "unowned"},
            {"type": "packet", "id": "unowned", "caseId": "site-022", "packet": "{}"},
            {"type": "capabilities", "id": "old-current-capability", "caseId": "site-022"},
        ):
            with self.subTest(event=event):
                host, _ = self.host(lambda r: [event] if r["action"] == "capabilities" else [])
                host.send(self.prior_request())
                host.send({"id": "old-current-capability", "caseId": "site-022", "action": "capabilities"})
                # The setup send also queued an event. Both are invalid fresh replies.
                with self.assertRaisesRegex(capture.CaptureError, "capabilities_identity_mismatch"):
                    host.capabilities("site-022")

    def test_current_errors_and_transport_failures_retain_deferred_evidence(self):
        for code in ("provider_unavailable", "host_eof", "host_invalid_jsonl"):
            with self.subTest(code=code):
                host, _ = self.host(lambda r: [{"type": "error", "id": r["id"], "caseId": r["caseId"],
                                               "code": code}] if r["action"] == "capabilities" else [])
                host.send(self.prior_request())
                late = {"type": "terminal", "id": "site-021-rpc-2535", "caseId": "site-021"}
                host.backlog.append(late)
                with self.assertRaisesRegex(capture.CaptureError, code):
                    host.capabilities("site-022")
                self.assertEqual(host.poll(), late)

    def test_live_and_target_checks_are_preserved(self):
        for fields in ({"live": 1}, {"live": None}, {"targetLanguage": ""},
                       {"targetLanguage": 1}, {"targetLanguage": "x" * 65}):
            with self.subTest(fields=fields):
                host, _ = self.host(lambda r: [self.reply(r, **fields)])
                with self.assertRaisesRegex(capture.CaptureError, "invalid_capabilities"):
                    host.capabilities("site-022")

    def test_original_timeout_and_event_bound_keep_backlog_without_cycling(self):
        host, _ = self.host()
        host.send(self.prior_request())
        late = {"type": "error", "id": "site-021-rpc-2535", "caseId": "site-021", "code": "cancelled"}
        host.backlog = [late] * (capture.FORWARD_EVENT_LIMIT + 1)
        with self.assertRaisesRegex(capture.CaptureError, "capabilities_event_limit"):
            host.capabilities("site-022")
        self.assertEqual(host.backlog, [late] * (capture.FORWARD_EVENT_LIMIT + 1))
        host.backlog = [late]
        clock = [1.0]
        original = host.poll

        def poll(timeout=0):
            if host.backlog:
                return original(0)
            clock[0] += timeout
            return None

        with mock.patch.object(host, "poll", poll), mock.patch.object(capture, "time", support.local_capture_time(
                monotonic=lambda: clock[0])):
            with self.assertRaisesRegex(capture.CaptureError, "capabilities_timeout"):
                host.capabilities("site-022", timeout=.5)
        self.assertEqual(clock[0], 1.5)
        self.assertEqual(host.poll(), late)

    def test_failed_writes_cannot_establish_event_ownership(self):
        host, _ = self.host()
        host.process.stdin.close()
        with self.assertRaisesRegex(capture.CaptureError, "host_write_failed"):
            host.send(self.prior_request())
        self.assertFalse(host.owns_event({"id": "site-021-rpc-2535", "caseId": "site-021"}))

    def test_malformed_stdout_and_eof_are_not_deferred(self):
        for line in ('{"id":"prior","caseId":"site-021"}\n', 'not JSON\n'):
            host, _ = self.host()
            host.process.stdout = io.StringIO(line)
            host._read()
            with self.assertRaisesRegex(capture.CaptureError, "host_invalid_jsonl"):
                host.capabilities("site-022")

    def test_late_events_reach_case_journal_without_current_terminals_or_delivery(self):
        def reply(request):
            if request["action"] == "capabilities":
                return [self.reply(request, live=False)]
            if request["action"] == "assess":
                return [{"type": "assessment", "id": request["id"], "caseId": request["caseId"],
                         "needsTranslation": False}]
            return []

        host, _ = self.host(reply)
        prior = self.prior_request()
        host.send(prior)
        late = [{"type": "error", "id": prior["id"], "caseId": prior["caseId"], "code": "cancelled"},
                {"type": "terminal", "id": prior["id"], "caseId": prior["caseId"],
                 "success": False, "requestCount": 0, "failedCount": 47}]
        host.backlog.extend(late)
        browser = support.WorkflowTests.browser(self, skipped=True, probe_states=[{
            "id": "probe-1", "stage": "completed", "result": {"samples": [], "statistics": {}}}])
        run = capture.EvidenceRun(self.root, "late-case", {"sourceHead": "a" * 40})
        result = capture.CaseCapture(browser, host, run, support.HERE.parents[2], stable_seconds=.001,
                                     poll_seconds=.001).capture({**support.SITE, "expectedPageTranslation": False})
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertEqual(result["lateHostEvents"], late)
        self.assertEqual(result["terminals"], [])
        self.assertEqual(browser.delivered, [])
        self.assertFalse(result["completed"] or result["evidenceReady"])
        rows = [json.loads(line) for line in host.journal.path.read_text(encoding="utf-8").splitlines()]
        self.assertEqual([r["event"] for r in rows if r["kind"] == "host_deferred_event"], late)
        self.assertEqual([r["event"] for r in rows if r["kind"] == "host_event" and
                          r["event"].get("caseId") == prior["caseId"]], late)

    def test_late_packets_assessments_and_previous_capabilities_are_also_retained(self):
        host, _ = self.host(lambda r: [self.reply(r)] if r["caseId"] == "site-022" else [])
        late = []
        for action, kind in (("message", "packet"), ("assess", "assessment"), ("capabilities", "capabilities")):
            request = {"id": "prior-" + action, "caseId": "site-021", "action": action}
            host.send(request)
            late.append({"id": request["id"], "caseId": request["caseId"], "type": kind,
                         **({"live": True, "targetLanguage": "zh-CN"} if kind == "capabilities" else {}),
                         **({"packet": "{}"} if kind == "packet" else {})})
        host.backlog.extend(late)
        host.capabilities("site-022")
        self.assertEqual([host.poll() for _ in late], late)

    def test_known_prior_identity_does_not_waive_malformed_packet_or_wrong_event_type(self):
        for fields, code in (({"type": "packet", "packet": "broken JSON"}, "host_invalid_packet"),
                             ({"type": "assessment"}, "host_uncorrelated_event")):
            host, _ = self.host(lambda r: [self.reply(r)] if r["action"] == "capabilities" else [])
            host.send(self.prior_request())
            event = {"id": "site-021-rpc-2535", "caseId": "site-021", **fields}
            host.backlog.append(event)
            with self.assertRaisesRegex(capture.CaptureError, code):
                host.capabilities("site-022")
            self.assertEqual(host.poll(), event)
