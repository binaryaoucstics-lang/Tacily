"""Browser Use CLI evidence driver. Standard library only; no browser/network fallback."""
from __future__ import annotations

import argparse
import hashlib
import json
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


class CaptureError(RuntimeError):
    pass


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
        return encode([False, any(p.get("failed") for p in value["predictions"])])
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
        while time.monotonic() < deadline:
            event = self.poll(min(.25, max(0, deadline - time.monotonic())))
            if event and event.get("type") == "capabilities":
                if event.get("id") != request["id"] or event.get("caseId") != request["caseId"]:
                    raise CaptureError("capabilities_identity_mismatch")
                if type(event.get("live")) is not bool or not isinstance(event.get("targetLanguage"), str) \
                        or not event["targetLanguage"] or len(event["targetLanguage"]) > 64:
                    raise CaptureError("invalid_capabilities")
                return event
            if event and event.get("type") == "error":
                if event.get("code") not in ("host_eof", "host_invalid_jsonl") and (
                        event.get("id") != request["id"] or event.get("caseId") != request["caseId"]):
                    raise CaptureError("capabilities_identity_mismatch")
                raise CaptureError(event.get("code", "host_error"))
        raise CaptureError("capabilities_timeout")

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
    if case["expectedPageTranslation"]:
        if not case.get("liveTranslation"):
            reasons.append("live_translation_required")
        if not requests or any(r not in by_id or by_id[r].get("success") is not True for r in requests):
            reasons.append("terminal_evidence_missing")
        if not any(event.get("requestCount", 0) > 0 for event in terminals):
            reasons.append("real_model_request_missing")
        if not case.get("predictions") or any(p.get("failed") or not p.get("completed") for p in case["predictions"]):
            reasons.append("node_completion_missing")
    elif any(event.get("requestCount", 0) > 0 for event in terminals) or requests:
        # Frontend-skipped cases use assess (rules only), never a translation message.
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

        def save():
            atomic_json(path / "case.json", case, on_retry=log.append)

        def sample(phase, value=None):
            if value is None:
                value = self.browser.qa("sample", phase)
            if not isinstance(value, dict):
                raise CaptureError("adapter_lost_or_invalid")
            log.append({"kind": "sample", "phase": phase, "value": value})
            for row in value["sourceNodes"]:
                source_history[row["id"]] = row
            for row in value["predictions"]:
                prediction_history[row["id"]] = row
            if configured:
                record_source_observations(source_observations, value, phase)
            case.update(finalUrl=value["finalUrl"], title=value["title"], viewport=value["viewport"],
                        sourceNodes=list(source_history.values()), predictions=value["predictions"],
                        predictionHistory=list(prediction_history.values()),
                        sourceObservations=list(source_observations.values()),
                        productionEligibility=value.get("productionEligibility", []),
                        pageDecision=value["pageDecision"],
                        retractions=[event for event in value["events"] if event["type"] == "retraction"])
            case["annotations"] = [{"sourceId": row["id"], "needTranslation": "unknown"} for row in case["sourceNodes"]]
            case["domainVerified"] = belongs_to(case["finalUrl"], site["registeredDomain"])
            if not case["domainVerified"]:
                raise CaptureError("final_url_outside_registered_domain")
            save()
            return value

        def screenshot(phase, full=False):
            value = sample(phase)
            image = path / (phase + ".png")
            if full:
                self.browser.screenshot(image, full=True)
            else:
                self.browser.screenshot(image)
            case["screenshots"].append({"phase": phase, "path": image.name, "at": now(), "viewport": value["viewport"],
                                        "sha256": hashlib.sha256(image.read_bytes()).hexdigest(), "fullPage": full})
            save()

        def forward(phase=None):
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
                nonlocal events_read
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
                        queue_frame(entry["rpcId"], packet, acknowledge)
                        if wire.get("error"):
                            flush()
                            raise CaptureError("backend_packet_error")
                    if event["type"] == "terminal":
                        flush()
                        entry["terminal"] = True
                        case["terminals"].append(event)
                        if event.get("success") is not True or event.get("failedCount", 0):
                            raise CaptureError("backend_terminal_failed")
                    if event["type"] == "assessment":
                        flush()
                        case["localAssessment"] = event
                        entry["terminal"] = True
                flush()
                return events_read < FORWARD_EVENT_LIMIT

            if not receive():
                save()
                return False  # Preserve the per-pass event bound; next pass drains first again.
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
            return value if drained else False

        def settle(phase):
            quiet_since, signature = None, None
            geometry, geometry_changes = None, 0
            while time.monotonic() - started < self.case_timeout:
                value = forward(phase)
                check_deadline(deadline)
                if value is False:
                    continue
                value = sample(phase, value)
                check_deadline(deadline)
                candidate_signature = semantic_signature(value)
                candidate_geometry = geometry_signature(value)
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
                done = idle and status_ready and terminals_complete and predictions_complete and decision_known
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
            if self.browser.qa("configure", {"enabled": True, "active": True, "generation": 1,
                                              "targetLanguage": capabilities.get("targetLanguage"), "concurrency": 10}) is not True:
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
            case["settled"] = True
            case["semanticStable"] = True
        except (CaptureError, OSError, ValueError, KeyError, TypeError) as error:
            case["errors"].append(error_record(error))
        finally:
            if installed:
                try:
                    self.browser.evaluate("if(globalThis.__qaDisclosure){globalThis.__qaDisclosure.open=false;delete globalThis.__qaDisclosure}if(globalThis.__qaLayout){document.body.style.setProperty('max-width',globalThis.__qaLayout.value,globalThis.__qaLayout.priority);delete globalThis.__qaLayout}true")
                    self.browser.qa("dispose")
                    if configured:
                        forward()
                except (CaptureError, OSError, ValueError, KeyError, TypeError) as error:
                    case["errors"].append(error_record(error, "cleanup_"))
            case["captureFinished"] = True
            case["liveTranslation"] = case.get("capabilities", {}).get("live") is True and any(
                event.get("requestCount", 0) > 0 for event in case["terminals"])
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
