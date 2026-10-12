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
    def test_drain_sample_is_one_synchronous_wrapper_call_and_keeps_pending_snapshot(self):
        calls = []
        node = shutil.which("node")
        self.assertIsNotNone(node)
        script = """
const expression = require('node:fs').readFileSync(0, 'utf8');
let pending = 1;
globalThis.__websiteQa = {
  sample: phase => ({phase, rpcPending: pending}),
  drain: () => { pending = 0; return [{rpcId:'r', message:{type:'pageAutoCandidate'}}]; }
};
process.stdout.write(JSON.stringify({data:{result:eval(expression)}}));
"""
        def runner(argv, **kwargs):
            calls.append(argv)
            self.assertEqual(Path(argv[1]), capture.WRAPPER)
            self.assertIs(kwargs["shell"], False)
            return subprocess.run([node, "-e", script], input=argv[-1], capture_output=True,
                                  text=True, encoding="utf-8", timeout=10)
        cli = capture.BrowserCli("combined-test", self.journal, runner)
        value = cli.drain_sample('after"中文😀')
        self.assertEqual(value["sample"], {"phase": 'after"中文😀', "rpcPending": 1})
        self.assertEqual(value["outbound"][0]["rpcId"], "r")
        self.assertEqual(len(calls), 1)
        for result in ({}, {"sample": {}, "outbound": {}}, {"sample": None, "outbound": []}):
            cli.runner = lambda argv, **kwargs: subprocess.CompletedProcess(argv, 0,
                capture.encode({"data": {"result": result}}), "")
            with self.assertRaisesRegex(capture.CaptureError, "adapter_lost_or_invalid"):
                cli.drain_sample("after")

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

    def test_full_screenshot_uses_same_wrapper_and_missing_file_check(self):
        calls = []
        def runner(argv, **kwargs):
            calls.append((argv, kwargs))
            self.assertEqual(argv[-3:], ["screenshot", str(self.root / "full.png"), "--full"])
            return subprocess.CompletedProcess(argv, 0, '{"success":true,"data":{}}', "")
        cli = capture.BrowserCli("full-test", self.journal, runner)
        with self.assertRaisesRegex(capture.CaptureError, "screenshot_missing"):
            cli.screenshot(self.root / "full.png", full=True)
        (self.root / "full.png").write_bytes(b"OFFLINE FULL SCREENSHOT")
        cli.screenshot(self.root / "full.png", full=True)
        self.assertEqual(len(calls), 2)
        self.assertTrue(all(Path(argv[1]) == capture.WRAPPER and kwargs["shell"] is False
                            for argv, kwargs in calls))


class BatchDeliveryTests(Fixture):
    def collecting_cli(self, result=True):
        expressions = []
        def runner(argv, **kwargs):
            self.assertEqual(Path(argv[1]), capture.WRAPPER)
            self.assertEqual(argv[-2], "eval")
            self.assertIs(kwargs["shell"], False)
            expressions.append(argv[-1])
            return subprocess.CompletedProcess(argv, 0, capture.encode({"data": {"result": result}}), "")
        return capture.BrowserCli("batch-test", self.journal, runner), expressions

    def execute_expressions(self, expressions, fail_packet=None):
        # Execute the actual transport expressions in an offline JS context;
        # no DOM, browser, provider or network is involved.
        node = shutil.which("node")
        self.assertIsNotNone(node, "Node is required for offline batch expression checks")
        script = """
const vm = require('node:vm');
const input = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
const calls = [];
const context = vm.createContext({__websiteQa: {
  acknowledge: (rpcId, response) => { calls.push(['acknowledge', rpcId, response]); return true; },
  deliver: (rpcId, packet) => {
    calls.push(['deliver', rpcId, packet]);
    if (packet === input.failPacket) throw new Error('synthetic_delivery_failure');
    return false;
  }
}});
let error = null;
for (const expression of input.expressions) {
  try { vm.runInContext(expression, context); }
  catch (failure) { error = failure.message; break; }
}
process.stdout.write(JSON.stringify({calls, error,
  stored: Object.hasOwn(context, '__qaFrameBatch'),
  chunks: Object.hasOwn(context, '__qaSourceChunks'), injected: context.injected === true}));
"""
        result = subprocess.run([node, "-e", script], shell=False, capture_output=True,
                                input=capture.encode({"expressions": expressions, "failPacket": fail_packet}),
                                text=True, encoding="utf-8", timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_synchronous_batch_preserves_identity_order_and_original_frame_strings(self):
        cli, expressions = self.collecting_cli()
        raw = ' { "type": "pageAutoTranslations", "text": "中文😀\\\\\\\"" }\n'
        frames = [["rpc-1", raw, True], ["rpc-2", {"type": "pageAutoAccepted"}, True],
                  ["rpc-1", '{"type":"pageAutoTranslations","finished":true}', False]]
        cli.deliver_batch(frames)
        self.assertEqual(len(expressions), 1)
        result = self.execute_expressions(expressions)
        self.assertIsNone(result["error"])
        self.assertEqual(result["calls"], [["acknowledge", "rpc-1", {"accepted": True}],
            ["deliver", "rpc-1", raw], ["acknowledge", "rpc-2", {"accepted": True}],
            ["deliver", "rpc-2", frames[1][1]], ["deliver", "rpc-1", frames[2][1]]])

    def test_full_wrapper_utf16_boundary_and_bounded_count(self):
        cli, expressions = self.collecting_cli()
        overhead = len(capture.delivery_expression([["rpc", "", True]]).encode("utf-16-le"))
        packet = "x" * ((capture.EVAL_LIMIT_BYTES - overhead) // 2)
        cli.deliver_batch([["rpc", packet, True]])
        self.assertEqual(len(expressions[0].encode("utf-16-le")), capture.EVAL_LIMIT_BYTES)
        self.assertEqual(len(expressions), 1)
        expressions.clear()
        cli.deliver_batch([["rpc", packet + "😀", False]])
        self.assertGreater(len(expressions), 1)
        self.assertTrue(all(len(expression.encode("utf-16-le")) <= capture.EVAL_LIMIT_BYTES for expression in expressions))
        self.assertEqual(self.execute_expressions(expressions)["calls"], [["deliver", "rpc", packet + "😀"]])
        expressions.clear()
        cli.deliver_batch([])
        with self.assertRaisesRegex(capture.CaptureError, "delivery_batch_too_many_packets"):
            cli.deliver_batch([["r", "{}", True]] * (capture.DELIVERY_BATCH_PACKETS + 1))
        with self.assertRaisesRegex(capture.CaptureError, "delivery_batch_too_large"):
            cli.deliver_batch([["r", "😀" * 5000, True]] * 2)
        self.assertEqual(expressions, [])

    def test_oversize_data_uses_safe_chunks_without_frame_rewriting(self):
        cli, expressions = self.collecting_cli()
        packet = json.dumps({"type": "pageAutoTranslations", "text":
                            ('中文😀𠮷"\\\n\r\t\x00' * 4000) + '";globalThis.injected=true;//'},
                            ensure_ascii=False, indent=2) + "\n"
        cli.deliver_batch([["rpc-oversize", packet, True]])
        pushes = [expression for expression in expressions if expression.startswith("globalThis.__qaSourceChunks.push(")]
        self.assertGreater(len(pushes), 1)
        self.assertTrue(all(len(expression.encode("utf-16-le")) <= capture.EVAL_LIMIT_BYTES for expression in expressions))
        result = self.execute_expressions(expressions)
        self.assertEqual(result["calls"], [["acknowledge", "rpc-oversize", {"accepted": True}],
                                           ["deliver", "rpc-oversize", packet]])
        self.assertIsNone(result["error"])
        self.assertFalse(result["stored"])
        self.assertFalse(result["chunks"])
        self.assertFalse(result["injected"])
        # A failed receive still cleans stored oversize data and is not retried.
        failed = self.execute_expressions(expressions, fail_packet=packet)
        self.assertEqual(failed["error"], "synthetic_delivery_failure")
        self.assertEqual(failed["calls"], result["calls"])
        self.assertFalse(failed["stored"])

    def test_batch_receive_exception_stops_later_frames_and_eval_errors_fail(self):
        cli, expressions = self.collecting_cli()
        frames = [["r", "first", True], ["r", "bad", False], ["r", "later", False]]
        cli.deliver_batch(frames)
        result = self.execute_expressions(expressions, fail_packet="bad")
        self.assertEqual(result["error"], "synthetic_delivery_failure")
        self.assertEqual([row[2] for row in result["calls"] if row[0] == "deliver"], ["first", "bad"])
        for value in ({}, False, None):
            cli, _ = self.collecting_cli(result=value)
            with self.subTest(value=value), self.assertRaisesRegex(capture.CaptureError, "delivery_invalid_result"):
                cli.deliver_batch(frames)

    def test_cli_timeout_is_bounded_by_case_deadline_and_late_results_fail(self):
        clock, timeouts = [10.0], []
        def runner(argv, **kwargs):
            timeouts.append(kwargs["timeout"])
            clock[0] += .6
            return subprocess.CompletedProcess(argv, 0, '{"data":{"result":true}}', "")
        cli = capture.BrowserCli("deadline-test", self.journal, runner)
        cli.deadline = 10.5
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]):
            with self.assertRaisesRegex(capture.CaptureError, "case_timeout_without_settlement"):
                cli.deliver_batch([["r", "{}", True]])
            with self.assertRaisesRegex(capture.CaptureError, "case_timeout_without_settlement"):
                cli.deliver_batch([["r", "{}", True]])
        self.assertEqual(timeouts, [.5])
        self.assertIn('"kind":"cli"', self.journal.path.read_text(encoding="utf-8"))

    def test_oversize_chunk_delivery_stops_at_deadline(self):
        clock, expressions = [0.0], []
        def runner(argv, **kwargs):
            expressions.append(argv[-1])
            clock[0] += .1
            return subprocess.CompletedProcess(argv, 0, '{"data":{"result":true}}', "")
        cli = capture.BrowserCli("deadline-chunks", self.journal, runner)
        cli.deadline = .35
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]):
            with self.assertRaisesRegex(capture.CaptureError, "case_timeout_without_settlement"):
                cli.deliver_batch([["r", "😀" * 50000, True]])
        self.assertEqual(len(expressions), 4)
        self.assertFalse(any(expression.startswith("(()=>") for expression in expressions))


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


class SourceObservationTests(unittest.TestCase):
    def value(self, completed=True, pending=0):
        return {"at": "sample-time", "pageDecision": True, "jobs": pending,
                "nodes": [{"id": "s1"}], "sourceNodes": [{"id": "s1", "sourceText": "Foreign sentence."}],
                "productionEligibility": [{"sourceId": "s1", "eligible": True}],
                "predictions": [{"id": "p1", "sourceId": "s1", "eligible": True,
                                 "completed": completed, "failed": False}]}

    def test_hidden_source_retains_last_actual_flags(self):
        for complete in (True, False):
            observations, value = {}, self.value(complete)
            capture.record_source_observations(observations, value, "after")
            value["nodes"] = []; value["predictions"] = []
            value["productionEligibility"][0]["eligible"] = False
            capture.record_source_observations(observations, value, "hidden")
            self.assertTrue(observations["s1"]["eligible"])
            self.assertEqual(complete, observations["s1"]["pieces"][0]["completed"])
            self.assertEqual("after", observations["s1"]["phase"])

    def test_last_pending_snapshot_replaces_success_without_borrowing_it(self):
        observations = {}
        capture.record_source_observations(observations, self.value(), "success")
        value = self.value(False, pending=1)
        value["predictions"][0]["id"] = "new-piece"
        capture.record_source_observations(observations, value, "pending")
        self.assertEqual("new-piece", observations["s1"]["pieces"][0]["id"])
        self.assertFalse(observations["s1"]["pieces"][0]["completed"])

    def test_hidden_owner_completion_does_not_replace_last_visible_pending(self):
        observations = {}
        capture.record_source_observations(observations, self.value(False, pending=1), "visible-pending")
        hidden = self.value()
        hidden["nodes"] = []
        capture.record_source_observations(observations, hidden, "hidden-completed")
        self.assertFalse(observations["s1"]["pieces"][0]["completed"])
        self.assertEqual("visible-pending", observations["s1"]["phase"])

    def test_unprocessed_visible_source_is_unknown_and_before_has_no_verdict(self):
        observations, value = {}, self.value(pending=1)
        value["predictions"] = []; value["productionEligibility"][0]["eligible"] = False
        capture.record_source_observations(observations, value, "scanning")
        self.assertIsNone(observations["s1"]["eligible"])
        observations.clear(); value["pageDecision"] = None
        capture.record_source_observations(observations, value, "before")
        self.assertEqual({}, observations)

    def test_atomic_peer_uses_explicit_production_membership_with_independent_source(self):
        observations, value = {}, self.value()
        value["sourceNodes"].append({"id": "peer", "sourceText": "foreign peer"})
        value["productionEligibility"].append({"sourceId": "peer", "eligible": True})
        value["predictions"][0]["sourceIds"] = ["s1", "peer"]
        capture.record_source_observations(observations, value, "atomic")
        self.assertFalse(observations["peer"]["rendered"])
        self.assertEqual("s1", observations["peer"]["representedBy"])
        self.assertEqual("p1", observations["peer"]["pieces"][0]["id"])


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

    def test_negative_native_assessment_requires_explicit_successful_zero_terminal(self):
        case = {**self.valid(False), "requestIds": ["native-rpc"], "terminals": [
            {"id": "native-rpc", "success": True, "requestCount": 0, "failedCount": 0}]}
        self.assertTrue(capture.completion_gate(case, self.root)[1])
        self.assertEqual(case["requestIds"], ["native-rpc"])
        for count in (None, True, "0", -1, .0):
            with self.subTest(count=count):
                terminal = {**case["terminals"][0], "requestCount": count}
                self.assertIn("terminal_evidence_missing", capture.completion_gate(
                    {**case, "terminals": [terminal]}, self.root)[2])
        for patch, reason in (({"requestCount": 2}, "unnecessary_translation_request"),
                              ({"success": False}, "terminal_evidence_missing"),
                              ({"failedCount": 1}, "terminal_evidence_missing")):
            self.assertIn(reason, capture.completion_gate(
                {**case, "terminals": [{**case["terminals"][0], **patch}]}, self.root)[2])
        self.assertIn("terminal_evidence_missing", capture.completion_gate(
            {**case, "terminals": [{"id": "native-rpc", "success": True}]}, self.root)[2])
        for patch, reason in (({"retractions": [{"type": "retraction"}]}, "translation_retractions"),
                              ({"finalState": {"rpcPending": 1}}, "production_work_unresolved"),
                              ({"finalState": {"pageDecision": False, "status": {"state": "assessing"}}},
                               "production_work_unresolved"),
                              ({"predictions": [{"sourceId": "source-1", "failed": True}]}, "node_failure")):
            self.assertIn(reason, capture.completion_gate({**case, **patch}, self.root)[2])

    def test_full_phases_require_files_hashes_and_independent_review(self):
        case = self.valid()
        case["fullScreenshots"] = True
        self.assertIn("screenshots_incomplete", capture.completion_gate(case, self.root)[2])
        for phase in ("before-full", "after-full", "stable-full"):
            image = self.root / (phase + ".png")
            image.write_bytes(b"OFFLINE FULL PAGE FIXTURE")
            case["screenshots"].append({"phase": phase, "path": image.name, "fullPage": True})
        ready, complete, reasons = capture.completion_gate(case, self.root)
        self.assertTrue(ready)
        self.assertFalse(complete)
        self.assertIn("screenshot_review_pending", reasons)
        for shot in case["screenshots"]:
            shot["sha256"] = capture.hashlib.sha256((self.root / shot["path"]).read_bytes()).hexdigest()
        case_path = self.root / "case.json"
        capture.atomic_json(case_path, case)
        original = case_path.read_bytes()
        review_path = self.root / "review.json"
        independent = {"caseSha256": capture.hashlib.sha256(original).hexdigest(),
                       "screenshots": {shot["phase"]: True for shot in case["screenshots"]}, "annotations": []}
        # Viewport-only votes cannot authorize the additional full-page phases.
        capture.atomic_json(review_path, {**independent, "screenshots": case["screenshotReview"]})
        with self.assertRaisesRegex(capture.CaptureError, "all_screenshot_votes"):
            review.apply_review(case_path, review_path, self.root / "missing-votes.json")
        capture.atomic_json(review_path, independent)
        self.assertTrue(review.apply_review(case_path, review_path, self.root / "reviewed-full.json")["completed"])
        self.assertEqual(case_path.read_bytes(), original)
        (self.root / "stable-full.png").write_bytes(b"tampered")
        with self.assertRaisesRegex(capture.CaptureError, "screenshot_hash"):
            review.apply_review(case_path, review_path, self.root / "tampered-full.json")

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
            deliver_batch = capture.BrowserCli.deliver_batch
            drain_sample = capture.BrowserCli.drain_sample
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
                prefix = "(()=>{const q=globalThis.__websiteQa;return {sample:q.sample("
                if expression.startswith(prefix):
                    phase = json.loads(expression[len(prefix):expression.index("),outbound:q.drain()")])
                    return {"sample": self.qa("sample", phase), "outbound": self.qa("drain")}
                if expression.startswith("(()=>{const q=globalThis.__websiteQa;for(const [rpcId,packet,ack] of "):
                    prefix = "(()=>{const q=globalThis.__websiteQa;for(const [rpcId,packet,ack] of "
                    frames = json.loads(expression[len(prefix):expression.rindex("){if(ack)")])
                    for rpc_id, packet, ack in frames:
                        if ack:
                            self.qa("acknowledge", rpc_id, {"accepted": True})
                        self.qa("deliver", rpc_id, packet)
                    return True
                if "chrome?.runtime" in expression or "querySelector('details" in expression:
                    return False
                return True
            def screenshot(self, path, full=False):
                self.calls.append(("screenshot", Path(path).name, full))
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

    def scripted_case(self, events_for_request, browser=None, run_id="scripted", poll_hook=None, case_timeout=5,
                      stable_seconds=.001, site=None, full_screenshots=False):
        class ScriptedHost:
            def __init__(self):
                self.events, self.requests = [], []
                self.polls = 0
                self.journal = None
            def capabilities(self, case_id, timeout=15):
                return {"type": "capabilities", "id": "synthetic-capabilities", "caseId": case_id,
                        "live": False, "targetLanguage": "zh-CN"}
            def send(self, request):
                self.requests.append(copy.deepcopy(request))
                self.journal.append({"kind": "host_request", "request": request})
                self.events.extend(events_for_request(request, self.requests))
            def poll(self, timeout=0):
                self.polls += 1
                if poll_hook:
                    poll_hook(self)
                return self.events.pop(0) if self.events else None
        host = ScriptedHost()
        browser = browser or self.browser()
        run = capture.EvidenceRun(self.root, run_id, {"sourceHead": "a" * 40})
        result = capture.CaseCapture(browser, host, run, HERE.parents[2], case_timeout=case_timeout,
                                     stable_seconds=stable_seconds, poll_seconds=.002,
                                     full_screenshots=full_screenshots).capture(site or SITE)
        log = next(run.path.glob("*/attempt-*/events.jsonl"))
        rows = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()]
        return result, browser, host, rows

    @staticmethod
    def packet_event(request, packet):
        return {"type": "packet", "id": request["id"], "caseId": request["caseId"], "packet": packet}

    @staticmethod
    def terminal_event(request, **extra):
        return {"type": "terminal", "id": request["id"], "caseId": request["caseId"],
                "success": True, "requestCount": 0, "failedCount": 0, **extra}

    def moving_browser(self, skipped=False, replace_sources=False, change=None, unresolved=None,
                       skipped_candidates=0):
        """Synthetic motion only; never represents a real browser/model result."""
        browser = self.browser(skipped=skipped, probe_states=[{
            "id": "probe-1", "stage": "completed", "result": {"samples": [], "statistics": {}}}])
        original_qa = browser.qa
        browser.motion_samples = 0
        def qa(method, *args):
            value = original_qa(method, *args)
            if method != "sample":
                return value
            browser.motion_samples += 1
            tick = browser.motion_samples
            rows = []
            for index in range(max(2, skipped_candidates)):
                source_id = f"replacement-{tick}-{index}" if replace_sources else f"s{index + 1}"
                rows.append({"id": source_id, "sourceText": f"Synthetic source {index}",
                             "displayedText": f"Synthetic source {index}" if skipped else "合成显示文本",
                             "tag": "P", "role": "article", "locator": f"moving/{tick}/{index}",
                             "firstSeen": f"time-{tick}", "phase": args[0],
                             "rects": [{"x": tick * -100, "y": 700 + index * 20,
                                        "width": 80, "height": 16}],
                             "rendered": True, "ancestorsVisible": True, "inViewport": False})
            if browser.configured and change:
                if change == "multiplicity":
                    rows.extend(copy.deepcopy(rows) * (tick % 2))
                else:
                    rows[0][change] += str(tick)
            if tick % 2:
                rows.reverse()
            value["nodes"] = rows
            value["sourceNodes"] = copy.deepcopy(rows)
            if browser.configured and skipped and skipped_candidates:
                # Production may retain eligible untranslated blocks after a
                # negative page decision. Keep their actual flags and identities.
                value["predictions"] = [
                    {"id": f"negative-piece-{tick}-{index}", "sourceId": row["id"],
                     "sourceText": row["sourceText"], "text": row["sourceText"],
                     "eligible": True, "completed": False, "failed": False,
                     "sourceEvidence": "matched", "auditIssue": None}
                    for index, row in enumerate(rows)]
            # Prediction IDs/grouping are bookkeeping and may churn independently.
            for prediction in ([] if skipped_candidates else value["predictions"]):
                prediction.update(id=f"piece-{tick}", groupId=f"group-{tick}", sourceId="s1",
                                  sourceText="Synthetic source 0", text="Synthetic source 0")
            if browser.configured and unresolved:
                if unresolved in ("jobs", "pending", "scanning", "rpcPending"):
                    value[unresolved] = 1
                elif unresolved == "status":
                    value["status"] = {"state": "assessing"}
                elif unresolved == "pageDecision":
                    value["pageDecision"] = None
                elif unresolved == "failed":
                    value["predictions"][0]["failed"] = True
                elif unresolved == "completed":
                    value["predictions"][0]["completed"] = False
            value["at"] = f"sample-time-{tick}"
            return value
        browser.qa = qa
        return browser

    def motion_events(self, request, requests):
        if request["action"] == "assess":
            return [{"type": "assessment", "id": request["id"], "caseId": request["caseId"],
                     "needsTranslation": False}]
        return [self.packet_event(request, '{"type":"pageAutoTranslations","finished":true}'),
                self.terminal_event(request)]

    def test_perpetual_geometry_locator_order_motion_settles_semantically(self):
        for skipped in (False, True):
            with self.subTest(skipped=skipped):
                clock = [1.0]
                with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                        mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                    result, _, _, rows = self.scripted_case(
                        self.motion_events, browser=self.moving_browser(skipped=skipped),
                        run_id=f"motion-{skipped}", case_timeout=.3, stable_seconds=.01,
                        site={**SITE, "expectedPageTranslation": not skipped})
                self.assertEqual(result["errors"], [])
                self.assertTrue(result["settled"])
                self.assertTrue(result["semanticStable"])
                self.assertEqual(result["settlementKind"], "semantic")
                self.assertTrue(result["layoutReviewRequired"])
                for observation in result["stability"].values():
                    self.assertTrue(observation["semanticStable"])
                    self.assertTrue(observation["geometryVaried"])
                    self.assertGreaterEqual(observation["stableForSeconds"], .01)
                    self.assertNotIn("layoutStable", observation)
                samples = [row["value"] for row in rows if row["kind"] == "sample" and row["phase"] == "after"]
                self.assertGreater(len(samples), 4)
                self.assertNotEqual(samples[0]["nodes"], samples[1]["nodes"])
                self.assertTrue(any(row.get("semanticStable") and row.get("geometryChanged")
                                    for row in rows if row["kind"] == "stability"))
                self.assertTrue(result["simulation"])
                self.assertFalse(result["completed"])

    def test_identical_replacement_content_keeps_every_source_and_unknown_annotation(self):
        clock = [1.0]
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
            result, _, _, rows = self.scripted_case(
                self.motion_events, browser=self.moving_browser(skipped=True, replace_sources=True),
                run_id="replaced-sources", case_timeout=.3, stable_seconds=.01,
                site={**SITE, "expectedPageTranslation": False})
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        raw_sources = {source["id"]: source for row in rows if row["kind"] == "sample"
                       for source in row["value"]["sourceNodes"]}
        self.assertGreater(len(raw_sources), 30)
        self.assertEqual({row["id"]: row for row in result["sourceNodes"]}, raw_sources)
        self.assertEqual({row["sourceId"] for row in result["annotations"]}, set(raw_sources))
        self.assertTrue(all(row["needTranslation"] == "unknown" for row in result["annotations"]))

    def test_skipped_page_retains_13_untranslated_candidates_and_captures_all_phases(self):
        clock = [1.0]
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
            result, browser, host, rows = self.scripted_case(
                self.motion_events,
                browser=self.moving_browser(skipped=True, replace_sources=True, skipped_candidates=13),
                run_id="negative-untranslated", case_timeout=.3, stable_seconds=.01,
                site={**SITE, "expectedPageTranslation": False}, full_screenshots=True)
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertTrue(result["semanticStable"])
        self.assertIs(result["pageDecision"], False)
        self.assertEqual(result["requestIds"], [])
        self.assertEqual(result["terminals"], [])
        self.assertEqual([request["action"] for request in host.requests], ["assess"])
        self.assertFalse(any(call[0] in ("acknowledge", "deliver") for call in browser.calls))
        samples = [row["value"] for row in rows if row["kind"] == "sample"]
        after_samples = [row["value"] for row in rows if row["kind"] == "sample" and row["phase"] == "after"]
        self.assertGreater(len(after_samples), 4)
        self.assertTrue(all(len(value["predictions"]) == 13 for value in after_samples))
        for prediction in result["predictions"]:
            self.assertIs(prediction["eligible"], True)
            self.assertIs(prediction["completed"], False)
            self.assertIs(prediction["failed"], False)
        raw_predictions = {p["id"]: p for value in samples for p in value["predictions"]}
        raw_sources = {s["id"]: s for value in samples for s in value["sourceNodes"]}
        self.assertEqual({p["id"]: p for p in result["predictionHistory"]}, raw_predictions)
        self.assertEqual(result["predictions"], samples[-1]["predictions"])
        self.assertEqual({s["id"]: s for s in result["sourceNodes"]}, raw_sources)
        self.assertGreater(len(raw_sources), 13)
        self.assertEqual(result["annotations"], [{"sourceId": s["id"], "needTranslation": "unknown"}
                                               for s in result["sourceNodes"]])
        self.assertEqual([shot["phase"] for shot in result["screenshots"]],
                         ["before", "before-full", "after", "after-full", "scroll-before", "scroll-after",
                          "return-after", "reflow-after", "stable", "stable-full"])
        attempt = next((self.root / "negative-untranslated").glob("*/attempt-*"))
        for shot in result["screenshots"]:
            self.assertEqual(shot["sha256"], capture.hashlib.sha256((attempt / shot["path"]).read_bytes()).hexdigest())
        for observation in result["stability"].values():
            self.assertTrue(observation["semanticStable"])
            self.assertTrue(observation["geometryVaried"])
            self.assertGreaterEqual(observation["stableForSeconds"], .01)
        self.assertTrue(result["simulation"])
        self.assertFalse(result["completed"])
        self.assertEqual(result["screenshotReview"], {})
        # Test the unchanged negative gate without pretending the fixture is live.
        self.assertNotIn("node_completion_missing", result["blockedReason"])
        self.assertIn("simulation_not_real_case", result["blockedReason"])

    def test_skipped_candidates_still_block_for_changed_text_or_unresolved_work(self):
        checks = [("unresolved", key) for key in
                   ("jobs", "pending", "scanning", "rpcPending", "status", "pageDecision", "failed")]
        for kind, key in checks:
            with self.subTest(kind=kind, key=key):
                clock = [1.0]
                with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                        mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                    result, _, host, rows = self.scripted_case(
                        self.motion_events,
                        browser=self.moving_browser(skipped=True, skipped_candidates=13, **{kind: key}),
                        run_id=f"negative-unsettled-{key}", case_timeout=.04, stable_seconds=.01,
                        site={**SITE, "expectedPageTranslation": False})
                self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement")
                self.assertFalse(result["settled"])
                self.assertFalse(result["completed"])
                self.assertFalse(result["evidenceReady"])
                self.assertEqual(host.requests, [])
                self.assertFalse(any(row["semanticStable"] for row in rows if row["kind"] == "stability"))

    def test_skipped_live_ticker_records_content_changes_without_waiting_for_static_content(self):
        for field in ("sourceText", "displayedText"):
            clock = [1.0]
            with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                    mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                result, _, host, rows = self.scripted_case(
                    self.motion_events,
                    browser=self.moving_browser(skipped=True, skipped_candidates=13, change=field),
                    run_id="skipped-ticker-" + field, case_timeout=.3, stable_seconds=.01,
                    site={**SITE, "expectedPageTranslation": False})
            self.assertEqual(result["errors"], [])
            self.assertTrue(result["settled"])
            self.assertEqual([r["action"] for r in host.requests], ["assess"])
            self.assertTrue(all(s["stabilityBasis"] == "page-decision" for s in result["stability"].values()))
            observed = [e["value"]["nodes"][0][field] for e in rows if e.get("kind") == "sample"]
            self.assertGreater(len(set(observed)), 1)
            self.assertTrue(all(a["needTranslation"] == "unknown" for a in result["annotations"]))

    def test_retired_pending_piece_is_archived_not_counted_as_current_omission(self):
        for unmatched in (False, True):
            browser = self.moving_browser(skipped=False)
            original = browser.qa
            injected = [False]
            def qa(method, *args):
                value = original(method, *args)
                if method == "sample" and args[0] == "after" and not injected[0]:
                    injected[0] = True
                    retired = dict(value["predictions"][0], id="retired-pending", completed=False)
                    if unmatched:
                        retired.update(sourceId="never-observed", sourceEvidence="unmatched",
                                       auditIssue="source_not_independently_visible")
                    value["predictions"] = [retired]
                return value
            browser.qa = qa
            clock = [1.0]
            with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                    mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                result, _, _, _ = self.scripted_case(self.motion_events, browser=browser,
                    run_id="retired-pending-" + str(unmatched), case_timeout=.3, stable_seconds=.01)
            self.assertEqual(result["errors"], [])
            self.assertTrue(all(p["completed"] for p in result["predictions"]))
            self.assertTrue(any(p["id"] == "retired-pending" and not p["completed"] for p in result["predictionHistory"]))
            self.assertNotIn("node_completion_missing", result["blockedReason"])
            if unmatched:
                self.assertIn("historical_prediction_source_evidence_unmatched", result["blockedReason"])

    def test_negative_decision_does_not_bypass_missing_or_failed_request_terminal(self):
        for name, terminal in (("missing", None), ("failed", {"success": False}), ("failed-count", {"failedCount": 1})):
            with self.subTest(terminal=terminal):
                browser = self.moving_browser(skipped=True, skipped_candidates=13)
                original_qa = browser.qa
                def qa(method, *args):
                    value = original_qa(method, *args)
                    if method == "configure":
                        browser.outgoing.append({"rpcId": "rpc-negative", "message": {
                            "type": "pageAutoCandidate", "requestId": "negative-request",
                            "documentToken": "doc1", "generation": 1, "revision": 1,
                            "documentLanguage": "zh-CN", "blocks": [], "samples": [], "pageSummary": {}}})
                    return value
                browser.qa = qa
                def events(request, requests):
                    packets = [self.packet_event(request, '{"type":"pageAutoAccepted"}')]
                    # Sample the actual negative decision before a later terminal
                    # arrives; None is a temporarily empty host queue.
                    return packets if terminal is None else packets + [None, self.terminal_event(request, **terminal)]
                clock = [1.0]
                with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                        mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                    result, _, _, rows = self.scripted_case(
                        events, browser=browser, run_id="negative-terminal-" + name,
                        case_timeout=.04, stable_seconds=.01,
                        site={**SITE, "expectedPageTranslation": False})
                self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement"
                                 if terminal is None else "backend_terminal_failed")
                self.assertIs(result["pageDecision"], False)
                self.assertFalse(result["settled"])
                self.assertFalse(result["evidenceReady"])
                self.assertFalse(result["completed"])
                self.assertIn("terminal_evidence_missing", result["blockedReason"])
                self.assertNotIn("unnecessary_translation_request", result["blockedReason"])
                self.assertFalse(any(row["semanticStable"] for row in rows if row["kind"] == "stability"))
                if terminal is None:
                    self.assertTrue(all(not row["modelRequestsZero"] for row in rows if row["kind"] == "stability"))

    def native_negative_browser(self):
        browser = self.moving_browser(skipped=True, skipped_candidates=13)
        original_qa = browser.qa
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "configure":
                browser.outgoing.append({"rpcId": "rpc-negative", "message": {
                    "type": "pageAutoCandidate", "requestId": "negative-request", "documentToken": "doc1",
                    "generation": 1, "revision": 1, "documentLanguage": "zh-CN",
                    "blocks": [], "samples": [], "pageSummary": {}}})
            return value
        browser.qa = qa
        return browser

    def test_native_negative_zero_model_packets_keep_candidates_and_capture_all_phases(self):
        packet = ' {"type":"pageAutoAssessment","needsTranslation":false} '
        def events(request, requests):
            return [self.packet_event(request, packet), None, self.terminal_event(request)]
        clock = [1.0]
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
            result, browser, host, rows = self.scripted_case(events, browser=self.native_negative_browser(),
                run_id="native-negative-zero", case_timeout=.3, stable_seconds=.01,
                site={**SITE, "expectedPageTranslation": False})
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertEqual(result["requestIds"], ["site-001-rpc-negative"])
        self.assertEqual(result["terminals"][0]["requestCount"], 0)
        self.assertEqual([request["action"] for request in host.requests], ["message"])
        self.assertEqual(browser.delivered, [packet])
        self.assertEqual(sum(call[0] == "acknowledge" for call in browser.calls), 1)
        self.assertEqual(len(result["predictions"]), 13)
        self.assertTrue(all(p["eligible"] and not p["completed"] and not p["failed"] for p in result["predictions"]))
        self.assertEqual([shot["phase"] for shot in result["screenshots"]],
            ["before", "after", "scroll-before", "scroll-after", "return-after", "reflow-after", "stable"])
        self.assertNotIn("unnecessary_translation_request", result["blockedReason"])
        self.assertFalse(result["evidenceReady"])  # Explicit simulation, never a real completed domain.
        self.assertFalse(result["completed"])
        self.assertEqual(len([row for row in rows if row["kind"] == "host_event"]), 2)

    def test_native_negative_actual_model_and_unknown_counts_cannot_settle(self):
        for label, count in (("actual", 1), ("missing", None), ("boolean", False), ("negative", -1)):
            def events(request, requests):
                terminal = self.terminal_event(request, requestCount=count)
                if label == "missing":
                    del terminal["requestCount"]
                return [self.packet_event(request, '{"type":"pageAutoAssessment","needsTranslation":false}'), terminal]
            clock = [1.0]
            with self.subTest(count=count), mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                    mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                result, _, _, _ = self.scripted_case(events, browser=self.native_negative_browser(),
                    run_id="native-negative-" + label, case_timeout=.04, stable_seconds=.01,
                    site={**SITE, "expectedPageTranslation": False})
            self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement" if label == "actual"
                             else "backend_terminal_request_count_invalid")
            self.assertIn("unnecessary_translation_request" if label == "actual" else "terminal_evidence_missing",
                          result["blockedReason"])
            self.assertFalse(result["settled"])
            self.assertFalse(result["evidenceReady"])
            self.assertFalse(result["completed"])
            self.assertEqual(result["terminals"][0].get("requestCount"), count)

    def test_rules_only_assessment_event_cannot_replace_candidate_terminal(self):
        def events(request, requests):
            return [self.packet_event(request, '{"type":"pageAutoAssessment","needsTranslation":false}'),
                    {"type": "assessment", "id": request["id"], "caseId": request["caseId"],
                     "needsTranslation": False}]
        result, _, _, _ = self.scripted_case(events, browser=self.native_negative_browser(),
            run_id="candidate-no-terminal", site={**SITE, "expectedPageTranslation": False})
        self.assertEqual(result["errors"][0]["code"], "host_unexpected_assessment")
        self.assertEqual(result["terminals"], [])
        self.assertIn("terminal_evidence_missing", result["blockedReason"])
        self.assertFalse(result["settled"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

    def test_negative_reset_and_assessment_storm_between_idle_samples_never_settles(self):
        for kind in ("decision", "candidate"):
            browser = self.moving_browser(skipped=True, skipped_candidates=13)
            original_qa = browser.qa
            activity = []
            def qa(method, *args):
                value = original_qa(method, *args)
                if method == "sample" and browser.configured:
                    if kind == "decision":
                        activity.extend([{"type": "decision", "value": {"assessed": False}},
                                         {"type": "status", "value": {"state": "assessing"}},
                                         {"type": "decision", "value": {"assessed": True}}])
                    else:
                        number = len(activity)
                        message = {"type": "pageAutoCandidate", "requestId": f"req-{number}",
                                   "documentToken": "doc1", "generation": 1, "revision": number + 1,
                                   "documentLanguage": "zh-CN", "blocks": [], "samples": [], "pageSummary": {}}
                        browser.outgoing.append({"rpcId": f"rpc-{number}", "message": message})
                        activity.append({"type": "outbound", "message": message})
                    value["events"] = copy.deepcopy(activity)
                return value
            browser.qa = qa
            clock = [1.0]
            def events(request, requests):
                return [self.packet_event(request, '{"type":"pageAutoAssessment","needsTranslation":false}'),
                        self.terminal_event(request)]
            with self.subTest(kind=kind), mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                    mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                result, _, host, rows = self.scripted_case(events, browser=browser,
                    run_id="negative-storm-" + kind, case_timeout=.04, stable_seconds=.01,
                    site={**SITE, "expectedPageTranslation": False})
            self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement")
            self.assertFalse(result["settled"])
            self.assertFalse(result["completed"])
            self.assertFalse(any(row["semanticStable"] for row in rows if row["kind"] == "stability"))
            if kind == "candidate":
                self.assertGreater(len(host.requests), 1)
                self.assertTrue(all(event["requestCount"] == 0 for event in result["terminals"]))

    def failed_browser(self):
        browser = self.browser()
        original_qa = browser.qa
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "deliver" and json.loads(args[1]).get("error"):
                browser.done = True
                browser.predictions[0].update(completed=False, failed=True, text="实际失败前的译文")
            if method == "sample" and browser.configured:
                row = {"id": "s1", "sourceText": "Synthetic source", "displayedText": "实际失败前的译文",
                       "tag": "P", "role": "article"}
                value["nodes"] = [row]
                value["sourceNodes"] = [copy.deepcopy(row)]
                if browser.predictions[0]["failed"]:
                    value["status"] = {"state": "failed", "reason": "synthetic_guard_failure"}
            return value
        browser.qa = qa
        return browser

    def test_failed_packet_drains_original_failed_terminal_and_final_snapshot_via_fake_cli(self):
        simulator = self.failed_browser()
        argv_seen = []
        def runner(argv, **options):
            self.assertIs(options["shell"], False)
            self.assertLessEqual(options["timeout"], 5)
            self.assertEqual(argv[1], str(capture.WRAPPER))
            argv_seen.append(argv)
            action, arguments = argv[5], argv[6:]
            if action == "eval":
                expression = arguments[0]
                prefix = "globalThis.__websiteQa."
                if expression.startswith(prefix):
                    method, data = expression[len(prefix):].split("(", 1)
                    value = simulator.qa(method, *json.loads("[" + data[:-1] + "]"))
                else:
                    value = simulator.evaluate(expression)
                data = {"result": value}
            elif action == "screenshot":
                Path(arguments[0]).write_bytes(capture.encode(simulator.predictions).encode("utf-8"))
                data = {}
            elif action == "state":
                data = simulator.state()
            else:
                simulator.command(action, *arguments)
                data = {}
            return subprocess.CompletedProcess(argv, 0, json.dumps({"success": True, "data": data}), "")
        browser = capture.BrowserCli("offline-failed", self.journal, runner=runner)
        packets = [' {"type":"pageAutoTranslations","finished":false,"updates":[]} ',
                   ' {"type":"pageAutoTranslations","finished":true,"error":"synthetic_guard_failure"}\n']
        originals = []
        def events(request, requests):
            batch = [self.packet_event(request, packet) for packet in packets]
            terminal = self.terminal_event(request, success=False, requestCount=7, failedCount=15, cacheHits=2)
            originals.extend(batch + [terminal])
            return batch + [None, None, terminal]
        result, _, host, rows = self.scripted_case(events, browser=browser, run_id="failed-cli", full_screenshots=True)
        self.assertEqual(simulator.delivered, packets)
        self.assertEqual([row["event"] for row in rows if row["kind"] == "host_event"], originals)
        self.assertEqual(result["terminals"], [originals[-1]])
        self.assertEqual([error["code"] for error in result["errors"]],
                         ["backend_packet_error", "backend_terminal_failed"])
        self.assertEqual(result["failureSnapshot"]["predictions"], result["predictions"])
        self.assertTrue(result["predictions"][0]["failed"])
        self.assertFalse(result["predictions"][0]["completed"])
        self.assertEqual(result["failureSnapshot"]["nodes"][0]["displayedText"], "实际失败前的译文")
        self.assertEqual(sum(call[0] == "acknowledge" for call in simulator.calls), 1)
        self.assertEqual(len(host.requests), 1)
        config = next(call[1] for call in simulator.calls if call[0] == "configure")
        self.assertEqual(config["chineseProseShareThresholdPercent"], 50)
        self.assertTrue(all(len(argv[6].encode("utf-16-le")) <= capture.EVAL_LIMIT_BYTES
                            for argv in argv_seen if argv[5] == "eval"))
        self.assertEqual([shot["phase"] for shot in result["screenshots"]],
                         ["before", "before-full", "after", "after-full", "stable", "stable-full"])
        attempt = next((self.root / "failed-cli").glob("*/attempt-*"))
        for shot in result["screenshots"]:
            raw = (attempt / shot["path"]).read_bytes()
            self.assertEqual(shot["sha256"], capture.hashlib.sha256(raw).hexdigest())
            if shot["phase"] in ("stable", "stable-full"):
                self.assertTrue(json.loads(raw)[0]["failed"])
        self.assertEqual(result["screenshotReview"], {})
        self.assertFalse(result["settled"])
        self.assertFalse(result["semanticStable"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

    def test_failed_packet_missing_terminal_retains_available_evidence_without_deadline_extension(self):
        clock = [1.0]
        browser = self.failed_browser()
        def events(request, requests):
            return [self.packet_event(request, '{"type":"pageAutoTranslations","error":"synthetic_failure"}')]
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
            result, browser, _, _ = self.scripted_case(events, browser=browser,
                run_id="failed-missing-terminal", case_timeout=.01)
        self.assertAlmostEqual(clock[0], 1.01)
        self.assertEqual(result["errors"][0]["code"], "backend_packet_error")
        self.assertIn("case_timeout_without_settlement", [error["code"] for error in result["errors"]])
        self.assertEqual(result["terminals"], [])
        self.assertTrue(result["failureSnapshot"]["predictions"][0]["failed"])
        self.assertEqual([shot["phase"] for shot in result["screenshots"]], ["before", "after"])
        self.assertIn("terminal_evidence_missing", result["blockedReason"])
        self.assertFalse(result["settled"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

    def test_failed_evidence_screenshot_failure_keeps_delayed_terminal(self):
        browser = self.failed_browser()
        original_shot = browser.screenshot
        def screenshot(path, full=False):
            if Path(path).name == "after.png":
                raise capture.CaptureError("screenshot_missing")
            original_shot(path, full=full)
        browser.screenshot = screenshot
        def events(request, requests):
            return [self.packet_event(request, '{"type":"pageAutoTranslations","error":"synthetic_failure"}'),
                    None, None, self.terminal_event(request, success=False, requestCount=1, failedCount=1)]
        result, _, _, _ = self.scripted_case(events, browser=browser, run_id="failed-screenshot")
        self.assertEqual(len(result["terminals"]), 1)
        self.assertIn("screenshot_missing", [error["code"] for error in result["errors"]])
        self.assertEqual([shot["phase"] for shot in result["screenshots"]], ["before", "stable"])
        self.assertTrue(result["failureSnapshot"]["predictions"][0]["failed"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

    def test_failed_packet_at_deadline_does_not_start_snapshot_or_screenshot(self):
        clock = [1.0]
        browser = self.failed_browser()
        original_qa = browser.qa
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "deliver":
                clock[0] = 1.01
            return value
        browser.qa = qa
        def events(request, requests):
            return [self.packet_event(request, '{"type":"pageAutoTranslations","error":"synthetic_failure"}'),
                    self.terminal_event(request, success=False, requestCount=7, failedCount=15)]
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]):
            result, browser, host, _ = self.scripted_case(events, browser=browser,
                run_id="failed-deadline", case_timeout=.01)
        self.assertEqual(result["errors"][0]["code"], "backend_packet_error")
        self.assertEqual(result["terminals"], [])  # Beyond deadline: never invent or consume a terminal.
        self.assertEqual(host.events[0]["type"], "terminal")
        self.assertNotIn("failureSnapshot", result)
        self.assertEqual([shot["phase"] for shot in result["screenshots"]], ["before"])
        self.assertFalse(result["settled"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

    def test_changed_semantics_and_unresolved_translation_do_not_settle(self):
        checks = [("change", key) for key in ("sourceText", "displayedText", "tag", "role", "multiplicity")]
        checks += [("unresolved", key) for key in
                   ("jobs", "pending", "scanning", "rpcPending", "status", "pageDecision", "failed", "completed")]
        for kind, key in checks:
            with self.subTest(kind=kind, key=key):
                clock = [1.0]
                with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                        mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
                    result, _, _, rows = self.scripted_case(
                        self.motion_events, browser=self.moving_browser(**{kind: key}),
                        run_id=f"unsettled-{key}", case_timeout=.04, stable_seconds=.01)
                self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement")
                self.assertFalse(result["settled"])
                self.assertFalse(result["semanticStable"])
                self.assertFalse(result["completed"])
                self.assertFalse(result["evidenceReady"])
                self.assertFalse(any(row["semanticStable"] for row in rows if row["kind"] == "stability"))

    def test_optional_full_screenshots_are_separate_hashed_unreviewed_phases(self):
        result, browser, _, _ = self.scripted_case(self.motion_events, run_id="full-screenshots",
                                                  full_screenshots=True)
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertTrue(result["fullScreenshots"])
        full = [shot for shot in result["screenshots"] if shot["fullPage"]]
        self.assertEqual([shot["phase"] for shot in full], ["before-full", "after-full", "stable-full"])
        attempt = next((self.root / "full-screenshots").glob("*/attempt-*"))
        for shot in result["screenshots"]:
            self.assertEqual(shot["sha256"], capture.hashlib.sha256((attempt / shot["path"]).read_bytes()).hexdigest())
        self.assertEqual([call[1] for call in browser.calls if call[0] == "screenshot" and call[2]],
                         ["before-full.png", "after-full.png", "stable-full.png"])
        self.assertEqual(result["screenshotReview"], {})
        self.assertFalse(result["completed"])

    def test_text_changes_are_not_reported_as_geometry_variation(self):
        browser = self.moving_browser(change="sourceText")
        moving_qa = browser.qa
        def qa(method, *args):
            value = moving_qa(method, *args)
            if method == "sample":
                for row in value["nodes"]:
                    row["rects"] = [{"x": 10, "y": 700, "width": 80, "height": 16}]
            return value
        browser.qa = qa
        clock = [1.0]
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
            result, _, _, rows = self.scripted_case(self.motion_events, browser=browser,
                                                    run_id="text-only-changes", case_timeout=.04, stable_seconds=.01)
        self.assertFalse(result["settled"])
        observations = [row for row in rows if row["kind"] == "stability"]
        self.assertGreater(len(observations), 4)
        self.assertTrue(all(not row["semanticUnchanged"] for row in observations))
        self.assertTrue(all(not row["geometryChanged"] and not row["geometryVaried"] for row in observations))

    def test_102_frames_use_four_cli_evals_with_one_ack_and_individual_journal_events(self):
        packets = [' { "type": "pageAutoTranslations", "index": ' + str(index) +
                   ', "finished": ' + ("true" if index == 101 else "false") + ' }\n' for index in range(102)]
        original_events = []
        def events(request, requests):
            batch = [self.packet_event(request, packet) for packet in packets] + [self.terminal_event(request)]
            original_events.extend(copy.deepcopy(batch))
            return batch
        result, browser, host, rows = self.scripted_case(events)
        self.assertEqual(result["errors"], [])
        self.assertTrue(result["settled"])
        self.assertFalse(result["completed"])
        self.assertFalse(result["evidenceReady"])
        self.assertEqual(browser.delivered, packets)
        evaluations = [call[1] for call in browser.calls if call[0] == "eval" and "for(const [rpcId,packet,ack]" in call[1]]
        self.assertEqual(len(evaluations), 4)  # 204 previous transport calls -> 4 for this synthetic fixture.
        self.assertTrue(all(len(expression.encode("utf-16-le")) <= capture.EVAL_LIMIT_BYTES for expression in evaluations))
        self.assertEqual([call for call in browser.calls if call[0] == "acknowledge"],
                         [("acknowledge", "rpc-1", {"accepted": True})])
        self.assertLess(next(i for i, call in enumerate(browser.calls) if call[0] == "acknowledge"),
                        next(i for i, call in enumerate(browser.calls) if call[0] == "deliver"))
        self.assertEqual([row["event"] for row in rows if row["kind"] == "host_event"], original_events)
        self.assertEqual(result["terminals"], [original_events[-1]])

    def test_interleaved_rpcs_keep_packet_order_and_do_not_ack_twice(self):
        browser = self.browser()
        original_qa = browser.qa
        def qa(method, *args):
            value = original_qa(method, *args)
            if method == "configure":
                second = copy.deepcopy(browser.outgoing[0])
                second["rpcId"] = "rpc-2"
                second["message"]["requestId"] = "req2"
                browser.outgoing.append(second)
            return value
        browser.qa = qa
        packets = ['{"type":"pageAutoTranslations","sequence":' + str(i) +
                   ',"finished":' + ("true" if i >= 2 else "false") + '}' for i in range(4)]
        def events(request, requests):
            if len(requests) == 1:
                return []
            first, second = requests
            return [self.packet_event(first, packets[0]), self.packet_event(second, packets[1]),
                    self.packet_event(first, packets[2]), self.packet_event(second, packets[3]),
                    self.terminal_event(first), self.terminal_event(second)]
        result, browser, _, _ = self.scripted_case(events, browser)
        self.assertEqual(result["errors"], [])
        transport = [call for call in browser.calls if call[0] in ("acknowledge", "deliver")]
        self.assertEqual(transport, [("acknowledge", "rpc-1", {"accepted": True}), ("deliver", "rpc-1", packets[0]),
            ("acknowledge", "rpc-2", {"accepted": True}), ("deliver", "rpc-2", packets[1]),
            ("deliver", "rpc-1", packets[2]), ("deliver", "rpc-2", packets[3])])
        self.assertEqual([event["id"] for event in result["terminals"]], ["site-001-rpc-1", "site-001-rpc-2"])

    def test_serialized_frame_wrapper_budget_splits_batch_before_limit(self):
        packets = [json.dumps({"type": "pageAutoTranslations", "text": ('中文😀"\\\n' * 350),
                               "finished": index == 4}, ensure_ascii=False, indent=2) for index in range(5)]
        def events(request, requests):
            return [self.packet_event(request, packet) for packet in packets] + [self.terminal_event(request)]
        result, browser, _, _ = self.scripted_case(events)
        self.assertEqual(result["errors"], [])
        self.assertEqual(browser.delivered, packets)
        evaluations = [call[1] for call in browser.calls if call[0] == "eval" and "for(const [rpcId,packet,ack]" in call[1]]
        self.assertGreater(len(evaluations), 1)
        self.assertTrue(all(len(expression.encode("utf-16-le")) <= capture.EVAL_LIMIT_BYTES for expression in evaluations))
        self.assertEqual(sum(call[0] == "acknowledge" for call in browser.calls), 1)

    def test_protocol_failures_flush_prior_frames_and_preserve_failure_evidence(self):
        first = '{"type":"pageAutoTranslations","finished":false}'
        error_packet = '{"type":"pageAutoTranslations","error":"synthetic_failure"}'
        scenarios = [
            ("uncorrelated", lambda request: {"type": "packet", "id": "wrong-rpc", "caseId": SITE["id"], "packet": "{}"}, "host_uncorrelated_event"),
            ("error", lambda request: {"type": "error", "id": request["id"], "caseId": SITE["id"], "code": "synthetic_host_failure"}, "synthetic_host_failure"),
            ("packet-error", lambda request: self.packet_event(request, error_packet), "backend_packet_error"),
            ("terminal-error", lambda request: self.terminal_event(request, success=False), "backend_terminal_failed"),
            ("failed-count", lambda request: self.terminal_event(request, failedCount=1), "backend_terminal_failed"),
            ("malformed", lambda request: self.packet_event(request, "not json"), "JSONDecodeError"),
            ("invalid-packet", lambda request: self.packet_event(request, "[]"), "host_invalid_packet"),
            ("eof", lambda request: {"type": "error", "code": "host_eof"}, "host_eof"),
            ("invalid-jsonl", lambda request: {"type": "error", "code": "host_invalid_jsonl"}, "host_invalid_jsonl")]
        for name, failure, code in scenarios:
            def events(request, requests):
                return [self.packet_event(request, first), failure(request)]
            with self.subTest(name=name):
                result, browser, _, rows = self.scripted_case(events, run_id="failure-" + name)
                self.assertEqual(result["errors"][0]["code"], code)
                self.assertEqual(browser.delivered, [first, error_packet] if name == "packet-error" else [first])
                self.assertEqual(sum(call[0] == "acknowledge" for call in browser.calls), 1)
                self.assertEqual(len([row for row in rows if row["kind"] == "host_event"]), 2)
                self.assertFalse(result["settled"])
                self.assertFalse(result["completed"])
                self.assertFalse(result["evidenceReady"])

    def test_host_error_before_first_packet_rejects_rpc_once(self):
        def events(request, requests):
            return [{"type": "error", "id": request["id"], "caseId": SITE["id"], "code": "synthetic_denial"}]
        result, browser, _, _ = self.scripted_case(events)
        self.assertEqual(result["errors"][0]["code"], "synthetic_denial")
        self.assertEqual(browser.delivered, [])
        self.assertEqual([call for call in browser.calls if call[0] == "acknowledge"],
                         [("acknowledge", "rpc-1", {"accepted": False, "reason": "synthetic_denial"})])

    def test_late_other_case_events_are_journaled_without_delivery_or_completion(self):
        packet = '{"type":"pageAutoTranslations","finished":true}'
        stale = {"type": "terminal", "id": "prior-rpc", "caseId": "prior-site", "success": True}
        def events(request, requests):
            return [stale, {**stale, "type": "packet", "packet": "{}"},
                    self.packet_event(request, packet), self.terminal_event(request)]
        result, browser, _, rows = self.scripted_case(events)
        self.assertEqual(result["errors"], [])
        self.assertEqual(browser.delivered, [packet])
        self.assertEqual(len(result["terminals"]), 1)
        self.assertEqual(result["terminals"][0]["caseId"], SITE["id"])
        self.assertEqual(len([row for row in rows if row["kind"] == "host_event"]), 4)

    def test_forward_polling_is_bounded_by_case_deadline(self):
        clock = [0.0]
        def events(request, requests):
            return [self.packet_event(request, '{"type":"pageAutoTranslations","finished":false}')] * 1000
        def poll_hook(host):
            clock[0] += .1
        browser = self.browser()
        browser.deadline = 99
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]):
            result, browser, host, rows = self.scripted_case(events, browser=browser, poll_hook=poll_hook, case_timeout=.25)
        self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement")
        self.assertEqual(host.polls, 3)
        # The first poll checks the queue before the CLI/outbound round trip.
        # It is empty; the next two actual packets consume the remaining budget.
        self.assertEqual(len([row for row in rows if row["kind"] == "host_event"]), 2)
        self.assertEqual(browser.delivered, [])
        self.assertEqual(browser.deadline, 99)
        self.assertFalse(result["settled"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

    def test_existing_qa_only_browser_interface_still_delivers_and_acks_once(self):
        browser = self.browser()
        browser.deliver_batch = None
        browser.drain_sample = None
        packets = ['{"type":"pageAutoTranslations","finished":false}',
                   '{"type":"pageAutoTranslations","finished":true}']
        def events(request, requests):
            return [self.packet_event(request, packet) for packet in packets] + [self.terminal_event(request)]
        result, browser, _, _ = self.scripted_case(events, browser=browser)
        self.assertEqual(result["errors"], [])
        self.assertEqual(browser.delivered, packets)
        self.assertEqual(sum(call[0] == "acknowledge" for call in browser.calls), 1)
        self.assertFalse(result["completed"])

    def test_rpc_acknowledgement_is_not_repeated_across_forward_passes(self):
        packets = ['{"type":"pageAutoTranslations","index":1,"finished":false}',
                   '{"type":"pageAutoTranslations","index":2,"finished":true}']
        def events(request, requests):
            # None represents a temporarily empty host queue, not an event.
            return [self.packet_event(request, packets[0]), None,
                    self.packet_event(request, packets[1]), self.terminal_event(request)]
        result, browser, _, rows = self.scripted_case(events)
        self.assertEqual(result["errors"], [])
        self.assertEqual(browser.delivered, packets)
        self.assertEqual(sum(call[0] == "acknowledge" for call in browser.calls), 1)
        self.assertEqual(len([row for row in rows if row["kind"] == "host_event"]), 3)

    def test_queued_completion_precedes_slow_cli_drain_and_dom_sample(self):
        for combined in (False, True):
            with self.subTest(combined=combined):
                clock, drains = [1.0], [0]
                browser = self.browser()
                if not combined:
                    browser.drain_sample = None
                original_qa = browser.qa
                def qa(method, *args):
                    if method == "drain" and browser.configured:
                        drains[0] += 1
                        if drains[0] == 2:
                            # Offline stand-in for an expensive wrapper/DOM call.
                            # Queued completion must have reached receive first.
                            self.assertTrue(browser.done)
                            clock[0] += .4
                    return original_qa(method, *args)
                browser.qa = qa
                packet = ' { "type": "pageAutoTranslations", "finished": true }\n'
                def events(request, requests):
                    return [None, self.packet_event(request, packet), self.terminal_event(request)]
                def sleep(seconds):
                    clock[0] += seconds
                with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                        mock.patch.object(capture.time, "sleep", sleep):
                    result, browser, _, rows = self.scripted_case(events, browser=browser,
                        run_id="host-first-" + str(combined), case_timeout=2)
                self.assertEqual(result["errors"], [])
                self.assertTrue(result["settled"])
                self.assertEqual(browser.delivered, [packet])
                configured = next(i for i, call in enumerate(browser.calls) if call[0] == "configure")
                drain_calls = [i for i, call in enumerate(browser.calls) if call[0] == "drain" and i > configured]
                delivery = next(i for i, call in enumerate(browser.calls) if call[0] == "deliver")
                self.assertLess(delivery, drain_calls[1])
                self.assertEqual(len([row for row in rows if row["kind"] == "host_event"]), 2)
                combined_calls = [call for call in browser.calls if call[0] == "eval"
                                  and "outbound:q.drain()" in call[1]]
                self.assertEqual(bool(combined_calls), combined)
                self.assertFalse(result["completed"])  # Synthetic transport never certifies a website.

    def test_frontend_timer_failure_survives_successful_late_terminal_and_cancel_ack(self):
        clock, expired = [1.0], [False]
        browser = self.browser()
        original_qa = browser.qa
        def qa(method, *args):
            if method == "deliver" and expired[0]:
                # Production rejects updates for an expired job. Preserve the
                # original packet and failed source rather than restoring success.
                browser.calls.append((method, *args))
                browser.delivered.append(args[1])
                return False
            value = original_qa(method, *args)
            if method == "sample" and expired[0]:
                value["status"] = {"state": "failed", "reason": "translation_timeout"}
                value["events"] = [{"type": "status", "value": value["status"]}]
            return value
        browser.qa = qa
        packet = '{"type":"pageAutoTranslations","finished":true}'
        def events(request, requests):
            if request["action"] == "cancel":
                return []  # Same-case late cancellation has no extra terminal.
            return [None, self.packet_event(request, packet), self.terminal_event(request, elapsedMilliseconds=667)]
        def sleep(seconds):
            clock[0] += seconds
            if not expired[0]:
                expired[0] = browser.done = True
                browser.predictions[0].update(completed=False, failed=True)
                browser.outgoing.append({"rpcId": "cancel-rpc", "message": {
                    "type": "pageAutoCancel", "requestId": "req1", "reason": "translation_timeout"}})
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", sleep):
            result, browser, host, rows = self.scripted_case(events, browser=browser,
                run_id="frontend-timeout", case_timeout=.03)
        self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement")
        self.assertEqual(browser.delivered, [packet])
        self.assertEqual(len(result["terminals"]), 1)
        self.assertTrue(result["terminals"][0]["success"])
        cancelled = next(request for request in host.requests if request["action"] == "cancel")
        original = next(request for request in host.requests if request["action"] == "message")
        self.assertEqual(cancelled["id"], original["id"])
        self.assertEqual(cancelled["message"]["reason"], "translation_timeout")
        self.assertEqual(result["cancellations"][0]["id"], original["id"])
        self.assertTrue(any(row["kind"] == "sample" and row["value"]["status"].get("reason")
                            == "translation_timeout" for row in rows))
        self.assertTrue(result["predictions"][0]["failed"])
        self.assertFalse(result["settled"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

    def test_finished_frame_without_host_terminal_cannot_settle_or_complete(self):
        clock = [1.0]
        packet = '{"type":"pageAutoTranslations","finished":true}'
        def events(request, requests):
            return [self.packet_event(request, packet)]
        def sleep(seconds):
            clock[0] += seconds
        with mock.patch.object(capture.time, "monotonic", lambda: clock[0]), \
                mock.patch.object(capture.time, "sleep", sleep):
            result, browser, _, _ = self.scripted_case(events, case_timeout=.01)
        self.assertEqual(browser.delivered, [packet])
        self.assertEqual(result["errors"][0]["code"], "case_timeout_without_settlement")
        self.assertEqual(result["terminals"], [])
        self.assertFalse(result["settled"])
        self.assertFalse(result["evidenceReady"])
        self.assertFalse(result["completed"])

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
        config = next(call[1] for call in browser.calls if call[0] == "configure")
        self.assertEqual(config["chineseProseShareThresholdPercent"], 50)
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
