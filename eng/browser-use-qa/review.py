"""Apply an independent evidence review to a new file; never overwrite capture history."""
import argparse
import hashlib
import json
from pathlib import Path
import re

from capture import CaptureError, atomic_json, completion_gate, encode, now


def apply_review(case_path, review_path, output_path):
    case_path, review_path, output_path = map(Path, (case_path, review_path, output_path))
    if output_path.exists() or output_path.resolve() in (case_path.resolve(), review_path.resolve()):
        raise CaptureError("review_output_must_be_new_file")
    raw = case_path.read_bytes()
    case = json.loads(raw)
    review = json.loads(review_path.read_text(encoding="utf-8"))
    if review.get("caseSha256") != hashlib.sha256(raw).hexdigest():
        raise CaptureError("review_case_hash_mismatch")
    phases = {shot["phase"] for shot in case["screenshots"]}
    votes = review.get("screenshots")
    if not isinstance(votes, dict) or set(votes) != phases or any(type(value) is not bool for value in votes.values()):
        raise CaptureError("review_requires_all_screenshot_votes")
    for shot in case["screenshots"]:
        if not re.fullmatch(r"[A-Za-z0-9_-]+\.png", shot["path"]):
            raise CaptureError("invalid_screenshot_path")
        image = case_path.parent / shot["path"]
        if not image.is_file() or hashlib.sha256(image.read_bytes()).hexdigest() != shot.get("sha256"):
            raise CaptureError("review_screenshot_hash_mismatch")
    sources = {row["id"] for row in case["sourceNodes"]}
    annotations, seen = [], set()
    for row in review.get("annotations", []):
        source_id, label = row.get("sourceId"), row.get("needTranslation")
        if source_id not in sources or source_id in seen or not (type(label) is bool or label == "unknown"):
            raise CaptureError("invalid_independent_annotation")
        seen.add(source_id); annotations.append({"sourceId": source_id, "needTranslation": label})
    annotations.extend({"sourceId": source_id, "needTranslation": "unknown"} for source_id in sorted(sources - seen))
    case["annotations"] = annotations
    case["screenshotReview"] = votes
    case["evidenceReady"], case["completed"], reasons = completion_gate(case, case_path.parent)
    # Optional disclosure screenshots are also required to pass review.
    if not all(votes.values()):
        case["completed"] = False
        reasons.append("screenshot_review_rejected")
    case["blockedReason"] = ";".join(reasons)
    case["review"] = {"at": now(), "caseSha256": hashlib.sha256(raw).hexdigest(),
                      "reviewSha256": hashlib.sha256(review_path.read_bytes()).hexdigest()}
    atomic_json(output_path, case)
    return case


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", type=Path, required=True)
    parser.add_argument("--review", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    repo = Path(__file__).resolve().parents[2]
    if any(path.resolve() == repo or repo in path.resolve().parents for path in (args.case, args.review, args.output)):
        parser.error("evidence and review paths must be outside the repository")
    try:
        result = apply_review(args.case, args.review, args.output)
        print(encode({"completed": result["completed"], "blockedReason": result["blockedReason"]}))
        return 0 if result["completed"] else 1
    except (CaptureError, OSError, ValueError, KeyError, TypeError) as error:
        print(encode({"completed": False, "error": str(error)}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
