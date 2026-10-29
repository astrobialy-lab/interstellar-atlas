from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import sys
import tarfile
import uuid
from pathlib import Path, PurePosixPath

# Assumes this script is saved in <UnityProject>/Tools/Builds/Install/.
PROJECT_DIR = Path(__file__).resolve().parents[3]
DATA_DIR = PROJECT_DIR / "Data"
ARCHIVE_PREFIX = "Data_QuestTransfer_"
MANIFEST_PATH = PROJECT_DIR / "Data_QuestTransfer_manifest.json"
LEGACY_ARCHIVE = PROJECT_DIR / "Data_QuestTransfer.tar"
PART_COUNT = 20
MAX_WORKERS = 20  # One worker process per TAR.
PROGRESS_EVERY = 2000
MANIFEST_VERSION = 2


def format_gib(value: int) -> str:
    return f"{value / (1024**3):.2f} GiB"


def generated_archive_paths() -> list[Path]:
    return sorted(PROJECT_DIR.glob(f"{ARCHIVE_PREFIX}[0-9][0-9][0-9][0-9].tar"))


def clean_generated_archives() -> None:
    for path in PROJECT_DIR.glob(f"{ARCHIVE_PREFIX}*.tar"):
        path.unlink(missing_ok=True)
    for path in PROJECT_DIR.glob(f"{ARCHIVE_PREFIX}*.tar.part"):
        path.unlink(missing_ok=True)
    MANIFEST_PATH.unlink(missing_ok=True)
    MANIFEST_PATH.with_suffix(MANIFEST_PATH.suffix + ".part").unlink(missing_ok=True)
    LEGACY_ARCHIVE.unlink(missing_ok=True)


def reuse_existing_if_valid() -> bool:
    """Reuse a completed v2 set without walking Data again. Use --force to rebuild."""
    archives = generated_archive_paths()
    if not archives and not MANIFEST_PATH.exists() and not LEGACY_ARCHIVE.exists():
        return False

    if not MANIFEST_PATH.is_file():
        raise RuntimeError(
            "Generated TAR files exist without a compatible manifest. "
            "Run this script with --force to remove and rebuild the generated TAR set."
        )
    try:
        manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError("Could not read the TAR manifest. Run with --force to rebuild.") from exc

    if manifest.get("format_version") != MANIFEST_VERSION or manifest.get("part_count") != PART_COUNT:
        raise RuntimeError(
            "The existing TAR set uses an older format or part count. "
            "Run this script with --force to rebuild it."
        )

    parts = manifest.get("parts")
    if not isinstance(parts, list) or len(parts) != PART_COUNT:
        raise RuntimeError("The existing manifest does not contain exactly 20 parts. Run with --force to rebuild.")

    expected_names = {f"{ARCHIVE_PREFIX}{i:04d}.tar" for i in range(1, PART_COUNT + 1)}
    actual_names = {path.name for path in archives}
    if actual_names != expected_names:
        raise RuntimeError("Existing TAR files do not match the manifest. Run with --force to rebuild.")

    by_name = {part.get("name"): part for part in parts}
    if set(by_name) != expected_names:
        raise RuntimeError("Existing manifest has invalid part names. Run with --force to rebuild.")

    for name in sorted(expected_names):
        path = PROJECT_DIR / name
        if not path.is_file() or path.stat().st_size != by_name[name].get("size_bytes"):
            raise RuntimeError(f"TAR is missing or has changed: {path}. Run with --force to rebuild.")

    print("A complete 20-part TAR set already exists; reusing it without rescanning Data.")
    print(f"Files: {int(manifest.get('file_count', 0)):,}; archive size: {format_gib(int(manifest.get('total_archive_bytes', 0)))}")
    print("Use --force to rebuild after changing Data.")
    return True


def enumerate_data(root: Path) -> tuple[list[str], list[str]]:
    """Collect relative paths only; deliberately do not stat every file for its size/mtime."""
    if not root.is_dir():
        raise FileNotFoundError(f"Data folder not found: {root}")

    files: list[str] = []
    directories: list[str] = []

    for current, dirnames, filenames in os.walk(root, topdown=True, followlinks=False):
        dirnames.sort(key=str.casefold)
        filenames.sort(key=str.casefold)
        current_path = Path(current)

        # Do not let symlink directories silently become TAR symlinks.
        real_dirnames: list[str] = []
        for dirname in dirnames:
            full = current_path / dirname
            if full.is_symlink():
                raise RuntimeError(f"Symlink found in Data: {full}. Resolve it before archiving.")
            real_dirnames.append(dirname)
            directories.append(full.relative_to(root).as_posix())
        dirnames[:] = real_dirnames

        for filename in filenames:
            # No per-file size/mtime/type pass: tar.add() reads each file directly later.
            files.append((current_path / filename).relative_to(root).as_posix())

    if not files:
        raise RuntimeError(f"Data folder contains no regular files: {root}")

    # Stable order gives a repeatable assignment when the source tree is unchanged.
    return files, directories


def split_evenly_by_file_count(files: list[str], count: int) -> list[list[str]]:
    """Partition the sorted file list into exactly count chunks with near-equal file counts."""
    quotient, remainder = divmod(len(files), count)
    chunks: list[list[str]] = []
    start = 0
    for index in range(count):
        amount = quotient + (1 if index < remainder else 0)
        chunks.append(files[start : start + amount])
        start += amount
    return chunks


def build_one_part(args: tuple[str, str, int, list[str], list[str], int]) -> dict:
    """Worker process: create one uncompressed TAR and atomically publish it."""
    root_text, output_text, part_index, chunk_files, all_directories, total_parts = args
    root = Path(root_text)
    output_path = Path(output_text)
    partial_path = output_path.with_suffix(output_path.suffix + ".part")
    partial_path.unlink(missing_ok=True)

    try:
        with tarfile.open(partial_path, mode="w", format=tarfile.PAX_FORMAT) as tar:
            # Part 1 creates the entire directory tree before other workers extract files.
            # Other parts include only file members, avoiding concurrent directory creation.
            if part_index == 1:
                tar.add(root, arcname="Data", recursive=False)
                ordered_dirs = sorted(
                    all_directories,
                    key=lambda value: (len(PurePosixPath(value).parts), value.casefold()),
                )
                for rel_dir in ordered_dirs:
                    local_dir = root.joinpath(*PurePosixPath(rel_dir).parts)
                    tar.add(local_dir, arcname=f"Data/{rel_dir}", recursive=False)

            for index, rel in enumerate(chunk_files, start=1):
                local_file = root.joinpath(*PurePosixPath(rel).parts)
                # tar.add() itself reads the current file metadata/content; do not run a
                # separate per-file size/mtime scan before archiving.
                tar.add(local_file, arcname=f"Data/{rel}", recursive=False)
                if index % PROGRESS_EVERY == 0:
                    print(
                        f"  Process {part_index:02d}/{total_parts:02d}: added "
                        f"{index:,}/{len(chunk_files):,} files",
                        flush=True,
                    )

        # Only inspect the first TAR header. Full getmembers() scans are intentionally
        # avoided; the transfer stage verifies remote byte size and extracted sample files.
        with tarfile.open(partial_path, mode="r:") as check:
            if check.next() is None:
                raise RuntimeError(f"Created an empty/invalid TAR: {partial_path}")

        os.replace(partial_path, output_path)
        return {
            "name": output_path.name,
            "size_bytes": output_path.stat().st_size,
            "file_count": len(chunk_files),
            "first_file": chunk_files[0] if chunk_files else None,
        }
    except Exception:
        partial_path.unlink(missing_ok=True)
        raise


def create_archives(force: bool = False) -> None:
    if force:
        clean_generated_archives()
    elif reuse_existing_if_valid():
        return
    elif generated_archive_paths() or MANIFEST_PATH.exists() or LEGACY_ARCHIVE.exists():
        raise RuntimeError("Generated TAR artifacts already exist. Use --force to rebuild them.")

    files, directories = enumerate_data(DATA_DIR)
    chunks = split_evenly_by_file_count(files, PART_COUNT)

    print(f"Project:       {PROJECT_DIR}")
    print(f"Source:        {DATA_DIR}")
    print(f"Source files:  {len(files):,}")
    print(f"Directories:   {len(directories):,}")
    print(f"TAR parts:     {PART_COUNT}")
    print(f"Workers:       {MAX_WORKERS} processes (one per TAR)")
    print(f"Files per part: {min(map(len, chunks)):,}–{max(map(len, chunks)):,}", flush=True)
    print("File sizes are not scanned for balancing; actual TAR sizes may differ.", flush=True)

    worker_args = []
    for index, chunk in enumerate(chunks, start=1):
        name = f"{ARCHIVE_PREFIX}{index:04d}.tar"
        output_path = PROJECT_DIR / name
        if output_path.exists():
            raise RuntimeError(f"Refusing to overwrite existing archive: {output_path}. Use --force.")
        worker_args.append(
            (
                str(DATA_DIR),
                str(output_path),
                index,
                chunk,
                directories if index == 1 else [],
                PART_COUNT,
            )
        )

    results_by_index: dict[int, dict] = {}
    try:
        with concurrent.futures.ProcessPoolExecutor(max_workers=MAX_WORKERS) as executor:
            futures = {
                executor.submit(build_one_part, args): index
                for index, args in enumerate(worker_args, start=1)
            }
            finished = 0
            for future in concurrent.futures.as_completed(futures):
                index = futures[future]
                result = future.result()
                results_by_index[index] = result
                finished += 1
                print(
                    f"Completed TAR {finished}/{PART_COUNT}: {result['name']} "
                    f"({format_gib(result['size_bytes'])}; {result['file_count']:,} files)",
                    flush=True,
                )

        parts = [results_by_index[index] for index in range(1, PART_COUNT + 1)]
        total_archive_bytes = sum(part["size_bytes"] for part in parts)
        manifest = {
            "format_version": MANIFEST_VERSION,
            "archive_format": "uncompressed-tar",
            "part_count": PART_COUNT,
            "worker_processes": MAX_WORKERS,
            "transfer_id": uuid.uuid4().hex,
            "file_count": len(files),
            "total_archive_bytes": total_archive_bytes,
            "directory_count": len(directories),
            "parts": parts,
        }
        temp_manifest = MANIFEST_PATH.with_suffix(MANIFEST_PATH.suffix + ".part")
        temp_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        os.replace(temp_manifest, MANIFEST_PATH)
    except Exception:
        # A partial archive set without a manifest must not be mistaken for a finished set.
        for path in generated_archive_paths():
            path.unlink(missing_ok=True)
        for path in PROJECT_DIR.glob(f"{ARCHIVE_PREFIX}*.tar.part"):
            path.unlink(missing_ok=True)
        MANIFEST_PATH.with_suffix(MANIFEST_PATH.suffix + ".part").unlink(missing_ok=True)
        raise

    print("\nAll 20 TAR parts were created.")
    for part in parts:
        print(f"  {part['name']}: {format_gib(part['size_bytes'])}, {part['file_count']:,} files")
    print(f"Total TAR size: {format_gib(total_archive_bytes)}")
    print(f"Manifest: {MANIFEST_PATH}")
    print("Keep all 20 TAR files and the manifest for script 2.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Split Data into exactly 20 uncompressed TAR files by file count, in parallel."
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="delete generated Data_QuestTransfer archives/manifest and rebuild them",
    )
    args = parser.parse_args()
    try:
        create_archives(force=args.force)
        return 0
    except (OSError, RuntimeError, tarfile.TarError, KeyError, ValueError) as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
