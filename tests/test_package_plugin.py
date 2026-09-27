import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/package_plugin.py"
SOURCE = ROOT / "tests/fixtures/manifest.json"
URL = "https://example.invalid/releases/download/v1.2.3.4/3pic-fin_1.2.3.4.zip"


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dir = Path(self.temp.name)
        self.dll = self.dir / "Rowan.Jellyfin.Plugin.dll"
        self.dll.write_bytes(b"MZ\x00fixture")
        self.manifest = self.dir / "manifest.json"
        self.manifest.write_bytes(SOURCE.read_bytes())

    def run_package(self, output="dist", version="1.2.3.4", check=True):
        return subprocess.run([sys.executable, str(SCRIPT), "package", "--dll", str(self.dll),
            "--manifest", str(self.manifest), "--output", str(self.dir / output),
            "--version", version, "--source-url", URL, "--epoch", "1780000000",
            "--changelog", "Fixture changes"], capture_output=True, text=True, check=check)

    def test_deterministic_append_replace_and_validate(self):
        self.run_package()
        out = self.dir / "dist"
        archive = out / "3pic-fin_1.2.3.4.zip"
        before = archive.read_bytes()
        manifest = json.loads((out / "manifest.json").read_text())
        self.assertEqual(manifest[0], json.loads(SOURCE.read_text())[0])
        self.assertEqual(manifest[1]["versions"][0]["targetAbi"], "12.1.0.0")
        self.assertEqual(len(manifest), 2)
        self.run_package("second")
        self.assertEqual(before, (self.dir / "second" / archive.name).read_bytes())
        self.manifest.write_bytes((out / "manifest.json").read_bytes())
        self.run_package("updated")
        updated = json.loads((self.dir / "updated/manifest.json").read_text())
        self.assertEqual(len(updated), 2)
        self.assertEqual(len(updated[1]["versions"]), 1)
        subprocess.run([sys.executable, str(SCRIPT), "validate", "--package", str(archive),
            "--manifest", str(out / "manifest.json"), "--version", "1.2.3.4",
            "--source-url", URL], check=True)

    def test_rejects_bad_version_and_tampering(self):
        self.assertNotEqual(self.run_package(version="1.2.3", check=False).returncode, 0)
        self.run_package()
        archive = self.dir / "dist/3pic-fin_1.2.3.4.zip"
        archive.write_bytes(archive.read_bytes() + b"tampered")
        result = subprocess.run([sys.executable, str(SCRIPT), "validate", "--package", str(archive),
            "--manifest", str(self.dir / "dist/manifest.json"), "--version", "1.2.3.4",
            "--source-url", URL], capture_output=True)
        self.assertNotEqual(result.returncode, 0)

    def test_preserves_multiple_versions_and_rejects_changed_retry(self):
        self.run_package()
        first = self.dir / "dist/manifest.json"
        self.manifest.write_bytes(first.read_bytes())
        second_url = URL.replace("1.2.3.4", "1.2.3.5")
        import scripts.package_plugin as pkg
        original = json.loads(first.read_text())
        new = json.loads(first.read_text())
        row = dict(new[1]["versions"][0], version="1.2.3.5", sourceUrl=second_url)
        merged = pkg.merge_catalog(new, row, new[1])
        self.assertEqual([v["version"] for v in merged[1]["versions"]], ["1.2.3.5", "1.2.3.4"])
        self.assertEqual(pkg.merge_catalog(merged, row, new[1]), merged)
        self.assertEqual(original[1]["versions"][0], merged[1]["versions"][1])
        with self.assertRaisesRegex(ValueError, "conflict"):
            pkg.merge_catalog(merged, dict(row, checksum="0" * 32), new[1])

    def test_downloaded_archive_validation_detects_corrupted_bytes(self):
        import hashlib
        from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
        from threading import Thread
        from urllib.request import urlopen
        self.run_package()
        archive = self.dir / "dist/3pic-fin_1.2.3.4.zip"
        class Handler(SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=str(archive.parent), **kwargs)
            def log_message(self, format, *args):
                pass
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        downloaded = self.dir / "downloaded.zip"
        downloaded.write_bytes(urlopen(f"http://127.0.0.1:{server.server_port}/{archive.name}").read())
        self.assertEqual(hashlib.sha256(downloaded.read_bytes()).digest(), hashlib.sha256(archive.read_bytes()).digest())
        def validate():
            return subprocess.run([sys.executable, str(SCRIPT), "validate", "--package", str(downloaded),
                "--manifest", str(self.dir / "dist/manifest.json"), "--version", "1.2.3.4",
                "--source-url", URL], capture_output=True)
        self.assertEqual(validate().returncode, 0)
        downloaded.write_bytes(downloaded.read_bytes() + b"corruption")
        self.assertNotEqual(validate().returncode, 0)

    def test_rejects_duplicate_guid(self):
        source = json.loads(SOURCE.read_text())
        source.append(source[0])
        self.manifest.write_text(json.dumps(source))
        self.assertNotEqual(self.run_package(check=False).returncode, 0)


if __name__ == "__main__":
    unittest.main()
