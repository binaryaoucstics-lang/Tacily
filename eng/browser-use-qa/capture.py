"""Browser Use CLI evidence driver. Standard library only; no browser/network fallback."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import threading
import time
import traceback
import uuid
from datetime import datetime, timezone
from urllib.parse import urlsplit

WRAPPER = Path("C:/Users/cool848457/.codex/skills/browser-use-cli/scripts/run_browser_use.py")
HERE = Path(__file__).resolve().parent
PRODUCTION = ("page-auto-policy.js", "translation-motion.js", "page-auto-content.js")
EVAL_LIMIT_BYTES = 20000
SOURCE_CHUNK_CHARACTERS = 8000
DELIVERY_BATCH_PACKETS = 32
FORWARD_EVENT_LIMIT = 1000
REPLACE_RETRY_DELAYS = (.02, .05, .1)
FINAL_SCREENSHOT_REFRESH_LIMIT = 3


class CaptureError(RuntimeError):
    pass


class BackendFailure(CaptureError):
    """Control flow only: the original backend errors are already recorded."""


def terminal_valid(event):
    count = event.get("requestCount")
    return (event.get("success") is True and not event.get("failedCount", 0)
            and type(count) is int and count >= 0)


def now():
    return datetime.now(timezone.utc).isoformat()


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def check_deadline(deadline):
    if deadline is not None and time.monotonic() >= deadline:
        raise CaptureError("case_timeout_without_settlement")


def delivery_expression(frames, stored=False):
    # Synchronous execution is essential: CLI eval does not await Promises.
    data = "globalThis.__qaFrameBatch" if stored else encode(frames)
    expression = ("(()=>{const q=globalThis.__websiteQa;for(const [rpcId,packet,ack] of " + data +
                  "){if(ack)q.acknowledge(rpcId,{accepted:true});q.deliver(rpcId,packet);}return true})()")
    if stored:
        expression = "(()=>{try{return " + expression + "}finally{delete globalThis.__qaFrameBatch}})()"
    return expression


def semantic_signature(value):
    """Current content multiset, independent of motion and bookkeeping identity.

    Do not use sourceNodes here: that is the cumulative, unfiltered audit history.
    Sorting encoded rows preserves duplicate counts without relying on DOM order.
    """
    if value["pageDecision"] is False:
        # A live Chinese ticker can change forever while the production decision
        # remains skipped. Stability here means the decision is stable, not that
        # the site's authored content stopped changing. Failures/jobs are checked
        # separately and all source/geometry snapshots remain in the journal.
        # The adapter retains ordered events. Resets/reassessments between two
        # idle samples must restart the quiet period even if both end in false.
        activity = [[event["type"], event.get("value")] for event in value.get("events", [])
                    if event["type"] in ("decision", "outbound") or
                    (event["type"] == "status" and
                     event.get("value", {}).get("state") in ("assessing", "waiting_for_page"))]
        return encode([False, any(p.get("failed") for p in value["predictions"]), activity])
    nodes = sorted(encode([row.get(key) for key in
                          ("sourceText", "displayedText", "tag", "role")])
                   for row in value["nodes"])
    predictions = sorted(encode([row.get(key) for key in
                                ("sourceText", "text", "eligible", "completed", "failed",
                                 "sourceEvidence", "auditIssue")])
                         for row in value["predictions"])
    return encode([nodes, predictions, value["pageDecision"]])


def geometry_signature(value):
    """Diagnostic only; matching geometry never certifies layout quality."""
    nodes = sorted(encode([row.get(key) for key in
                          ("rects", "rendered", "ancestorsVisible", "inViewport")])
                   for row in value["nodes"])
    return encode([nodes, value["viewport"]])


def screenshot_content_signature(value, dynamic_sources=()):
    """Visible content, including authored text on rules-only skipped pages."""
    nodes = sorted(encode(["dynamic-source", row["id"], row.get("tag"), row.get("role")]
                         if row.get("id") in dynamic_sources else
                         [row.get(key) for key in ("sourceText", "displayedText", "tag", "role")])
                   for row in value["nodes"])
    return hashlib.sha256(encode([nodes, value["pageDecision"]]).encode("utf-8")).hexdigest()


def content_observation(value):
    """The actual visible rows and their independent evidence at one timestamp."""
    visible = {row["id"] for row in value["nodes"]}
    return {"at": value.get("at"), "pageDecision": value["pageDecision"],
            "nodes": value["nodes"], "predictions": value["predictions"],
            "sourceNodes": [row for row in value["sourceNodes"] if row["id"] in visible]}


def authored_content_changes(before, after, observed=()):
    """Explain source versions/visibility, never changed display on the same source.

    IDs come from the independent adapter traversal. A locator plus unchanged
    source also protects against display changes disguised by a fresh ID. Keep
    last visible rows across gaps; hiding and revealing a source is not a license
    to change its translation. Geometry remains an independent review question.
    """
    changes, dynamic, unexplained = [], set(), set()
    seen, seen_predictions = {}, {}
    previous = None
    valid = before["pageDecision"] is after["pageDecision"]
    for value in (before, *observed, after):
        valid &= value["pageDecision"] is after["pageDecision"]
        nodes = {row["id"]: row for row in value["nodes"]}
        independent = {row["id"]: row for row in value["sourceNodes"]}
        valid &= len(nodes) == len(value["nodes"])
        for sid, row in nodes.items():
            source = row.get("sourceText")
            valid &= (isinstance(source, str) and isinstance(row.get("displayedText"), str)
                      and sid in independent and independent[sid].get("sourceText") == source)
            keys = [("id", sid, source)]
            if row.get("locator"):
                keys.append(("locator", row["locator"], source))
            display = [row.get(k) for k in ("displayedText", "tag", "role")]
            for key in keys:
                if key in seen and seen[key] != display:
                    unexplained.add(sid)
                seen[key] = display
        predictions = {}
        for prediction in value["predictions"]:
            key = (prediction.get("sourceId"), prediction.get("sourceText"))
            predictions.setdefault(key, []).append(encode([prediction.get(k) for k in
                ("text", "eligible", "completed", "failed", "sourceEvidence", "auditIssue")]))
        for key, pieces in predictions.items():
            state = sorted(pieces)
            if key in seen_predictions and seen_predictions[key] != state:
                unexplained.add(key[0])
            seen_predictions[key] = state
        if previous is not None:
            added, removed = nodes.keys() - previous.keys(), previous.keys() - nodes.keys()
            versions = {sid for sid in nodes.keys() & previous.keys()
                        if nodes[sid].get("sourceText") != previous[sid].get("sourceText")}
            if added or removed or versions:
                dynamic.update(added | removed | versions)
                changes.append({"at": value.get("at"), "addedSourceIds": sorted(added),
                                "removedSourceIds": sorted(removed), "changedSourceIds": sorted(versions)})
        previous = nodes
    return {"explained": bool(changes) and bool(valid) and not unexplained,
            "independentEvidenceComplete": bool(valid), "changes": changes,
            "dynamicSourceIds": sorted(dynamic),
            "unexplainedSourceIds": sorted(sid for sid in unexplained if sid is not None)}


def error_record(error, prefix=""):
    record = {"code": prefix + (str(error) if isinstance(error, CaptureError) else type(error).__name__), "at": now()}
    if isinstance(error, OSError):
        # Local evidence I/O diagnostics only; never serialize arbitrary exception
        # messages, configuration or frame locals.
        record.update(errno=error.errno, winerror=getattr(error, "winerror", None),
                      filename=error.filename, filename2=error.filename2,
                      trace=[{"file": frame.filename, "line": frame.lineno, "function": frame.name}
                             for frame in traceback.extract_tb(error.__traceback__)])
    return record


def atomic_json(path, value, on_retry=None):
    path = Path(path)
    # A failed save's fully flushed temporary remains available for diagnosis.
    # A later save must not overwrite it, nor another writer's temporary.
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    with temporary.open("x", encoding="utf-8") as stream:
        stream.write(encode(value) + "\n")
        stream.flush()
        os.fsync(stream.fileno())
    for attempt in range(len(REPLACE_RETRY_DELAYS) + 1):
        try:
            # The writer handle has been closed before every replace attempt.
            os.replace(temporary, path)
            return
        except PermissionError as error:
            # Windows replace can report ACCESS_DENIED (5) as well as sharing/
            # lock violations (32/33) for temporary external handle conflicts.
            # Only retry this closed-file operation, never an open/ACL failure.
            if getattr(error, "winerror", None) not in (5, 32, 33) or attempt == len(REPLACE_RETRY_DELAYS):
                raise
            diagnostic = {"kind": "evidence_io_retry", "operation": "replace", "attempt": attempt + 1,
                          "delaySeconds": REPLACE_RETRY_DELAYS[attempt], **error_record(error)}
            if on_retry is None:
                print(encode(diagnostic), file=sys.stderr, flush=True)
            else:
                on_retry(diagnostic)
            time.sleep(REPLACE_RETRY_DELAYS[attempt])


class Journal:
    def __init__(self, path):
        self.path = Path(path)
        self.lock = threading.Lock()

    def append(self, value):
        with self.lock, self.path.open("a", encoding="utf-8") as stream:
            stream.write(encode({"at": now(), **value}) + "\n")
            stream.flush()
            os.fsync(stream.fileno())


class BrowserCli:
    def __init__(self, session, journal, runner=subprocess.run, python=sys.executable):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", session):
            raise CaptureError("invalid_session")
        self.session, self.journal, self.runner, self.python = session, journal, runner, python
        self.deadline = None

    def command(self, *arguments):
        deadline = self.deadline
        check_deadline(deadline)
        timeout = min(60, deadline - time.monotonic()) if deadline is not None else 60
        if timeout <= 0:
            raise CaptureError("case_timeout_without_settlement")
        argv = [self.python, str(WRAPPER), "--session", self.session, "--json", *map(str, arguments)]
        try:
            result = self.runner(argv, shell=False, capture_output=True, text=True,
                                 encoding="utf-8", timeout=timeout)
        except (OSError, subprocess.TimeoutExpired) as error:
            self.journal.append({"kind": "cli_failure", "argv": argv, "error": type(error).__name__})
            check_deadline(deadline)
            raise CaptureError("cli_unavailable_or_timeout") from error
        self.journal.append({"kind": "cli", "argv": argv, "exitCode": result.returncode,
                             "stdout": result.stdout, "stderr": result.stderr})
        check_deadline(deadline)
        if result.returncode:
            raise CaptureError("cli_nonzero_exit")
        try:
            envelope = json.loads(result.stdout)
        except ValueError as error:
            raise CaptureError("cli_invalid_json") from error
        if not isinstance(envelope, dict) or envelope.get("success") is False or envelope.get("error"):
            raise CaptureError("cli_error_response")
        return envelope.get("data", envelope)

    def evaluate(self, expression):
        if len(expression.encode("utf-16-le")) > EVAL_LIMIT_BYTES:
            raise CaptureError("eval_argument_too_large")
        data = self.command("eval", expression)
        if not isinstance(data, dict) or "result" not in data:
            raise CaptureError("cli_missing_eval_result")
        value = data["result"]
        # CLI versions may encode an evaluated JSON value as a string.
        if isinstance(value, str):
            try:
                return json.loads(value)
            except ValueError:
                return value
        return value

    def inject(self, source):
        self.evaluate("globalThis.__qaSourceChunks=[];true")
        # Budget the actual serialized expression, including escapes and UTF-16
        # surrogate pairs. Ordinary source uses 8000-character chunks.
        offset = 0
        while offset < len(source):
            def expression(length):
                return "globalThis.__qaSourceChunks.push(" + encode(source[offset:offset + length]) + ");true"
            length = min(SOURCE_CHUNK_CHARACTERS, len(source) - offset)
            if len(expression(length).encode("utf-16-le")) > EVAL_LIMIT_BYTES:
                low, high = 0, length
                while low < high:
                    middle = (low + high + 1) // 2
                    if len(expression(middle).encode("utf-16-le")) <= EVAL_LIMIT_BYTES:
                        low = middle
                    else:
                        high = middle - 1
                length = low
            if not length:
                raise CaptureError("source_chunk_exceeds_eval_budget")
            self.evaluate(expression(length))
            offset += length
        return self.evaluate("(0,eval)(globalThis.__qaSourceChunks.join(''));delete globalThis.__qaSourceChunks;true")

    def state(self):
        return self.command("state")  # Preserve data._raw_text without parsing human output.

    def screenshot(self, path, full=False):
        arguments = ("--full",) if full else ()
        self.command("screenshot", str(path), *arguments)
        if not Path(path).is_file() or Path(path).stat().st_size == 0:
            raise CaptureError("screenshot_missing")

    def qa(self, method, *args):
        return self.evaluate("globalThis.__websiteQa." + method + "(" + ",".join(map(encode, args)) + ")")

    def drain_sample(self, phase):
        """One synchronous CLI round trip; sample before draining preserves rpcPending."""
        value = self.evaluate("(()=>{const q=globalThis.__websiteQa;return {sample:q.sample(" +
                              encode(phase) + "),outbound:q.drain()}})()")
        if not isinstance(value, dict) or not isinstance(value.get("sample"), dict) \
                or not isinstance(value.get("outbound"), list):
            raise CaptureError("adapter_lost_or_invalid")
        return value

    def deliver_batch(self, frames):
        """Deliver a bounded ordered batch of [rpcId, original packet, acknowledge]."""
        if not frames:
            return
        if len(frames) > DELIVERY_BATCH_PACKETS:
            raise CaptureError("delivery_batch_too_many_packets")
        expression = delivery_expression(frames)
        if len(expression.encode("utf-16-le")) > EVAL_LIMIT_BYTES:
            if len(frames) != 1:
                raise CaptureError("delivery_batch_too_large")
            # Chunk the data assignment with the existing safe injector. The
            # frame itself is never parsed/re-serialized or split for receive.
            self.inject("globalThis.__qaFrameBatch=" + encode(frames) + ";true")
            expression = delivery_expression(frames, stored=True)
        if self.evaluate(expression) is not True:
            raise CaptureError("delivery_invalid_result")

    def probe(self, target_language, timeout=15, poll_seconds=.15):
        """CLI eval serializes Promises as {}; poll a synchronous QA job instead."""
        if timeout <= 0:
            raise CaptureError("probe_timeout")
        deadline = time.monotonic() + timeout
        job = self.qa("startProbe", target_language)
        if not isinstance(job, dict) or not isinstance(job.get("id"), str) or not job["id"] \
                or job.get("stage") != "running":
            raise CaptureError("probe_invalid_start")
        while time.monotonic() < deadline:
            state = self.qa("probeStatus", job["id"])
            if time.monotonic() >= deadline:
                raise CaptureError("probe_timeout")
            if not isinstance(state, dict) or state.get("id") != job["id"]:
                raise CaptureError("probe_invalid_status")
            if state.get("stage") == "failed":
                raise CaptureError("probe_failed")
            if state.get("stage") == "completed":
                result = state.get("result")
                if not isinstance(result, dict) or not isinstance(result.get("samples"), list) \
                        or not isinstance(result.get("statistics"), dict):
                    raise CaptureError("probe_invalid_evidence")
                return {"samples": result["samples"], "statistics": result["statistics"]}
            if state.get("stage") != "running":
                raise CaptureError("probe_invalid_stage")
            time.sleep(min(poll_seconds, max(0, deadline - time.monotonic())))
        raise CaptureError("probe_timeout")


class JsonlHost:
    """Persistent JSONL process; stdout read concurrently with CLI calls."""
    def __init__(self, command, journal, factory=subprocess.Popen):
        if not isinstance(command, list) or not command or not all(isinstance(x, str) for x in command):
            raise CaptureError("host_command_must_be_string_list")
        self.journal, self.events, self.backlog = journal, queue.Queue(), []
        self.sent_requests = {}
        self.process = factory(command, shell=False, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
        self.threads = [threading.Thread(target=self._read, daemon=True),
                        threading.Thread(target=self._stderr, daemon=True)]
        for thread in self.threads:
            thread.start()

    def _read(self):
        try:
            for line in self.process.stdout:
                self.journal.append({"kind": "host_stdout", "line": line.rstrip("\n")})
                try:
                    event = json.loads(line)
                    if not isinstance(event, dict) or not isinstance(event.get("type"), str):
                        raise ValueError()
                except ValueError:
                    event = {"type": "error", "code": "host_invalid_jsonl"}
                self.events.put(event)
        finally:
            self.events.put({"type": "error", "code": "host_eof"})

    def _stderr(self):
        for line in self.process.stderr:
            self.journal.append({"kind": "host_stderr", "line": line.rstrip("\n")})

    def send(self, request):
        self.journal.append({"kind": "host_request", "request": request})
        try:
            self.process.stdin.write(encode(request) + "\n")
            self.process.stdin.flush()
        except (OSError, ValueError) as error:
            raise CaptureError("host_write_failed") from error
        self.sent_requests[(request.get("id"), request.get("caseId"))] = request.get("action")

    def owns_event(self, event):
        return (event.get("id"), event.get("caseId")) in self.sent_requests

    def validate_late_event(self, event):
        action = self.sent_requests.get((event.get("id"), event.get("caseId")))
        allowed = {"capabilities": ("capabilities", "error"), "assess": ("assessment", "error"),
                   "message": ("packet", "terminal", "error"), "cancel": ("packet", "terminal", "error")}
        if event.get("type") not in allowed.get(action, ()):
            raise CaptureError("host_uncorrelated_event")
        if event["type"] == "packet":
            try:
                packet = event["packet"]
                wire = json.loads(packet) if isinstance(packet, str) else packet
                if not isinstance(wire, dict):
                    raise ValueError()
            except (KeyError, TypeError, ValueError) as error:
                raise CaptureError("host_invalid_packet") from error
        if event["type"] == "capabilities" and (type(event.get("live")) is not bool or
                not isinstance(event.get("targetLanguage"), str) or
                not 0 < len(event["targetLanguage"]) <= 64):
            raise CaptureError("invalid_capabilities")

    def poll(self, timeout=0):
        if self.backlog:
            return self.backlog.pop(0)
        try:
            return self.events.get(timeout=timeout)
        except queue.Empty:
            return None

    def capabilities(self, case_id, timeout=15):
        if not isinstance(case_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", case_id):
            raise CaptureError("invalid_capabilities_case_id")
        request = {"schemaVersion": 1, "id": "capabilities-" + uuid.uuid4().hex,
                   "action": "capabilities", "caseId": case_id}
        self.send(request)
        deadline = time.monotonic() + timeout
        # Do not poll events deferred by this handshake again. Restore the
        # original order even on failure so later forwarding retains the evidence.
        deferred = []
        events_read = 0
        try:
            while time.monotonic() < deadline:
                event = self.poll(min(.25, max(0, deadline - time.monotonic())))
                if time.monotonic() >= deadline:
                    if event is not None:
                        self.backlog.insert(0, event)
                    raise CaptureError("capabilities_timeout")
                if event is None:
                    continue
                events_read += 1
                if event.get("code") in ("host_eof", "host_invalid_jsonl"):
                    raise CaptureError(event["code"])
                if event.get("caseId") != case_id and self.owns_event(event):
                    deferred.append(event)
                    self.journal.append({"kind": "host_deferred_event", "waitingFor": request, "event": event})
                    self.validate_late_event(event)
                    if events_read >= FORWARD_EVENT_LIMIT:
                        raise CaptureError("capabilities_event_limit")
                    continue
                if event.get("id") != request["id"] or event.get("caseId") != case_id:
                    raise CaptureError("capabilities_identity_mismatch")
                if event.get("type") == "capabilities":
                    if type(event.get("live")) is not bool or not isinstance(event.get("targetLanguage"), str) \
                            or not event["targetLanguage"] or len(event["targetLanguage"]) > 64:
                        raise CaptureError("invalid_capabilities")
                    return event
                if event.get("type") == "error":
                    raise CaptureError(event.get("code", "host_error"))
                raise CaptureError("host_unexpected_capabilities_event")
            raise CaptureError("capabilities_timeout")
        finally:
            self.backlog[:0] = deferred

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=5)
        for thread in self.threads:
            thread.join(timeout=1)
        self.process.stdout.close()
        self.process.stderr.close()


def read_manifest(path):
    raw = Path(path).read_bytes()
    value = json.loads(raw)
    sites = value if isinstance(value, list) else value.get("sites")
    if not isinstance(sites, list) or not sites:
        raise CaptureError("manifest_requires_nonempty_sites")
    ids, domains = set(), set()
    required = ("id", "registeredDomain", "url", "category", "language", "expectedPageTranslation", "split")
    for site in sites:
        if not isinstance(site, dict) or any(key not in site for key in required):
            raise CaptureError("invalid_manifest_site")
        if not isinstance(site["id"], str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", site["id"]):
            raise CaptureError("invalid_case_id")
        if type(site["expectedPageTranslation"]) is not bool or site["split"] not in ("development", "holdout"):
            raise CaptureError("invalid_manifest_label")
        domain = site["registeredDomain"].lower()
        if domain in domains or site["id"] in ids:
            raise CaptureError("duplicate_manifest_domain_or_id")
        if not re.fullmatch(r"[a-z0-9-]+(?:\.[a-z0-9-]+)+", domain) or not belongs_to(site["url"], domain):
            raise CaptureError("manifest_url_outside_domain")
        domains.add(domain); ids.add(site["id"])
    return sites, hashlib.sha256(raw).hexdigest()


def belongs_to(url, domain):
    domain = domain.lower()
    parsed = urlsplit(url)
    host = (parsed.hostname or "").lower()
    return parsed.scheme in ("https", "http") and not parsed.username and not parsed.password and (host == domain or host.endswith("." + domain))


def source_identity(repo, runner=subprocess.run):
    result = runner(["git", "rev-parse", "HEAD"], cwd=repo, shell=False, capture_output=True,
                    text=True, encoding="utf-8", timeout=10)
    head = result.stdout.strip()
    if result.returncode or not re.fullmatch(r"[0-9a-f]{40}", head):
        raise CaptureError("source_head_unavailable")
    hashes = {name: hashlib.sha256((repo / "browser-extension" / name).read_bytes()).hexdigest()
              for name in PRODUCTION}
    hashes["page-adapter.js"] = hashlib.sha256((HERE / "page-adapter.js").read_bytes()).hexdigest()
    hashes["capture.py"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    return {"sourceHead": head, "sourceHashes": hashes}


class EvidenceRun:
    def __init__(self, root, run_id, frozen, resume=False):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", run_id):
            raise CaptureError("invalid_run_id")
        self.path = Path(root) / run_id
        if resume:
            saved = json.loads((self.path / "run.json").read_text(encoding="utf-8"))
            if saved["frozen"] != frozen:
                raise CaptureError("resume_source_or_manifest_changed")
        else:
            self.path.mkdir(parents=True, exist_ok=False)
            atomic_json(self.path / "run.json", {"schemaVersion": 1, "createdAt": now(), "frozen": frozen})
        self.frozen = frozen

    def attempt(self, case_id):
        parent = self.path / case_id
        parent.mkdir(exist_ok=True)
        number = 1
        while (parent / f"attempt-{number:04d}").exists():
            number += 1
        path = parent / f"attempt-{number:04d}"
        path.mkdir()
        return path

    def captured(self, case_id):
        for file in (self.path / case_id).glob("attempt-*/case.json"):
            value = json.loads(file.read_text(encoding="utf-8"))
            if value.get("captureFinished") is True and value.get("evidenceReady") is True:
                return True
        return False


def completion_gate(case, path):
    """Capture readiness and audited completion are separate; no mock counts as complete."""
    reasons = []
    if not case.get("visitSuccess") or not case.get("domainVerified"):
        reasons.append("visit_or_domain_invalid")
    if case.get("errors"):
        reasons.append("capture_or_backend_errors")
    if type(case.get("pageDecision")) is not bool:
        reasons.append("page_decision_missing")
    if case.get("pageDecision") is not case["expectedPageTranslation"]:
        reasons.append("page_decision_mismatch")
    required = {"before", "after", "stable", "scroll-before", "scroll-after", "return-after", "reflow-after"}
    if case.get("fullScreenshots"):
        required.update(("before-full", "after-full", "stable-full"))
    shots = {shot["phase"]: shot for shot in case.get("screenshots", [])}
    if not required.issubset(shots) or any(not (path / shot["path"]).is_file()
                                         or (path / shot["path"]).stat().st_size == 0 for shot in shots.values()):
        reasons.append("screenshots_incomplete")
    if not case.get("sourceNodes") or not case.get("settled"):
        reasons.append("coverage_or_settlement_missing")
    source_ids = {row["id"] for row in case.get("sourceNodes", [])}
    if any(p.get("sourceEvidence") == "unmatched" or p.get("auditIssue")
           or p.get("sourceId") not in source_ids for p in case.get("predictions", [])):
        reasons.append("prediction_source_evidence_unmatched")
    if any(any(sid not in source_ids for sid in p.get("sourceIds", [p.get("sourceId")]))
           for p in case.get("predictions", []) + case.get("predictionHistory", [])):
        reasons.append("historical_prediction_source_evidence_unmatched")
    terminals = case.get("terminals", [])
    requests = case.get("requestIds", [])
    by_id = {event["id"]: event for event in terminals}
    if any(r not in by_id or not terminal_valid(by_id[r]) for r in requests) or any(
            not terminal_valid(event) for event in terminals):
        reasons.append("terminal_evidence_missing")
    if case.get("retractions"):
        reasons.append("translation_retractions")
    if any(p.get("failed") for p in case.get("predictions", [])):
        reasons.append("node_failure")
    final_state = case.get("finalState", {})
    if any(final_state.get(key) for key in ("jobs", "pending", "scanning", "rpcPending")) or (
            final_state and (type(final_state.get("pageDecision")) is not bool or
                             (final_state.get("status") or {}).get("state") not in ("skipped", "completed"))):
        reasons.append("production_work_unresolved")
    if case["expectedPageTranslation"]:
        if not case.get("liveTranslation"):
            reasons.append("live_translation_required")
        if not requests and "terminal_evidence_missing" not in reasons:
            reasons.append("terminal_evidence_missing")
        if not any(type(event.get("requestCount")) is int and event["requestCount"] > 0 for event in terminals):
            reasons.append("real_model_request_missing")
        if not case.get("predictions") or any(p.get("failed") or not p.get("completed") for p in case["predictions"]):
            reasons.append("node_completion_missing")
    elif any(type(event.get("requestCount")) is int and event["requestCount"] > 0 for event in terminals):
        # Native candidates may only assess rules. Actual model calls are proved
        # by valid terminals, never inferred from candidate/RPC counts.
        reasons.append("unnecessary_translation_request")
    if case.get("simulation"):
        reasons.append("simulation_not_real_case")
    ready = not reasons
    reviews = case.get("screenshotReview", {})
    reviewed = all(reviews.get(phase) is True for phase in required)
    if not reviewed:
        reasons.append("screenshot_review_pending")
    return ready, ready and reviewed, reasons


def record_source_observations(observations, value, phase):
    """Keep each source's last production observation while it was represented.

    This records production flags, never truth. A later hidden source must not
    become a rejected candidate just because its former session blocks retired.
    Sources that disappear before an idle observation remain unknown.
    """
    if type(value.get("pageDecision")) is not bool:
        return
    idle = not any(value.get(key) for key in ("jobs", "pending", "scanning", "rpcPending"))
    pieces = {}
    for piece in value["predictions"]:
        for source_id in piece.get("sourceIds", [piece.get("sourceId")]):
            pieces.setdefault(source_id, []).append(piece)
    eligibility = {row["sourceId"]: row["eligible"] for row in value.get("productionEligibility", [])}
    visible_ids = {source["id"] for source in value["nodes"]}
    independent = {source["id"]: source for source in value["sourceNodes"]}
    represented = {}
    for piece in value["predictions"]:
        owner = piece.get("sourceId")
        members = piece.get("sourceIds", [])
        if owner in visible_ids and len(members) > 1:
            for member in members:
                if member != owner:
                    represented[member] = owner
    # Atomic blocks represent their explicit peer records at the owner node.
    # Membership is taken from production records, never guessed from text.
    for source_id in (visible_ids | represented.keys()) & independent.keys():
        if source_id in eligibility:
            observations[source_id] = {"sourceId": source_id, "phase": phase,
                                       "at": value.get("at"), "rendered": source_id in visible_ids,
                                       "representedBy": represented.get(source_id),
                                       "sourceText": independent[source_id]["sourceText"],
                                       "pageDecision": value["pageDecision"], "idle": idle,
                                       "eligible": eligibility[source_id] if idle or source_id in pieces else None,
                                       "pieces": pieces.get(source_id, [])}


def _observation_time(value):
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            raise ValueError("timezone_missing")
        return parsed
    except (AttributeError, TypeError, ValueError) as error:
        raise CaptureError("invalid_producer_observation_timestamp") from error


def _source_snapshot(source):
    # currentlyRendered describes the controller sample, not historical geometry.
    return {key: value for key, value in source.items() if key != "currentlyRendered"}


def _validate_source_geometry(source):
    if (not isinstance(source.get("locator"), str)
            or not re.fullmatch(r"\d+(?:/(?:\d+|shadow))*", source["locator"])):
        raise CaptureError("invalid_producer_observation_source")
    rects = source.get("rects")
    if (not isinstance(rects, list) or not rects or any(not isinstance(rect, dict)
            or any(type(rect.get(key)) not in (int, float) or not math.isfinite(rect[key])
                   for key in ("x", "y", "width", "height", "documentX", "documentY"))
            or rect["width"] <= 0 or rect["height"] <= 0 for rect in rects)):
        raise CaptureError("invalid_producer_observation_geometry")
    if (type(source.get("rendered")) is not bool
            or type(source.get("ancestorsVisible")) is not bool
            or type(source.get("inViewport")) is not bool
            or source["rendered"] and not source["ancestorsVisible"]):
        raise CaptureError("invalid_producer_observation_visibility")


def _independent_source_versions(value, source_history, sample_at):
    if "independentSourceObservations" not in value:
        return None
    versions = value["independentSourceObservations"]
    dropped = value.get("independentSourceObservationDropped")
    if (not isinstance(versions, list) or len(versions) > 2048
            or len(encode(versions)) > 2000000 + 4096 or type(dropped) is not int or dropped < 0):
        raise CaptureError("invalid_independent_source_observations")
    if dropped:
        raise CaptureError("independent_source_observation_overflow")
    indexed, latest = {}, {}
    for version in versions:
        if not isinstance(version, dict) or not isinstance(version.get("source"), dict):
            raise CaptureError("invalid_independent_source_observation")
        source = version["source"]
        source_id, sequence = source.get("id"), version.get("sequence")
        independent = source_history.get(source_id) if isinstance(source_id, str) else None
        if (independent is None or type(sequence) is not int or sequence <= 0
                or any(source.get(key) != independent.get(key) for key in ("id", "sourceText", "firstSeen"))
                or not isinstance(source.get("sourceText"), str) or not source["sourceText"].strip()
                or not isinstance(source.get("phase"), str) or not source["phase"]):
            raise CaptureError("invalid_independent_source_observation")
        at = _observation_time(version.get("at"))
        if not _observation_time(source.get("firstSeen")) <= at <= sample_at:
            raise CaptureError("invalid_producer_observation_timestamp")
        _validate_source_geometry(source)
        key = (source_id, at, sequence)
        if key in indexed:
            raise CaptureError("invalid_independent_source_observation")
        indexed[key] = _source_snapshot(source)
        if source_id not in latest or latest[source_id][0] < (at, sequence):
            latest[source_id] = ((at, sequence), source)
    for source_id, (_, source) in latest.items():
        if _source_snapshot(source) != _source_snapshot(source_history[source_id]):
            raise CaptureError("independent_source_observation_not_current")
    return indexed


def _source_version_matches(source, reference, indexed, independent, observed_at, observed_sequence):
    if indexed is None:
        # Legacy samples have no trajectory proof: require the actual current row.
        return _source_snapshot(source) == _source_snapshot(independent)
    if (not isinstance(reference, dict) or type(reference.get("sequence")) is not int
            or reference["sequence"] <= 0):
        return False
    at = _observation_time(reference.get("at"))
    if (at, reference["sequence"]) > (observed_at, observed_sequence):
        return False
    return indexed.get((source["id"], at, reference["sequence"])) == _source_snapshot(source)


def _expand_producer_transport(value, indexed):
    """Decode internal adapter references; the public v1 case stays unchanged.

    The two original budgets apply to actual retained wire data, including all
    work/piece definitions. Source definitions are charged to the independent
    version budget. Every reference must resolve before any evidence is imported.
    """
    transport = value.get("producerObservationTransport")
    if (not isinstance(transport, dict) or set(transport) != {"schemaVersion", "values"}
            or type(transport["schemaVersion"]) is not int or transport["schemaVersion"] != 1
            or not isinstance(transport["values"], dict) or indexed is None):
        raise CaptureError("invalid_producer_observation_transport")
    definitions = transport["values"]
    if any(not isinstance(key, str) or not re.fullmatch(r"[1-9]\d*", key)
           or not isinstance(item, dict) for key, item in definitions.items()):
        raise CaptureError("invalid_producer_observation_transport")
    used, expanded = set(), []

    def definition(reference):
        if type(reference) is not int or reference <= 0 or str(reference) not in definitions:
            raise CaptureError("invalid_producer_observation_transport_reference")
        used.add(str(reference))
        return definitions[str(reference)]

    def source(source_id, reference):
        if (not isinstance(source_id, str) or not isinstance(reference, dict)
                or set(reference) != {"at", "sequence"}
                or type(reference["sequence"]) is not int or reference["sequence"] <= 0):
            raise CaptureError("invalid_producer_observation_transport_reference")
        snapshot = indexed.get((source_id, _observation_time(reference["at"]), reference["sequence"]))
        if snapshot is None:
            raise CaptureError("invalid_producer_observation_transport_reference")
        return snapshot

    fields = {"sourceId", "phase", "at", "sequence", "sourceObservation",
              "representedSourceObservation", "rendered", "representedBy", "pageDecision",
              "work", "eligible", "pieces"}
    for row in value["producerObservations"]:
        if not isinstance(row, dict) or set(row) != fields or not isinstance(row["pieces"], list):
            raise CaptureError("invalid_producer_observation_transport")
        snapshot = source(row["sourceId"], row["sourceObservation"])
        owner = None
        if row["representedBy"] is not None:
            owner = source(row["representedBy"], row["representedSourceObservation"])
        elif row["representedSourceObservation"] is not None:
            raise CaptureError("invalid_producer_observation_transport_reference")
        expanded.append({**row, "source": snapshot, "sourceText": snapshot["sourceText"],
                         "representedSource": owner, "work": definition(row["work"]),
                         "pieces": [definition(reference) for reference in row["pieces"]]})
    if used != definitions.keys():
        raise CaptureError("invalid_producer_observation_transport")
    return expanded


def record_producer_observations(observations, value, source_history, prediction_history):
    """Validate and import actual intervening producer views into the v1 interface.

    The raw sample is journaled before this call. Historical views never replace
    newer source facts or a different version of an already journaled piece.
    Stale piece versions remain unknown rather than borrowing hidden completion.
    Validation is atomic: malformed input cannot partially change accounting.
    """
    rows = value.get("producerObservations")
    dropped = value.get("producerObservationDropped")
    retained_characters = len(encode(rows))
    if "producerObservationTransport" in value:
        retained_characters += len(encode(value["producerObservationTransport"]))
    if (not isinstance(rows, list) or len(rows) > 2048
            or retained_characters > 2000000 + 4096
            or type(dropped) is not int or dropped < 0):
        raise CaptureError("invalid_producer_observations")
    if dropped:
        raise CaptureError("producer_observation_overflow")
    sample_at = _observation_time(value.get("at"))
    indexed = _independent_source_versions(value, source_history, sample_at)
    if "producerObservationTransport" in value:
        rows = _expand_producer_transport(value, indexed)
    first_versions = {}
    for member, at, sequence in indexed or ():
        order = (at, sequence)
        if member not in first_versions or order < first_versions[member]:
            first_versions[member] = order
    prepared, seen = [], set()
    for row in rows:
        if not isinstance(row, dict):
            raise CaptureError("invalid_producer_observation")
        source_id, source = row.get("sourceId"), row.get("source")
        independent = source_history.get(source_id) if isinstance(source_id, str) else None
        if (not source_id or source_id in seen or not isinstance(source, dict) or independent is None
                or any(source.get(key) != independent.get(key) for key in ("id", "sourceText", "firstSeen"))
                or source.get("id") != source_id or row.get("sourceText") != independent.get("sourceText")
                or not isinstance(row.get("sourceText"), str) or not row["sourceText"].strip()
                or type(row.get("rendered")) is not bool or type(source.get("rendered")) is not bool
                or type(row.get("pageDecision")) is not bool
                or type(row.get("sequence")) is not int or row["sequence"] <= 0
                or not isinstance(row.get("phase"), str) or not row["phase"]
                or row.get("eligible") is not None and type(row.get("eligible")) is not bool
                or not isinstance(row.get("pieces"), list)):
            raise CaptureError("invalid_producer_observation_source")
        seen.add(source_id)
        at = _observation_time(row.get("at"))
        if not _observation_time(source.get("firstSeen")) <= at <= sample_at:
            raise CaptureError("invalid_producer_observation_timestamp")
        _validate_source_geometry(source)
        if not _source_version_matches(source, row.get("sourceObservation"), indexed,
                                       independent, at, row["sequence"]):
            raise CaptureError("invalid_producer_observation_source")
        if row["rendered"] and (source["rendered"] is not True or source.get("phase") != row["phase"]):
            raise CaptureError("invalid_producer_observation_visibility")
        if row["rendered"] and indexed is not None and row.get("sourceObservation") != {
                "at": row["at"], "sequence": row["sequence"]}:
            raise CaptureError("invalid_producer_observation_visibility")
        work = row.get("work")
        if (not isinstance(work, dict)
                or any(type(work.get(key)) is not bool for key in
                       ("scanning", "active", "paused", "originalView", "automatic", "documentVisible"))
                or any(type(work.get(key)) is not int or work[key] < 0
                       for key in ("jobs", "pending", "rpcPending", "scheduledScans"))):
            raise CaptureError("invalid_producer_observation_work")
        idle = (work["scanning"] is False
                and all(work[key] == 0 for key in ("jobs", "pending", "rpcPending", "scheduledScans"))
                and work["active"] and not work["paused"] and not work["originalView"]
                and work["automatic"] and work["documentVisible"])
        pieces, piece_ids = row["pieces"], set()
        matched = True
        for piece in pieces:
            if not isinstance(piece, dict):
                raise CaptureError("invalid_producer_observation_piece")
            piece_id, owner = piece.get("id"), piece.get("sourceId")
            members = piece.get("sourceIds")
            if (not isinstance(piece_id, str) or not piece_id or piece_id in piece_ids
                    or not isinstance(owner, str) or not isinstance(members, list) or not members
                    or any(not isinstance(member, str) or not member for member in members)
                    or len(set(members)) != len(members) or owner not in members or source_id not in members
                    or any(type(piece.get(key)) is not bool for key in ("eligible", "completed", "failed"))
                    or any(key in piece and type(piece[key]) is not bool for key in ("local", "fromCache"))
                    or piece["eligible"] is not True or piece["completed"] and piece["failed"]
                    or piece.get("sourceEvidence") not in ("matched", "unmatched")):
                raise CaptureError("invalid_producer_observation_piece")
            piece_ids.add(piece_id)
            if owner in source_history and piece.get("sourceText") != source_history[owner]["sourceText"]:
                raise CaptureError("invalid_producer_observation_piece_source")
            matched = matched and all(member in source_history for member in members)
            matched = matched and all(member in source_history
                and _observation_time(source_history[member].get("firstSeen")) <= at for member in members)
            if indexed is not None:
                matched = matched and all(member in first_versions
                    and first_versions[member] <= (at, row["sequence"]) for member in members)
            matched = matched and piece["sourceEvidence"] == "matched" and not piece.get("auditIssue")
        if row["eligible"] is not (True if pieces else False if idle else None):
            raise CaptureError("invalid_producer_observation_eligibility")
        represented = row.get("representedBy")
        if represented is not None and (not isinstance(represented, str) or represented == source_id
                or represented not in source_history or not any(piece["sourceId"] == represented
                    and source_id in piece["sourceIds"] and len(piece["sourceIds"]) > 1 for piece in pieces)):
            raise CaptureError("invalid_producer_observation_membership")
        if represented is not None:
            owner = row.get("representedSource")
            if (not isinstance(owner, dict) or owner.get("id") != represented
                    or owner.get("rendered") is not True or owner.get("phase") != row["phase"]
                    or any(owner.get(key) != source_history[represented].get(key)
                           for key in ("id", "sourceText", "firstSeen"))):
                raise CaptureError("invalid_producer_observation_representation")
            _validate_source_geometry(owner)
            if (not _source_version_matches(owner, row.get("representedSourceObservation"), indexed,
                                            source_history[represented], at, row["sequence"])
                    or indexed is not None and row.get("representedSourceObservation") != {
                        "at": row["at"], "sequence": row["sequence"]}):
                raise CaptureError("invalid_producer_observation_representation")
        observed = row["rendered"] or represented is not None
        stale = any(piece["id"] in prediction_history and prediction_history[piece["id"]] != piece for piece in pieces)
        record = {key: row.get(key) for key in ("sourceId", "sourceText", "phase", "at", "sequence",
                                               "rendered", "representedBy", "pageDecision", "eligible")}
        record.update(idle=idle, pieces=pieces)
        if not observed or not matched or stale:
            record.update(eligible=None, pieces=[])
        old = observations.get(source_id)
        if old and (_observation_time(old["at"]), old.get("sequence", 0)) >= (at, row["sequence"]):
            continue
        prepared.append(json.loads(encode(record)))
    imported = {}
    for record in prepared:
        order = (_observation_time(record["at"]), record["sequence"])
        for piece in record["pieces"]:
            prior = imported.get(piece["id"])
            if prior is None or prior[0] < order:
                imported[piece["id"]] = (order, piece)
            elif prior[0] == order and prior[1] != piece:
                raise CaptureError("conflicting_producer_observation_piece")
    for piece_id, (_, piece) in imported.items():
        prediction_history.setdefault(piece_id, json.loads(encode(piece)))
    for record in prepared:
        observations[record["sourceId"]] = record
    # Current raw pieces can advance while a source is hidden. The normalizer's
    # v1 history has one version per piece ID; never substitute that newer hidden
    # version for flags that were actually observed at independent visibility.
    for source_id, record in list(observations.items()):
        if any(prediction_history.get(piece["id"]) != piece for piece in record["pieces"]):
            observations[source_id] = {**record, "eligible": None, "pieces": []}


class CaseCapture:
    def __init__(self, browser, host, run, repo, case_timeout=120, stable_seconds=2, poll_seconds=.15,
                 full_screenshots=False):
        self.browser, self.host, self.run, self.repo = browser, host, run, Path(repo)
        self.case_timeout, self.stable_seconds, self.poll_seconds = case_timeout, stable_seconds, poll_seconds
        self.full_screenshots = full_screenshots

    def capture(self, site):
        path = self.run.attempt(site["id"])
        log = Journal(path / "events.jsonl")
        case = {**site, **self.run.frozen, "schemaVersion": 1, "capturedAt": now(), "finalUrl": None,
                "title": None, "viewport": None, "visitSuccess": False, "domainVerified": False,
                "liveTranslation": False, "completed": False, "captureFinished": False, "evidenceReady": False,
                "simulation": False, "pageDecision": None, "sourceNodes": [], "predictions": [],
                "annotations": [], "screenshots": [], "screenshotReview": {}, "retractions": [],
                "errors": [], "blockedReason": "", "terminals": [], "requestIds": [], "settled": False,
                "settlementKind": "semantic", "semanticStable": False, "stability": {},
                "layoutReviewRequired": True, "fullScreenshots": self.full_screenshots}
        pending, production_requests, prediction_history, source_history, source_observations = {}, {}, {}, {}, {}
        self.browser.journal = log; self.host.journal = log
        started = time.monotonic()
        deadline = started + self.case_timeout
        previous_deadline = getattr(self.browser, "deadline", None)
        configured = False
        installed = False
        backend_failed = False
        content_observations = []

        def save():
            atomic_json(path / "case.json", case, on_retry=log.append)

        def sample(phase, value=None):
            if value is None:
                value = self.browser.qa("sample", phase)
            if not isinstance(value, dict):
                raise CaptureError("adapter_lost_or_invalid")
            log.append({"kind": "sample", "phase": phase, "value": value})
            content_observations.append(content_observation(value))
            for row in value["sourceNodes"]:
                source_history[row["id"]] = row
            for row in value["predictions"]:
                prediction_history[row["id"]] = row
            if configured:
                if "producerObservations" in value:
                    record_producer_observations(source_observations, value, source_history, prediction_history)
                else:
                    # Original adapter/sample fixtures retain the published v1 interface.
                    record_source_observations(source_observations, value, phase)
            case.update(finalUrl=value["finalUrl"], title=value["title"], viewport=value["viewport"],
                        sourceNodes=list(source_history.values()), predictions=value["predictions"],
                        predictionHistory=list(prediction_history.values()),
                        sourceObservations=list(source_observations.values()),
                        productionEligibility=value.get("productionEligibility", []),
                        finalState={key: value.get(key) for key in
                                    ("at", "jobs", "pending", "scanning", "rpcPending", "status", "pageDecision")},
                        pageDecision=value["pageDecision"],
                        retractions=[event for event in value["events"] if event["type"] == "retraction"])
            case["annotations"] = [{"sourceId": row["id"], "needTranslation": "unknown"} for row in case["sourceNodes"]]
            case["domainVerified"] = belongs_to(case["finalUrl"], site["registeredDomain"])
            if not case["domainVerified"]:
                raise CaptureError("final_url_outside_registered_domain")
            save()
            return value

        def screenshot(phase, full=False, base_phase=None):
            check_deadline(deadline)
            value = sample(phase)
            check_deadline(deadline)
            # Each phase is an independently reviewable observation. Refreshes
            # get new phases/files, retaining the canonical phase as metadata.
            image = path / (phase + ".png")
            if image.exists() or any(shot["phase"] == phase for shot in case["screenshots"]):
                raise CaptureError("screenshot_phase_or_file_exists")
            if full:
                self.browser.screenshot(image, full=True)
            else:
                self.browser.screenshot(image)
            case["screenshots"].append({"phase": phase, "path": image.name, "at": now(), "viewport": value["viewport"],
                                        "sha256": hashlib.sha256(image.read_bytes()).hexdigest(), "fullPage": full,
                                        "contentSignature": screenshot_content_signature(value),
                                        "dynamicContentSignature": screenshot_content_signature(value,
                                            case["stability"].get("stable", {}).get("dynamicDisplaySourceIds", [])),
                                        "semanticSignature": hashlib.sha256(semantic_signature(value).encode("utf-8")).hexdigest(),
                                        "requestCount": len(case["requestIds"]),
                                        "sampleAt": value.get("at"),
                                        "contentObservation": content_observations[-1],
                                        "observationIndex": len(content_observations) - 1,
                                        **({"basePhase": base_phase} if base_phase is not None else {})})
            save()
            check_deadline(deadline)

        def forward(phase=None, evidence_only=False, cleanup=False):
            frames = []

            def flush():
                check_deadline(deadline)
                if frames:
                    deliver = getattr(self.browser, "deliver_batch", None)
                    if deliver is not None:
                        deliver(frames)
                    else:
                        # Preserve the existing duck-typed qa interface for
                        # callers/tests. Production BrowserCli always batches.
                        for rpc_id, packet, acknowledge in frames:
                            check_deadline(deadline)
                            if acknowledge:
                                self.browser.qa("acknowledge", rpc_id, {"accepted": True})
                                check_deadline(deadline)
                            self.browser.qa("deliver", rpc_id, packet)
                    frames.clear()
                    check_deadline(deadline)

            def queue_frame(rpc_id, packet, acknowledge):
                frame = [rpc_id, packet, acknowledge]
                if frames and (len(frames) >= DELIVERY_BATCH_PACKETS or
                               len(delivery_expression(frames + [frame]).encode("utf-16-le")) > EVAL_LIMIT_BYTES):
                    flush()
                frames.append(frame)
                if len(frames) >= DELIVERY_BATCH_PACKETS or \
                        len(delivery_expression(frames).encode("utf-16-le")) > EVAL_LIMIT_BYTES:
                    flush()

            events_read = 0

            def receive():
                nonlocal events_read, backend_failed
                for _ in range(FORWARD_EVENT_LIMIT - events_read):
                    check_deadline(deadline)
                    event = self.host.poll()
                    if event is None:
                        break
                    events_read += 1
                    log.append({"kind": "host_event", "event": event})
                    check_deadline(deadline)
                    if event.get("code") in ("host_eof", "host_invalid_jsonl"):
                        flush()
                        raise CaptureError(event["code"])
                    if event.get("caseId") != site["id"]:
                        # Late cancellation from a previous site is audited but cannot finish this site.
                        owns = getattr(self.host, "owns_event", None)
                        if owns is not None and not owns(event):
                            flush()
                            raise CaptureError("host_uncorrelated_event")
                        validate = getattr(self.host, "validate_late_event", None)
                        if validate is not None:
                            try:
                                validate(event)
                            except CaptureError:
                                flush()
                                raise
                        case.setdefault("lateHostEvents", []).append(event)
                        continue
                    entry = pending.get(event.get("id"))
                    if not entry:
                        flush()
                        raise CaptureError("host_uncorrelated_event")
                    if event["type"] == "error":
                        flush()
                        if entry.get("rpcId") and not entry.get("acknowledged"):
                            self.browser.qa("acknowledge", entry["rpcId"], {"accepted": False, "reason": event.get("code", "host_error")})
                            entry["acknowledged"] = True
                        raise CaptureError(event.get("code", "host_error"))
                    if event["type"] == "packet":
                        try:
                            packet = event["packet"]
                            wire = json.loads(packet) if isinstance(packet, str) else packet
                            if not isinstance(wire, dict) or not entry.get("rpcId"):
                                raise CaptureError("host_invalid_packet")
                        except (KeyError, TypeError, ValueError, CaptureError):
                            flush()
                            raise
                        # Preserve raw serialized frame for receive; acknowledge the transport
                        # once, before delivering its first frame (even across batch boundaries).
                        acknowledge = not entry["acknowledged"]
                        # Do not retry an acknowledgement after a partially executed
                        # eval fails; that case is failed evidence, not a replay.
                        entry["acknowledged"] = True
                        if wire.get("error"):
                            backend_failed = True
                            case["errors"].append(error_record(CaptureError("backend_packet_error")))
                        queue_frame(entry["rpcId"], packet, acknowledge)
                        if wire.get("error"):
                            flush()
                    if event["type"] == "terminal":
                        entry["terminal"] = True
                        case["terminals"].append(event)
                        if event.get("success") is not True or event.get("failedCount", 0):
                            backend_failed = True
                            case["errors"].append(error_record(CaptureError("backend_terminal_failed")))
                        if type(event.get("requestCount")) is not int or event["requestCount"] < 0:
                            backend_failed = True
                            case["errors"].append(error_record(CaptureError("backend_terminal_request_count_invalid")))
                        flush()
                    if event["type"] == "assessment":
                        flush()
                        if entry["action"] != "assess":
                            raise CaptureError("host_unexpected_assessment")
                        case["localAssessment"] = event
                        entry["terminal"] = True
                flush()
                return events_read < FORWARD_EVENT_LIMIT

            if not receive():
                save()
                return False  # Preserve the per-pass event bound; next pass drains first again.
            if evidence_only:
                save()
                return None
            if backend_failed and not cleanup:
                raise BackendFailure("backend_failure_recorded")
            value = None
            collect = getattr(self.browser, "drain_sample", None)
            # Admit the first candidate via the cheap drain before a full DOM
            # traversal. Later polls can share the sample/drain wrapper call.
            if phase is not None and collect is not None and pending:
                collected = collect(phase)
                value, outbound = collected["sample"], collected["outbound"]
            else:
                outbound = self.browser.qa("drain")
            check_deadline(deadline)
            for item in outbound:
                check_deadline(deadline)
                message = item["message"]
                rpc_id = item["rpcId"]
                request_id = site["id"] + "-" + rpc_id
                if message["type"] not in ("pageAutoCandidate", "pageAutoCancel"):
                    raise CaptureError("unsupported_production_outbound_type")
                action = "message" if message["type"] == "pageAutoCandidate" else "cancel"
                if action == "message":
                    pending[request_id] = {"rpcId": rpc_id, "action": action, "terminal": False, "acknowledged": False}
                    production_requests[message["requestId"]] = request_id
                    # Only transport identity fields added, no rewriting production blocks/samples.
                    message = {**message, "tabId": 1, "browserWindowId": 1, "frameId": 0,
                               "siteHost": urlsplit(case["finalUrl"]).hostname}
                    case["requestIds"].append(request_id)
                else:
                    request_id = production_requests.get(message.get("requestId"))
                    if not request_id:
                        raise CaptureError("unmapped_production_cancellation")
                    case.setdefault("cancellations", []).append({"id": request_id, "message": message})
                self.host.send({"schemaVersion": 1, "id": request_id, "caseId": site["id"],
                                "url": case["finalUrl"], "action": action, "message": message})
                if action == "cancel":
                    self.browser.qa("acknowledge", rpc_id, {"accepted": True})
            drained = receive()
            save()
            if backend_failed and not cleanup:
                raise BackendFailure("backend_failure_recorded")
            return value if drained else False

        def settle(phase):
            quiet_since, signature = None, None
            geometry, geometry_changes = None, 0
            content, content_changed = None, False
            source_content, dynamic_sources = {}, set()
            authored_sources = set()
            previous_nodes = None
            while time.monotonic() - started < self.case_timeout:
                value = forward(phase)
                check_deadline(deadline)
                if value is False:
                    continue
                value = sample(phase, value)
                check_deadline(deadline)
                candidate_signature = semantic_signature(value)
                if value["pageDecision"] is False:
                    candidate_signature = encode([candidate_signature, len(case["requestIds"])])
                candidate_geometry = geometry_signature(value)
                candidate_content = screenshot_content_signature(value)
                content_changed |= content is not None and candidate_content != content
                content = candidate_content
                current_nodes = {row["id"]: row.get("sourceText") for row in value["nodes"]}
                if previous_nodes is not None:
                    authored_sources.update(current_nodes.keys() ^ previous_nodes.keys())
                previous_nodes = current_nodes
                for row in value["nodes"]:
                    row_content = encode([row.get("sourceText"), row.get("displayedText")])
                    source_id = row.get("id")
                    if source_id in source_content and source_content[source_id] != row_content:
                        dynamic_sources.add(source_id)
                    source_content[source_id] = row_content
                geometry_changed = geometry is not None and candidate_geometry != geometry
                geometry_changes += int(geometry_changed)
                idle = not value["jobs"] and not value["pending"] and not value["scanning"] and not value["rpcPending"]
                status_ready = bool(value["status"] and value["status"]["state"] in ("skipped", "completed"))
                terminals_complete = all(entry["terminal"] for entry in pending.values())
                # A skipped page may retain untranslated eligible blocks. Their
                # flags remain evidence for recognition scoring; only a positive
                # decision requires translation completion. Failures always block.
                predictions_complete = all(not p.get("failed") and
                                           (value["pageDecision"] is False or
                                            p.get("eligible") is False or p.get("completed") is True)
                                           for p in value["predictions"])
                decision_known = type(value["pageDecision"]) is bool
                model_requests_zero = terminals_complete and all(
                    terminal_valid(event) and event["requestCount"] == 0 for event in case["terminals"])
                done = (idle and status_ready and terminals_complete and predictions_complete and decision_known
                        and (value["pageDecision"] is not False or model_requests_zero))
                unchanged = candidate_signature == signature
                sampled_at = time.monotonic()
                if done:
                    if not unchanged or quiet_since is None:
                        quiet_since = sampled_at
                else:
                    quiet_since = None
                stable_for = sampled_at - quiet_since if quiet_since is not None else 0
                semantic_stable = done and unchanged and stable_for >= self.stable_seconds
                observation = {"phase": phase, "at": now(), "semanticUnchanged": unchanged,
                               "stabilityBasis": "page-decision" if value["pageDecision"] is False else "content-and-completion",
                               "semanticStable": semantic_stable, "stableForSeconds": stable_for,
                               "geometryChanged": geometry_changed, "geometryVaried": geometry_changes > 0,
                               "geometryChanges": geometry_changes, "idle": idle, "statusReady": status_ready,
                               "terminalsComplete": terminals_complete, "predictionsComplete": predictions_complete,
                               "modelRequestsZero": model_requests_zero,
                               "contentVaried": content_changed,
                               "dynamicSourceIds": sorted(sid for sid in dynamic_sources | authored_sources if sid is not None),
                               "dynamicDisplaySourceIds": sorted(sid for sid in dynamic_sources if sid is not None),
                               "pageDecisionKnown": decision_known, "layoutReviewRequired": True}
                log.append({"kind": "stability", **observation})
                case["stability"][phase] = observation
                save()
                check_deadline(deadline)
                if semantic_stable:
                    return value
                signature = candidate_signature
                geometry = candidate_geometry
                time.sleep(min(self.poll_seconds, max(0, deadline - time.monotonic())))
            raise CaptureError("case_timeout_without_settlement")

        def final_settlement():
            # Screenshot commands can trigger production scans, work and text
            # changes. Every final observation uses the normal settlement proof,
            # host forwarding and the original deadline, before QA disposal.
            dynamic_sources = (case["stability"]["stable"]["dynamicDisplaySourceIds"]
                               if case["pageDecision"] is False else [])
            for refresh in range(FINAL_SCREENSHOT_REFRESH_LIMIT + 1):
                value = settle("final-idle")
                latest = {shot.get("basePhase", shot["phase"]): shot for shot in case["screenshots"]}
                phases = ["stable"] + (["stable-full"] if self.full_screenshots else [])
                content_matches = all(latest[phase]["contentSignature"] == screenshot_content_signature(value)
                                      for phase in phases)
                # Existing skip settlement certifies a decision even with a live
                # authored ticker. Such screenshots are timestamped observations,
                # never a claim that the site's content became static. A newly
                # changed previously stable skip page still requires a refresh.
                dynamic_skipped = bool(dynamic_sources) and value["pageDecision"] is False
                comparable_content_matches = all(
                    latest[phase]["dynamicContentSignature"] == screenshot_content_signature(value, dynamic_sources)
                    for phase in phases) if dynamic_skipped else content_matches
                final_semantics = hashlib.sha256(semantic_signature(value).encode("utf-8")).hexdigest()
                current = all(latest[phase]["requestCount"] == len(case["requestIds"]) and
                              latest[phase]["semanticSignature"] == final_semantics
                              for phase in phases)
                authored = {phase: authored_content_changes(
                    latest[phase]["contentObservation"], content_observations[-1],
                    content_observations[latest[phase]["observationIndex"] + 1:-1]) for phase in phases}
                # A real settled final state can follow an authored carousel
                # version/visibility change. Images remain timestamped evidence,
                # not a claim of static content. Unchanged-source display changes
                # still require refresh/exhaustion under the original limit.
                authored_current = all(
                    latest[phase]["requestCount"] == len(case["requestIds"]) and
                    ((latest[phase]["contentSignature"] == screenshot_content_signature(value) and
                      latest[phase]["semanticSignature"] == final_semantics) or authored[phase]["explained"])
                    for phase in phases)
                timestamped_dynamic = authored_current and any(a["explained"] for a in authored.values())
                observed_dynamic_ids = sorted(set(case["stability"]["stable"]["dynamicSourceIds"]).union(
                    *(set(a["dynamicSourceIds"]) for a in authored.values())))
                proof = {"refreshes": refresh, "contentMatches": content_matches,
                         "dynamicSkippedContent": dynamic_skipped, "dynamicSourceIds": observed_dynamic_ids,
                         "timestampedDynamicContent": timestamped_dynamic, "authoredSourceChanges": authored,
                         "screenshotsCurrent": (current and comparable_content_matches) or authored_current,
                         "at": value.get("at"),
                         "contentSignature": screenshot_content_signature(value),
                         "paths": [latest[phase]["path"] for phase in phases],
                         "images": [{"basePhase": phase, **{key: latest[phase][key]
                                     for key in ("phase", "path", "sha256")}} for phase in phases]}
                log.append({"kind": "final_screenshot_proof", **proof})
                case["finalScreenshotProof"] = proof
                save()
                check_deadline(deadline)
                if proof["screenshotsCurrent"]:
                    return
                if refresh == FINAL_SCREENSHOT_REFRESH_LIMIT:
                    raise CaptureError("final_screenshot_content_unstable")
                for phase in phases:
                    screenshot(f"{phase}-refresh-{refresh + 1}", full=phase == "stable-full", base_phase=phase)

        def failed_evidence():
            # No new candidates or retries: retain original ordered host events.
            # Every operation uses the same case deadline, including screenshots.
            def snapshot():
                check_deadline(deadline)
                case["failureSnapshot"] = sample("failure-final")
                check_deadline(deadline)
                save()

            def attempt(action):
                try:
                    action()
                    return True
                except (CaptureError, OSError, ValueError, KeyError, TypeError) as error:
                    case["errors"].append(error_record(error))
                    check_deadline(deadline)
                    return False

            snapshot_available = attempt(snapshot)
            phases = {shot["phase"] for shot in case["screenshots"]}
            for phase, full in (("after", False), ("after-full", True)):
                if snapshot_available and phase not in phases and (not full or self.full_screenshots):
                    check_deadline(deadline)
                    attempt(lambda: screenshot(phase, full=full))
                    check_deadline(deadline)
            while not all(entry["terminal"] for entry in pending.values()):
                check_deadline(deadline)
                forward(evidence_only=True)
                if not all(entry["terminal"] for entry in pending.values()):
                    time.sleep(min(self.poll_seconds, max(0, deadline - time.monotonic())))
            # Also drain events queued after a terminal before taking final flags.
            while forward(evidence_only=True) is False:
                check_deadline(deadline)
            snapshot_available = attempt(snapshot)
            for phase, full in (("stable", False), ("stable-full", True)):
                if snapshot_available and phase not in phases and (not full or self.full_screenshots):
                    check_deadline(deadline)
                    attempt(lambda: screenshot(phase, full=full))
                    check_deadline(deadline)
            attempt(snapshot)

        save()
        try:
            self.browser.deadline = deadline
            capabilities = self.host.capabilities(site["id"], timeout=min(15, max(0, deadline - time.monotonic())))
            case["capabilities"] = capabilities
            case["simulation"] = capabilities.get("live") is not True
            case["liveTranslation"] = capabilities.get("live") is True
            self.browser.command("open", site["url"])
            log.append({"kind": "state", "value": self.browser.state()})
            if self.browser.evaluate("!!globalThis.chrome?.runtime?.sendMessage"):
                raise CaptureError("extension_runtime_present_use_clean_session")
            for name in PRODUCTION:
                raw = (self.repo / "browser-extension" / name).read_bytes()
                expected_hash = self.run.frozen.get("sourceHashes", {}).get(name)
                if expected_hash and hashlib.sha256(raw).hexdigest() != expected_hash:
                    raise CaptureError("production_source_changed_since_freeze")
                self.browser.inject(raw.decode("utf-8"))
            self.browser.inject((HERE / "page-adapter.js").read_text(encoding="utf-8"))
            installed = True
            sample("before")
            if not case["sourceNodes"]:
                raise CaptureError("no_visible_source_candidates")
            case["visitSuccess"] = True
            screenshot("before")
            if self.full_screenshots:
                screenshot("before-full", full=True)
            configured = True
            # Official evaluation uses the fixed baseline, never a user's setting.
            if self.browser.qa("configure", {"enabled": True, "active": True, "generation": 1,
                                              "targetLanguage": capabilities.get("targetLanguage"), "concurrency": 10,
                                              "chineseProseShareThresholdPercent": 50}) is not True:
                raise CaptureError("production_config_rejected")
            settle("after")
            # Local skip cross-check: call rules-only host assess with the production
            # snapshot, never infer truth from it or submit a model request.
            if not case["requestIds"]:
                evidence = self.browser.probe(capabilities.get("targetLanguage"),
                                              timeout=min(15, self.case_timeout - (time.monotonic() - started)),
                                              poll_seconds=self.poll_seconds)
                log.append({"kind": "probe_evidence", "value": evidence})
                request_id = site["id"] + "-assess"
                pending[request_id] = {"action": "assess", "terminal": False}
                self.host.send({"schemaVersion": 1, "id": request_id, "caseId": site["id"], "url": case["finalUrl"],
                                "action": "assess", **evidence})
                assessment_deadline = min(deadline, time.monotonic() + 15)
                while not pending[request_id]["terminal"]:
                    check_deadline(deadline)
                    if time.monotonic() >= assessment_deadline:
                        raise CaptureError("assessment_timeout")
                    forward()
                    time.sleep(min(self.poll_seconds, max(0, assessment_deadline - time.monotonic())))
            screenshot("after")
            if self.full_screenshots:
                screenshot("after-full", full=True)
            screenshot("scroll-before")
            self.browser.evaluate("window.scrollBy(0,Math.max(innerHeight*.85,400));true")
            settle("scroll-after"); screenshot("scroll-after")
            self.browser.evaluate("window.scrollTo(0,0);true")
            settle("return-after"); screenshot("return-after")
            disclosure = self.browser.evaluate("(()=>{const e=document.querySelector('details:not([open])');if(!e)return false;globalThis.__qaDisclosure=e;e.open=true;return true})()")
            case["disclosureCheck"] = "not_present"
            if disclosure is True:
                settle("disclosure-open"); screenshot("disclosure-open")
                self.browser.evaluate("globalThis.__qaDisclosure.open=false;delete globalThis.__qaDisclosure;true")
                settle("disclosure-restored"); screenshot("disclosure-restored")
                case["disclosureCheck"] = "captured"
            # Induce actual line wrapping, preserving the prior inline CSS exactly.
            self.browser.evaluate("globalThis.__qaLayout={value:document.body.style.getPropertyValue('max-width'),priority:document.body.style.getPropertyPriority('max-width')};document.body.style.setProperty('max-width',Math.floor(innerWidth*.8)+'px','important');true")
            self.browser.evaluate("window.dispatchEvent(new Event('resize'));true")
            settle("reflow-after"); screenshot("reflow-after")
            self.browser.evaluate("document.body.style.setProperty('max-width',globalThis.__qaLayout.value,globalThis.__qaLayout.priority);delete globalThis.__qaLayout;window.dispatchEvent(new Event('resize'));true")
            settle("stable"); screenshot("stable")
            if self.full_screenshots:
                screenshot("stable-full", full=True)
            final_settlement()
            case["settled"] = True
            case["semanticStable"] = True
        except (CaptureError, OSError, ValueError, KeyError, TypeError) as error:
            if not isinstance(error, BackendFailure):
                case["errors"].append(error_record(error))
            if backend_failed:
                case["settled"] = case["semanticStable"] = False
                try:
                    failed_evidence()
                except (CaptureError, OSError, ValueError, KeyError, TypeError) as evidence_error:
                    case["errors"].append(error_record(evidence_error))
        finally:
            if installed:
                try:
                    self.browser.evaluate("if(globalThis.__qaDisclosure){globalThis.__qaDisclosure.open=false;delete globalThis.__qaDisclosure}if(globalThis.__qaLayout){document.body.style.setProperty('max-width',globalThis.__qaLayout.value,globalThis.__qaLayout.priority);delete globalThis.__qaLayout}true")
                    self.browser.qa("dispose")
                    if configured:
                        forward(cleanup=True)
                except (CaptureError, OSError, ValueError, KeyError, TypeError) as error:
                    case["errors"].append(error_record(error, "cleanup_"))
            case["captureFinished"] = True
            case["liveTranslation"] = case.get("capabilities", {}).get("live") is True and any(
                type(event.get("requestCount")) is int and event["requestCount"] > 0 for event in case["terminals"])
            case["evidenceReady"], case["completed"], reasons = completion_gate(case, path)
            case["blockedReason"] = ";".join(reasons)
            try:
                save()
            finally:
                self.browser.deadline = previous_deadline
        return case


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--host-command-json", required=True, help="JSON string array; executed shell=false")
    parser.add_argument("--session", required=True)
    parser.add_argument("--limit", type=int)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--case-timeout", type=float, default=120)
    parser.add_argument("--full-screenshots", "--full", action="store_true",
                        help="Also capture before/after/stable full-page screenshots via screenshot --full")
    args = parser.parse_args(argv)
    repo = HERE.parent.parent.resolve()
    evidence = args.evidence_dir.resolve()
    if evidence == repo or repo in evidence.parents:
        parser.error("--evidence-dir must be outside the repository")
    if args.limit is not None and args.limit < 1 or args.case_timeout <= 0:
        parser.error("limit and timeout must be positive")
    try:
        sites, manifest_hash = read_manifest(args.manifest)
        host_command = json.loads(args.host_command_json)
        if not isinstance(host_command, list) or not host_command or not all(isinstance(x, str) for x in host_command):
            raise CaptureError("host_command_must_be_string_list")
        frozen = {**source_identity(repo), "manifestHash": manifest_hash, "hostCommand": host_command,
                  "wrapper": str(WRAPPER), "session": args.session, "caseTimeout": args.case_timeout,
                  "fullScreenshots": args.full_screenshots}
        run = EvidenceRun(evidence, args.run_id, frozen, args.resume)
        if not args.resume:
            atomic_json(run.path / "manifest.json", sites)
        journal = Journal(run.path / ("startup-" + str(time.time_ns()) + ".jsonl"))
        browser = BrowserCli(args.session, journal)
        host = JsonlHost(host_command, journal)
        count, issues = 0, 0
        try:
            for site in sites:
                if args.resume and run.captured(site["id"]):
                    continue
                if args.limit is not None and count >= args.limit:
                    break
                result = CaseCapture(browser, host, run, repo, args.case_timeout,
                                     full_screenshots=args.full_screenshots).capture(site)
                count += 1
                issues += int(not result["evidenceReady"])
                print(encode({"id": site["id"], "completed": result["completed"], "evidenceReady": result["evidenceReady"],
                              "blockedReason": result["blockedReason"]}), flush=True)
        finally:
            host.close()
        return 1 if issues else 0
    except (CaptureError, OSError, ValueError) as error:
        print(encode({"error": str(error), "completed": False}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
