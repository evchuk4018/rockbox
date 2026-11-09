#!/usr/bin/env python3
"""Package only portable Manager release files and a matching validated firmware ZIP."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def package(args):
    args.output.mkdir(parents=True, exist_ok=True)
    manager = ROOT / "build-rockbox-manager"
    manifest = json.loads((manager / "tools/manifest.json").read_text())
    for name, row in manifest["files"].items():
        assert sha(manager / "tools" / name) == row["sha256"]
    portable = args.output / "RockboxManager-Podcasts-win-x64.zip"
    with zipfile.ZipFile(portable, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name in ("RockboxManager.exe", "README.md", "THIRD-PARTY-NOTICES.txt", "COPYING.txt"):
            archive.write(manager / name, name)
        for file in sorted((manager / "tools").rglob("*")):
            if file.is_file():
                archive.write(file, file.relative_to(manager).as_posix())
        archive.write(ROOT / "docs/podcasts-install.md", "INSTALL.md")
    firmware = args.output / "rockbox-clipplus-podcasts.zip"
    shutil.copyfile(args.firmware, firmware)
    shutil.copyfile(ROOT / "docs/podcasts-install.md", args.output / "INSTALL.md")
    with zipfile.ZipFile(firmware) as archive:
        info = archive.read(".rockbox/rockbox-info.txt").decode()
        assert "Target: sansaclipplus\n" in info
        version = next(line.split(":", 1)[1].strip() for line in info.splitlines() if line.startswith("Version:"))
    files = {path.name: sha(path) for path in (portable, firmware, args.output / "INSTALL.md")}
    (args.output / "SHA256SUMS.txt").write_text("".join(f"{value}  {name}\n" for name, value in files.items()), encoding="utf-8")
    result = dict(firmwareVersion=version, files=files, tools=manifest["files"])
    (args.output / "release.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--firmware", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    package(parser.parse_args())
