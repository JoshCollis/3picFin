#!/usr/bin/env python3
"""Select the next Jellyfin four-part version, reusing a HEAD tag on retry."""
import json
import re
import subprocess
import sys
from pathlib import Path

if __package__:
    from .package_plugin import GUID, validate_catalog
else:
    from package_plugin import GUID, validate_catalog

TAG = re.compile(r"v(\d+)\.(\d+)\.(\d+)\.(\d+)\Z")


def select_version(catalog, tags, head):
    validate_catalog(catalog)
    published = {v["version"] for entry in catalog if entry["guid"] == GUID for v in entry["versions"]}
    parsed = [(tuple(map(int, match.groups())), name, sha) for name, sha in tags
              if (match := TAG.fullmatch(name))]
    same_head = [(parts, name) for parts, name, sha in parsed if sha == head]
    if same_head:
        if len(same_head) != 1:
            raise ValueError("multiple release tags point to this commit")
        return same_head[0][1][1:]
    all_versions = [tuple(map(int, version.split('.'))) for version in published]
    all_versions.extend(parts for parts, _, _ in parsed)
    major, minor, patch, revision = max(all_versions, default=(0, 0, 0, 0))
    return f"{major}.{minor}.{patch}.{revision + 1}"


def main():
    catalog = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    names = subprocess.check_output(["git", "tag", "--list", "v*"], text=True).splitlines()
    tags = [(name, subprocess.check_output(["git", "rev-list", "-n", "1", name], text=True).strip())
            for name in names if TAG.fullmatch(name)]
    print(select_version(catalog, tags, head))


if __name__ == "__main__":
    main()
