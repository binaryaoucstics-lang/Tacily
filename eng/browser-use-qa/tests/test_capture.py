import copy
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("capture", HERE.parent / "capture.py")
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)
sys.modules["capture"] = capture
review_spec = importlib.util.spec_from_file_location("qa_review", HERE.parent / "review.py")
review = importlib.util.module_from_spec(review_spec)
review_spec.loader.exec_module(review)
SITE = {"id": "site-001", "registeredDomain": "example.org", "url": "https://www.example.org/article",
        "category": "reference", "language": "en", "expectedPageTranslation": True, "split": "holdout"}


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="qa-test-", dir=HERE)
        self.root = Path(self.temp.name)
        self.journal = capture.Journal(self.root / "log.jsonl")

    def tearDown(self):
        self.temp.cleanup()


def windows_replace_error(source, destination, winerror=5):
    error = PermissionError(13, "synthetic closed-file replace conflict", str(source))
    error.filename2 = str(destination)
    error.winerror = winerror
    return error


class AtomicEvidenceTests(Fixture):
    def test_transient_replace_conflict_retries_closed_flushed_file_and_audits(self):
        destination = self.root / "case.json"
        destination.write_text('{"old":true}', encoding="utf-8")
        replace = capture.os.replace
        calls, diagnostics = [], []
        def conflict(source, target):
            calls.append((source, target))
            # Reopening for read/write checks that the writer was already closed;
            # the full serialized value must be present before retrying rename.
            with Path(source).open("r+", encoding="utf-8") as stream:
                self.assertEqual(json.load(stream), {"new": "中文😀"})
            if len(calls) <= 2:
                raise windows_replace_error(source, target, 5 if len(calls) == 1 else 32)
            replace(source, target)
        with mock.patch.object(capture.os, "replace", conflict), mock.patch.object(capture.time, "sleep") as sleep:
            capture.atomic_json(destination, {"new": "中文😀"}, on_retry=diagnostics.append)
        self.assertEqual(len(calls), 3)
        self.assertEqual(len({str(call[0]) for call in calls}), 1)
        self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), {"new": "中文😀"})
        self.assertEqual([event["winerror"] for event in diagnostics], [5, 32])
        self.assertEqual([event["operation"] for event in diagnostics], ["replace", "replace"])
        self.assertEqual(sleep.call_count, 2)
        self.assertFalse(list(self.root.glob("case.json.*.tmp")))

    def test_exhausted_conflict_preserves_previous_json_and_failed_temporary(self):
        destination = self.root / "case.json"
        destination.write_text('{"old":true}', encoding="utf-8")
        diagnostics, calls = [], []
        def denied(source, target):
            calls.append((source, target))
            raise windows_replace_error(source, target)
        with mock.patch.object(capture.os, "replace", denied), \
                mock.patch.object(capture.time, "sleep") as sleep:
            with self.assertRaises(PermissionError):
                capture.atomic_json(destination, {"pending": 1}, on_retry=diagnostics.append)
            self.assertEqual(len(calls), 4)
            self.assertEqual(sleep.call_count, 3)
        self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), {"old": True})
        temporary = next(self.root.glob("case.json.*.tmp"))
        self.assertEqual(json.loads(temporary.read_text(encoding="utf-8")), {"pending": 1})
        capture.atomic_json(destination, {"next": 2})
        self.assertEqual(json.loads(temporary.read_text(encoding="utf-8")), {"pending": 1})
        self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), {"next": 2})

    def test_no_retry_for_non_windows_permission_or_file_open_denial(self):
        destination = self.root / "case.json"
        with mock.patch.object(capture.os, "replace", side_effect=PermissionError(13, "denied")) as replace, \
                mock.patch.object(capture.time, "sleep") as sleep:
            with self.assertRaises(PermissionError):
                capture.atomic_json(destination, {"failed": True})
            self.assertEqual(replace.call_count, 1)
            sleep.assert_not_called()
        with mock.patch.object(Path, "open", side_effect=windows_replace_error(destination, destination)), \
                mock.patch.object(capture.os, "replace") as replace, mock.patch.object(capture.time, "sleep") as sleep:
            with self.assertRaises(PermissionError):
                capture.atomic_json(destination, {})
            replace.assert_not_called()
            sleep.assert_not_called()

    @unittest.skipUnless(os.name == "nt", "Actual Windows handle-sharing regression")
    def test_actual_windows_destination_handle_conflict_is_released_before_retry(self):
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                                      wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        destination = self.root / "case.json"
        destination.write_text('{"old":true}', encoding="utf-8")
        # Permit read/write sharing, deliberately omit FILE_SHARE_DELETE.
        handle = kernel.CreateFileW(str(destination), 0x80000000, 3, None, 3, 0x80, None)
        self.assertNotEqual(handle, wintypes.HANDLE(-1).value, ctypes.get_last_error())
        diagnostics = []
        def release(event):
            nonlocal handle
            diagnostics.append(event)
            self.journal.append(event)
            self.assertTrue(kernel.CloseHandle(handle))
            handle = None
        try:
            capture.atomic_json(destination, {"new": True}, on_retry=release)
        finally:
            if handle is not None:
                kernel.CloseHandle(handle)
        self.assertEqual(len(diagnostics), 1)
        self.assertIn(diagnostics[0]["winerror"], (5, 32, 33))
        self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), {"new": True})
        self.assertIn('"operation":"replace"', self.journal.path.read_text(encoding="utf-8"))


class CliTests(Fixture):
    def test_only_wrapper_list_arguments_and_json_layouts(self):
        calls = []
        def runner(argv, **kwargs):
            calls.append((argv, kwargs))
            data = {"result": {"x": 1}} if argv[-2] == "eval" else {"_raw_text": "Observed CLI state"}
            return subprocess.CompletedProcess(argv, 0, json.dumps({"success": True, "data": data}), "")
        cli = capture.BrowserCli("offline-test", self.journal, runner)
        self.assertEqual(cli.evaluate("({x:1})"), {"x": 1})
        self.assertEqual(cli.state(), {"_raw_text": "Observed CLI state"})
        for argv, kwargs in calls:
            self.assertIsInstance(argv, list)
            self.assertEqual(Path(argv[1]), capture.WRAPPER)
            self.assertEqual(argv[2:6], ["--session", "offline-test", "--json", argv[5]])
            self.assertIs(kwargs["shell"], False)
        self.assertIn("Observed CLI state", self.journal.path.read_text(encoding="utf-8"))

    def test_eval_result_string_and_error_fail_closed(self):
        def runner(argv, **kwargs):
            return subprocess.CompletedProcess(argv, 0, '{"data":{"result":"{\\"ok\\":true}"}}', "")
        self.assertEqual(capture.BrowserCli("test", self.journal, runner).evaluate("true"), {"ok": True})
        for result in [subprocess.CompletedProcess([], 3, "{}", "error"),
                       subprocess.CompletedProcess([], 0, '{"success":false}', ""),
                       subprocess.CompletedProcess([], 0, 'not json', "")]:
            with self.assertRaises(capture.CaptureError):
                capture.BrowserCli("test", self.journal, lambda *a, **k: result).state()

    def test_chunk_injection_reconstructs_exact_production_sources(self):
        class ChunkCli(capture.BrowserCli):
            def __init__(self):
                self.parts, self.expressions = [], []
            def evaluate(self, expression):
                self.expressions.append(expression)
                if ".push(" in expression:
                    self.parts.append(json.loads(expression.split(".push(", 1)[1].rsplit(");true", 1)[0]))
                return True
        cli = ChunkCli()
        repo = HERE.parents[2]
        for name in capture.PRODUCTION:
            cli.parts = []
            source = (repo / "browser-extension" / name).read_text(encoding="utf-8")
            cli.inject(source)
            self.assertEqual("".join(cli.parts), source)
        self.assertTrue(all(len(value.encode("utf-16-le")) <= capture.EVAL_LIMIT_BYTES for value in cli.expressions))

    def test_serialized_chunk_budget_handles_escapes_unicode_and_boundaries(self):
        expressions = []
        cli = capture.BrowserCli("test", self.journal)
        cli.evaluate = lambda expression: expressions.append(expression)
        prefix, suffix = "globalThis.__qaSourceChunks.push(", ");true"
        sources = ["a" * 24001, "中文" * 9000, "😀𠮷" * 9000,
                   ('"\\\n\r\t\x00' * 4000), ('中文😀"\\\x00' * 5000), ""]
        for source in sources:
            with self.subTest(source_kind=source[:10]):
                expressions.clear()
                cli.inject(source)
                pushes = [value for value in expressions if value.startswith(prefix)]
                parts = [json.loads(value[len(prefix):-len(suffix)]) for value in pushes]
                self.assertEqual("".join(parts), source)
                self.assertTrue(all(len(value.encode("utf-16-le")) <= 20000 for value in expressions))
                self.assertTrue(all(len(part) <= 8000 for part in parts))
                offset = 0
                for part in parts[:-1]:
                    offset += len(part)
                    if len(part) < 8000:
                        bigger = prefix + capture.encode(part + source[offset]) + suffix
                        self.assertGreater(len(bigger.encode("utf-16-le")), 20000)
                if source == "a" * 24001 or source == "中文" * 9000:
                    self.assertEqual(len(parts[0]), 8000)
        # Exact eval limit is allowed; the next UTF-16 code unit is rejected
        # before invoking even the mock CLI process runner.
        calls = []
        def runner(argv, **kwargs):
            calls.append(argv)
            return subprocess.CompletedProcess(argv, 0, '{"data":{"result":true}}', "")
        cli = capture.BrowserCli("test", self.journal, runner)
        self.assertTrue(cli.evaluate("a" * 10000))
        with self.assertRaisesRegex(capture.CaptureError, "eval_argument_too_large"):
            cli.evaluate("a" * 10001)
        self.assertEqual(len(calls), 1)

    def probe_cli(self, states):
        calls = []
        states = copy.deepcopy(states)
        def runner(argv, **kwargs):
            expression = argv[-1]
            calls.append(expression)
            if expression.startswith("(async") or "extractPageSnapshot" in expression:
                value = {}  # Actual CLI behavior: unresolved Promise serializes empty.
            elif ".startProbe(" in expression:
                value = {"id": "probe-1", "stage": "running"}
            elif ".probeStatus(" in expression:
                value = states.pop(0) if len(states) > 1 else states[0]
            else:
                self.fail("unexpected eval: " + expression)
            self.assertIs(kwargs["shell"], False)
            return subprocess.CompletedProcess(argv, 0, capture.encode({"data": {"result": value}}), "")
        return capture.BrowserCli("probe-test", self.journal, runner), calls

    def test_promise_empty_object_is_not_probe_evidence_and_completed_job_is_polled(self):
        evidence = {"samples": [{"text": "合成测试"}], "statistics": {"textCharacters": 4}}
        cli, calls = self.probe_cli([{"id": "probe-1", "stage": "running", "result": None},
                                    {"id": "probe-1", "stage": "completed", "result": evidence}])
        self.assertEqual(cli.evaluate("(async()=>({samples:[],statistics:{}}))()"), {})
        self.assertEqual(cli.probe("zh-CN", poll_seconds=0), evidence)
        self.assertEqual(sum(".probeStatus(" in value for value in calls), 2)
        self.assertTrue(all("extractPageSnapshot" not in value and "async" not in value for value in calls[1:]))
        rows = [json.loads(line) for line in self.journal.path.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(json.loads(rows[-1]["stdout"])["data"]["result"]["stage"], "completed")

    def test_failed_empty_or_malformed_probe_cannot_supply_evidence(self):
        for state, error in [({"id": "probe-1", "stage": "failed", "result": None}, "probe_failed"),
                             ({}, "probe_invalid_status"),
                             ({"id": "other", "stage": "completed"}, "probe_invalid_status"),
                             ({"id": "probe-1", "stage": "unknown"}, "probe_invalid_stage")]:
            with self.subTest(state=state):
                cli, _ = self.probe_cli([state])
                with self.assertRaisesRegex(capture.CaptureError, error):
                    cli.probe("zh-CN")
        for result in [{}, None, {"samples": []}, {"statistics": {}},
                       {"samples": {}, "statistics": {}}, {"samples": [], "statistics": []}]:
            cli, _ = self.probe_cli([{"id": "probe-1", "stage": "completed", "result": result}])
            with self.subTest(result=result), self.assertRaisesRegex(capture.CaptureError, "probe_invalid_evidence"):
                cli.probe("zh-CN")
        cli = capture.BrowserCli("test", self.journal,
            lambda argv, **kwargs: subprocess.CompletedProcess(argv, 0, '{"data":{"result":{}}}', ""))
        with self.assertRaisesRegex(capture.CaptureError, "probe_invalid_start"):
            cli.probe("zh-CN")

    def test_probe_timeout_has_no_completed_result(self):
        cli, calls = self.probe_cli([{"id": "probe-1", "stage": "running", "result": None}])
        clock = [0.0]
        def sleep(seconds):
            clock[0] += seconds
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", sleep):
            with self.assertRaisesRegex(capture.CaptureError, "probe_timeout"):
                cli.probe("zh-CN", timeout=.3, poll_seconds=.1)
        self.assertEqual(sum(".probeStatus(" in value for value in calls), 3)

    def test_missing_screenshot_and_timeout_not_success(self):
        cli = capture.BrowserCli("test", self.journal,
            lambda *a, **k: subprocess.CompletedProcess([], 0, '{"data":{}}', ""))
        with self.assertRaisesRegex(capture.CaptureError, "screenshot_missing"):
            cli.screenshot(self.root / "absent.png")
        def timeout(*a, **k):
            raise subprocess.TimeoutExpired(a[0], 1)
        with self.assertRaisesRegex(capture.CaptureError, "timeout"):
            capture.BrowserCli("test", self.journal, timeout).state()


class ManifestAndResumeTests(Fixture):
    def manifest(self, sites):
        file = self.root / "manifest.json"
        file.write_text(json.dumps(sites), encoding="utf-8")
        return file

    def test_freeze_manifest_hash_and_unique_domain(self):
        file = self.manifest([SITE])
        sites, digest = capture.read_manifest(file)
        self.assertEqual(sites, [SITE]); self.assertEqual(len(digest), 64)
        self.assertTrue(capture.belongs_to("https://www.example.org/a", "example.org"))
        self.assertFalse(capture.belongs_to("https://example.org.evil.invalid/a", "example.org"))
        self.assertFalse(capture.belongs_to("https://user:secret@example.org", "example.org"))
        for bad in [[SITE, {**SITE, "id": "another"}], [{**SITE, "id": "../escape"}],
                    [{**SITE, "expectedPageTranslation": "true"}], [{**SITE, "url": "https://evil.invalid"}]]:
            with self.assertRaises(capture.CaptureError):
                capture.read_manifest(self.manifest(bad))

    def test_resume_preserves_history_and_rejects_new_head(self):
        frozen = {"sourceHead": "a" * 40, "manifestHash": "b" * 64}
        run = capture.EvidenceRun(self.root, "run-1", frozen)
        one = run.attempt(SITE["id"])
        capture.atomic_json(one / "case.json", {"captureFinished": True, "evidenceReady": False})
        resumed = capture.EvidenceRun(self.root, "run-1", frozen, True)
        self.assertFalse(resumed.captured(SITE["id"]))
        two = resumed.attempt(SITE["id"])
        self.assertNotEqual(one, two); self.assertTrue((one / "case.json").is_file())
        capture.atomic_json(two / "case.json", {"captureFinished": True, "evidenceReady": True, "completed": False})
        self.assertTrue(resumed.captured(SITE["id"]))
        with self.assertRaisesRegex(capture.CaptureError, "resume_source"):
            capture.EvidenceRun(self.root, "run-1", {**frozen, "sourceHead": "c" * 40}, True)
        with self.assertRaises(FileExistsError):
            capture.EvidenceRun(self.root, "run-1", frozen)


class CompletionTests(Fixture):
    def valid(self, expected=True):
        phases = ["before", "after", "stable", "scroll-before", "scroll-after", "return-after", "reflow-after"]
        for phase in phases:
            (self.root / (phase + ".png")).write_bytes(b"synthetic fixture image")
        return {**SITE, "expectedPageTranslation": expected, "visitSuccess": True, "domainVerified": True,
                "errors": [], "pageDecision": expected, "sourceNodes": [{"id": "source-1"}], "settled": True,
                "liveTranslation": True, "simulation": False, "requestIds": ["r1"] if expected else [],
                "terminals": [{"id": "r1", "success": True, "requestCount": 1}] if expected else [],
                "predictions": [{"sourceId": "source-1", "completed": True, "failed": False}],
                "screenshots": [{"phase": phase, "path": phase + ".png"} for phase in phases],
                "screenshotReview": {phase: True for phase in phases}}

    def test_completion_requires_live_request_terminal_nodes_and_review(self):
        case = self.valid()
        self.assertEqual(capture.completion_gate(case, self.root)[:2], (True, True))
        for patch in [{"liveTranslation": False}, {"terminals": []}, {"simulation": True},
                      {"errors": [{"code": "quota"}]}, {"visitSuccess": False}, {"pageDecision": None},
                      {"predictions": [{"completed": False}]}, {"screenshots": []}, {"settled": False},
                      {"terminals": [{"id": "r1", "success": True, "requestCount": 0}]}]:
            self.assertEqual(capture.completion_gate({**case, **patch}, self.root)[:2], (False, False))
        ready, completed, reasons = capture.completion_gate({**case, "screenshotReview": {}}, self.root)
        self.assertTrue(ready); self.assertFalse(completed); self.assertIn("screenshot_review_pending", reasons)

    def test_target_language_page_requires_zero_model_requests(self):
        case = self.valid(False)
        self.assertTrue(capture.completion_gate(case, self.root)[1])
        self.assertFalse(capture.completion_gate({**case, "requestIds": ["unnecessary"]}, self.root)[1])

    def test_predictions_without_independent_source_evidence_cannot_complete(self):
        case = self.valid()
        for prediction in [
            {"sourceId": "never-visible", "completed": True},
            {"sourceId": "source-1", "completed": True, "sourceEvidence": "unmatched",
             "auditIssue": "source_not_independently_visible"},
        ]:
            with self.subTest(prediction=prediction):
                ready, completed, reasons = capture.completion_gate(
                    {**case, "predictions": [prediction]}, self.root)
                self.assertFalse(ready)
                self.assertFalse(completed)
                self.assertIn("prediction_source_evidence_unmatched", reasons)

    def test_review_checks_hashes_retains_unknown_labels_and_does_not_overwrite(self):
        import hashlib
        case = self.valid()
        for shot in case["screenshots"]:
            shot["sha256"] = hashlib.sha256((self.root / shot["path"]).read_bytes()).hexdigest()
        case["screenshotReview"] = {}
        case_path = self.root / "case.json"
        capture.atomic_json(case_path, case)
        raw = case_path.read_bytes()
        review_path = self.root / "human.json"
        independent = {"caseSha256": hashlib.sha256(raw).hexdigest(),
                       "screenshots": {shot["phase"]: True for shot in case["screenshots"]}, "annotations": []}
        capture.atomic_json(review_path, independent)
        output = self.root / "reviewed-001.json"
        result = review.apply_review(case_path, review_path, output)
        self.assertTrue(result["completed"])
        self.assertEqual(result["annotations"], [{"sourceId": "source-1", "needTranslation": "unknown"}])
        self.assertEqual(case_path.read_bytes(), raw)
        with self.assertRaisesRegex(capture.CaptureError, "new_file"):
            review.apply_review(case_path, review_path, output)
        (self.root / "stable.png").write_bytes(b"tampered")
        with self.assertRaisesRegex(capture.CaptureError, "screenshot_hash"):
            review.apply_review(case_path, review_path, self.root / "reviewed-002.json")

    def test_review_cannot_promote_simulation(self):
        import hashlib
        case = self.valid()
        case["simulation"] = True
        for shot in case["screenshots"]:
            shot["sha256"] = hashlib.sha256((self.root / shot["path"]).read_bytes()).hexdigest()
        case_path = self.root / "case.json"
        capture.atomic_json(case_path, case)
        independent = {"caseSha256": hashlib.sha256(case_path.read_bytes()).hexdigest(),
                       "screenshots": case["screenshotReview"], "annotations": []}
        review_path = self.root / "human.json"
        capture.atomic_json(review_path, independent)
        self.assertFalse(review.apply_review(case_path, review_path, self.root / "reviewed.json")["completed"])


class HostTests(Fixture):
    def test_jsonl_process_is_offline_and_correlated(self):
        host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
        try:
            self.assertIs(host.capabilities("c1")["live"], False)
            host.send({"schemaVersion": 1, "id": "r1", "caseId": "c1", "action": "message",
                       "message": {"requestId": "req", "documentToken": "doc", "generation": 1, "revision": 1,
                                   "blocks": [{"id": "b1", "text": "Synthetic source"}]}})
            events = [host.poll(2) for _ in range(3)]
            self.assertEqual([event["type"] for event in events], ["packet", "packet", "terminal"])
            self.assertTrue(all(event["id"] == "r1" and event["caseId"] == "c1" for event in events))
            self.assertEqual(events[-1]["requestCount"], 0)
        finally:
            host.close()
        self.assertIn('host_request', self.journal.path.read_text(encoding="utf-8"))

    def test_strict_host_rejects_empty_identity_and_duplicate_requests(self):
        host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
        try:
            for key in ("id", "caseId"):
                request = {"schemaVersion": 1, "id": "empty-" + key, "caseId": "c1", "action": "capabilities"}
                request[key] = ""
                host.send(request)
                self.assertEqual(host.poll(2)["code"], "invalid_identity")
            request = {"schemaVersion": 1, "id": "duplicate", "caseId": "c1", "action": "capabilities"}
            host.send(request)
            self.assertEqual(host.poll(2)["type"], "capabilities")
            host.send(request)
            self.assertEqual(host.poll(2)["code"], "duplicate_id")
            responses = [host.capabilities(case_id) for case_id in ("c1", "c2", "c1")]
            self.assertEqual([event["caseId"] for event in responses], ["c1", "c2", "c1"])
            self.assertEqual(len({event["id"] for event in responses}), 3)
            for case_id in ("", None, "../outside"):
                with self.assertRaisesRegex(capture.CaptureError, "invalid_capabilities_case_id"):
                    host.capabilities(case_id)
        finally:
            host.close()
        self.assertTrue(all(not thread.is_alive() for thread in host.threads))
        self.assertTrue(all(stream.closed for stream in (host.process.stdin, host.process.stdout, host.process.stderr)))

    def test_capabilities_rejects_uncorrelated_response_identity(self):
        for flag in ("--wrong-capabilities-id", "--wrong-capabilities-case"):
            with self.subTest(flag=flag):
                host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py"), flag], self.journal)
                try:
                    with self.assertRaisesRegex(capture.CaptureError, "capabilities_identity_mismatch"):
                        host.capabilities("c1")
                finally:
                    host.close()

    def test_rejects_shell_command_string(self):
        with self.assertRaises(capture.CaptureError):
            capture.JsonlHost("host.exe --live", self.journal)


class WorkflowTests(Fixture):
    def browser(self, failure=None, skipped=False, probe_states=None):
        class MockBrowser:
            probe = capture.BrowserCli.probe
            def __init__(self):
                self.configured = False
                self.outgoing = []
                self.delivered = []
                self.calls = []
                self.done = False
                self.journal = None
                self.predictions = [{"id": "b1", "sourceId": "s1", "eligible": True, "completed": False, "failed": False}]
                self.probe_states = copy.deepcopy(probe_states)
            def command(self, *args):
                self.calls.append(args)
                if failure == "visit":
                    raise capture.CaptureError("cli_access_denied")
            def state(self):
                return {"_raw_text": "SYNTHETIC OFFLINE"}
            def inject(self, source):
                self.calls.append(("inject", len(source)))
            def evaluate(self, expression):
                self.calls.append(("eval", expression))
                if "chrome?.runtime" in expression or "querySelector('details" in expression:
                    return False
                return True
            def screenshot(self, path):
                self.calls.append(("screenshot", Path(path).name))
                Path(path).write_bytes(b"SYNTHETIC OFFLINE SCREENSHOT FIXTURE")
            def qa(self, method, *args):
                self.calls.append((method, *args))
                if method == "configure":
                    self.configured = True
                    if skipped:
                        self.done = True
                        self.predictions = []
                        return True
                    self.outgoing.append({"rpcId": "rpc-1", "message": {"type": "pageAutoCandidate", "requestId": "req1",
                        "documentToken": "doc1", "generation": 1, "revision": 1, "documentLanguage": "en",
                        "blocks": [{"id": "b1", "text": "Synthetic source"}], "samples": [], "pageSummary": {}}})
                    if failure == "cancel":
                        self.outgoing.append({"rpcId": "rpc-2", "message": {"type": "pageAutoCancel", "requestId": "req1",
                            "documentToken": "doc1", "generation": 1, "revision": 1, "reason": "source_changed"}})
                    return True
                if method == "sample":
                    return {"finalUrl": SITE["url"], "title": "SYNTHETIC OFFLINE", "viewport": {"width": 800, "height": 600},
                            "sourceNodes": [{"id": "s1", "sourceText": "Synthetic source"}], "nodes": [{"id": "s1"}],
                            "predictions": copy.deepcopy(self.predictions) if self.configured else [],
                            "pageDecision": not skipped if self.done else None, "events": [], "jobs": 0 if self.done else int(self.configured),
                            "pending": 0, "scanning": False, "rpcPending": 0,
                            "status": {"state": ("skipped" if skipped else "completed") if self.done else "assessing"}}
                if method == "startProbe":
                    return {"id": "probe-1", "stage": "running"}
                if method == "probeStatus":
                    return self.probe_states.pop(0) if len(self.probe_states) > 1 else self.probe_states[0]
                if method == "drain":
                    value, self.outgoing = self.outgoing, []
                    return value
                if method == "deliver":
                    self.delivered.append(args[1])
                    packet = json.loads(args[1])
                    if packet.get("finished"):
                        self.done = True; self.predictions[0]["completed"] = True
                    return True
                return True
        return MockBrowser()

    def test_skip_assess_only_after_completed_probe_and_failures_are_recoverable(self):
        evidence = {"samples": [{"text": "合成负例"}], "statistics": {"textCharacters": 4}}
        states = [("completed", [{"id": "probe-1", "stage": "running"},
                                 {"id": "probe-1", "stage": "completed", "result": evidence}], None),
                  ("failed", [{"id": "probe-1", "stage": "failed"}], "probe_failed"),
                  ("empty", [{"id": "probe-1", "stage": "completed", "result": {}}], "probe_invalid_evidence"),
                  ("timeout", [{"id": "probe-1", "stage": "running"}], "probe_timeout")]
        for name, sequence, error in states:
            with self.subTest(name=name):
                run = capture.EvidenceRun(self.root, "probe-" + name, {"sourceHead": "a" * 40})
                host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
                try:
                    result = capture.CaseCapture(self.browser(skipped=True, probe_states=sequence), host, run,
                        HERE.parents[2], case_timeout=.4 if name == "timeout" else 5,
                        stable_seconds=.001, poll_seconds=.002).capture({**SITE, "expectedPageTranslation": False})
                finally:
                    host.close()
                case_path = next(run.path.glob("*/attempt-*/case.json"))
                log = case_path.with_name("events.jsonl").read_text(encoding="utf-8")
                requests = [json.loads(line)["request"] for line in log.splitlines()
                            if json.loads(line).get("kind") == "host_request"]
                assessments = [request for request in requests if request["action"] == "assess"]
                if error:
                    self.assertEqual(assessments, [])
                    self.assertEqual(result["errors"][0]["code"], error)
                    self.assertFalse(result["settled"])
                    self.assertFalse(run.captured(SITE["id"]))
                else:
                    self.assertEqual(result["errors"], [])
                    self.assertEqual(len(assessments), 1)
                    self.assertEqual(assessments[0]["samples"], evidence["samples"])
                    self.assertEqual(assessments[0]["statistics"], evidence["statistics"])
                    self.assertIn("localAssessment", result)
                    self.assertTrue(result["settled"])
                self.assertEqual(json.loads(case_path.read_text(encoding="utf-8"))["errors"], result["errors"])
                self.assertTrue(result["simulation"])
                self.assertFalse(result["completed"])
                self.assertFalse(result["evidenceReady"])

    def test_offline_full_case_preserves_frames_and_all_screenshot_stages(self):
        run = capture.EvidenceRun(self.root, "mock-run", {"sourceHead": "a" * 40, "manifestHash": "b" * 64})
        browser = self.browser()
        host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
        try:
            result = capture.CaseCapture(browser, host, run, HERE.parents[2], case_timeout=5,
                                         stable_seconds=.001, poll_seconds=.002).capture(SITE)
        finally:
            host.close()
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertTrue(result["captureFinished"])
        self.assertFalse(result["liveTranslation"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])
        self.assertTrue(result["simulation"])
        self.assertEqual(len(browser.delivered), 2)
        self.assertEqual(json.loads(browser.delivered[-1])["type"], "pageAutoTranslations")
        self.assertEqual([shot["phase"] for shot in result["screenshots"]],
                         ["before", "after", "scroll-before", "scroll-after", "return-after", "reflow-after", "stable"])
        log = next(run.path.glob("*/attempt-*/events.jsonl")).read_text(encoding="utf-8")
        request_events = [json.loads(line)["request"] for line in log.splitlines() if json.loads(line).get("kind") == "host_request"]
        message = next(request["message"] for request in request_events if request["action"] == "message")
        self.assertEqual(message["blocks"], [{"id": "b1", "text": "Synthetic source"}])
        self.assertEqual(message["frameId"], 0)
        self.assertEqual(result["annotations"], [{"sourceId": "s1", "needTranslation": "unknown"}])

    def test_same_strict_host_captures_two_consecutive_sites(self):
        run = capture.EvidenceRun(self.root, "two-sites", {"sourceHead": "a" * 40})
        host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
        results = []
        try:
            for site in (SITE, {**SITE, "id": "site-002"}):
                results.append(capture.CaseCapture(self.browser(), host, run, HERE.parents[2], case_timeout=5,
                                                   stable_seconds=.001, poll_seconds=.002).capture(site))
        finally:
            host.close()
        self.assertNotEqual(results[0]["capabilities"]["id"], results[1]["capabilities"]["id"])
        for result in results:
            self.assertEqual(result["errors"], [])
            self.assertTrue(result["settled"])
            self.assertTrue(result["captureFinished"])
            self.assertFalse(result["completed"])
            self.assertFalse(result["evidenceReady"])
            self.assertTrue(result["simulation"])
            self.assertEqual(result["capabilities"]["caseId"], result["id"])
            log_path = next((run.path / result["id"]).glob("attempt-*/events.jsonl"))
            requests = [row["request"] for row in map(json.loads, log_path.read_text(encoding="utf-8").splitlines())
                        if row["kind"] == "host_request"]
            self.assertEqual(requests[0]["action"], "capabilities")
            self.assertTrue(all(row["caseId"] == result["id"] for row in requests))

    def test_case_replace_failure_has_trace_and_remains_recoverable(self):
        run = capture.EvidenceRun(self.root, "replace-failure", {"sourceHead": "a" * 40})
        host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
        replace = capture.os.replace
        calls = []
        def fail_one_save(source, destination):
            calls.append((source, destination))
            # Initial checkpoint succeeds. Exhaust the next save's bounded
            # replacement attempts; later failure checkpoints can be persisted.
            if 2 <= len(calls) <= 5:
                raise windows_replace_error(source, destination)
            replace(source, destination)
        try:
            with mock.patch.object(capture.os, "replace", fail_one_save):
                result = capture.CaseCapture(self.browser(), host, run, HERE.parents[2], case_timeout=5,
                                             stable_seconds=.001, poll_seconds=.002).capture(SITE)
        finally:
            host.close()
        self.assertFalse(result["completed"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["settled"])
        self.assertFalse(run.captured(SITE["id"]))
        self.assertEqual(result["errors"][0]["code"], "PermissionError")
        self.assertEqual(result["errors"][0]["winerror"], 5)
        self.assertTrue(any(row["function"] == "atomic_json" for row in result["errors"][0]["trace"]))
        case_path = next(run.path.glob("*/attempt-*/case.json"))
        self.assertEqual(json.loads(case_path.read_text(encoding="utf-8"))["errors"], result["errors"])
        self.assertTrue(list(case_path.parent.glob("case.json.*.tmp")))
        events = list(map(json.loads, case_path.with_name("events.jsonl").read_text(encoding="utf-8").splitlines()))
        self.assertEqual(len([row for row in events if row["kind"] == "evidence_io_retry"]), 3)

    def test_access_failure_saved_as_recoverable_failure(self):
        run = capture.EvidenceRun(self.root, "mock-failure", {"sourceHead": "a" * 40})
        host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
        try:
            result = capture.CaseCapture(self.browser("visit"), host, run, HERE.parents[2]).capture(SITE)
        finally:
            host.close()
        self.assertFalse(result["completed"]); self.assertFalse(result["visitSuccess"])
        self.assertEqual(result["errors"][0]["code"], "cli_access_denied")
        self.assertFalse(run.captured(SITE["id"]))
        self.assertTrue(next(run.path.glob("*/attempt-*/case.json")).is_file())

    def test_cancel_targets_original_host_request_id(self):
        run = capture.EvidenceRun(self.root, "mock-cancel", {"sourceHead": "a" * 40})
        host = capture.JsonlHost([sys.executable, str(HERE / "mock_host.py")], self.journal)
        try:
            result = capture.CaseCapture(self.browser("cancel"), host, run, HERE.parents[2], case_timeout=5,
                                         stable_seconds=.001, poll_seconds=.002).capture(SITE)
        finally:
            host.close()
        self.assertEqual(result["errors"], [])
        log = next(run.path.glob("*/attempt-*/events.jsonl")).read_text(encoding="utf-8")
        requests = [json.loads(line)["request"] for line in log.splitlines() if json.loads(line).get("kind") == "host_request"]
        original = next(request for request in requests if request["action"] == "message")
        cancelled = next(request for request in requests if request["action"] == "cancel")
        self.assertEqual(original["id"], cancelled["id"])
        self.assertEqual(cancelled["message"]["requestId"], original["message"]["requestId"])
        self.assertFalse(result["completed"])


class AdapterTests(unittest.TestCase):
    def test_synthetic_dom_rpc_retractions_and_listener_interfaces(self):
        node = shutil.which("node")
        self.assertIsNotNone(node, "Node is required for the offline JavaScript adapter checks")
        result = subprocess.run([node, str(HERE / "adapter-check.js")], shell=False,
                                capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
