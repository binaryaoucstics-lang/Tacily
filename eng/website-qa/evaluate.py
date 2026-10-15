"""Offline, fail-closed scoring of the website QA baseline contract (stdlib only)."""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
from fractions import Fraction
import hashlib
import json
from pathlib import Path, PureWindowsPath
import re
import sys
from urllib.parse import urlsplit


SPLITS = ("development", "holdout")
COVERAGE_SCOPES = ("strict", "automatic-page")
THRESHOLDS = {"independentDomains": 100, "nodeAccuracy": .95,
              "nodePrecision": .95, "pageAccuracy": .97,
              "completedHoldoutDomains": 30}
VISUAL_COUNTS = ("unknownVisualNodes", "incorrectVisualTranslations",
                 "unnecessaryVisualTranslations")
VISUAL_OUTCOMES = frozenset(("correct_translation", "partial_translation",
                            "incorrect_translation", "unchanged",
                            "unnecessary_translation", "not_visible", "unknown"))
ORIGINAL_PHASES = frozenset(("before", "after", "stable", "scroll-before",
                             "scroll-after", "return-after", "reflow-after"))
VISUAL_OUTPUT_PHASES = frozenset(("after", "after-full", "stable", "stable-full",
                                  "scroll-after", "return-after",
                                  "reflow-after", "disclosure-open", "disclosure-restored"))


def _hash(value, *, allow_uppercase=False):
    pattern = r"[0-9a-fA-F]{64}" if allow_uppercase else r"[0-9a-f]{64}"
    return isinstance(value, str) and re.fullmatch(pattern, value) is not None


def _visual_option(value):
    if type(value) is not bool:
        raise ValueError("require_visual_review must be boolean")


def _same(left, right):
    """JSON equality including types (True must not stand in for 1)."""
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return left.keys() == right.keys() and all(_same(left[k], right[k]) for k in left)
    if isinstance(left, list):
        return len(left) == len(right) and all(_same(a, b) for a, b in zip(left, right))
    return left == right


def _frozen_label(row):
    # These defaults are the public source-review interface, not visual votes.
    row = row if isinstance(row, dict) else {}
    value = row.get("needTranslation", "unknown")
    return {"needTranslation": value,
            "reviewed": row.get("reviewed", type(value) is bool),
            "independent": row.get("independent", type(value) is bool)}


def _visual_evidence(case, nodes, labels, evidence_root):
    """Check retained originals independently; never consume submitted totals.

    Raw capture byte authentication belongs to the normalizer. Here the visual
    review must bind to that retained hash, the frozen source review and exact
    original texts. Only original screenshot paths inside evidence_root are read.
    """
    reasons = []
    provenance = case.get("provenance")
    if not isinstance(provenance, dict):
        return {}, set(), ["missing_visual_provenance"]
    raw, review = provenance.get("rawCapture"), provenance.get("rawReview")
    visual = case.get("visualReview")
    if not isinstance(raw, dict) or not isinstance(review, dict):
        return {}, set(), ["missing_visual_originals"]
    if (not isinstance(visual, dict) or type(visual.get("schemaVersion")) is not int
            or visual["schemaVersion"] != 1 or visual.get("reviewed") is not True
            or visual.get("independent") is not True):
        return {}, set(), ["invalid_visual_review"]
    if not _same(visual, review.get("visualReview")):
        reasons.append("visual_review_original_mismatch")
    if (not _hash(provenance.get("caseSha256"))
            or visual.get("caseSha256") != provenance.get("caseSha256")
            or review.get("caseSha256") != provenance.get("caseSha256")):
        reasons.append("visual_case_hash_mismatch")
    if (not _hash(review.get("sourceReviewSha256"), allow_uppercase=True)
            or visual.get("sourceReviewSha256") != review.get("sourceReviewSha256")):
        reasons.append("visual_source_review_hash_mismatch")
    for key in ("id", "registeredDomain", "url", "category", "language", "split",
                "expectedPageTranslation", "finalUrl", "sourceHead", "capturedAt",
                "title", "viewport", "pageDecision", "visitSuccess", "liveTranslation",
                "errors", "requestIds", "terminals", "sourceHashes", "manifestHash"):
        if not _same(case.get(key), raw.get(key)):
            reasons.append("visual_original_metadata_mismatch:" + key)
    if (any(raw.get(k) is not True for k in
            ("captureFinished", "evidenceReady", "settled", "domainVerified"))
            or raw.get("simulation") is True or raw.get("synthetic") is True
            or raw.get("mock") is True or raw.get("errors") != []):
        reasons.append("visual_original_capture_incomplete")
    blocked = raw.get("blockedReason")
    pending = (isinstance(blocked, str) and all(
        r in ("", "screenshot_review_pending") for r in blocked.split(";")))
    if (not pending or not (raw.get("completed") is True or
            raw.get("completed") is False and "screenshot_review_pending" in blocked)):
        reasons.append("visual_original_capture_blocked")
    originals = raw.get("sourceNodes")
    source_map = {}
    if isinstance(originals, list):
        for source in originals:
            if (not isinstance(source, dict) or not isinstance(source.get("id"), str)
                    or not source["id"].strip() or source["id"] in source_map
                    or not isinstance(source.get("sourceText"), str)
                    or not source["sourceText"].strip()):
                reasons.append("invalid_visual_original_source")
                continue
            source_map[source["id"]] = source
    if set(source_map) != {n["id"] for n in nodes}:
        reasons.append("visual_original_source_identity_mismatch")
    for node in nodes:
        if node["text"] != source_map.get(node["id"], {}).get("sourceText"):
            reasons.append("visual_original_source_text_mismatch:" + node["id"])
    source_labels = review.get("annotations")
    frozen = {}
    if isinstance(source_labels, list):
        for row in source_labels:
            if (not isinstance(row, dict) or not isinstance(row.get("sourceId"), str)
                    or row["sourceId"] not in source_map or row["sourceId"] in frozen):
                reasons.append("invalid_visual_frozen_annotation")
                continue
            frozen[row["sourceId"]] = row
    else:
        reasons.append("invalid_visual_frozen_annotations")
    for node in nodes:
        label = labels.get(node["id"], {})
        if not _same(_frozen_label(frozen.get(node["id"])),
                     {k: label.get(k) for k in ("needTranslation", "reviewed", "independent")}):
            reasons.append("visual_frozen_label_mismatch:" + node["id"])
    page = case.get("pageAnnotation", {})
    if not _same(_frozen_label(review.get("pageAnnotation")),
                 {k: page.get(k) for k in ("needTranslation", "reviewed", "independent")}):
        reasons.append("visual_frozen_page_label_mismatch")
    reasons.extend(_original_retraction_checks(raw, review, case, source_map))

    phases = set()
    shots, votes = raw.get("screenshots"), review.get("screenshots")
    normalized = case.get("screenshots", {})
    if (not isinstance(shots, list) or not isinstance(votes, dict)
            or not isinstance(provenance.get("casePath"), str)
            or not provenance["casePath"]):
        reasons.append("invalid_visual_original_screenshots")
    else:
        for shot in shots:
            if (not isinstance(shot, dict) or not isinstance(shot.get("phase"), str)
                    or not shot["phase"].strip() or shot["phase"] == "checks"
                    or shot["phase"] in phases):
                reasons.append("invalid_visual_original_phase")
                continue
            phase, name = shot["phase"], shot.get("path")
            phases.add(phase)
            item = normalized.get(phase)
            if (not isinstance(name, str) or not name or "\\" in name or ":" in name
                    or "\0" in name or PureWindowsPath(name).root
                    or ".." in Path(name).parts or not _hash(shot.get("sha256"))
                    or votes.get(phase) is not True or not isinstance(item, dict)
                    or item.get("phase") != phase or item.get("sha256") != shot["sha256"]
                    or not _screenshot_ok(item, evidence_root)):
                reasons.append("invalid_visual_original_screenshot:" + phase)
                continue
            try:
                original_path = (Path(provenance["casePath"]).parent / name).resolve()
                relative = original_path.relative_to(evidence_root).as_posix()
                if (item["path"] != relative
                        or hashlib.sha256(original_path.read_bytes()).hexdigest() != shot["sha256"]):
                    reasons.append("visual_original_screenshot_mismatch:" + phase)
            except (OSError, ValueError, RuntimeError):
                reasons.append("visual_original_screenshot_mismatch:" + phase)
        if (set(votes) != phases or not ORIGINAL_PHASES.issubset(phases)
                or any(v is not True for v in votes.values())
                or set(normalized) != phases | {"checks"}):
            reasons.append("visual_original_phase_votes_mismatch")

    rows = visual.get("nodes")
    outcomes = {}
    if not isinstance(rows, list):
        reasons.append("invalid_visual_nodes")
    else:
        for row in rows:
            if (not isinstance(row, dict) or not isinstance(row.get("sourceId"), str)
                    or row["sourceId"] not in source_map or row["sourceId"] in outcomes):
                reasons.append("duplicate_or_unmatched_visual_node")
                continue
            identity = row["sourceId"]
            evidence = row.get("evidence")
            outcome = row.get("outcome")
            valid = (row.get("reviewed") is True and row.get("independent") is True
                     and isinstance(outcome, str) and outcome in VISUAL_OUTCOMES
                     and row.get("sourceTextSha256") == hashlib.sha256(
                         source_map[identity]["sourceText"].encode("utf-8")).hexdigest()
                     and isinstance(evidence, list) and bool(evidence))
            evidence = evidence if isinstance(evidence, list) else []
            valid = valid and all(isinstance(e, dict) and isinstance(e.get("phase"), str)
                and e["phase"] in phases and isinstance(e.get("note"), str)
                and bool(e["note"].strip()) for e in evidence)
            if outcome in ("correct_translation", "partial_translation", "incorrect_translation",
                           "unchanged", "unnecessary_translation"):
                # Unchanged also asserts inspected output. Only capture output
                # phases and its preserved stable refreshes establish that.
                valid = valid and any(isinstance(e, dict) and e.get("phase") in phases
                    and (e["phase"] in VISUAL_OUTPUT_PHASES or re.fullmatch(
                        r"stable(?:-full)?-refresh-[1-9][0-9]*", e["phase"])) for e in evidence)
            outcomes[identity] = outcome if valid else None
            if not valid:
                reasons.append("invalid_visual_node:" + identity)
        if set(outcomes) != set(source_map):
            reasons.append("missing_visual_nodes")
    # A malformed proof cannot lend credit to any row. Recognition still scores.
    if reasons:
        return {}, set(), list(dict.fromkeys(reasons))
    native, native_reasons = _original_native_completion(raw, case, source_map)
    return outcomes, native, native_reasons


def _original_native_completion(raw, case, sources):
    """Establish source completion from original native pieces, including history."""
    reasons = []
    events = ([raw["capabilities"]] if isinstance(raw.get("capabilities"), dict) else [])
    terminals = raw.get("terminals")
    requests = raw.get("requestIds")
    if not isinstance(terminals, list) or not all(isinstance(e, dict) for e in terminals):
        return set(), ["invalid_visual_original_terminals"]
    events += terminals
    if "localAssessment" in raw and case.get("coverageScope", {}).get("mode") == "automatic-page":
        events.append(raw["localAssessment"])
    if not _same(events, case.get("hostEvents")):
        reasons.append("visual_original_host_events_mismatch")
    if (not isinstance(requests, list) or any(not isinstance(r, str) or not r for r in requests)
            or len(set(requests)) != len(requests)
            or set(requests) != {e.get("id") for e in terminals if isinstance(e.get("id"), str)}
            or len(requests) != len(terminals)):
        reasons.append("visual_original_request_identity_mismatch")
    pieces = raw.get("predictions")
    if not isinstance(pieces, list) or not all(isinstance(p, dict) for p in pieces):
        return set(), reasons + ["invalid_visual_original_predictions"]
    selected = {}
    for p in pieces:
        if not isinstance(p.get("id"), str) or not p["id"] or p["id"] in selected:
            reasons.append("invalid_visual_original_piece_id")
        else:
            selected[p["id"]] = (p, None)
    records, history = raw.get("sourceObservations", []), raw.get("predictionHistory", [])
    if not isinstance(records, list) or not isinstance(history, list):
        return set(), reasons + ["invalid_visual_original_history"]
    seen_records, retired_eligibility = set(), {}
    for record in records:
        if (not isinstance(record, dict) or not isinstance(record.get("sourceId"), str)
                or record["sourceId"] not in sources or record["sourceId"] in seen_records
                or record.get("eligible") is not None and type(record.get("eligible")) is not bool
                or not isinstance(record.get("pieces"), list)):
            reasons.append("invalid_visual_original_source_observation")
            continue
        seen_records.add(record["sourceId"])
        if sources[record["sourceId"]].get("currentlyRendered") is not False:
            continue
        identity = record["sourceId"]
        if (record.get("sourceText") != sources[identity]["sourceText"]
                or type(record.get("eligible")) is not bool
                or not isinstance(record.get("pieces"), list)):
            reasons.append("invalid_visual_original_source_observation")
            continue
        retired_eligibility[identity] = record["eligible"]
        represented = record.get("representedBy")
        atomic_peer = any(isinstance(p, dict) and p.get("sourceId") == represented
            and represented != identity and isinstance(p.get("sourceIds"), list)
            and identity in p["sourceIds"] and len(p["sourceIds"]) > 1
            for p in record["pieces"])
        if record.get("rendered") is not True and not atomic_peer:
            reasons.append("unobserved_visual_original_retired_source:" + identity)
        for p in record["pieces"]:
            if (not isinstance(p, dict) or not isinstance(p.get("id"), str)
                    or not any(_same(p, h) for h in history)):
                reasons.append("visual_original_piece_history_mismatch")
                continue
            existing = selected.get(p["id"])
            if existing and not _same(existing[0], p):
                reasons.append("visual_original_piece_history_mismatch")
            elif not existing:
                selected[p["id"]] = (p, {identity})
            elif existing[1] is not None:
                existing[1].add(identity)
    if any(source.get("currentlyRendered") is False and identity not in seen_records
           for identity, source in sources.items()):
        reasons.append("missing_visual_original_retired_observation")
    grouped = {identity: [] for identity in sources}
    for piece, retained in selected.values():
        members = piece.get("sourceIds", [piece.get("sourceId")])
        if (not isinstance(members, list) or not members
                or any(not isinstance(m, str) or m not in sources for m in members)
                or len(set(members)) != len(members) or piece.get("sourceId") not in members
                or piece.get("sourceEvidence") == "unmatched" or piece.get("auditIssue")):
            reasons.append("invalid_visual_original_piece_source")
            continue
        if (any(type(piece.get(k)) is not bool for k in ("eligible", "completed", "failed"))
                or piece["completed"] and (not piece["eligible"] or piece["failed"])):
            reasons.append("invalid_visual_original_piece_flags")
        for member in members if retained is None else retained:
            if member not in members:
                reasons.append("invalid_visual_original_piece_source")
            else:
                grouped[member].append(piece)
    observations = raw.get("productionEligibility", [])
    observed = {}
    if not isinstance(observations, list):
        reasons.append("invalid_visual_original_eligibility")
        observations = []
    for observation in observations:
        if (isinstance(observation, dict) and isinstance(observation.get("sourceId"), str)
                and sources.get(observation["sourceId"], {}).get("currentlyRendered") is False):
            continue  # The retained original observation is authoritative here.
        if (not isinstance(observation, dict) or not isinstance(observation.get("sourceId"), str)
                or observation["sourceId"] not in sources or observation["sourceId"] in observed
                or type(observation.get("eligible")) is not bool):
            reasons.append("invalid_visual_original_eligibility")
        else:
            observed[observation["sourceId"]] = observation["eligible"]
    observed.update(retired_eligibility)
    for identity, rows in grouped.items():
        if observed.get(identity) is False and any(p.get("eligible") is True for p in rows):
            reasons.append("contradictory_visual_original_eligibility:" + identity)
    native = {identity for identity, rows in grouped.items() if rows
              and all(type(p.get(k)) is bool for p in rows for k in ("eligible", "completed", "failed"))
              and any(p["eligible"] for p in rows) and not any(p["failed"] for p in rows)
              and all(p["completed"] for p in rows if p["eligible"])
              and not any(p["completed"] and not p["eligible"] for p in rows)}
    return (set() if reasons else native), list(dict.fromkeys(reasons))


def _original_retraction_checks(raw, review, case, sources):
    """Original observed events cannot disappear from normalized evidence."""
    events, votes, retained = raw.get("retractions"), review.get("retractions", []), case.get("retractions")
    if (not isinstance(events, list) or not all(isinstance(e, dict) for e in events)
            or not isinstance(votes, list) or not isinstance(retained, list)
            or len(events) != len(retained)):
        return ["visual_original_retractions_mismatch"]
    by_index = {}
    for row in votes:
        if not isinstance(row, dict):
            return ["invalid_visual_original_retraction_vote"]
        index = row.get("eventIndex", row.get("index"))
        if index is None:
            matches = [i for i, e in enumerate(events) if e.get("sourceId") == row.get("sourceId")]
            index = matches[0] if len(matches) == 1 else None
        if (type(index) is not int or not 0 <= index < len(events) or index in by_index
                or not isinstance(events[index].get("sourceId"), str)
                or events[index]["sourceId"] not in sources
                or "sourceId" in row and row["sourceId"] != events[index].get("sourceId")
                or any(type(row.get(k)) is not bool for k in ("confirmed", "sourceUnchanged"))
                or "reviewed" in row and type(row["reviewed"]) is not bool):
            return ["invalid_visual_original_retraction_vote"]
        by_index[index] = row
    for index, (event, item) in enumerate(zip(events, retained)):
        vote = by_index.get(index, {})
        expected = {**event, "nodeId": event.get("sourceId"), "eventIndex": index,
                    "reviewed": bool(vote) and vote.get("reviewed", True),
                    "confirmed": vote.get("confirmed"), "sourceUnchanged": vote.get("sourceUnchanged")}
        if not _same(expected, item):
            return ["visual_original_retractions_mismatch"]
    return []


def _domain(value):
    if not isinstance(value, str):
        return None
    value = value.lower()
    # The fixed manifest is the authority for registrable domains, not a guessed
    # last-two-label rule (which is wrong for e.g. co.uk and com.tw).
    if (value != value.strip() or len(value) > 253 or "." not in value
            or not all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", p)
                       for p in value.split("."))):
        return None
    return value


def url_belongs(url, domain):
    """Exact domain or dot-delimited subdomain; never substring matching."""
    try:
        parsed = urlsplit(url)
        parsed.port  # Reject malformed ports as well as malformed hosts.
        host = (parsed.hostname or "").lower().rstrip(".")
        return (parsed.scheme in ("http", "https") and parsed.username is None
                and parsed.password is None and bool(domain)
                and (host == domain or host.endswith("." + domain)))
    except (TypeError, ValueError, AttributeError):
        return False


def validate_sites(manifest):
    """Validate fixed identities; return entries and corpus coverage requirements."""
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1:
        raise ValueError("manifest requires schemaVersion=1")
    sites = manifest.get("sites")
    if not isinstance(sites, list) or not sites:
        raise ValueError("manifest.sites must be a nonempty array")
    ids, domains = set(), set()
    for site in sites:
        if not isinstance(site, dict):
            raise ValueError("site must be an object")
        domain = _domain(site.get("registeredDomain"))
        if not isinstance(site.get("id"), str) or not site["id"] or site["id"] in ids:
            raise ValueError("site id missing or duplicated")
        if not domain or domain in domains:
            raise ValueError("registeredDomain missing, invalid or duplicated")
        if any(domain.endswith("." + d) or d.endswith("." + domain) for d in domains):
            raise ValueError("nested registeredDomain declarations would count subdomains twice")
        if (site.get("registeredDomain") != domain
                or not url_belongs(site.get("url"), domain)):
            raise ValueError("manifest URL/domain mismatch")
        if (type(site.get("expectedPageTranslation")) is not bool
                or site.get("split") not in SPLITS
                or not isinstance(site.get("category"), str) or not site["category"]
                or not isinstance(site.get("language"), str) or not site["language"]):
            raise ValueError("invalid site labels/split/category/language")
        ids.add(site["id"])
        domains.add(domain)
    negatives = sum(s["expectedPageTranslation"] is False for s in sites)
    holdout = sum(s["split"] == "holdout" for s in sites)
    return sites, {"independentDomains": len(domains), "negativePages": negatives,
                   "holdoutDomains": holdout,
                   "requirementsMet": len(domains) >= 120 and negatives >= 35
                   and holdout >= 30}


def _screenshot_ok(item, evidence_root):
    if (not isinstance(item, dict) or item.get("reviewed") is not True
            or not isinstance(item.get("path"), str) or not item["path"]):
        return False
    try:
        path = (evidence_root / item["path"]).resolve()
        path.relative_to(evidence_root)
        if not path.is_file():
            return False
        with path.open("rb") as stream:
            header = stream.read(12)
        # Exclude empty placeholders / JSON renamed to an image. Visual truth
        # still requires the independent human reviewer, not this magic check.
        return (header.startswith(b"\x89PNG\r\n\x1a\n")
                or header.startswith(b"\xff\xd8\xff")
                or (header.startswith(b"RIFF") and header[8:12] == b"WEBP"))
    except (OSError, ValueError, RuntimeError):
        return False


def _count(value):
    return type(value) is int and value >= 0


def _coverage_metadata(coverage_scope):
    if not isinstance(coverage_scope, str) or coverage_scope not in COVERAGE_SCOPES:
        raise ValueError("coverage_scope must be strict or automatic-page")
    return {"schemaVersion": 1, "mode": coverage_scope}


def _case_scope_matches(case, coverage_scope):
    if "coverageScope" not in case:
        return coverage_scope == "strict"
    metadata = case["coverageScope"]
    return (isinstance(metadata, dict) and type(metadata.get("schemaVersion")) is int
            and metadata["schemaVersion"] == 1 and metadata.get("mode") == coverage_scope)


def _metric(numerator, denominator):
    return numerator / denominator if denominator else None


def _at_least(numerator, denominator, threshold):
    # Use exact rational comparisons at the 95% and 97% boundaries.
    return denominator > 0 and Fraction(numerator, denominator) >= Fraction(threshold)


def _empty(require_visual_review=False):
    stats = {"plannedDomains": 0, "submittedCases": 0, "evidenceDomains": 0,
            "completedDomains": 0, "completedPositiveDomains": 0,
            "completedNegativeDomains": 0, "TP": 0, "TN": 0, "FP": 0, "FN": 0,
            "unknownNodes": 0, "unknownPredictions": 0, "unknownPages": 0, "pageCorrect": 0,
            "pageTotal": 0, "annotatedPositiveNodes": 0,
            "translatedPositiveNodes": 0, "unTranslatedPositiveNodes": 0,
            "policySkippedPositiveNodes": 0, "confirmedOmissions": 0,
            "confirmedRetractions": 0, "unnecessaryRequests": 0}
    if require_visual_review:
        stats.update(dict.fromkeys(VISUAL_COUNTS, 0))
    return stats


def _finish(stats):
    total = sum(stats[k] for k in ("TP", "TN", "FP", "FN"))
    stats.update(nodeTotal=total,
                 accuracy=_metric(stats["TP"] + stats["TN"], total),
                 precision=_metric(stats["TP"], stats["TP"] + stats["FP"]),
                 recall=_metric(stats["TP"], stats["TP"] + stats["FN"]),
                 pageAccuracy=_metric(stats["pageCorrect"], stats["pageTotal"]),
                 translationCoverage=_metric(stats["translatedPositiveNodes"],
                                             stats["annotatedPositiveNodes"]),
                 completionRate=_metric(stats["completedDomains"], stats["plannedDomains"]))
    return stats


def _quality_gates(stats):
    gates = {
        "nodeAccuracy": _at_least(stats["TP"] + stats["TN"], stats["nodeTotal"], "0.95"),
        "nodePrecision": _at_least(stats["TP"], stats["TP"] + stats["FP"], "0.95"),
        "pageAccuracy": _at_least(stats["pageCorrect"], stats["pageTotal"], "0.97"),
        "noConfirmedOmissions": stats["confirmedOmissions"] == 0,
        "noConfirmedRetractions": stats["confirmedRetractions"] == 0,
        "noUnnecessaryRequests": stats["unnecessaryRequests"] == 0,
        "noUnknownLabels": stats["unknownNodes"] == stats["unknownPages"] == 0,
        "noUnknownPredictions": stats["unknownPredictions"] == 0,
        "bothPageClassesCompleted": stats["completedPositiveDomains"] > 0
        and stats["completedNegativeDomains"] > 0,
    }
    if "unknownVisualNodes" in stats:
        gates.update(noUnknownVisualNodes=stats["unknownVisualNodes"] == 0,
                     noIncorrectVisualTranslations=stats["incorrectVisualTranslations"] == 0)
    return gates


def _score_case(case, site, evidence_root, *, coverage_scope="strict", require_visual_review=False):
    """Admissible evidence may expose errors even when a run is incomplete."""
    _coverage_metadata(coverage_scope)
    _visual_option(require_visual_review)
    stats = _empty(require_visual_review)
    reasons = []
    if not _case_scope_matches(case, coverage_scope):
        reasons.append("coverage_scope_metadata_mismatch")
    required = ("id", "registeredDomain", "url", "category", "language",
                "expectedPageTranslation", "split")
    if any(case.get(k) != site[k] or type(case.get(k)) is not type(site[k]) for k in required):
        reasons.append("manifest_mismatch")
    if not url_belongs(case.get("finalUrl"), site["registeredDomain"]):
        reasons.append("final_url_outside_registered_domain")
    if case.get("visitSuccess") is not True:
        reasons.append("visit_not_successful")
    if case.get("synthetic") is True or case.get("mock") is True:
        reasons.append("synthetic_or_mock_evidence")
    viewport = case.get("viewport", {})
    if (not isinstance(case.get("title"), str) or not case["title"]
            or not isinstance(viewport, dict)
            or any(type(viewport.get(k)) is not int or viewport[k] <= 0 for k in ("width", "height"))
            or not isinstance(case.get("sourceHead"), str)
            or not re.fullmatch(r"[0-9a-f]{40}", case["sourceHead"])):
        reasons.append("missing_capture_metadata")
    try:
        captured = datetime.fromisoformat(case.get("capturedAt", "").replace("Z", "+00:00"))
        if captured.tzinfo is None:
            raise ValueError("timezone required")
    except (ValueError, TypeError, AttributeError):
        reasons.append("invalid_capture_time")
    screenshots = case.get("screenshots", {})
    if not isinstance(screenshots, dict) or not all(
            _screenshot_ok(screenshots.get(k), evidence_root) for k in ("before", "after", "stable")):
        reasons.append("missing_or_unreviewed_screenshots")
    checks = screenshots.get("checks", {}) if isinstance(screenshots, dict) else {}
    if not isinstance(checks, dict) or not all(checks.get(k) is True for k in (
            "firstViewport", "mainContent", "afterScroll", "layout", "dynamicReturn")):
        reasons.append("missing_screenshot_checks")

    # Independent labels are never inferred from the manifest or predictions.
    page_label = case.get("pageAnnotation", {})
    page_known = (isinstance(page_label, dict) and page_label.get("reviewed") is True
                  and page_label.get("independent") is True
                  and type(page_label.get("needTranslation")) is bool)
    if page_known and page_label["needTranslation"] != site["expectedPageTranslation"]:
        reasons.append("initial_page_label_disagrees_with_review")
    events = case.get("hostEvents", [])
    event_shape_ok = isinstance(events, list) and all(isinstance(e, dict) for e in events)
    if not event_shape_ok:
        events = []
        reasons.append("invalid_host_events")
    if any(e.get("caseId") != site["id"] or not isinstance(e.get("id"), str)
           or not e["id"] for e in events):
        reasons.append("host_event_case_identity_mismatch")
    if coverage_scope == "automatic-page" and any(
            e.get("type") not in ("capabilities", "terminal", "assessment") for e in events):
        reasons.append("invalid_host_event_type")
    capabilities = [e for e in events if e.get("type") == "capabilities"]
    terminals = [e for e in events if e.get("type") == "terminal"]
    assessments = [e for e in events if e.get("type") == "assessment"]
    assessment_requests_ok = False
    if coverage_scope == "automatic-page":
        if any(not isinstance(e.get("id"), str) or not e["id"].strip()
               or not isinstance(e.get("caseId"), str) or not e["caseId"].strip()
               or e["caseId"] != case.get("id") for e in assessments):
            reasons.append("host_event_case_identity_mismatch")
        # An assessment is original evidence with its own identity. Repeated
        # copies or ids borrowed from capabilities/terminals cannot prove a
        # policy skip, even when successful native work accompanies them.
        assessment_ids = Counter(e["id"] for e in assessments
                                 if isinstance(e.get("id"), str))
        if any(count > 1 for count in assessment_ids.values()):
            reasons.append("duplicate_assessment_event")
        other_event_ids = {e["id"] for e in capabilities + terminals
                           if isinstance(e.get("id"), str)}
        if assessment_ids.keys() & other_event_ids:
            reasons.append("host_event_identity_collision")
        if any(type(e.get("schemaVersion")) is not int or e["schemaVersion"] != 1
               or e.get("live") is not True or e.get("needsTranslation") is not False
               for e in assessments):
            reasons.append("invalid_host_assessment")
        # Legacy normalized terminal fixtures may omit these raw fields. An
        # assessment alternative must carry both, and any supplied identities
        # must agree with the actual host terminals (including mixed requests).
        if assessments or "requestIds" in case or "terminals" in case:
            requests = case.get("requestIds")
            request_ids_ok = (isinstance(requests, list)
                              and all(isinstance(r, str) and r for r in requests)
                              and len(set(requests)) == len(requests))
            if not request_ids_ok:
                reasons.append("invalid_request_identities")
            raw_terminals = case.get("terminals")
            terminal_list_ok = (isinstance(raw_terminals, list)
                                and all(isinstance(e, dict) for e in raw_terminals)
                                and all(e.get("type") == "terminal"
                                        and type(e.get("success")) is bool
                                        and type(e.get("failedCount")) is int
                                        and _count(e.get("cacheHits"))
                                        and _count(e.get("requestCount"))
                                        for e in raw_terminals)
                                and raw_terminals == terminals)
            if not terminal_list_ok:
                reasons.append("terminal_host_events_mismatch")
            terminal_ids = [e.get("id") for e in terminals]
            terminal_ids_ok = all(isinstance(i, str) and i for i in terminal_ids)
            if (request_ids_ok and terminal_ids_ok
                    and (len(requests) != len(terminal_ids)
                         or set(requests) != set(terminal_ids))):
                reasons.append("request_terminal_identity_mismatch")
            assessment_requests_ok = (request_ids_ok and terminal_list_ok
                                      and not requests and not terminals)
    if len({e.get("id") for e in terminals if isinstance(e.get("id"), str)}) != len(terminals):
        reasons.append("duplicate_terminal_event")
    if any(e.get("live") is not True for e in capabilities) or case.get("liveTranslation") is not True and site["expectedPageTranslation"]:
        reasons.append("non_live_model")
    request_count = case.get("requestCount")
    if not _count(request_count):
        reasons.append("invalid_request_count")
    if any(not _count(e.get("requestCount")) for e in terminals):
        reasons.append("invalid_terminal_request_count")
    elif terminals and sum(e["requestCount"] for e in terminals) != request_count:
        reasons.append("request_count_disagrees_with_terminals")
    terminal_ok = bool(terminals) and all(
        e.get("success") is True and e.get("failedCount") == 0
        and type(e.get("failedCount")) is int and _count(e.get("cacheHits"))
        for e in terminals)
    automatic_negative = coverage_scope == "automatic-page" and not site["expectedPageTranslation"]
    if (site["expectedPageTranslation"] or automatic_negative) and not capabilities:
        reasons.append("missing_live_capabilities")
    if site["expectedPageTranslation"] and (
            not terminal_ok or not _count(request_count) or request_count == 0):
        reasons.append("missing_live_request_or_successful_terminal")
    # An actual live negative assess event has no translation terminal. It is
    # proof only with explicit empty request/terminal lists and reviewed page
    # agreement; it never substitutes for a terminal for issued work.
    assessment_zero_call = (automatic_negative and bool(assessments)
                            and assessment_requests_ok and _count(request_count)
                            and request_count == 0 and case.get("pageDecision") is False
                            and page_known and page_label["needTranslation"] is False)
    zero_call_proof = terminal_ok or assessment_zero_call
    if automatic_negative and not zero_call_proof:
        reasons.append("missing_or_invalid_zero_call_terminal")
    # Mock evidence and incomplete screenshots cannot contribute correct results.
    # A live run with an unsuccessful terminal remains auditable for omissions
    # and retractions, but cannot be completed.
    fatal = set(reasons) - {"missing_live_request_or_successful_terminal",
                           "missing_or_invalid_zero_call_terminal"}
    if automatic_negative:
        # Missing capabilities block completion and policy credit, but the
        # independent labels still expose genuine omissions. Explicit non-live
        # declarations and malformed event identities/counts stay fatal.
        fatal.discard("missing_live_capabilities")
    if fatal:
        return stats, False, reasons

    nodes = case.get("sourceNodes")
    annotations = case.get("annotations")
    predictions = case.get("predictions")
    if (not isinstance(nodes, list) or not nodes or not isinstance(annotations, list)
            or not isinstance(predictions, list)):
        return stats, False, ["missing_node_evidence"]
    node_ids = []
    for node in nodes:
        if (not isinstance(node, dict) or not isinstance(node.get("id"), str) or not node["id"]
                or not isinstance(node.get("text"), str) or not node["text"].strip()
                or not isinstance(node.get("tag"), str) or not node["tag"]
                or not isinstance(node.get("role"), str)
                or not isinstance(node.get("position"), dict)
                or any(type(node["position"].get(k)) not in (int, float)
                       or not -1e7 < node["position"][k] < 1e7 for k in ("x", "y", "width", "height"))
                or node["position"]["width"] <= 0 or node["position"]["height"] <= 0):
            return stats, False, ["invalid_source_node"]
        node_ids.append(node["id"])
    if len(set(node_ids)) != len(node_ids):
        return stats, False, ["duplicate_source_node_id"]
    maps = []
    for items in (annotations, predictions):
        result = {}
        for item in items:
            if (not isinstance(item, dict) or not isinstance(item.get("nodeId"), str)
                    or item.get("nodeId") not in node_ids
                    or item["nodeId"] in result):
                return stats, False, ["duplicate_or_unmatched_node_evidence"]
            result[item["nodeId"]] = item
        maps.append(result)
    labels, predicted = maps
    visual, native = {}, set()
    if require_visual_review:
        visual, native, visual_reasons = _visual_evidence(case, nodes, labels, evidence_root)
        reasons.extend(visual_reasons)
    stats["evidenceDomains"] = 1
    if not page_known:
        stats["unknownPages"] = 1
        reasons.append("unknown_page_label")
    if page_known:
        stats["pageTotal"] = 1
        stats["pageCorrect"] = int(type(case.get("pageDecision")) is bool
                                   and case["pageDecision"] == page_label["needTranslation"])
    if type(case.get("pageDecision")) is not bool:
        reasons.append("invalid_page_decision")
    if not site["expectedPageTranslation"]:
        stats["unnecessaryRequests"] = request_count
        if request_count:
            reasons.append("unnecessary_translation_requests")
    policy_skip = (coverage_scope == "automatic-page" and not reasons
                   and site["expectedPageTranslation"] is False
                   and page_known and page_label["needTranslation"] is False
                   and case.get("pageDecision") is False and request_count == 0
                   and capabilities and all(e.get("live") is True for e in capabilities)
                   and zero_call_proof and case.get("completed") is True
                   and isinstance(case.get("errors"), list) and not case["errors"]
                   and case.get("blockedReason") in (None, ""))
    if require_visual_review:
        policy_skip = policy_skip and all(
            visual.get(identity) == "unchanged"
            and labels.get(identity, {}).get("reviewed") is True
            and labels.get(identity, {}).get("independent") is True
            and type(labels.get(identity, {}).get("needTranslation")) is bool
            for identity in node_ids)
    for node_id in node_ids:
        label = labels.get(node_id, {})
        prediction = predicted.get(node_id, {})
        known = (label.get("reviewed") is True and label.get("independent") is True
                 and type(label.get("needTranslation")) is bool)
        prediction_ok = all(type(prediction.get(k)) is bool for k in ("eligible", "completed", "failed"))
        if not prediction_ok:
            reasons.append("missing_or_invalid_prediction:" + node_id)
        elif prediction["failed"]:
            reasons.append("node_failed:" + node_id)
        eligible = prediction.get("eligible") is True
        translated = (prediction_ok and eligible and prediction["completed"]
                      and not prediction["failed"] and request_count > 0
                      and case.get("liveTranslation") is True and terminal_ok)
        contradictory = prediction_ok and ((prediction["completed"] and (not eligible or prediction["failed"]))
                                            or (prediction["completed"] and not translated))
        if contradictory:
            reasons.append("contradictory_node_completion:" + node_id)
        if not prediction_ok or contradictory:
            stats["unknownPredictions"] += 1
        outcome = visual.get(node_id)
        visual_valid = True
        if require_visual_review:
            visual_valid = outcome is not None and known
            # A false source selected on a translating page still represents
            # admitted native work. Its visual vote cannot hide an unfinished
            # original piece, even though it earns no positive coverage credit.
            if eligible and case.get("pageDecision") is True and node_id not in native:
                reasons.append("visual_eligible_source_without_native_completion:" + node_id)
            if outcome in (None, "unknown", "not_visible") or not known:
                stats["unknownVisualNodes"] += 1
                reasons.append("unresolved_visual_node:" + node_id)
                visual_valid = False
            elif ((outcome in ("correct_translation", "partial_translation", "incorrect_translation")
                   and label["needTranslation"] is False)
                  or outcome == "unnecessary_translation" and label["needTranslation"] is True):
                stats["unknownVisualNodes"] += 1
                reasons.append("contradictory_visual_label_outcome:" + node_id)
                visual_valid = False
            if outcome in ("partial_translation", "incorrect_translation"):
                stats["incorrectVisualTranslations"] += 1
                reasons.append("incorrect_visual_translation:" + node_id)
            if outcome == "unnecessary_translation":
                stats["unnecessaryVisualTranslations"] += 1
                # False positives remain measured by the original 95% accuracy
                # and precision gates; this observed output adds no zero gate.
            # Visible votes cannot establish machine work. Keep the original
            # translated flag above for recognition contradiction checks.
            if outcome == "correct_translation" and (not translated or node_id not in native):
                if visual_valid:
                    stats["unknownVisualNodes"] += 1
                reasons.append("visual_translation_without_native_completion:" + node_id)
                visual_valid = False
        if not known:
            stats["unknownNodes"] += 1
            reasons.append("unknown_node_label:" + node_id)
            continue
        need = label["needTranslation"]
        if prediction_ok and not contradictory:
            stats[("TP" if eligible else "FN") if need else ("FP" if eligible else "TN")] += 1
        elif need:
            stats["FN"] += 1  # Missing prediction is a confirmed omission, never a TP.
        if require_visual_review:
            translated = (translated and visual_valid and outcome == "correct_translation"
                          and node_id in native)
        if need:
            stats["annotatedPositiveNodes"] += 1
            stats["translatedPositiveNodes"] += int(translated)
            if not translated:
                stats["unTranslatedPositiveNodes"] += 1
                if (policy_skip and prediction_ok and not prediction["failed"] and not contradictory
                        and (not require_visual_review or visual_valid and outcome == "unchanged")):
                    stats["policySkippedPositiveNodes"] += 1
                else:
                    stats["confirmedOmissions"] += 1
                    reasons.append("confirmed_omission:" + node_id)
    if site["expectedPageTranslation"] and not stats["annotatedPositiveNodes"]:
        reasons.append("positive_page_without_positive_node_labels")
    retractions = case.get("retractions")
    if not isinstance(retractions, list):
        reasons.append("missing_retraction_observation")
    else:
        for event in retractions:
            if (not isinstance(event, dict) or event.get("reviewed") is not True
                    or type(event.get("confirmed")) is not bool
                    or type(event.get("sourceUnchanged")) is not bool
                    or event.get("nodeId") not in node_ids):
                reasons.append("unreviewed_retraction")
            elif event["confirmed"] and event["sourceUnchanged"]:
                stats["confirmedRetractions"] += 1
                reasons.append("confirmed_retraction:" + event["nodeId"])
    if case.get("completed") is not True:
        reasons.append("run_not_completed")
    if not isinstance(case.get("errors"), list) or case["errors"] or case.get("blockedReason") not in (None, ""):
        reasons.append("run_errors_or_blocked")
    completed = not reasons
    if completed:
        stats["completedDomains"] = 1
        stats["completedPositiveDomains" if site["expectedPageTranslation"] else "completedNegativeDomains"] = 1
    return stats, completed, list(dict.fromkeys(reasons))


def evaluate(manifest, cases, evidence_root, *, coverage_scope="strict", require_visual_review=False):
    """Return an auditable report; no files are changed and no network is used."""
    coverage_metadata = _coverage_metadata(coverage_scope)
    _visual_option(require_visual_review)
    sites, corpus = validate_sites(manifest)
    if not isinstance(cases, list) or not all(isinstance(c, dict) for c in cases):
        raise ValueError("cases must be an array of objects")
    if any(not isinstance(c.get("id"), str) or not c["id"] for c in cases):
        raise ValueError("every case requires a nonempty string id")
    evidence_root = Path(evidence_root).resolve()
    by_id = {s["id"]: s for s in sites}
    domain_counts = Counter(by_id[c["id"]]["registeredDomain"] for c in cases if c.get("id") in by_id)
    groups = {name: _empty(require_visual_review) for name in ("overall",) + SPLITS}
    records = []
    for site in sites:
        for name in ("overall", site["split"]):
            groups[name]["plannedDomains"] += 1
    for index, case in enumerate(cases):
        site = by_id.get(case.get("id"))
        if site is None:
            records.append({"index": index, "id": case.get("id"), "completed": False,
                            "counts": {k: 0 for k in ("annotatedPositiveNodes", "translatedPositiveNodes",
                                       "unTranslatedPositiveNodes", "policySkippedPositiveNodes")},
                            "reasons": ["case_not_in_fixed_manifest"]})
            continue
        for name in ("overall", site["split"]):
            groups[name]["submittedCases"] += 1
        if domain_counts[site["registeredDomain"]] > 1:
            stats, completed, reasons = _empty(require_visual_review), False, ["duplicate_registered_domain_attempts"]
        else:
            stats, completed, reasons = _score_case(case, site, evidence_root,
                                                     coverage_scope=coverage_scope,
                                                     require_visual_review=require_visual_review)
        for name in ("overall", site["split"]):
            for key, value in stats.items():
                groups[name][key] += value
        records.append({"index": index, "id": site["id"],
                        "registeredDomain": site["registeredDomain"], "split": site["split"],
                        "evidenceAccepted": bool(stats["evidenceDomains"]),
                        "completed": completed, "reasons": reasons,
                        "counts": {k: stats[k] for k in ("TP", "TN", "FP", "FN", "unknownNodes",
                                  "unknownPredictions", "annotatedPositiveNodes", "translatedPositiveNodes",
                                  "unTranslatedPositiveNodes", "policySkippedPositiveNodes",
                                  "confirmedOmissions", "confirmedRetractions", "unnecessaryRequests")}})
        if require_visual_review:
            records[-1]["counts"].update({k: stats[k] for k in VISUAL_COUNTS})
    submitted = {c.get("id") for c in cases if c.get("id") in by_id}
    missing = [s["id"] for s in sites if s["id"] not in submitted]
    for stats in groups.values():
        _finish(stats)
    gates = {"corpusRequirements": corpus["requirementsMet"],
             "independentDomains": groups["overall"]["completedDomains"] >= 100,
             "completedHoldoutDomains": groups["holdout"]["completedDomains"] >= 30,
             "overall": _quality_gates(groups["overall"]),
             "holdout": _quality_gates(groups["holdout"])}
    achieved = (gates["corpusRequirements"] and gates["independentDomains"]
                and gates["completedHoldoutDomains"]
                and all(gates["overall"].values()) and all(gates["holdout"].values()))
    report = {"schemaVersion": 1, "coverageScope": coverage_metadata,
            "achieved": achieved, "thresholds": THRESHOLDS,
            "corpus": corpus, "groups": groups, "gates": gates,
            "missingCases": missing, "cases": records,
            "evidenceTrust": "Local files and independent reviewer/live-host declarations; "
            "offline scoring cannot authenticate browser capture or model provenance."}
    if require_visual_review:
        report["visualCoverage"] = {"schemaVersion": 1, "required": True}
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sites", type=Path, default=Path(__file__).with_name("sites.json"))
    parser.add_argument("--cases", type=Path, required=True, help="JSON array or schemaVersion=1/cases envelope")
    parser.add_argument("--evidence-root", type=Path, required=True, help="Only screenshots inside this directory are read")
    parser.add_argument("--coverage-scope", choices=COVERAGE_SCOPES, default="strict")
    parser.add_argument("--require-visual-review", action="store_true",
                        help="Require independent outcomes bound to original rendered evidence")
    parser.add_argument("--output", type=Path, help="Exclusively create a new report file; use distinct files for each scope")
    args = parser.parse_args(argv)
    try:
        manifest = json.loads(args.sites.read_text(encoding="utf-8-sig"))
        cases = json.loads(args.cases.read_text(encoding="utf-8-sig"))
        if isinstance(cases, dict):
            if cases.get("schemaVersion") != 1:
                raise ValueError("case envelope requires schemaVersion=1")
            if "coverageScope" in cases and not _case_scope_matches(cases, args.coverage_scope):
                raise ValueError("case envelope coverage_scope_metadata_mismatch")
            cases = cases.get("cases")
        report = evaluate(manifest, cases, args.evidence_root, coverage_scope=args.coverage_scope,
                          require_visual_review=args.require_visual_review)
        serialized = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
        if args.output is not None:
            with args.output.open("x", encoding="utf-8", newline="\n") as stream:
                stream.write(serialized + "\n")
    except (OSError, ValueError, TypeError, KeyError) as error:
        failure = {"schemaVersion": 1, "achieved": False,
                          "coverageScope": _coverage_metadata(args.coverage_scope),
                          "error": str(error)}
        if args.require_visual_review:
            failure["visualCoverage"] = {"schemaVersion": 1, "required": True}
        print(json.dumps(failure, ensure_ascii=False))
        return 2
    print(serialized)
    return 0 if report["achieved"] else 1


if __name__ == "__main__":
    sys.exit(main())
