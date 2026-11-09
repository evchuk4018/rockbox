#!/usr/bin/env python3
"""Reproducibly bundle portable Windows podcast tools; never execute downloads."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def download(url, path):
    request = urllib.request.Request(url, headers={"User-Agent": "RockboxManager-release"})
    with urllib.request.urlopen(request, timeout=120) as response, path.open("wb") as output:
        shutil.copyfileobj(response, output, 1024 * 1024)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def bundle(output, cache):
    lock_path = ROOT / "utils/rockbox-manager/podcast-tools.lock.json"
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    tools = output / "tools"
    tools.mkdir(parents=True, exist_ok=True)
    cache.mkdir(parents=True, exist_ok=True)
    notices = tools / "licenses"
    notices.mkdir(exist_ok=True)
    manifest = {"schema": 1, "files": {}}
    for tool in lock["tools"]:
        asset = cache / tool["url"].rsplit("/", 1)[1]
        if not asset.exists() or digest(asset) != tool["sha256"]:
            download(tool["url"], asset)
        if digest(asset) != tool["sha256"]:
            raise ValueError(f"Checksum mismatch: {tool['name']}")
        for member, name in tool["files"].items():
            target = tools / name
            if zipfile.is_zipfile(asset):
                with zipfile.ZipFile(asset) as archive, archive.open(member) as source, target.open("wb") as dest:
                    shutil.copyfileobj(source, dest, 1024 * 1024)
            else:
                shutil.copyfile(asset, target)
            manifest["files"][name] = {"version": tool["version"], "sha256": digest(target)}
        license_path = notices / (tool["name"] + ".txt")
        download(tool["license"], license_path)
        print(f"Verified {tool['name']} {tool['version']}", flush=True)
    shutil.copyfile(lock_path, tools / lock_path.name)
    (tools / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    shutil.copyfile(ROOT / "docs/COPYING", output / "COPYING.txt")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "build-rockbox-manager")
    parser.add_argument("--cache", type=Path, required=True)
    args = parser.parse_args()
    bundle(args.output.resolve(), args.cache.resolve())
