"""Offline, fail-closed scoring of the website QA baseline contract (stdlib only)."""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
from fractions import Fraction
import json
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit


SPLITS = ("development", "holdout")
COVERAGE_SCOPES = ("strict", "automatic-page")
THRESHOLDS = {"independentDomains": 100, "nodeAccuracy": .95,
              "nodePrecision": .95, "pageAccuracy": .97,
              "completedHoldoutDomains": 30}


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


def _empty():
    return {"plannedDomains": 0, "submittedCases": 0, "evidenceDomains": 0,
            "completedDomains": 0, "completedPositiveDomains": 0,
            "completedNegativeDomains": 0, "TP": 0, "TN": 0, "FP": 0, "FN": 0,
            "unknownNodes": 0, "unknownPredictions": 0, "unknownPages": 0, "pageCorrect": 0,
            "pageTotal": 0, "annotatedPositiveNodes": 0,
            "translatedPositiveNodes": 0, "unTranslatedPositiveNodes": 0,
            "policySkippedPositiveNodes": 0, "confirmedOmissions": 0,
            "confirmedRetractions": 0, "unnecessaryRequests": 0}


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
    return {
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


def _score_case(case, site, evidence_root, *, coverage_scope="strict"):
    """Admissible evidence may expose errors even when a run is incomplete."""
    _coverage_metadata(coverage_scope)
    stats = _empty()
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
            e.get("type") not in ("capabilities", "terminal") for e in events):
        reasons.append("invalid_host_event_type")
    capabilities = [e for e in events if e.get("type") == "capabilities"]
    terminals = [e for e in events if e.get("type") == "terminal"]
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
    # Automatic skips require live capabilities plus a successful local/native
    # zero-call terminal. Neither declaration alone establishes the exemption.
    if automatic_negative and not terminal_ok:
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
                   and terminal_ok and case.get("completed") is True
                   and isinstance(case.get("errors"), list) and not case["errors"]
                   and case.get("blockedReason") in (None, ""))
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
        if not known:
            stats["unknownNodes"] += 1
            reasons.append("unknown_node_label:" + node_id)
            continue
        need = label["needTranslation"]
        if prediction_ok and not contradictory:
            stats[("TP" if eligible else "FN") if need else ("FP" if eligible else "TN")] += 1
        elif need:
            stats["FN"] += 1  # Missing prediction is a confirmed omission, never a TP.
        if need:
            stats["annotatedPositiveNodes"] += 1
            stats["translatedPositiveNodes"] += int(translated)
            if not translated:
                stats["unTranslatedPositiveNodes"] += 1
                if policy_skip and prediction_ok and not prediction["failed"] and not contradictory:
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


def evaluate(manifest, cases, evidence_root, *, coverage_scope="strict"):
    """Return an auditable report; no files are changed and no network is used."""
    coverage_metadata = _coverage_metadata(coverage_scope)
    sites, corpus = validate_sites(manifest)
    if not isinstance(cases, list) or not all(isinstance(c, dict) for c in cases):
        raise ValueError("cases must be an array of objects")
    if any(not isinstance(c.get("id"), str) or not c["id"] for c in cases):
        raise ValueError("every case requires a nonempty string id")
    evidence_root = Path(evidence_root).resolve()
    by_id = {s["id"]: s for s in sites}
    domain_counts = Counter(by_id[c["id"]]["registeredDomain"] for c in cases if c.get("id") in by_id)
    groups = {name: _empty() for name in ("overall",) + SPLITS}
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
            stats, completed, reasons = _empty(), False, ["duplicate_registered_domain_attempts"]
        else:
            stats, completed, reasons = _score_case(case, site, evidence_root,
                                                     coverage_scope=coverage_scope)
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
    return {"schemaVersion": 1, "coverageScope": coverage_metadata,
            "achieved": achieved, "thresholds": THRESHOLDS,
            "corpus": corpus, "groups": groups, "gates": gates,
            "missingCases": missing, "cases": records,
            "evidenceTrust": "Local files and independent reviewer/live-host declarations; "
            "offline scoring cannot authenticate browser capture or model provenance."}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sites", type=Path, default=Path(__file__).with_name("sites.json"))
    parser.add_argument("--cases", type=Path, required=True, help="JSON array or schemaVersion=1/cases envelope")
    parser.add_argument("--evidence-root", type=Path, required=True, help="Only screenshots inside this directory are read")
    parser.add_argument("--coverage-scope", choices=COVERAGE_SCOPES, default="strict")
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
        report = evaluate(manifest, cases, args.evidence_root, coverage_scope=args.coverage_scope)
        serialized = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
        if args.output is not None:
            with args.output.open("x", encoding="utf-8", newline="\n") as stream:
                stream.write(serialized + "\n")
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(json.dumps({"schemaVersion": 1, "achieved": False,
                          "coverageScope": _coverage_metadata(args.coverage_scope),
                          "error": str(error)}, ensure_ascii=False))
        return 2
    print(serialized)
    return 0 if report["achieved"] else 1


if __name__ == "__main__":
    sys.exit(main())
