"""Offline review bundle regression fixtures; no browser, host, or model calls."""
import contextlib
import copy
import csv
import hashlib
from html.parser import HTMLParser
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock


HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("qa_review_index", HERE.parent / "review_index.py")
review_index = importlib.util.module_from_spec(spec)
spec.loader.exec_module(review_index)


class Document(HTMLParser):
    def __init__(self, text):
        super().__init__()
        self.tags, self.links, self.text = [], [], []
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        self.tags.append(tag)
        self.links.extend(value for name, value in attrs if name in ("href", "src"))

    def handle_data(self, data):
        self.text.append(data)


class ReviewIndexTests(unittest.TestCase):
    def setUp(self):
        # Keep synthetic evidence and outputs inside this task checkout.
        self.temp = tempfile.TemporaryDirectory(prefix="review-index-", dir=HERE)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.evidence = self.root / "raw"
        self.evidence.mkdir()
        self.case_path = self.evidence / "case.json"
        self.output = self.root / "review"
        self.image = review_index.PNG_SIGNATURE + b"offline synthetic PNG fixture"
        (self.evidence / "before.png").write_bytes(self.image)
        self.case = {
            "id": "case-001", "url": "https://example.org/?x=<script>",
            "finalUrl": "javascript:alert('must never be a link')",
            "title": '<script>alert("title")</script>', "capturedAt": "2026-10-11T00:00:00Z",
            "sourceHead": "a" * 40, "sourceHashes": {"page-adapter.js": "b" * 64},
            "manifestHash": "c" * 64,
            "viewport": {"width": 1280, "height": 720, "scrollY": 100},
            "expectedPageTranslation": True, "pageDecision": True,
            "completed": True, "simulation": True, "liveTranslation": True,
            "sourceNodes": [
                {"id": 'source<1>"', "sourceText": '<script>alert("source")</script>\t&\n中文',
                 "tag": '<img src="https://remote.invalid/">', "role": '" onmouseover="bad',
                 "locator": "body > p", "rects": [
                     {"x": 10, "y": -100, "width": 200, "height": 20, "documentX": 10, "documentY": 0},
                     {"x": 10, "y": -80, "width": 100, "height": 20, "documentX": 10, "documentY": 20}],
                 "rendered": True, "inViewport": False, "firstSeen": "first", "phase": "before",
                 "displayedText": "PREDICTION_SECRET_TRANSLATION", "eligible": True},
                {"id": "unselected", "sourceText": "Unselected text " + "long " * 1000,
                 "tag": "span", "role": "", "rects": [], "rendered": False},
            ],
            "predictions": [{"sourceId": 'source<1>"', "eligible": True,
                             "text": "PREDICTION_SECRET_TRANSLATION", "completed": True}],
            "productionEligibility": [{"sourceId": "unselected", "eligible": False}],
            "annotations": [{"sourceId": "unselected", "needTranslation": True, "reviewed": True}],
            "pageAnnotation": {"needTranslation": True, "reviewed": True},
            "screenshotReview": {"before": True},
            "screenshots": [{"phase": '"><script>alert("phase")</script>', "path": "before.png",
                             "sha256": hashlib.sha256(self.image).hexdigest(), "at": "shot-time"}],
            "retractions": [{"type": "retraction", "sourceId": 'source<1>"', "sourceText": "Original",
                             "sourceUnchanged": True, "reviewed": True, "confirmed": True,
                             "previousText": "PREDICTION_SECRET_TRANSLATION", "phase": "stable", "at": "event-time"}],
        }
        self.save()

    def save(self):
        # Deliberately noncanonical raw encoding to verify hashing original bytes.
        self.case_path.write_bytes(json.dumps(self.case, ensure_ascii=False, indent=3).encode("utf-8") + b"\n")

    def bundle(self):
        review_index.build_review_index(self.case_path, self.output)
        source = json.loads((self.output / "sources.json").read_text(encoding="utf-8"))
        template = json.loads((self.output / "review-template.json").read_text(encoding="utf-8"))
        return source, template

    def test_all_sources_full_geometry_tsv_and_raw_hash_provenance(self):
        original = {path.name: path.read_bytes() for path in self.evidence.iterdir()}
        source, template = self.bundle()
        self.assertEqual(len(source["sourceNodes"]), 2)
        for raw, exported in zip(self.case["sourceNodes"], source["sourceNodes"]):
            for key in ("id", "sourceText", "tag", "role", "rects"):
                self.assertEqual(exported[key], raw[key])
        with (self.output / "sources.tsv").open(encoding="utf-8", newline="") as stream:
            tsv = list(csv.DictReader(stream, delimiter="\t"))
        self.assertEqual([row["sourceText"] for row in tsv], [row["sourceText"] for row in self.case["sourceNodes"]])
        self.assertEqual(json.loads(tsv[0]["rects"]), self.case["sourceNodes"][0]["rects"])
        self.assertEqual(source["provenance"]["caseSha256"], hashlib.sha256(original["case.json"]).hexdigest())
        self.assertEqual(template["caseSha256"], source["provenance"]["caseSha256"])
        self.assertEqual(source["case"]["sourceHead"], self.case["sourceHead"])
        self.assertEqual(source["case"]["sourceHashes"], self.case["sourceHashes"])
        self.assertEqual(source["case"]["manifestHash"], self.case["manifestHash"])
        self.assertEqual(Path(source["provenance"]["casePath"]), self.case_path.resolve())
        self.assertEqual((self.output / "screenshots" / "before.png").read_bytes(), self.image)
        self.assertEqual({path.name: path.read_bytes() for path in self.evidence.iterdir()}, original)

    def test_blank_votes_ignore_predictions_simulated_completion_and_existing_truth(self):
        source, template = self.bundle()
        self.assertEqual(template["pageAnnotation"], {
            "needTranslation": "unknown", "reviewed": False, "independent": False})
        self.assertEqual([row["sourceId"] for row in template["annotations"]],
                         [row["id"] for row in self.case["sourceNodes"]])
        for row in template["annotations"]:
            self.assertEqual(row["needTranslation"], "unknown")
            self.assertIs(row["reviewed"], False)
            self.assertIs(row["independent"], False)
        self.assertEqual(template["screenshots"], {self.case["screenshots"][0]["phase"]: False})
        event = template["retractions"][0]
        self.assertEqual(event["eventIndex"], 0)
        self.assertEqual(event["sourceId"], self.case["retractions"][0]["sourceId"])
        self.assertIs(event["reviewed"], False)
        self.assertEqual(event["confirmed"], "unknown")
        self.assertEqual(event["sourceUnchanged"], "unknown")
        for path in self.output.iterdir():
            if path.is_file():
                text = path.read_text(encoding="utf-8")
                for secret in ('"predictions"', '"productionEligibility"', '"displayedText"',
                               '"eligible"', '"completed"', '"pageDecision"', '"expectedPageTranslation"',
                               "PREDICTION_SECRET_TRANSLATION"):
                    self.assertNotIn(secret, text)
        self.assertNotIn("annotations", source)
        # Reversing predictions cannot change exported independent evidence.
        self.case["predictions"] = [{"sourceId": "unselected", "eligible": True, "completed": False}]
        self.case["annotations"] = []
        self.case["pageDecision"] = False
        self.case["completed"] = False
        self.save()
        other = review_index.build_review_index(self.case_path, self.root / "other")
        self.assertEqual(source["sourceNodes"], other["sourceNodes"])

    def test_html_escapes_text_labels_metadata_and_only_local_links(self):
        self.bundle()
        text = (self.output / "index.html").read_text(encoding="utf-8")
        document = Document(text)
        self.assertNotIn("script", document.tags)
        self.assertEqual(document.tags.count("img"), 1)
        self.assertIn("&lt;script&gt;", text)
        self.assertIn("&quot; onmouseover=&quot;bad", text)
        for link in document.links:
            self.assertFalse(link.startswith(("/", "//")))
            self.assertNotIn(":", link)
            self.assertNotIn("..", link)
            self.assertTrue((self.output / link).is_file())
        self.assertIn(self.case["sourceNodes"][0]["sourceText"], "".join(document.text))
        self.assertIn(self.case["sourceNodes"][1]["sourceText"], "".join(document.text))
        self.assertIn("Content-Security-Policy", text)
        self.assertIn("default-src 'none'", text)

    def test_rejects_remote_traversal_absolute_encoded_and_windows_paths_before_output(self):
        for path in ("../before.png", "sub/before.png", "sub\\before.png", "/before.png", "C:\\before.png",
                     "C:before.png", "\\\\server\\share\\before.png", "before.png:stream",
                     "https://remote.invalid/before.png", "//remote.invalid/before.png",
                     "%2e%2e%2fbefore.png", "before.png?x=1", "before.svg", "before.png\x00"):
            with self.subTest(path=path):
                self.case["screenshots"][0]["path"] = path
                self.save()
                with self.assertRaisesRegex(review_index.ReviewIndexError, "invalid_screenshot_path"):
                    review_index.build_review_index(self.case_path, self.output)
                self.assertFalse(self.output.exists())

    def test_hash_mismatch_missing_image_and_non_png_are_not_linked(self):
        self.case["screenshots"][0]["sha256"] = "0" * 64
        self.save()
        with self.assertRaisesRegex(review_index.ReviewIndexError, "screenshot_hash_mismatch"):
            self.bundle()
        self.assertFalse(self.output.exists())
        (self.evidence / "before.png").unlink()
        with self.assertRaises(FileNotFoundError):
            self.bundle()
        bad_image = b'<html><script>alert("not PNG")</script></html>'
        (self.evidence / "before.png").write_bytes(bad_image)
        self.case["screenshots"][0]["sha256"] = hashlib.sha256(bad_image).hexdigest()
        self.save()
        with self.assertRaisesRegex(review_index.ReviewIndexError, "invalid_screenshot_png"):
            self.bundle()
        self.assertFalse(self.output.exists())

    def test_symlink_and_resolved_escape_are_rejected_without_reading_target(self):
        # Mock path resolution avoids requiring Windows symlink privileges.
        original_resolve = Path.resolve
        def resolve(path, *args, **kwargs):
            if path.name == "before.png":
                return self.root / "outside.png"
            return original_resolve(path, *args, **kwargs)
        with mock.patch.object(Path, "resolve", resolve):
            with self.assertRaisesRegex(review_index.ReviewIndexError, "screenshot_path_outside_evidence"):
                self.bundle()
        original_is_symlink = Path.is_symlink
        def is_symlink(path):
            return path.name == "before.png" or original_is_symlink(path)
        with mock.patch.object(Path, "is_symlink", is_symlink):
            with self.assertRaisesRegex(review_index.ReviewIndexError, "screenshot_path_outside_evidence"):
                self.bundle()
        self.assertFalse(self.output.exists())

    def test_output_never_reuses_existing_directory_file_or_raw_directory(self):
        self.bundle()
        original = {str(path.relative_to(self.root)): path.read_bytes()
                    for path in self.root.rglob("*") if path.is_file()}
        for output in (self.output, self.evidence, self.case_path):
            with self.subTest(output=output), self.assertRaisesRegex(
                    review_index.ReviewIndexError, "output_directory_must_be_new"):
                review_index.build_review_index(self.case_path, output)
        empty = self.root / "empty"
        empty.mkdir()
        with self.assertRaisesRegex(review_index.ReviewIndexError, "output_directory_must_be_new"):
            review_index.build_review_index(self.case_path, empty)
        self.assertEqual({str(path.relative_to(self.root)): path.read_bytes()
                          for path in self.root.rglob("*") if path.is_file()}, original)

    def test_racing_file_and_directory_creation_cannot_overwrite(self):
        mkdir = Path.mkdir
        def collide(path, *args, **kwargs):
            if path == self.output:
                mkdir(path)
                (path / "sources.json").write_bytes(b"prior evidence")
                return None
            return mkdir(path, *args, **kwargs)
        with mock.patch.object(Path, "mkdir", collide):
            with self.assertRaises(FileExistsError):
                self.bundle()
        self.assertEqual((self.output / "sources.json").read_bytes(), b"prior evidence")
        target = self.root / "racing-directory"
        def directory_race(path, *args, **kwargs):
            if path == target:
                mkdir(path)
            return mkdir(path, *args, **kwargs)
        with mock.patch.object(Path, "mkdir", directory_race):
            with self.assertRaises(FileExistsError):
                review_index.build_review_index(self.case_path, target)
        self.assertEqual(list(target.iterdir()), [])

    def test_empty_failed_capture_stays_blank_and_duplicate_screenshots_are_all_listed(self):
        self.case["sourceNodes"] = []
        self.case["screenshots"] = []
        self.case["retractions"] = []
        self.save()
        source, template = self.bundle()
        self.assertEqual(source["sourceNodes"], [])
        self.assertEqual(template["annotations"], [])
        self.assertEqual(template["screenshots"], {})
        self.assertEqual(template["pageAnnotation"]["needTranslation"], "unknown")
        shot = {"phase": "before", "path": "before.png", "sha256": hashlib.sha256(self.image).hexdigest()}
        self.case["screenshots"] = [shot, dict(shot, phase="after"), dict(shot, phase="before")]
        self.save()
        result = review_index.build_review_index(self.case_path, self.root / "duplicates")
        self.assertEqual(len(result["screenshots"]), 3)
        self.assertEqual(Document((self.root / "duplicates/index.html").read_text(encoding="utf-8")).tags.count("img"), 3)

    def test_invalid_source_identity_geometry_and_non_array_evidence_are_rejected(self):
        original = copy.deepcopy(self.case)
        invalid = [dict(original, sourceNodes=None), dict(original, screenshots={}),
                   dict(original, retractions=[True])]
        for field, value in (("id", ""), ("sourceText", None), ("rects", {}),
                             ("rects", [{"x": 0, "y": 1, "width": True, "height": 2}]),
                             ("rects", [{"x": float("nan"), "y": 1, "width": 2, "height": 2}])):
            case = copy.deepcopy(original)
            case["sourceNodes"][0][field] = value
            invalid.append(case)
        duplicate = copy.deepcopy(original)
        duplicate["sourceNodes"].append(duplicate["sourceNodes"][0])
        invalid.append(duplicate)
        for case in invalid:
            self.case = case
            self.save()
            with self.subTest(case=case), self.assertRaises(review_index.ReviewIndexError):
                self.bundle()
            self.assertFalse(self.output.exists())

    def test_cli_interface_reports_counts_without_review_or_completion(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = review_index.main(["--case", str(self.case_path), "--output-dir", str(self.output)])
        self.assertEqual(code, 0)
        result = json.loads(output.getvalue())
        self.assertEqual(result["sourceCount"], 2)
        self.assertEqual(result["screenshotCount"], 1)
        self.assertNotIn("completed", result)
        self.assertNotIn("reviewed", result)
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = review_index.main(["--case", str(self.case_path), "--output-dir", str(self.output)])
        self.assertEqual(code, 2)
        self.assertIn("error", json.loads(output.getvalue()))
        self.assertNotIn(str(self.case_path), output.getvalue())


if __name__ == "__main__":
    unittest.main()
