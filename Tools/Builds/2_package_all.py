#!/usr/bin/env python3
"""Package existing Unity Windows, macOS, and Linux builds.

Place this script in Tools/Builds/ and run:
    python package_builds.py

Outputs:
    Builds/Packages/<Product> Windows.zip
    Builds/Packages/<Product> macOS.zip
    Builds/Packages/<Product> Linux.tar.gz
"""
from __future__ import annotations

import os
import re
import shutil
import stat
import tarfile
import zipfile
from pathlib import Path
from typing import Iterable

# These scripts live in <project>/Tools/Builds/.
PROJECT_ROOT = Path(__file__).resolve().parents[2]
BUILD_ROOT = PROJECT_ROOT / "Builds"
PACKAGE_ROOT = BUILD_ROOT / "Packages"

# Set any platform to False to skip packaging it.
PACKAGE_WINDOWS = True
PACKAGE_MACOS = True
PACKAGE_LINUX = True

# Compression level: 1 is faster/larger; 9 is slower/smaller.
COMPRESSION_LEVEL = 6

IGNORED_NAMES = {".DS_Store", "Thumbs.db", "desktop.ini"}


def product_name() -> str:
    settings = PROJECT_ROOT / "ProjectSettings" / "ProjectSettings.asset"
    if settings.is_file():
        text = settings.read_text(encoding="utf-8", errors="replace")
        match = re.search(r"(?m)^\s*productName:\s*(.*?)\s*$", text)
        if match:
            name = match.group(1).strip().strip('"').strip("'")
            name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip().rstrip(".")
            if name:
                return name
    raise RuntimeError(f"Could not read productName from {settings}")


def should_ignore(path: Path) -> bool:
    return path.name in IGNORED_NAMES or path.name == "__MACOSX"


def iter_tree(root: Path) -> Iterable[Path]:
    """Yield every file and directory under root, without following symlinks."""
    yield root
    for current, dirs, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        dirs[:] = sorted(d for d in dirs if not should_ignore(current_path / d))
        for dirname in dirs:
            yield current_path / dirname
        for filename in sorted(files):
            path = current_path / filename
            if not should_ignore(path):
                yield path


def validate_build(platform: str, name: str) -> Path:
    folder = BUILD_ROOT / platform
    if not folder.is_dir():
        raise FileNotFoundError(f"Build folder is missing: {folder}")

    expected = {
        "Windows": folder / f"{name}.exe",
        "Linux": folder / f"{name}.x86_64",
        "macOS": folder / f"{name}.app",
    }[platform]
    if not expected.exists():
        raise FileNotFoundError(f"Expected build output is missing: {expected}")

    data_folder = folder / "Data"
    if not data_folder.is_dir():
        raise FileNotFoundError(
            f"External Data folder is missing: {data_folder}\n"
            "Build/copy the Data folder before packaging; otherwise the app may not run."
        )
    return folder


def zip_build(source: Path, archive: Path, product: str, macos: bool = False) -> None:
    """Create a ZIP with a top-level product folder and preserved Unix modes."""
    if archive.exists():
        archive.unlink()

    file_count = 0
    with zipfile.ZipFile(
        archive,
        mode="w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=COMPRESSION_LEVEL,
        allowZip64=True,
    ) as zf:
        for path in iter_tree(source):
            if path == source:
                relative = Path()
            else:
                relative = path.relative_to(source)
            archive_name = (Path(product) / relative).as_posix()
            if path.is_dir() and not path.is_symlink():
                archive_name = archive_name.rstrip("/") + "/"

            if path.is_symlink():
                info = zipfile.ZipInfo(archive_name)
                info.create_system = 3
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
                zf.writestr(info, os.readlink(path))
                file_count += 1
                continue

            info = zipfile.ZipInfo.from_file(path, archive_name)
            info.create_system = 3  # Store Unix mode bits in the ZIP metadata.
            info.compress_type = zipfile.ZIP_DEFLATED

            source_mode = stat.S_IMODE(path.stat().st_mode)
            if path.is_dir():
                mode = source_mode or 0o755
                info.external_attr = (stat.S_IFDIR | mode) << 16
                zf.writestr(info, b"")
            else:
                mode = source_mode or 0o644
                # Builds made on Windows don't carry Unix execute bits. Set them
                # on macOS app executables so the main app binary remains runnable.
                if macos and ".app/Contents/macOS/" in ("/" + archive_name + "/"):
                    mode |= 0o111
                info.external_attr = (stat.S_IFREG | mode) << 16
                with path.open("rb") as src, zf.open(info, "w", force_zip64=True) as dst:
                    shutil.copyfileobj(src, dst, length=1024 * 1024)
                file_count += 1

    print(f"Created: {archive} ({file_count:,} files/links)")


def tar_gz_build(source: Path, archive: Path, product: str) -> None:
    """Create a gzip-compressed tar archive with the product as its top folder."""
    if archive.exists():
        archive.unlink()

    def clean_member(member: tarfile.TarInfo) -> tarfile.TarInfo | None:
        basename = Path(member.name).name
        if basename in IGNORED_NAMES or basename == "__MACOSX":
            return None
        # Avoid embedding the build machine's user/group ownership.
        member.uid = 0
        member.gid = 0
        member.uname = ""
        member.gname = ""

        # The builds are packaged on Windows, where Unix execute bits are not
        # reliably represented. Restore them for Linux launchers explicitly.
        if member.isdir():
            member.mode |= 0o755
        elif member.isfile() and (
            Path(member.name).name == f"{product}.x86_64"
            or Path(member.name).name.startswith("UnityCrashHandler")
        ):
            member.mode |= 0o111
        return member

    with tarfile.open(archive, mode="w:gz", compresslevel=COMPRESSION_LEVEL) as tf:
        tf.add(source, arcname=product, recursive=True, filter=clean_member)

    print(f"Created: {archive}")


def main() -> int:
    if not (PROJECT_ROOT / "Assets").is_dir() or not (PROJECT_ROOT / "ProjectSettings").is_dir():
        print(f"ERROR: Unity project root not found: {PROJECT_ROOT}")
        print("Expected this script to be in <project>/Tools/Builds/.")
        return 2

    try:
        name = product_name()
    except (OSError, RuntimeError) as exc:
        print(f"ERROR: {exc}")
        return 2

    PACKAGE_ROOT.mkdir(parents=True, exist_ok=True)
    tasks = []
    if PACKAGE_WINDOWS:
        tasks.append(("Windows", PACKAGE_ROOT / f"{name} Windows.zip", "zip"))
    if PACKAGE_MACOS:
        tasks.append(("macOS", PACKAGE_ROOT / f"{name} macOS.zip", "maczip"))
    if PACKAGE_LINUX:
        tasks.append(("Linux", PACKAGE_ROOT / f"{name} Linux.tar.gz", "tar"))

    if not tasks:
        print("Nothing to package: all PACKAGE_* switches are False.")
        return 0

    failures = 0
    for platform, archive, kind in tasks:
        print(f"\nPackaging {platform}...")
        try:
            source = validate_build(platform, name)
            if kind == "tar":
                tar_gz_build(source, archive, name)
            else:
                zip_build(source, archive, name, macos=(kind == "maczip"))
        except (OSError, RuntimeError, zipfile.BadZipFile, tarfile.TarError) as exc:
            failures += 1
            print(f"FAILED to package {platform}: {exc}")

    if failures:
        print(f"\nPackaging finished with {failures} failure(s). Output folder: {PACKAGE_ROOT}")
        return 1

    print(f"\nAll packages created in: {PACKAGE_ROOT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
