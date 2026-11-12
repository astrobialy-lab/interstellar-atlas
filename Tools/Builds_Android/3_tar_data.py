from __future__ import annotations

import concurrent.futures
import json
import os
import sys
import tarfile
import uuid
from pathlib import Path, PurePosixPath

# Script location: <UnityProject>/Tools/Builds_Android/
PROJECT_DIR = Path(__file__).resolve().parents[2]

DATA_DIR = PROJECT_DIR / "Data"
BUILD_DIR = PROJECT_DIR / "Builds" / "Android"
ARCHIVE_DIR = BUILD_DIR / "RequiredAssets"

ARCHIVE_PREFIX = "Data_AndroidTransfer_"
MANIFEST_NAME = "Data_AndroidTransfer_manifest.json"
MANIFEST_PATH = ARCHIVE_DIR / MANIFEST_NAME
ASSET_CONFIG_PATH = BUILD_DIR / "required_asset_files.json"
LEGACY_ARCHIVE = ARCHIVE_DIR / "Data_AndroidTransfer.tar"

MAX_PART_SIZE_GIB = 1.8
FORCE_REBUILD = True
MAX_WORKERS = 20
PROGRESS_EVERY = 2000
MANIFEST_VERSION = 3

# Approximate uncompressed TAR header/padding overhead per file.
# Used only for balancing; actual TAR sizes are recorded afterward.
TAR_MEMBER_OVERHEAD_BYTES = 1024


def format_gib(value: int) -> str:
    return f"{value / (1024**3):.2f} GiB"


def generated_archive_paths() -> list[Path]:
    return sorted(
        ARCHIVE_DIR.glob(f"{ARCHIVE_PREFIX}[0-9][0-9][0-9][0-9].tar")
    )


def write_json_atomic(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".part")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def write_required_asset_config(parts: list[dict]) -> None:
    """Create Meta's config marking every TAR and the manifest as required."""
    config = {
        part["name"]: {"required": True}
        for part in parts
    }
    config[MANIFEST_NAME] = {"required": True}
    write_json_atomic(ASSET_CONFIG_PATH, config)


def clean_generated_archives() -> None:
    ARCHIVE_DIR.mkdir(parents=True, exist_ok=True)

    for path in ARCHIVE_DIR.glob(f"{ARCHIVE_PREFIX}*.tar"):
        path.unlink(missing_ok=True)

    for path in ARCHIVE_DIR.glob(f"{ARCHIVE_PREFIX}*.tar.part"):
        path.unlink(missing_ok=True)

    MANIFEST_PATH.unlink(missing_ok=True)
    MANIFEST_PATH.with_suffix(MANIFEST_PATH.suffix + ".part").unlink(
        missing_ok=True
    )
    LEGACY_ARCHIVE.unlink(missing_ok=True)
    ASSET_CONFIG_PATH.unlink(missing_ok=True)
    ASSET_CONFIG_PATH.with_suffix(ASSET_CONFIG_PATH.suffix + ".part").unlink(
        missing_ok=True
    )


def reuse_existing_if_valid(max_part_size_bytes: int) -> bool:
    """Reuse a completed archive set without rescanning Data."""
    archives = generated_archive_paths()

    if not archives and not MANIFEST_PATH.exists() and not LEGACY_ARCHIVE.exists():
        return False

    if not MANIFEST_PATH.is_file():
        raise RuntimeError(
            "Generated TAR files exist without a compatible manifest. "
            "Set FORCE_REBUILD = True and run the script again to rebuild."
        )

    try:
        manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(
            "Could not read the TAR manifest. Set FORCE_REBUILD = True and rebuild."
        ) from exc

    if manifest.get("format_version") != MANIFEST_VERSION:
        raise RuntimeError(
            "The existing TAR set uses an older format. "
            "Set FORCE_REBUILD = True and rebuild it."
        )

    if manifest.get("max_part_size_bytes") != max_part_size_bytes:
        raise RuntimeError(
            "The existing TAR set was built with a different maximum part size. "
            "Set FORCE_REBUILD = True to rebuild it with the requested size."
        )

    parts = manifest.get("parts")
    part_count = manifest.get("part_count")

    if not isinstance(part_count, int) or part_count < 1:
        raise RuntimeError(
            "The existing manifest has an invalid part_count. "
            "Set FORCE_REBUILD = True and rebuild."
        )

    if not isinstance(parts, list) or len(parts) != part_count:
        raise RuntimeError(
            "The existing manifest has an invalid parts list. "
            "Set FORCE_REBUILD = True and rebuild."
        )

    expected_names = {
        f"{ARCHIVE_PREFIX}{index:04d}.tar"
        for index in range(1, part_count + 1)
    }

    actual_names = {path.name for path in archives}

    if actual_names != expected_names:
        raise RuntimeError(
            "Existing TAR files do not match the manifest. "
            "Set FORCE_REBUILD = True and rebuild."
        )

    by_name = {
        part.get("name"): part
        for part in parts
        if isinstance(part, dict)
    }

    if set(by_name) != expected_names:
        raise RuntimeError(
            "Existing manifest has invalid part names. "
            "Set FORCE_REBUILD = True and rebuild."
        )

    for name in sorted(expected_names):
        path = ARCHIVE_DIR / name

        if (
            not path.is_file()
            or path.stat().st_size != by_name[name].get("size_bytes")
        ):
            raise RuntimeError(
                f"TAR is missing or has changed: {path}. "
                "Set FORCE_REBUILD = True and rebuild."
            )

    # Re-create the config even when the archives are reused. This makes sure
    # it exists and always describes the exact current set of TAR files.
    write_required_asset_config(parts)

    print(
        f"A complete {part_count}-part TAR set already exists; "
        "reusing it without rescanning Data."
    )
    print(
        f"Files: {int(manifest.get('file_count', 0)):,}; "
        f"archive size: "
        f"{format_gib(int(manifest.get('total_archive_bytes', 0)))}"
    )
    print(f"Required assets: {ARCHIVE_DIR}")
    print(f"Asset config:    {ASSET_CONFIG_PATH}")
    print("Set FORCE_REBUILD = True after changing Data or the size limit.")

    return True


def enumerate_data(root: Path) -> tuple[list[tuple[str, int]], list[str]]:
    """Collect relative file paths and their sizes; does not read file contents."""
    if not root.is_dir():
        raise FileNotFoundError(f"Data folder not found: {root}")

    files: list[tuple[str, int]] = []
    directories: list[str] = []

    for current, dirnames, filenames in os.walk(
        root, topdown=True, followlinks=False
    ):
        dirnames.sort(key=str.casefold)
        filenames.sort(key=str.casefold)

        current_path = Path(current)

        # Do not let symlink directories silently become TAR symlinks.
        real_dirnames: list[str] = []

        for dirname in dirnames:
            full = current_path / dirname

            if full.is_symlink():
                raise RuntimeError(
                    f"Symlink found in Data: {full}. Resolve it before archiving."
                )

            real_dirnames.append(dirname)
            directories.append(full.relative_to(root).as_posix())

        dirnames[:] = real_dirnames

        for filename in filenames:
            full = current_path / filename

            if full.is_symlink():
                raise RuntimeError(
                    f"Symlink found in Data: {full}. Resolve it before archiving."
                )

            try:
                size_bytes = full.stat(follow_symlinks=False).st_size
            except OSError as exc:
                raise RuntimeError(
                    f"Could not read file size for {full}: {exc}"
                ) from exc

            files.append((full.relative_to(root).as_posix(), size_bytes))

    if not files:
        raise RuntimeError(f"Data folder contains no regular files: {root}")

    # Stable path ordering gives repeatable archive membership.
    return files, directories


def split_by_approximate_size(
    files: list[tuple[str, int]],
    max_part_size_bytes: int,
) -> list[list[tuple[str, int]]]:
    """Make sequential chunks using file sizes plus a small TAR-overhead estimate."""
    chunks: list[list[tuple[str, int]]] = []
    current_chunk: list[tuple[str, int]] = []
    current_estimate = 0

    for relative_path, size_bytes in files:
        estimated_member_size = size_bytes + TAR_MEMBER_OVERHEAD_BYTES

        # Start a new part before adding this file if it would exceed the target.
        if (
            current_chunk
            and current_estimate + estimated_member_size > max_part_size_bytes
        ):
            chunks.append(current_chunk)
            current_chunk = []
            current_estimate = 0

        current_chunk.append((relative_path, size_bytes))
        current_estimate += estimated_member_size

        # A single large file is placed alone in an oversized part.
        if current_estimate >= max_part_size_bytes:
            chunks.append(current_chunk)
            current_chunk = []
            current_estimate = 0

    if current_chunk:
        chunks.append(current_chunk)

    return chunks


def build_one_part(
    args: tuple[str, str, int, list[tuple[str, int]], list[str], int]
) -> dict:
    """Worker process: create one uncompressed TAR and atomically publish it."""
    (
        root_text,
        output_text,
        part_index,
        chunk_files,
        all_directories,
        total_parts,
    ) = args

    root = Path(root_text)
    output_path = Path(output_text)
    partial_path = output_path.with_suffix(output_path.suffix + ".part")

    partial_path.unlink(missing_ok=True)

    try:
        with tarfile.open(
            partial_path,
            mode="w",
            format=tarfile.PAX_FORMAT,
        ) as tar:

            # Part 1 creates the directory tree. Other parts contain files only.
            if part_index == 1:
                tar.add(root, arcname="Data", recursive=False)

                ordered_dirs = sorted(
                    all_directories,
                    key=lambda value: (
                        len(PurePosixPath(value).parts),
                        value.casefold(),
                    ),
                )

                for rel_dir in ordered_dirs:
                    local_dir = root.joinpath(
                        *PurePosixPath(rel_dir).parts
                    )
                    tar.add(
                        local_dir,
                        arcname=f"Data/{rel_dir}",
                        recursive=False,
                    )

            for index, (rel, _size_bytes) in enumerate(
                chunk_files,
                start=1,
            ):
                local_file = root.joinpath(
                    *PurePosixPath(rel).parts
                )

                tar.add(
                    local_file,
                    arcname=f"Data/{rel}",
                    recursive=False,
                )

                if index % PROGRESS_EVERY == 0:
                    print(
                        f"  Process {part_index:02d}/{total_parts:02d}: "
                        f"added {index:,}/{len(chunk_files):,} files",
                        flush=True,
                    )

        # Check only the first TAR header; avoid a full second archive scan.
        with tarfile.open(partial_path, mode="r:") as check:
            if check.next() is None:
                raise RuntimeError(
                    f"Created an empty/invalid TAR: {partial_path}"
                )

        os.replace(partial_path, output_path)

        return {
            "name": output_path.name,
            "size_bytes": output_path.stat().st_size,
            "file_count": len(chunk_files),
            "first_file": chunk_files[0][0] if chunk_files else None,
        }

    except Exception:
        partial_path.unlink(missing_ok=True)
        raise


def create_archives(
    max_part_size_bytes: int,
    force: bool = False,
) -> None:
    # Create both output directories if they do not exist yet.
    ARCHIVE_DIR.mkdir(parents=True, exist_ok=True)
    BUILD_DIR.mkdir(parents=True, exist_ok=True)

    if force:
        clean_generated_archives()
    elif reuse_existing_if_valid(max_part_size_bytes):
        return
    elif (
        generated_archive_paths()
        or MANIFEST_PATH.exists()
        or LEGACY_ARCHIVE.exists()
    ):
        raise RuntimeError(
            "Generated TAR artifacts already exist. "
            "Set FORCE_REBUILD = True to rebuild them."
        )

    files, directories = enumerate_data(DATA_DIR)

    chunks = split_by_approximate_size(
        files,
        max_part_size_bytes,
    )

    part_count = len(chunks)
    worker_count = min(MAX_WORKERS, part_count)

    estimated_sizes = [
        sum(size + TAR_MEMBER_OVERHEAD_BYTES for _, size in chunk)
        for chunk in chunks
    ]

    print(f"Project:              {PROJECT_DIR}")
    print(f"Source:               {DATA_DIR}")
    print(f"Output:               {ARCHIVE_DIR}")
    print(f"Source files:         {len(files):,}")
    print(f"Directories:          {len(directories):,}")
    print(
        f"Approx. size limit:   {format_gib(max_part_size_bytes)} per TAR"
    )
    print(f"TAR parts:            {part_count}")
    print(f"Workers:              {worker_count}")
    print(
        "Estimated part sizes: "
        + ", ".join(format_gib(size) for size in estimated_sizes),
        flush=True,
    )

    worker_args = []

    for index, chunk in enumerate(chunks, start=1):
        name = f"{ARCHIVE_PREFIX}{index:04d}.tar"
        output_path = ARCHIVE_DIR / name

        if output_path.exists():
            raise RuntimeError(
                f"Refusing to overwrite existing archive: {output_path}. "
                "Set FORCE_REBUILD = True to rebuild."
            )

        worker_args.append(
            (
                str(DATA_DIR),
                str(output_path),
                index,
                chunk,
                directories if index == 1 else [],
                part_count,
            )
        )

    results_by_index: dict[int, dict] = {}

    try:
        with concurrent.futures.ProcessPoolExecutor(
            max_workers=worker_count
        ) as executor:

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
                    f"Completed TAR {finished}/{part_count}: "
                    f"{result['name']} "
                    f"({format_gib(result['size_bytes'])}; "
                    f"{result['file_count']:,} files)",
                    flush=True,
                )

        parts = [
            results_by_index[index]
            for index in range(1, part_count + 1)
        ]

        total_archive_bytes = sum(
            part["size_bytes"] for part in parts
        )

        oversized = [
            part
            for part in parts
            if part["size_bytes"] > max_part_size_bytes
        ]

        manifest = {
            "format_version": MANIFEST_VERSION,
            "archive_format": "uncompressed-tar",
            "part_count": part_count,
            "max_part_size_bytes": max_part_size_bytes,
            "worker_processes": worker_count,
            "transfer_id": uuid.uuid4().hex,
            "file_count": len(files),
            "total_archive_bytes": total_archive_bytes,
            "directory_count": len(directories),
            "parts": parts,
        }

        write_json_atomic(MANIFEST_PATH, manifest)
        write_required_asset_config(parts)

    except Exception:
        # A partial archive set without a manifest must not be mistaken
        # for a finished set.
        for path in generated_archive_paths():
            path.unlink(missing_ok=True)

        for path in ARCHIVE_DIR.glob(f"{ARCHIVE_PREFIX}*.tar.part"):
            path.unlink(missing_ok=True)

        MANIFEST_PATH.with_suffix(
            MANIFEST_PATH.suffix + ".part"
        ).unlink(missing_ok=True)
        MANIFEST_PATH.unlink(missing_ok=True)
        ASSET_CONFIG_PATH.unlink(missing_ok=True)

        raise

    print("\nTAR creation completed.")

    for part in parts:
        print(
            f"  {part['name']}: "
            f"{format_gib(part['size_bytes'])}, "
            f"{part['file_count']:,} files"
        )

    print(f"Total TAR size: {format_gib(total_archive_bytes)}")
    print(f"Manifest:       {MANIFEST_PATH}")
    print(f"Asset config:   {ASSET_CONFIG_PATH}")

    if oversized:
        print(
            "\nWARNING: These TAR files exceed the approximate size limit. "
            "A single large source file or TAR overhead can cause this:"
        )

        for part in oversized:
            print(
                f"  {part['name']}: "
                f"{format_gib(part['size_bytes'])}"
            )


def main() -> int:
    if MAX_PART_SIZE_GIB <= 0:
        raise ValueError("MAX_PART_SIZE_GIB must be greater than zero.")

    max_part_size_bytes = int(
        MAX_PART_SIZE_GIB * (1024**3)
    )

    try:
        create_archives(
            max_part_size_bytes=max_part_size_bytes,
            force=FORCE_REBUILD,
        )
        return 0

    except (
        OSError,
        RuntimeError,
        tarfile.TarError,
        KeyError,
        ValueError,
    ) as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
