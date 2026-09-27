#!/usr/bin/env python3
"""Build/validate a deterministic Jellyfin catalog package; no network or publishing."""
import argparse
import datetime as dt
import hashlib
import json
import re
import sys
import uuid
import zipfile
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

GUID = "bd36ab75-0f4a-49b6-92ef-3a93da040c7a"
NAME = "3pic Fin"
DLL = "Rowan.Jellyfin.Plugin.dll"
ABI = "12.1.0.0"
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\Z")
DESCRIPTION = "Modular Jellyfin Home and discovery integration (staging)."
ZIP_TIME = (1980, 1, 1, 0, 0, 0)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_meta(meta, version):
    require(isinstance(meta, dict), "meta must be an object")
    require(str(uuid.UUID(meta["guid"])) == GUID, "meta GUID mismatch")
    require(meta["name"] == NAME and meta["targetAbi"] == ABI, "meta name/ABI mismatch")
    require(meta["version"] == version, "meta version mismatch")
    for field in ("category", "owner", "overview", "description", "changelog", "timestamp"):
        require(isinstance(meta[field], str) and meta[field].strip(), f"missing meta {field}")


def validate_catalog(catalog):
    require(isinstance(catalog, list), "manifest must be an array")
    guids = set()
    for entry in catalog:
        require(isinstance(entry, dict), "catalog entry must be an object")
        guid = str(uuid.UUID(entry["guid"]))
        require(guid not in guids, "duplicate plugin GUID")
        guids.add(guid)
        require(isinstance(entry["versions"], list), "versions must be an array")
        seen = set()
        for v in entry["versions"]:
            require(VERSION.fullmatch(v["version"]) is not None, "invalid catalog version")
            require(v["version"] not in seen, "duplicate catalog version")
            seen.add(v["version"])
            require(re.fullmatch(r"[0-9a-f]{32}", v["checksum"]) is not None, "checksum must be MD5")
            require(v["sourceUrl"].startswith("https://"), "sourceUrl must be HTTPS")
    return catalog


def validate_package(package, manifest, version, source_url):
    require(VERSION.fullmatch(version) is not None, "invalid requested version")
    require(source_url.startswith("https://") and source_url.endswith(f"/3pic-fin_{version}.zip"), "invalid requested source URL")
    catalog = validate_catalog(json.loads(Path(manifest).read_text(encoding="utf-8")))
    matches = [e for e in catalog if e["guid"] == GUID]
    require(len(matches) == 1 and matches[0]["name"] == NAME, "missing or mismatched catalog entry")
    versions = [v for v in matches[0]["versions"] if v["version"] == version]
    require(len(versions) == 1, "missing catalog version")
    row = versions[0]
    require(row["targetAbi"] == ABI and row["sourceUrl"] == source_url, "catalog ABI/URL mismatch")
    require(row["checksum"] == hashlib.md5(Path(package).read_bytes()).hexdigest(), "package MD5 mismatch")
    with zipfile.ZipFile(package) as z:
        require(sorted(z.namelist()) == sorted([DLL, "meta.json"]), "unexpected package contents")
        require(all(not x.is_dir() and x.file_size > 0 for x in z.infolist()), "empty package file")
        meta = json.loads(z.read("meta.json"))
        validate_meta(meta, version)
        require(z.read(DLL)[:2] == b"MZ", "DLL lacks PE header")
        require(meta["timestamp"] == row["timestamp"] and meta["changelog"] == row["changelog"], "catalog/meta mismatch")
    return row


def merge_catalog(catalog, row, meta):
    """Insert an immutable release row without discarding the published history."""
    validate_catalog(catalog)
    entry = next((e for e in catalog if e["guid"] == GUID), None)
    if entry is None:
        entry = {k: meta[k] for k in ("guid", "name", "description", "overview", "owner", "category")}
        entry["versions"] = []
        catalog.append(entry)
    else:
        require(entry["name"] == NAME, "catalog name mismatch")
    existing = next((v for v in entry["versions"] if v["version"] == row["version"]), None)
    require(existing is None or existing == row, "published version conflict")
    if existing is None:
        entry["versions"].insert(0, row)
    return catalog


def package(args):
    require(VERSION.fullmatch(args.version) is not None, "version must have four numeric components")
    require(Path(args.dll).is_file() and Path(args.dll).name == DLL, "expected Release DLL")
    require(Path(args.dll).read_bytes()[:2] == b"MZ", "DLL lacks PE header")
    url = urlparse(args.source_url)
    require(url.scheme == "https" and url.netloc and not url.username and not url.password and not url.query and not url.fragment, "sourceUrl must be a plain HTTPS URL")
    require(url.path.endswith(f"/3pic-fin_{args.version}.zip"), "sourceUrl filename mismatch")
    timestamp = dt.datetime.fromtimestamp(int(args.epoch), dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    require(args.changelog.strip(), "empty changelog")
    catalog = validate_catalog(json.loads(Path(args.manifest).read_text(encoding="utf-8")))
    meta = dict(guid=GUID, name=NAME, version=args.version, targetAbi=ABI,
                overview="Modular Home and discovery integration", description=DESCRIPTION,
                category="General", owner="Rowan", changelog=args.changelog, timestamp=timestamp)
    validate_meta(meta, args.version)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    zip_path = output / f"3pic-fin_{args.version}.zip"
    # Stable member ordering, modes, timestamps, JSON serialization and compression method.
    with zipfile.ZipFile(zip_path, "w") as z:
        for filename, content in [(DLL, Path(args.dll).read_bytes()),
                                  ("meta.json", (json.dumps(meta, sort_keys=True, indent=2, ensure_ascii=False) + "\n").encode())]:
            info = zipfile.ZipInfo(filename, ZIP_TIME)
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, content, compresslevel=9)
    row = dict(version=args.version, changelog=args.changelog, targetAbi=ABI,
               sourceUrl=args.source_url, checksum=hashlib.md5(zip_path.read_bytes()).hexdigest(), timestamp=timestamp)
    catalog = merge_catalog(catalog, row, meta)
    manifest_path = output / "manifest.json"
    require(manifest_path.resolve() != Path(args.manifest).resolve(), "output must not overwrite source manifest")
    manifest_path.write_text(json.dumps(catalog, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    validate_package(zip_path, manifest_path, args.version, args.source_url)
    (output / "SHA256SUMS").write_text(f"{hashlib.sha256(zip_path.read_bytes()).hexdigest()}  {zip_path.name}\n", encoding="ascii")
    print(f"Validated {zip_path} and {manifest_path}; MD5 {row['checksum']}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    p = sub.add_parser("package")
    p.add_argument("--dll", required=True)
    p.add_argument("--manifest", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--source-url", required=True)
    p.add_argument("--epoch", required=True, type=int, help="UTC timestamp from release commit")
    p.add_argument("--changelog", required=True)
    v = sub.add_parser("validate")
    for flag in ("package", "manifest", "version", "source-url"):
        v.add_argument("--" + flag, required=True)
    args = parser.parse_args()
    try:
        if args.action == "package":
            package(args)
        else:
            validate_package(args.package, args.manifest, args.version, args.source_url)
            print("Package and manifest validated")
    except (ValueError, KeyError, OSError, zipfile.BadZipFile) as exc:
        parser.exit(1, f"Invalid package: {exc}\n")


if __name__ == "__main__":
    main()
