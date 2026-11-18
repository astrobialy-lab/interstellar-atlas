from __future__ import annotations

import json
import re
import shlex
import subprocess
import sys
import tarfile
import time
from pathlib import Path

# Assumes this script is saved in <UnityProject>/Tools/Builds/Install/.
PROJECT_DIR = Path(__file__).resolve().parents[3]

ADB = Path(
    r"C:\Program Files\Unity\Hub\Editor\6000.4.12f1\Editor\Data"
    r"\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
)
APK = PROJECT_DIR / "Builds" / "Android" / "Interstellar Atlas-release.apk"

# Output created by the Android TAR builder.
ASSETS_DIR = PROJECT_DIR / "Builds" / "Android" / "RequiredAssets"
ARCHIVE_PREFIX = "Data_AndroidTransfer_"
MANIFEST_NAME = "Data_AndroidTransfer_manifest.json"
MANIFEST_PATH = ASSETS_DIR / MANIFEST_NAME

PROJECT_SETTINGS = PROJECT_DIR / "ProjectSettings" / "ProjectSettings.asset"
ANDROID_OBB_ROOT = "/sdcard/Android/obb"
MANIFEST_VERSION = 3
TRANSFER_ATTEMPTS = 3
RETRY_DELAY_SECONDS = 5

def run(args: list[str | Path], *, capture: bool = False, check: bool = True):
    """Run a command and report failures."""
    args = [str(arg) for arg in args]
    print(f"\n> {' '.join(args)}", flush=True)

    result = subprocess.run(args, text=True, capture_output=capture)

    if capture and result.stdout:
        print(result.stdout, end="" if result.stdout.endswith("\n") else "\n", flush=True)

    if result.returncode != 0 and check:
        if capture and result.stderr:
            print(result.stderr, file=sys.stderr, flush=True)
        raise RuntimeError(
            f"Command failed (exit code {result.returncode}): {' '.join(args)}"
        )

    return result


def adb_shell(adb_args: list[str], command: str, *, capture: bool = False, check: bool = True):
    return run(adb_args + ["shell", command], capture=capture, check=check)


def shell_quote(value: str | Path) -> str:
    return shlex.quote(str(value))


def remote_file_size(adb_args: list[str], path: str) -> int | None:
    result = adb_shell(
        adb_args,
        f"stat -c '%s' {shell_quote(path)}",
        capture=True,
        check=False,
    )
    if result.returncode != 0:
        return None

    match = re.search(r"(?m)^\s*(\d+)\s*$", result.stdout)
    return int(match.group(1)) if match else None


def format_gib(value: int) -> str:
    return f"{value / (1024**3):.2f} GiB"


def get_package_name() -> str:
    """Read the Android application identifier from Unity ProjectSettings.asset."""
    if not PROJECT_SETTINGS.is_file():
        raise FileNotFoundError(f"Unity settings not found: {PROJECT_SETTINGS}")

    lines = PROJECT_SETTINGS.read_text(encoding="utf-8-sig").splitlines()
    for index, line in enumerate(lines):
        if re.match(r"^\s*applicationIdentifier:\s*$", line):
            for candidate in lines[index + 1:index + 20]:
                match = re.match(r"^\s*Android:\s*(\S+)\s*$", candidate)
                if match:
                    package = match.group(1)
                    if not re.fullmatch(
                        r"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+",
                        package,
                    ):
                        raise RuntimeError(f"Invalid Android package name: {package}")
                    return package

    raise RuntimeError(
        f"Could not find the Android application identifier in {PROJECT_SETTINGS}."
    )


def get_device() -> str:
    """Require exactly one connected and authorized ADB device."""
    result = run([ADB, "devices"], capture=True)
    devices: list[str] = []
    unauthorized: list[str] = []

    for line in result.stdout.splitlines()[1:]:
        fields = line.split()
        if len(fields) < 2:
            continue

        serial, state = fields[0], fields[1]
        if state == "device":
            devices.append(serial)
        elif state == "unauthorized":
            unauthorized.append(serial)

    if unauthorized:
        raise RuntimeError(
            "Quest is connected but unauthorized. Put on the headset and accept "
            "the USB debugging prompt."
        )
    if not devices:
        raise RuntimeError(
            "No authorized ADB device found. Check the USB cable, Developer Mode, "
            "and USB debugging."
        )
    if len(devices) > 1:
        raise RuntimeError(
            f"Multiple devices are connected: {devices}. Leave only the Quest connected."
        )

    print(f"Connected device: {devices[0]}")
    return devices[0]


def validate_manifest_and_archives() -> tuple[dict, list[tuple[dict, Path]]]:
    """Validate the dynamic manifest and every local TAR without extracting them."""
    if not MANIFEST_PATH.is_file():
        raise FileNotFoundError(
            f"TAR manifest not found: {MANIFEST_PATH}\nRun the Android TAR builder first."
        )

    try:
        manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Invalid TAR manifest: {MANIFEST_PATH}: {exc}") from exc

    if manifest.get("format_version") != MANIFEST_VERSION:
        raise RuntimeError(
            f"Unsupported TAR manifest version. Expected {MANIFEST_VERSION}; "
            "rebuild the archives with the current Android TAR builder."
        )
    if manifest.get("archive_format") != "uncompressed-tar":
        raise RuntimeError("Manifest does not describe uncompressed TAR files.")
    if not isinstance(manifest.get("transfer_id"), str) or not manifest["transfer_id"]:
        raise RuntimeError("Manifest does not contain a valid transfer ID.")

    parts = manifest.get("parts")
    part_count = manifest.get("part_count")
    if not isinstance(part_count, int) or part_count < 1:
        raise RuntimeError("Manifest contains an invalid part_count.")
    if not isinstance(parts, list) or len(parts) != part_count:
        raise RuntimeError("Manifest part_count does not match its parts list.")

    validated: list[tuple[dict, Path]] = []
    expected_names: set[str] = set()
    total_file_count = 0
    total_archive_bytes = 0

    for index, part in enumerate(parts, start=1):
        if not isinstance(part, dict):
            raise RuntimeError(f"Invalid part record at index {index}.")

        name = part.get("name")
        expected_name = f"{ARCHIVE_PREFIX}{index:04d}.tar"
        if name != expected_name or not re.fullmatch(
            r"Data_AndroidTransfer_\d{4}\.tar", str(name)
        ):
            raise RuntimeError(f"Invalid TAR part name/order in manifest: {name!r}")
        if name in expected_names:
            raise RuntimeError(f"Duplicate TAR part in manifest: {name}")
        expected_names.add(name)

        path = ASSETS_DIR / name
        if not path.is_file():
            raise FileNotFoundError(f"Missing TAR part: {path}")

        size = path.stat().st_size
        if size <= 0 or size != part.get("size_bytes"):
            raise RuntimeError(f"TAR part size does not match manifest: {path}")

        try:
            with tarfile.open(path, mode="r:") as tar:
                if tar.next() is None:
                    raise RuntimeError(f"TAR part appears to be empty/invalid: {path}")
        except (tarfile.TarError, OSError) as exc:
            raise RuntimeError(f"TAR part is invalid: {path}: {exc}") from exc

        file_count = part.get("file_count")
        if not isinstance(file_count, int) or file_count < 1:
            raise RuntimeError(f"Invalid file count in manifest for {name}.")

        total_file_count += file_count
        total_archive_bytes += size
        validated.append((part, path))

    present_names = {
        path.name
        for path in ASSETS_DIR.glob(f"{ARCHIVE_PREFIX}[0-9][0-9][0-9][0-9].tar")
    }
    if present_names != expected_names:
        raise RuntimeError(
            "Local TAR files do not match the manifest. Rebuild them with FORCE_REBUILD = True."
        )
    if total_file_count != manifest.get("file_count"):
        raise RuntimeError("File counts across TAR parts do not match the manifest.")
    if total_archive_bytes != manifest.get("total_archive_bytes"):
        raise RuntimeError("Total TAR size does not match the manifest.")

    return manifest, validated


def get_remote_transfer_id(adb_args: list[str], remote_manifest_path: str) -> str | None:
    """Read the previously installed manifest to avoid reusing same-size stale TARs."""
    result = adb_shell(
        adb_args,
        f"cat {shell_quote(remote_manifest_path)}",
        capture=True,
        check=False,
    )
    if result.returncode != 0:
        return None

    try:
        manifest = json.loads(result.stdout)
    except json.JSONDecodeError:
        return None

    transfer_id = manifest.get("transfer_id")
    return transfer_id if isinstance(transfer_id, str) else None


def ensure_remote_file(
    adb_args: list[str],
    remote_path: str,
    local_path: Path,
    expected_size: int,
    name: str,
    *,
    allow_reuse: bool,
) -> None:
    """Copy one file to OBB using a temporary name and verify its byte size."""
    remote_upload = remote_path + ".uploading"

    if allow_reuse and remote_file_size(adb_args, remote_path) == expected_size:
        print(f"[{name}] Already present with the expected size and dataset version; skipping.")
        return

    last_error = "unknown transfer error"

    for attempt in range(1, TRANSFER_ATTEMPTS + 1):
        print(f"[{name}] Copy attempt {attempt}/{TRANSFER_ATTEMPTS}.")
        adb_shell(adb_args, f"rm -f {shell_quote(remote_upload)}", check=False)

        try:
            run(adb_args + ["push", local_path, remote_upload])
        except Exception as exc:
            last_error = str(exc)

        uploaded_size = remote_file_size(adb_args, remote_upload)
        if uploaded_size == expected_size:
            adb_shell(
                adb_args,
                f"mv {shell_quote(remote_upload)} {shell_quote(remote_path)}",
            )
            if remote_file_size(adb_args, remote_path) == expected_size:
                return
            last_error = "The uploaded file size changed after renaming."
        else:
            last_error = f"Uploaded size was {uploaded_size}; expected {expected_size}."

        adb_shell(adb_args, f"rm -f {shell_quote(remote_upload)}", check=False)
        if attempt < TRANSFER_ATTEMPTS:
            print(f"[{name}] {last_error} Retrying in {RETRY_DELAY_SECONDS}s.")
            time.sleep(RETRY_DELAY_SECONDS)

    raise RuntimeError(
        f"Could not copy {name} after {TRANSFER_ATTEMPTS} attempts: {last_error}"
    )


def main() -> int:
    if not ADB.is_file():
        raise FileNotFoundError(f"ADB not found: {ADB}")
    if not APK.is_file():
        raise FileNotFoundError(f"APK not found: {APK}")

    manifest, parts = validate_manifest_and_archives()
    package = get_package_name()
    serial = get_device()
    adb_args = [str(ADB), "-s", serial]
    obb_directory = f"{ANDROID_OBB_ROOT}/{package}"

    print(f"Unity project: {PROJECT_DIR}")
    print(f"Package name:  {package}")
    print(f"APK:           {APK}")
    print(f"Assets:        {ASSETS_DIR}")
    print(f"OBB directory: {obb_directory}")
    print(f"TAR parts:     {len(parts)}")
    print(f"Total TAR size: {format_gib(manifest['total_archive_bytes'])}")

    run(adb_args + ["shell", "df", "-h", "/sdcard"], capture=True)

    # Update the app without uninstalling it, preserving its app data.
    run(adb_args + ["install", "-r", APK])
    adb_shell(adb_args, f"mkdir -p {shell_quote(obb_directory)}")

    remote_manifest_path = f"{obb_directory}/{MANIFEST_NAME}"
    same_transfer = (
        get_remote_transfer_id(adb_args, remote_manifest_path)
        == manifest["transfer_id"]
    )

    # Copy each archive unchanged. Reuse existing copies only when the installed
    # manifest identifies this exact dataset version. Unity extracts them later.
    for part, local_path in parts:
        remote_path = f"{obb_directory}/{part['name']}"
        ensure_remote_file(
            adb_args,
            remote_path,
            local_path,
            int(part["size_bytes"]),
            part["name"],
            allow_reuse=same_transfer,
        )

    # Push the manifest last so Unity only sees the new manifest after all TARs exist.
    ensure_remote_file(
        adb_args,
        f"{obb_directory}/{MANIFEST_NAME}",
        MANIFEST_PATH,
        MANIFEST_PATH.stat().st_size,
        MANIFEST_NAME,
        allow_reuse=same_transfer,
    )

    print("\nManual installation completed successfully.")
    print(f"APK installed: {APK.name}")
    print(f"Copied {len(parts)} TAR archive(s) and the manifest to: {obb_directory}")
    print("No archives were extracted or deleted by this script.")
    print("Unity's DataInstaller will extract the archives on the next app launch.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, FileNotFoundError, OSError, tarfile.TarError, ValueError, KeyError) as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)
