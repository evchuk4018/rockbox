#!/usr/bin/env python3
"""Install a validated Clip+ resource ZIP on identified volumes, preserving user data."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import zipfile

AUDIO = {".mp3", ".opus", ".flac", ".m4a", ".ogg", ".wav", ".wma", ".aac"}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def identify(root, expected):
    serial = ctypes.c_uint32()
    api = ctypes.WinDLL("kernel32", use_last_error=True).GetVolumeInformationW
    api.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint32,
                    ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p, ctypes.c_void_p,
                    ctypes.c_wchar_p, ctypes.c_uint32]
    api.restype = ctypes.c_int
    if not api(str(root).rstrip("\\/") + "\\", None, 0, ctypes.byref(serial), None, None, None, 0):
        raise OSError(ctypes.get_last_error(), "Player volume is unavailable")
    if f"{serial.value:08X}" != expected.upper():
        raise ValueError("Player/card identity changed")


def inventory(roots):
    rows = []
    for storage, root in roots.items():
        for folder, dirs, files in os.walk(root, followlinks=False):
            dirs[:] = [d for d in dirs if d not in {"System Volume Information", "$RECYCLE.BIN"}]
            for name in files:
                path = Path(folder) / name
                if path.is_symlink():
                    raise ValueError("Linked paths are unsupported")
                stat = path.stat()
                audio = path.suffix.lower() in AUDIO
                rows.append(dict(storage=storage, path=path.relative_to(root).as_posix(),
                                 size=stat.st_size, mtime_ns=stat.st_mtime_ns, audio=audio,
                                 sha256=None if audio else digest(path)))
    return rows


def install(args):
    roots = {"Internal": args.internal.resolve(), "SD": args.sd.resolve()}
    def identities():
        identify(roots["Internal"], args.internal_serial)
        identify(roots["SD"], args.sd_serial)
        product = roots["Internal"] / "version.sdk"
        if not product.exists():
            product = roots["Internal"] / "MTABLE.SYS"
        if not product.exists():
            raise ValueError("Clip+ firmware identification file is missing")
        info = roots["Internal"] / ".rockbox/rockbox-info.txt"
        if not info.exists() or "Target: sansaclipplus" not in info.read_text(encoding="utf-8", errors="replace"):
            raise ValueError("This is not the customized Clip+ Rockbox installation")
    identities()
    if digest(args.package) != args.sha256.lower():
        raise ValueError("Firmware package checksum mismatch")
    args.evidence.mkdir(parents=True, exist_ok=True)
    def save(name, data):
        (args.evidence / name).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    with zipfile.ZipFile(args.package) as package:
        rows = [entry for entry in package.infolist() if not entry.is_dir()]
        names = [row.filename for row in rows]
        assert len(names) == len({name.casefold() for name in names})
        for name in names:
            assert name.startswith(".rockbox/") and ".." not in PurePosixPath(name).parts
            assert "\\" not in name and not any(ord(c) < 32 for c in name)
            assert name not in {".rockbox/config.cfg", ".rockbox/fixed.cfg", ".rockbox/nvram.bin"}
            assert not name.startswith((".rockbox/database_", ".rockbox/home-", ".rockbox/podcasts/", ".rockbox/playlists/"))
        info = package.read(".rockbox/rockbox-info.txt").decode()
        assert "Target: sansaclipplus\n" in info
        before = inventory(roots)
        save("device-before.json", before)
        if args.preflight_only:
            print(json.dumps(dict(preflight="passed", files=len(names), target="sansaclipplus")))
            return
        backup = args.evidence / "device-before/.rockbox"
        shutil.copytree(roots["Internal"] / ".rockbox", backup)
        for row in before:
            if row["storage"] == "Internal" and row["path"].startswith(".rockbox/"):
                assert digest(args.evidence / "device-before" / row["path"]) == row["sha256"]
        for storage, root in roots.items():
            if (root / "Playlists").exists():
                shutil.copytree(root / "Playlists", args.evidence / "device-before" / storage / "Playlists")
        assert shutil.disk_usage(roots["Internal"]).free > sum(row.file_size for row in rows)
        installed = {}
        # Install resources first; switch firmware and its build identity last.
        rows.sort(key=lambda row: row.filename.endswith(("/rockbox.sansa", "/rockbox-info.txt")))
        for row in rows:
            identities()
            target = roots["Internal"] / row.filename
            assert target.resolve().is_relative_to(roots["Internal"])
            target.parent.mkdir(parents=True, exist_ok=True)
            temp = target.with_name(target.name + ".rbupdate.tmp")
            data = package.read(row)
            expected = hashlib.sha256(data).hexdigest()
            with temp.open("wb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temp, target)
            assert digest(target) == expected
            installed[row.filename] = expected
            save("firmware-installed.json", installed)
    identities()
    audio_count = protected_count = 0
    for row in before:
        target = roots[row["storage"]] / row["path"]
        if row["audio"]:
            stat = target.stat()
            assert (stat.st_size, stat.st_mtime_ns) == (row["size"], row["mtime_ns"])
            audio_count += 1
        elif row["storage"] != "Internal" or row["path"] not in installed:
            assert digest(target) == row["sha256"], str(target)
            protected_count += 1
    for name, expected in installed.items():
        assert digest(roots["Internal"] / name) == expected
    result = dict(installed=True, packageSha256=args.sha256, build=info.splitlines()[0],
                  firmwareFilesVerified=len(installed), protectedFilesUnchanged=protected_count,
                  audioSizesTimestampsUnchanged=audio_count,
                  internalSerial=args.internal_serial, sdSerial=args.sd_serial,
                  physicalAcceptance="Safely eject both volumes, disconnect, restart; user checks display/buttons/audio")
    save("verification.json", result)
    print(json.dumps(result))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--internal", type=Path, required=True)
    parser.add_argument("--internal-serial", required=True)
    parser.add_argument("--sd", type=Path, required=True)
    parser.add_argument("--sd-serial", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--preflight-only", action="store_true")
    install(parser.parse_args())
