"""Build offline independent review materials: --case CASE --output-dir NEW_DIR.

Outputs index.html, sources.json, sources.tsv, review-template.json, and byte
identical screenshots. No prediction, existing annotation, or capture completion
flag is used to label the review. Raw case and image files are never rewritten.
All output files use exclusive creation; a partial output is retained on failure.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import html
import json
import math
from pathlib import Path
import re


class ReviewIndexError(ValueError):
    """Invalid independent evidence or an unsafe output destination."""


SOURCE_FIELDS = ("id", "sourceText", "tag", "role", "rects", "locator",
                 "firstSeen", "phase", "ancestorsVisible", "rendered", "inViewport")
METADATA_FIELDS = ("id", "registeredDomain", "url", "finalUrl", "title", "category",
                   "language", "split", "viewport", "capturedAt", "sourceHead", "sourceHashes",
                   "manifestHash")
RECT_FIELDS = {"x", "y", "width", "height", "documentX", "documentY"}
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def encode(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2)


def _rows(case):
    rows = case.get("sourceNodes")
    if not isinstance(rows, list):
        raise ReviewIndexError("sourceNodes_must_be_array")
    result, seen = [], set()
    for row in rows:
        if not isinstance(row, dict):
            raise ReviewIndexError("invalid_source_row")
        source_id = row.get("id")
        if not isinstance(source_id, str) or not source_id or source_id in seen:
            raise ReviewIndexError("invalid_or_duplicate_source_id")
        seen.add(source_id)
        if any(not isinstance(row.get(key), str) for key in ("sourceText", "tag", "role")):
            raise ReviewIndexError("source_text_tag_role_required")
        rects = row.get("rects")
        if not isinstance(rects, list):
            raise ReviewIndexError("source_rects_required")
        for rect in rects:
            if (not isinstance(rect, dict) or not {"x", "y", "width", "height"} <= rect.keys()
                    or not rect.keys() <= RECT_FIELDS
                    or any(type(value) not in (int, float) or not math.isfinite(value)
                           for value in rect.values())):
                raise ReviewIndexError("invalid_source_geometry")
        # displayedText and all production-derived fields deliberately excluded.
        result.append({key: row[key] for key in SOURCE_FIELDS if key in row})
    return result


def _screenshots(case, evidence_root):
    shots = case.get("screenshots")
    if not isinstance(shots, list):
        raise ReviewIndexError("screenshots_must_be_array")
    result = []
    for shot in shots:
        if not isinstance(shot, dict) or not isinstance(shot.get("phase"), str) or not shot["phase"]:
            raise ReviewIndexError("invalid_screenshot_phase")
        name = shot.get("path")
        if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_-]+\.png", name):
            raise ReviewIndexError("invalid_screenshot_path")
        path = evidence_root / name
        if path.is_symlink() or not path.resolve().is_relative_to(evidence_root):
            raise ReviewIndexError("screenshot_path_outside_evidence")
        raw = path.read_bytes()
        digest = hashlib.sha256(raw).hexdigest()
        if digest != shot.get("sha256"):
            raise ReviewIndexError("screenshot_hash_mismatch")
        if not raw.startswith(PNG_SIGNATURE):
            raise ReviewIndexError("invalid_screenshot_png")
        result.append(({"phase": shot["phase"], "path": "screenshots/" + name,
                        "originalPath": name, "sha256": digest,
                        **{key: shot[key] for key in ("at", "viewport") if key in shot}}, raw))
    return result


def _template(case, digest, rows, shots):
    events = case.get("retractions")
    if not isinstance(events, list) or any(not isinstance(event, dict) for event in events):
        raise ReviewIndexError("retractions_must_be_event_array")
    # Event index refers to the unchanged raw case. Observed sourceUnchanged is
    # not an independent human vote; neither it nor prior votes seed this form.
    retractions = []
    for index, event in enumerate(events):
        retractions.append({**{key: event[key] for key in ("sourceId", "sourceText", "phase", "at")
                               if key in event}, "eventIndex": index, "reviewed": False,
                            "confirmed": "unknown", "sourceUnchanged": "unknown"})
    return {"caseSha256": digest,
            "pageAnnotation": {"needTranslation": "unknown", "reviewed": False, "independent": False},
            "annotations": [{"sourceId": row["id"], "needTranslation": "unknown",
                             "reviewed": False, "independent": False} for row in rows],
            "screenshots": {shot["phase"]: False for shot in shots},
            "retractions": retractions}


def _html(sources):
    def escape(value):
        return html.escape(value if isinstance(value, str) else encode(value), quote=True)

    parts = ['<!doctype html><html lang="en"><head><meta charset="utf-8">',
             '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; '
             'img-src \'self\'; style-src \'unsafe-inline\'; base-uri \'none\'; form-action \'none\'">',
             '<meta name="referrer" content="no-referrer"><title>Independent source review</title>',
             '<style>body{font-family:system-ui;margin:2rem;line-height:1.5}'
             'pre{white-space:pre-wrap;overflow-wrap:anywhere}table{border-collapse:collapse;width:100%}'
             'td,th{border:1px solid #999;padding:.5rem;text-align:left;vertical-align:top}'
             'img{max-width:100%;height:auto}a{overflow-wrap:anywhere}</style></head><body>',
             '<h1>Independent source review</h1><p>All labels are unknown and all review votes are false. '
             'This index does not certify review or completion. Source rows exclude production predictions '
             'and translated display text. Screenshots are unchanged captured evidence.</p>',
             '<p><a href="sources.json">Source JSON</a> · <a href="sources.tsv">Source TSV</a> · '
             '<a href="review-template.json">Blank review template</a></p>',
             '<h2>Case metadata</h2><pre>' + escape(sources["case"]) + '</pre>',
             '<h2>Provenance</h2><pre>' + escape(sources["provenance"]) + '</pre>',
             '<h2>Original screenshots</h2>']
    for shot in sources["screenshots"]:
        label, link = escape(shot["phase"]), escape(shot["path"])
        parts.append('<figure><figcaption>' + label + '</figcaption><a href="' + link
                     + '"><img src="' + link + '" alt="' + label + '"></a><pre>'
                     + escape(shot) + '</pre></figure>')
    parts.append('<h2>All independent sources (' + str(len(sources["sourceNodes"])) + ')</h2>'
                 '<table><thead><tr>' + ''.join('<th>' + key + '</th>' for key in SOURCE_FIELDS)
                 + '</tr></thead><tbody>')
    for row in sources["sourceNodes"]:
        parts.append('<tr>' + ''.join('<td><pre>' + escape(row.get(key, ""))
                                     + '</pre></td>' for key in SOURCE_FIELDS) + '</tr>')
    parts.append('</tbody></table></body></html>')
    return '\n'.join(parts)


def build_review_index(case_path, output_dir):
    """Validate raw evidence, then exclusively create a source-only review bundle."""
    case_path, output_dir = Path(case_path).resolve(), Path(output_dir)
    if output_dir.exists() or output_dir.is_symlink():
        raise ReviewIndexError("output_directory_must_be_new")
    raw = case_path.read_bytes()
    case = json.loads(raw)
    if not isinstance(case, dict):
        raise ReviewIndexError("case_must_be_object")
    digest = hashlib.sha256(raw).hexdigest()
    rows = _rows(case)
    images = _screenshots(case, case_path.parent)
    shots = [shot for shot, _ in images]
    sources = {"schemaVersion": 1, "case": {key: case[key] for key in METADATA_FIELDS if key in case},
               "provenance": {"casePath": str(case_path), "caseSha256": digest},
               "sourceNodes": rows, "screenshots": shots}
    template = _template(case, digest, rows, shots)
    # Serialize before creating output so malformed inputs leave no directory.
    source_json, review_json, index_html = encode(sources), encode(template), _html(sources)
    output_dir.mkdir(exist_ok=False)
    for name, content in (("sources.json", source_json), ("review-template.json", review_json),
                          ("index.html", index_html)):
        with (output_dir / name).open("x", encoding="utf-8", newline="") as stream:
            stream.write(content + '\n')
    with (output_dir / "sources.tsv").open("x", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        writer.writerow(SOURCE_FIELDS)
        for row in rows:
            writer.writerow([row.get(key, "") if isinstance(row.get(key, ""), str)
                             else encode(row[key]) for key in SOURCE_FIELDS])
    if images:
        (output_dir / "screenshots").mkdir(exist_ok=False)
    copied = set()
    for shot, image in images:
        if shot["path"] in copied:
            continue  # Multiple observations may reference the same original.
        with (output_dir / shot["path"]).open("xb") as stream:
            stream.write(image)
        copied.add(shot["path"])
    return sources


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        sources = build_review_index(args.case, args.output_dir)
    except ReviewIndexError as error:
        print(json.dumps({"error": str(error)}))  # Only fixed validation codes.
        return 2
    except (OSError, ValueError, TypeError, KeyError):
        # Do not echo raw evidence, paths, or exception payloads to stdout.
        print(json.dumps({"error": "review_index_failed"}))
        return 2
    print(json.dumps({"caseSha256": sources["provenance"]["caseSha256"],
                      "sourceCount": len(sources["sourceNodes"]),
                      "screenshotCount": len(sources["screenshots"])}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
