"""Normalize original capture evidence for evaluate.py, offline and without edits.

Review labels are explicit human inputs: pageAnnotation is an object with
needTranslation, and annotations contains sourceId/needTranslation objects.
Boolean labels in this independent review are reviewed/independent by default;
explicit false flags are respected. Missing labels remain ``unknown``.
screenshots maps every captured phase to a boolean review vote. Retraction
reviews contain eventIndex (zero based, or sourceId when unique), confirmed and
sourceUnchanged booleans; missing reviews never dismiss observed events.

Screenshot paths in the result are relative to --evidence-root (default: the
case directory). Pass that same root to evaluate.py. No screenshots are copied.
The original capture/review objects and byte hashes remain in provenance.
Coverage defaults to strict. Explicit automatic-page scope can defer coverage
only for independently reviewed negative pages correctly skipped at zero calls,
with live capabilities and successful, identified local terminal evidence or a
matching live negative host assessment with no translation requests/terminals.
"""

from __future__ import annotations

import argparse
from copy import deepcopy
import hashlib
import json
import math
from pathlib import Path, PureWindowsPath
import re
import sys


REQUIRED_PHASES = {"before", "after", "stable", "scroll-before", "scroll-after",
                   "return-after", "reflow-after"}
CHECK_PHASES = {"firstViewport": ("before", "after"),
                "mainContent": ("after", "scroll-before", "scroll-after"),
                "afterScroll": ("scroll-before", "scroll-after"),
                "layout": ("reflow-after", "stable"),
                "dynamicReturn": ("return-after", "stable")}


class NormalizationError(ValueError):
    """Invalid review or evidence; no normalized file can be written."""


def _load(raw):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise NormalizationError("duplicate_json_key:" + key)
            result[key] = value
        return result

    def invalid_constant(value):
        raise NormalizationError("invalid_json_number:" + value)

    value = json.loads(raw.decode("utf-8"), object_pairs_hook=unique,
                       parse_constant=invalid_constant)
    if not isinstance(value, dict):
        raise NormalizationError("evidence_requires_object")
    return value


def _count(value):
    return type(value) is int and value >= 0


def _label(row, node_id=None):
    if row is None:
        row = {}
    if not isinstance(row, dict):
        raise NormalizationError("invalid_independent_annotation")
    value = row.get("needTranslation", "unknown")
    if type(value) is not bool and value != "unknown":
        raise NormalizationError("invalid_independent_annotation")
    for key in ("reviewed", "independent"):
        if key in row and type(row[key]) is not bool:
            raise NormalizationError("invalid_annotation_review_flags")
    result = deepcopy(row)
    result.update(needTranslation=value,
                  reviewed=row.get("reviewed", type(value) is bool),
                  independent=row.get("independent", type(value) is bool))
    if node_id is not None:
        result["nodeId"] = node_id
    return result


def _known(label):
    return (type(label["needTranslation"]) is bool
            and label["reviewed"] is True and label["independent"] is True)


def _position(rects):
    if not isinstance(rects, list):
        return None
    for rect in rects:
        if (isinstance(rect, dict)
                and all(type(rect.get(k)) in (int, float)
                        and math.isfinite(rect[k]) and -1e7 < rect[k] < 1e7
                        for k in ("x", "y", "width", "height"))
                and rect["width"] > 0 and rect["height"] > 0):
            return {k: rect[k] for k in ("x", "y", "width", "height")}
    return None


def _screenshots(case, review, case_path, root):
    shots, votes = case.get("screenshots"), review.get("screenshots")
    if not isinstance(shots, list) or not isinstance(votes, dict):
        raise NormalizationError("review_requires_all_screenshot_votes")
    result = {}
    for shot in shots:
        if not isinstance(shot, dict) or not isinstance(shot.get("phase"), str) or not shot["phase"]:
            raise NormalizationError("invalid_screenshot_phase")
        phase, name = shot["phase"], shot.get("path")
        if phase == "checks" or phase in result:
            raise NormalizationError("duplicate_or_reserved_screenshot_phase")
        if (not isinstance(name, str) or not name or "\\" in name or ":" in name or "\0" in name
                or Path(name).is_absolute() or PureWindowsPath(name).root or PureWindowsPath(name).drive
                or ".." in Path(name).parts):
            raise NormalizationError("invalid_screenshot_path")
        image = (case_path.parent / name).resolve()
        try:
            relative = image.relative_to(root)
        except ValueError as error:
            raise NormalizationError("screenshot_outside_evidence_root") from error
        if not image.is_file():
            raise NormalizationError("screenshot_file_missing")
        raw = image.read_bytes()
        if (not isinstance(shot.get("sha256"), str)
                or not re.fullmatch(r"[0-9a-f]{64}", shot["sha256"])
                or hashlib.sha256(raw).hexdigest() != shot["sha256"]):
            raise NormalizationError("review_screenshot_hash_mismatch")
        if not (raw.startswith(b"\x89PNG\r\n\x1a\n") or raw.startswith(b"\xff\xd8\xff")
                or (raw.startswith(b"RIFF") and raw[8:12] == b"WEBP")):
            raise NormalizationError("invalid_screenshot_image")
        result[phase] = {**deepcopy(shot), "path": relative.as_posix()}
    if set(votes) != set(result) or any(type(v) is not bool for v in votes.values()):
        raise NormalizationError("review_requires_all_screenshot_votes")
    for phase, shot in result.items():
        shot["reviewed"] = votes[phase]
    result["checks"] = {key: all(votes.get(p) is True for p in phases)
                        for key, phases in CHECK_PHASES.items()}
    return result


def _retractions(case, review, source_ids, issues):
    events, reviews = case.get("retractions"), review.get("retractions", [])
    if not isinstance(events, list) or not all(isinstance(e, dict) for e in events):
        raise NormalizationError("invalid_retraction_evidence")
    if not isinstance(reviews, list):
        raise NormalizationError("invalid_retraction_reviews")
    by_index = {}
    for row in reviews:
        if not isinstance(row, dict):
            raise NormalizationError("invalid_retraction_review")
        index = row.get("eventIndex", row.get("index"))
        if index is None:
            matches = [i for i, event in enumerate(events)
                       if event.get("sourceId") == row.get("sourceId")]
            if len(matches) != 1:
                raise NormalizationError("ambiguous_or_unmatched_retraction_review")
            index = matches[0]
        if (type(index) is not int or not 0 <= index < len(events) or index in by_index
                or events[index].get("sourceId") not in source_ids
                or ("sourceId" in row and row["sourceId"] != events[index].get("sourceId"))
                or any(type(row.get(k)) is not bool for k in ("confirmed", "sourceUnchanged"))
                or ("reviewed" in row and type(row["reviewed"]) is not bool)):
            raise NormalizationError("invalid_retraction_review")
        by_index[index] = row
    result = []
    for index, event in enumerate(events):
        row = by_index.get(index, {})
        item = {**deepcopy(event), "nodeId": event.get("sourceId"), "eventIndex": index,
                "reviewed": bool(row) and row.get("reviewed", True),
                "confirmed": row.get("confirmed"), "sourceUnchanged": row.get("sourceUnchanged")}
        if item["reviewed"] is not True or item["nodeId"] not in source_ids:
            issues.append("unreviewed_retraction:" + str(index))
        elif item["confirmed"] and item["sourceUnchanged"]:
            issues.append("confirmed_retraction:" + str(index))
        result.append(item)
    return result


def _predictions(case, nodes, issues, *, coverage_scope="strict"):
    pieces = case.get("predictions")
    if not isinstance(pieces, list) or not all(isinstance(p, dict) for p in pieces):
        raise NormalizationError("invalid_prediction_evidence")
    observations = case.get("productionEligibility", [])
    retained_members = {}
    current_piece_ids = {p.get("id") for p in pieces}
    if not isinstance(observations, list) or not all(isinstance(row, dict) for row in observations):
        raise NormalizationError("invalid_production_eligibility")
    if "sourceObservations" in case:
        records = case["sourceObservations"]
        history = case.get("predictionHistory", [])
        if not isinstance(records, list) or not isinstance(history, list):
            raise NormalizationError("invalid_source_observations")
        history_by_id = {p.get("id"): p for p in history if isinstance(p, dict)}
        node_by_id = {n["id"]: n for n in nodes}
        current_ids = {n["id"] for n in nodes if n.get("currentlyRendered") is not False}
        retired = node_by_id.keys() - current_ids
        retained, seen_sources = [], set()
        observations = [row for row in observations if row.get("sourceId") not in retired]
        for record in records:
            if (not isinstance(record, dict) or record.get("sourceId") in seen_sources
                    or record.get("sourceId") not in node_by_id
                    or record.get("eligible") is not None and type(record.get("eligible")) is not bool
                    or not isinstance(record.get("pieces"), list)):
                raise NormalizationError("invalid_source_observation")
            source_id = record["sourceId"]
            seen_sources.add(source_id)
            if source_id not in retired:
                continue
            source = node_by_id[source_id]
            if record.get("sourceText") != source["text"]:
                raise NormalizationError("source_observation_text_mismatch")
            if record.get("eligible") is None:
                issues.append("unknown_retired_source:" + source_id)
                continue
            represented = record.get("representedBy")
            atomic_peer = any(p.get("sourceId") == represented and represented != source_id
                              and source_id in p.get("sourceIds", []) and len(p.get("sourceIds", [])) > 1
                              for p in record["pieces"] if isinstance(p, dict))
            if record.get("rendered") is not True and not atomic_peer:
                issues.append("unobserved_retired_source:" + source_id)
                continue
            observations.append({"sourceId": source_id, "eligible": record["eligible"]})
            for piece in record["pieces"]:
                if (not isinstance(piece, dict) or history_by_id.get(piece.get("id")) != piece
                        or source_id not in piece.get("sourceIds", [piece.get("sourceId")])):
                    raise NormalizationError("source_observation_not_in_raw_prediction_history")
                retained.append(piece)
                retained_members.setdefault(piece["id"], set()).add(source_id)
        # One atomic piece legitimately covers several source records. Collapse
        # only identical evidence references, retaining every source membership.
        selected = {p["id"]: p for p in pieces}
        for piece in retained:
            if piece["id"] in selected and selected[piece["id"]] != piece:
                raise NormalizationError("conflicting_source_observation")
            selected[piece["id"]] = piece
        pieces = list(selected.values())
    grouped = {n["id"]: [] for n in nodes}
    seen = set()
    for piece in pieces:
        piece_id, source_id = piece.get("id"), piece.get("sourceId")
        if not isinstance(piece_id, str) or not piece_id or piece_id in seen:
            issues.append("missing_or_duplicate_prediction_id")
        else:
            seen.add(piece_id)
        members = piece.get("sourceIds", [source_id])
        if (not isinstance(members, list) or not members or source_id not in members
                or len(set(members)) != len(members)
                or any(not isinstance(member, str) or member not in grouped for member in members)):
            issues.append("unmatched_prediction_source:" + str(source_id))
            continue
        if piece.get("sourceEvidence") == "unmatched" or piece.get("auditIssue"):
            issues.append("prediction_source_audit_issue:" + source_id)
        projected = members if piece_id in current_piece_ids else retained_members.get(piece_id, members)
        for member in projected:
            grouped[member].append(piece)
    eligibility = {}
    if not isinstance(observations, list):
        raise NormalizationError("invalid_production_eligibility")
    for row in observations:
        if (not isinstance(row, dict) or not isinstance(row.get("sourceId"), str)
                or row["sourceId"] not in grouped or row["sourceId"] in eligibility
                or type(row.get("eligible")) is not bool):
            issues.append("invalid_production_eligibility")
            continue
        eligibility[row["sourceId"]] = row["eligible"]
    pending_reasons = case.get("blockedReason", "")
    no_capture_failure = (isinstance(pending_reasons, str)
                          and all(r in ("", "screenshot_review_pending")
                                  for r in pending_reasons.split(";")))
    finished = (case.get("captureFinished") is True and case.get("settled") is True
                and case.get("evidenceReady") is True and not case.get("errors")
                and case.get("visitSuccess") is True and case.get("domainVerified") is True
                and no_capture_failure
                and (case.get("completed") is True
                     or case.get("completed") is False and "screenshot_review_pending" in pending_reasons))
    result = []
    for node in nodes:
        source_id = node["id"]
        rows = grouped[source_id]
        if not rows:
            # Only an explicit production observation in a finished capture can
            # establish rejection. Absence from a partial run is unknown.
            rejected = finished and node.get("rendered") is True and eligibility.get(source_id) is False
            item = {"nodeId": source_id, "eligible": False if rejected else None,
                    "completed": False, "failed": False if rejected else None, "pieceIds": []}
        else:
            valid = all(type(p.get(k)) is bool for p in rows for k in ("eligible", "completed", "failed"))
            eligible = any(p.get("eligible") is True for p in rows)
            failed = any(p.get("failed") is True for p in rows)
            completed = (valid and eligible and not failed
                         and all(p["completed"] for p in rows if p["eligible"]))
            if not valid:
                issues.append("invalid_prediction_flags:" + source_id)
            if any(p.get("completed") is True and p.get("eligible") is not True for p in rows):
                issues.append("contradictory_piece_completion:" + source_id)
                completed = False
            if eligibility.get(source_id) is False and eligible:
                issues.append("contradictory_production_eligibility:" + source_id)
            elif (coverage_scope == "automatic-page" and eligibility.get(source_id) is True
                  and valid and not eligible):
                issues.append("contradictory_production_eligibility:" + source_id)
            item = {"nodeId": source_id, "eligible": eligible if valid or eligible else None,
                    "completed": completed, "failed": failed if valid or failed else None,
                    "pieceIds": [p.get("id") for p in rows]}
        if any(type(item[k]) is not bool for k in ("eligible", "completed", "failed")):
            issues.append("unknown_prediction:" + source_id)
        # Eligibility is candidate recognition, independent of the page decision.
        # A skipped page leaves candidates untranslated; retain their flags for
        # FP/FN scoring without requiring work that was never selected.
        elif item["failed"] or (case.get("pageDecision") is True
                                and item["eligible"] and not item["completed"]):
            issues.append("node_not_completed:" + source_id)
        result.append(item)
    return result


def _coverage_counts(result, coverage_scope, issues, reasons, request_evidence_valid,
                     assessment_zero_call_valid):
    """Derive coverage from labels, predictions and accounted host evidence."""
    terminals = [e for e in result["hostEvents"] if e.get("type") == "terminal"]
    terminal_ok = bool(terminals) and all(
        e.get("success") is True and type(e.get("failedCount")) is int
        and e["failedCount"] == 0 and _count(e.get("cacheHits"))
        and _count(e.get("requestCount")) for e in terminals)
    request_count = result["requestCount"]
    positive = translated_count = untranslated = skipped = 0
    rows = []
    for label, prediction in zip(result["annotations"], result["predictions"]):
        valid = all(type(prediction.get(k)) is bool for k in ("eligible", "completed", "failed"))
        translated = (valid and prediction["eligible"] and prediction["completed"]
                      and not prediction["failed"] and _count(request_count) and request_count > 0
                      and result.get("liveTranslation") is True and terminal_ok and request_evidence_valid)
        # A completion claim on a zero-call skipped page is contradictory even
        # when its individual boolean flags have the correct types.
        if coverage_scope == "automatic-page" and prediction["completed"] and not translated:
            issues.append("contradictory_node_completion:" + prediction["nodeId"])
        rows.append((label, prediction, valid, translated))
    page = result["pageAnnotation"]
    policy_page = (coverage_scope == "automatic-page" and not issues
                   and all(r == "screenshot_review_pending" for r in reasons)
                   and result.get("expectedPageTranslation") is False
                   and _known(page) and page["needTranslation"] is False
                   and result.get("pageDecision") is False
                   and _count(request_count) and request_count == 0
                   and (terminal_ok or assessment_zero_call_valid) and request_evidence_valid
                   and any(e.get("type") == "capabilities" and e.get("live") is True
                           for e in result["hostEvents"]))
    for label, prediction, valid, translated in rows:
        if not _known(label) or label["needTranslation"] is not True:
            continue
        positive += 1
        translated_count += int(translated)
        untranslated += int(not translated)
        policy_skip = (policy_page and valid and not prediction["failed"]
                       and not prediction["completed"])
        skipped += int(policy_skip)
        if coverage_scope == "automatic-page" and not translated and not policy_skip:
            issues.append("confirmed_omission:" + label["nodeId"])
    result.update(annotatedPositiveNodes=positive, translatedPositiveNodes=translated_count,
                  translationCoverage=translated_count / positive if positive else None,
                  unTranslatedPositiveNodes=untranslated, policySkippedPositiveNodes=skipped,
                  confirmedOmissions=untranslated - skipped)


def normalize_case(case_path, review_path, output_path, evidence_root=None, *, coverage_scope="strict"):
    """Create one scorer case exclusively, retaining the original evidence."""
    if not isinstance(coverage_scope, str) or coverage_scope not in ("strict", "automatic-page"):
        raise NormalizationError("invalid_coverage_scope")
    case_path, review_path, output_path = map(Path, (case_path, review_path, output_path))
    if output_path.exists() or output_path.resolve() in (case_path.resolve(), review_path.resolve()):
        raise NormalizationError("output_must_be_new_file")
    raw, review_raw = case_path.read_bytes(), review_path.read_bytes()
    case, review = _load(raw), _load(review_raw)
    case_hash = hashlib.sha256(raw).hexdigest()
    if review.get("caseSha256") != case_hash:
        raise NormalizationError("review_case_hash_mismatch")
    root = Path(evidence_root).resolve() if evidence_root is not None else case_path.parent.resolve()
    if not root.is_dir():
        raise NormalizationError("evidence_root_must_be_directory")
    issues = []
    result = deepcopy(case)
    result["coverageScope"] = {"schemaVersion": 1, "mode": coverage_scope}
    result["provenance"] = {"casePath": str(case_path.resolve()), "caseSha256": case_hash,
                            "reviewPath": str(review_path.resolve()),
                            "reviewSha256": hashlib.sha256(review_raw).hexdigest(),
                            "evidenceRoot": str(root), "rawCapture": deepcopy(case),
                            "rawReview": deepcopy(review)}
    # Metadata comes from the captured source identity, never this checkout.
    result["sourceHead"] = case.get("sourceHead")
    result["synthetic"] = case.get("synthetic") is True or case.get("simulation") is True
    result["screenshots"] = _screenshots(case, review, case_path, root)
    votes = review["screenshots"]
    if not REQUIRED_PHASES.issubset(votes) or not all(votes.values()):
        issues.append("missing_or_rejected_screenshot_reviews")
    sources = case.get("sourceNodes")
    if not isinstance(sources, list) or not sources:
        raise NormalizationError("missing_independent_source_nodes")
    nodes, source_ids = [], set()
    for source in sources:
        if (not isinstance(source, dict) or not isinstance(source.get("id"), str)
                or not source["id"] or source["id"] in source_ids):
            raise NormalizationError("invalid_or_duplicate_source_id")
        source_ids.add(source["id"])
        node = {**deepcopy(source), "text": source.get("sourceText"),
                "position": _position(source.get("rects"))}
        if (not isinstance(node["text"], str) or not node["text"].strip()
                or not isinstance(node.get("tag"), str) or not node["tag"]
                or not isinstance(node.get("role"), str) or node["position"] is None):
            issues.append("invalid_source_node:" + source["id"])
        nodes.append(node)
    result["sourceNodes"] = nodes
    labels = review.get("annotations", [])
    if not isinstance(labels, list):
        raise NormalizationError("invalid_independent_annotations")
    by_id = {}
    for row in labels:
        if (not isinstance(row, dict) or not isinstance(row.get("sourceId"), str)
                or row["sourceId"] not in source_ids or row["sourceId"] in by_id):
            raise NormalizationError("duplicate_or_unmatched_independent_annotation")
        by_id[row["sourceId"]] = _label(row, row["sourceId"])
    result["annotations"] = [by_id.get(n["id"], _label(None, n["id"])) for n in nodes]
    result["pageAnnotation"] = _label(review.get("pageAnnotation"))
    if not _known(result["pageAnnotation"]):
        issues.append("unknown_page_label")
    elif (coverage_scope == "automatic-page" and type(case.get("expectedPageTranslation")) is bool
          and result["pageAnnotation"]["needTranslation"] != case["expectedPageTranslation"]):
        issues.append("initial_page_label_disagrees_with_review")
    for label in result["annotations"]:
        if not _known(label):
            issues.append("unknown_node_label:" + label["nodeId"])
    result["predictions"] = _predictions(case, nodes, issues, coverage_scope=coverage_scope)
    if coverage_scope == "strict" and case.get("pageDecision") is False:
        for label in result["annotations"]:
            if _known(label) and label["needTranslation"] is True:
                issues.append("confirmed_omission:" + label["nodeId"])
    result["retractions"] = _retractions(case, review, source_ids, issues)
    capabilities, terminals = case.get("capabilities"), case.get("terminals", [])
    if capabilities is not None and not isinstance(capabilities, dict):
        raise NormalizationError("invalid_capabilities")
    if not isinstance(terminals, list) or not all(isinstance(e, dict) for e in terminals):
        raise NormalizationError("invalid_terminals")
    events = ([deepcopy(capabilities)] if capabilities is not None else []) + deepcopy(terminals)
    # The assess action emits an assessment, not a translation terminal. Keep
    # that original event verbatim; strict mode retains its legacy host events.
    assessment = None
    if coverage_scope == "automatic-page" and "localAssessment" in case:
        assessment = case["localAssessment"]
        if not isinstance(assessment, dict):
            raise NormalizationError("invalid_local_assessment")
        events.append(deepcopy(assessment))
    result["hostEvents"] = events
    request_issue_start = len(issues)
    if assessment is not None and (
            type(assessment.get("schemaVersion")) is not int or assessment["schemaVersion"] != 1
            or assessment.get("type") != "assessment"
            or not isinstance(assessment.get("id"), str) or not assessment["id"].strip()
            or not isinstance(case.get("id"), str) or not case["id"].strip()
            or assessment.get("caseId") != case["id"]
            or assessment.get("live") is not True or assessment.get("needsTranslation") is not False):
        issues.append("invalid_local_assessment")
    terminal_ids = [e.get("id") for e in terminals]
    if any(e.get("caseId") != case.get("id") or not isinstance(e.get("id"), str) or not e["id"]
           for e in events):
        issues.append("host_event_identity_mismatch")
    if any(e.get("type") != "terminal" for e in terminals) or capabilities and capabilities.get("type") != "capabilities":
        issues.append("host_event_type_mismatch")
    if any(not isinstance(i, str) for i in terminal_ids) or len(set(i for i in terminal_ids if isinstance(i, str))) != len(terminals):
        issues.append("duplicate_or_invalid_terminal_id")
    counts_valid = all(_count(e.get("requestCount")) for e in terminals)
    result["requestCount"] = sum(e["requestCount"] for e in terminals) if counts_valid else None
    if not counts_valid:
        issues.append("invalid_terminal_request_count")
    if any(e.get("success") is not True or type(e.get("failedCount")) is not int
           or e["failedCount"] != 0 or not _count(e.get("cacheHits")) for e in terminals):
        issues.append("failed_or_invalid_terminal")
    requests = case.get("requestIds")
    if (not isinstance(requests, list) or not all(isinstance(r, str) and r for r in requests)
            or len(set(requests)) != len(requests)):
        issues.append("invalid_request_identities")
        requests = []
    # Concurrent native work can finish in a different order from admission.
    # Require a bijection (duplicates/invalid IDs are blocked above), keeping
    # the original terminal sequence and each request's identity untouched.
    if (len(requests) != len(terminal_ids)
            or set(requests) != {i for i in terminal_ids if isinstance(i, str)}):
        issues.append("request_terminal_identity_mismatch")
    request_evidence_valid = len(issues) == request_issue_start
    # Empty lists must be present and valid in the raw capture. An assessment
    # never substitutes for terminal accounting of an admitted request.
    assessment_zero_call_valid = (assessment is not None and request_evidence_valid
                                  and case.get("requestIds") == [] and case.get("terminals") == []
                                  and case.get("pageDecision") is False)
    if case.get("expectedPageTranslation") is True:
        if (not capabilities or capabilities.get("live") is not True
                or case.get("liveTranslation") is not True or not requests or not terminals
                or not counts_valid or result["requestCount"] == 0):
            issues.append("missing_live_translation_evidence")
    elif case.get("expectedPageTranslation") is False:
        # Legacy zero-call terminal proof remains valid. The separate assess
        # action qualifies only with its real event and no admitted requests.
        if coverage_scope == "automatic-page":
            if not terminals and not assessment_zero_call_valid:
                issues.append("missing_local_terminal_evidence")
            if not capabilities or capabilities.get("live") is not True:
                issues.append("missing_live_capabilities_evidence")
        if result["requestCount"] != 0:
            issues.append("unnecessary_translation_request")
    else:
        issues.append("invalid_expected_page_translation")
    if capabilities and capabilities.get("live") is not True or result["synthetic"] or case.get("mock") is True:
        issues.append("non_live_or_simulated_capture")
    if type(case.get("pageDecision")) is not bool:
        issues.append("invalid_page_decision")
    errors = case.get("errors")
    if not isinstance(errors, list) or errors:
        issues.append("capture_errors")
    if any(case.get(k) is not True for k in ("captureFinished", "evidenceReady", "settled", "visitSuccess", "domainVerified")):
        issues.append("capture_not_successfully_finished")
    blocked = case.get("blockedReason")
    if not isinstance(blocked, str):
        issues.append("invalid_capture_blocked_reason")
        blocked = "invalid_capture_blocked_reason"
    # This is the sole pending state a review can resolve. All failure reasons
    # remain blocking, and the original string is retained in rawCapture.
    reasons = [r for r in blocked.split(";") if r]
    if (case.get("completed") is not True
            and not (case.get("completed") is False and "screenshot_review_pending" in reasons)):
        issues.append("capture_completion_not_established")
    _coverage_counts(result, coverage_scope, issues, reasons, request_evidence_valid,
                     assessment_zero_call_valid)
    if not issues:
        reasons = [r for r in reasons if r != "screenshot_review_pending"]
    issues = list(dict.fromkeys(reasons + issues))
    result["normalizationIssues"] = issues
    result["blockedReason"] = ";".join(issues)
    result["completed"] = not issues
    serialized = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    # Exclusive creation also prevents races and symlink aliases from replacing
    # any original case, review, screenshot or previous normalized result.
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(serialized)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", type=Path, required=True)
    parser.add_argument("--review", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path)
    parser.add_argument("--coverage-scope", choices=("strict", "automatic-page"), default="strict")
    args = parser.parse_args(argv)
    try:
        result = normalize_case(args.case, args.review, args.output, args.evidence_root,
                                coverage_scope=args.coverage_scope)
        print(json.dumps({"completed": result["completed"], "blockedReason": result["blockedReason"],
                          "coverageScope": result["coverageScope"]}))
        return 0 if result["completed"] else 1
    except (OSError, ValueError, TypeError, KeyError, RuntimeError) as error:
        print(json.dumps({"completed": False, "error": str(error)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
