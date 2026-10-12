from __future__ import annotations

import concurrent.futures
import json
import multiprocessing
import re
import shlex
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid
from pathlib import Path, PurePosixPath

# Assumes this script is saved in <UnityProject>/Tools/Install/.
PROJECT_DIR = Path(__file__).resolve().parents[2]
ADB = Path(
    r"C:\Program Files\Unity\Hub\Editor\6000.4.9f1\Editor\Data"
    r"\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
)
APK = PROJECT_DIR / "space-simulation.apk"
DATA_DIR = PROJECT_DIR / "Data"
PROJECT_SETTINGS = PROJECT_DIR / "ProjectSettings" / "ProjectSettings.asset"
MANIFEST_PATH = PROJECT_DIR / "Data_QuestTransfer_manifest.json"
ARCHIVE_PREFIX = "Data_QuestTransfer_"

ANDROID_OBB_ROOT = "/sdcard/Android/obb"
STAGING_DIR_NAME = ".Data_QuestExtracting"
STAGING_SIGNATURE_NAME = ".Data_QuestExtracting.signature"
COMPLETE_MARKER_NAME = ".SpaceSimulationDataInstallComplete"
COMPLETE_MARKER_VALUE = "installed_by=2_install_apk_to_device_obb.py"
MANIFEST_VERSION = 2
MAX_PROCESSES = 5
TRANSFER_ATTEMPTS = 3
EXTRACTION_ATTEMPTS = 3
RETRY_DELAY_SECONDS = 2


def safe_print(*args, **kwargs) -> None:
    print(*args, **kwargs)


def run(args, *, capture: bool = False, check: bool = True):
    """Run a command and include useful output in failures."""
    args = [str(arg) for arg in args]
    prefix = multiprocessing.current_process().name
    print(f"\n> [{prefix}] {' '.join(args)}", flush=True)

    result = subprocess.run(args, text=True, capture_output=capture)
    if capture and result.stdout:
        print(result.stdout, end="" if result.stdout.endswith("\n") else "\n", flush=True)
    if result.returncode != 0 and check:
        if capture and result.stderr:
            print(result.stderr, file=sys.stderr, flush=True)
        raise RuntimeError(f"Command failed (exit code {result.returncode}): {' '.join(args)}")
    return result


def adb_shell(adb_args: list[str], command: str, *, capture: bool = False, check: bool = True):
    return run(adb_args + ["shell", command], capture=capture, check=check)


def shell_quote(value: str | Path) -> str:
    return shlex.quote(str(value))


def remote_exists(adb_args: list[str], path: str, kind: str = "e") -> bool:
    result = adb_shell(adb_args, f"test -{kind} {shell_quote(path)}", capture=True, check=False)
    return result.returncode == 0


def remote_text(adb_args: list[str], path: str) -> str | None:
    result = adb_shell(adb_args, f"cat {shell_quote(path)}", capture=True, check=False)
    if result.returncode != 0:
        return None
    return result.stdout.strip()


def write_remote_text(adb_args: list[str], path: str, value: str) -> None:
    adb_shell(adb_args, f"printf '%s\\n' {shell_quote(value)} > {shell_quote(path)}")


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
    """Read Android applicationIdentifier from Unity ProjectSettings.asset."""
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
    raise RuntimeError(f"Could not find the Android application identifier in {PROJECT_SETTINGS}.")


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
            "Quest is connected but unauthorized. Put on the headset and accept the USB debugging prompt."
        )
    if not devices:
        raise RuntimeError("No authorized ADB device found. Check the USB cable, Developer Mode, and USB debugging.")
    if len(devices) > 1:
        raise RuntimeError(f"Multiple devices are connected: {devices}. Leave only the Quest connected.")
    safe_print(f"Connected device: {devices[0]}")
    return devices[0]


def validate_manifest_and_parts() -> tuple[dict, list[tuple[dict, Path]]]:
    """Validate the manifest, local sizes, and the first TAR header of each part."""
    if not MANIFEST_PATH.is_file():
        raise FileNotFoundError(f"TAR manifest not found: {MANIFEST_PATH}\nRun 1_create_data_tar.py first.")
    try:
        manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Invalid TAR manifest: {MANIFEST_PATH}: {exc}") from exc

    if manifest.get("format_version") != MANIFEST_VERSION:
        raise RuntimeError("Unsupported TAR manifest version. Recreate the archives with script 1 --force.")
    if manifest.get("archive_format") != "uncompressed-tar":
        raise RuntimeError("Manifest does not describe uncompressed TAR files.")
    if manifest.get("part_count") != 20 or not isinstance(manifest.get("transfer_id"), str):
        raise RuntimeError("Manifest must describe 20 TAR parts and contain a transfer ID.")

    parts = manifest.get("parts")
    if not isinstance(parts, list) or len(parts) != 20:
        raise RuntimeError("TAR manifest does not contain exactly 20 TAR parts.")

    validated: list[tuple[dict, Path]] = []
    expected_names: set[str] = set()
    total_file_count = 0
    total_archive_bytes = 0

    for index, part in enumerate(parts, start=1):
        if not isinstance(part, dict):
            raise RuntimeError(f"Invalid part record at index {index}.")
        name = part.get("name")
        expected_name = f"{ARCHIVE_PREFIX}{index:04d}.tar"
        if name != expected_name or not re.fullmatch(r"Data_QuestTransfer_\d{4}\.tar", str(name)):
            raise RuntimeError(f"Invalid TAR part name/order in manifest: {name!r}")
        if name in expected_names:
            raise RuntimeError(f"Duplicate TAR part in manifest: {name}")
        expected_names.add(name)

        path = PROJECT_DIR / name
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
        if not isinstance(file_count, int) or file_count < 0:
            raise RuntimeError(f"Invalid file count in manifest for {name}.")
        total_file_count += file_count
        total_archive_bytes += size
        validated.append((part, path))

    present_names = {path.name for path in PROJECT_DIR.glob(f"{ARCHIVE_PREFIX}[0-9][0-9][0-9][0-9].tar")}
    if present_names != expected_names:
        raise RuntimeError("Local TAR files do not match the manifest. Rebuild them with script 1 --force.")
    if total_file_count != manifest.get("file_count"):
        raise RuntimeError("File counts across TAR parts do not match the manifest.")
    if total_archive_bytes != manifest.get("total_archive_bytes"):
        raise RuntimeError("Total TAR size does not match the manifest.")

    return manifest, validated


def test_device_tar_extraction(adb_args: list[str], transfer_root: str) -> None:
    """Test a tiny uncompressed TAR before transferring the large shards."""
    result = adb_shell(adb_args, "command -v tar", capture=True, check=False)
    if result.returncode != 0 or not result.stdout.strip():
        raise RuntimeError("The Quest shell does not expose a 'tar' command.")

    token = uuid.uuid4().hex[:10]
    probe_name = f".quest_tar_probe_{token}"
    remote_probe_archive = f"{transfer_root}/{probe_name}.tar"
    remote_probe_dir = f"{transfer_root}/{probe_name}"

    with tempfile.TemporaryDirectory(prefix="quest_tar_probe_") as temp_dir:
        temp_root = Path(temp_dir)
        probe_root = temp_root / probe_name
        nested = probe_root / "nested"
        nested.mkdir(parents=True)
        (nested / "probe.txt").write_text("tar extraction works\n", encoding="utf-8")
        local_probe_archive = temp_root / f"{probe_name}.tar"
        with tarfile.open(local_probe_archive, mode="w") as tar:
            tar.add(probe_root, arcname=probe_name)

        try:
            run(adb_args + ["push", str(local_probe_archive), remote_probe_archive])
            safe_print("Testing TAR extraction into OBB storage...")
            adb_shell(adb_args, f"tar -xf {shell_quote(remote_probe_archive)} -C {shell_quote(transfer_root)}")
            expected_file = f"{remote_probe_dir}/nested/probe.txt"
            if not remote_exists(adb_args, expected_file, "f"):
                raise RuntimeError(f"TAR returned success but probe file is missing: {expected_file}")
            content_result = adb_shell(adb_args, f"cat {shell_quote(expected_file)}", capture=True, check=False)
            if content_result.returncode != 0 or content_result.stdout.strip() != "tar extraction works":
                raise RuntimeError(
                    "Extracted probe file could not be read or had unexpected contents: "
                    f"{content_result.stdout!r} {content_result.stderr!r}"
                )
            safe_print("TAR extraction test passed.")
        except Exception as exc:
            raise RuntimeError(
                "The tiny TAR extraction test failed. No real data part was uploaded. "
                f"Details: {exc}"
            ) from exc
        finally:
            cleanup = f"rm -rf {shell_quote(remote_probe_dir)}; rm -f {shell_quote(remote_probe_archive)}"
            run(adb_args + ["shell", cleanup], capture=True, check=False)


def ensure_remote_tar(
    adb_args: list[str], remote_tar: str, remote_upload: str, local_path: Path, expected_size: int, name: str
) -> None:
    """Reuse a complete remote TAR; otherwise upload with retries and verify size."""
    remote_size = remote_file_size(adb_args, remote_tar)
    if remote_size == expected_size:
        safe_print(f"[{name}] Reusing existing remote TAR ({format_gib(expected_size)}); no re-upload needed.")
        return
    if remote_size is not None:
        safe_print(f"[{name}] Existing remote TAR has wrong size ({remote_size}); replacing it.", file=sys.stderr)
        run(adb_args + ["shell", f"rm -f {shell_quote(remote_tar)}"], check=False)

    # A previous adb push may have completed before the PC lost the connection.
    upload_size = remote_file_size(adb_args, remote_upload)
    if upload_size == expected_size:
        safe_print(f"[{name}] Recovering a complete .uploading file from the previous run.")
        run(adb_args + ["shell", f"mv {shell_quote(remote_upload)} {shell_quote(remote_tar)}"])
        if remote_file_size(adb_args, remote_tar) == expected_size:
            return
        raise RuntimeError(f"[{name}] Recovered upload failed its post-rename size check.")
    if upload_size is not None:
        run(adb_args + ["shell", f"rm -f {shell_quote(remote_upload)}"], check=False)

    last_error: str | None = None
    for attempt in range(1, TRANSFER_ATTEMPTS + 1):
        safe_print(f"[{name}] Upload attempt {attempt}/{TRANSFER_ATTEMPTS}.")
        run(adb_args + ["shell", f"rm -f {shell_quote(remote_upload)}"], check=False)
        try:
            run(adb_args + ["push", str(local_path), remote_upload])
        except Exception as exc:
            last_error = str(exc)
            safe_print(f"[{name}] adb push failed: {exc}", file=sys.stderr)

        uploaded_size = remote_file_size(adb_args, remote_upload)
        if uploaded_size == expected_size:
            run(adb_args + ["shell", f"mv {shell_quote(remote_upload)} {shell_quote(remote_tar)}"])
            final_size = remote_file_size(adb_args, remote_tar)
            if final_size == expected_size:
                return
            last_error = f"renamed TAR size was {final_size}, expected {expected_size}"
        else:
            last_error = f"uploaded size was {uploaded_size}, expected {expected_size}"
            run(adb_args + ["shell", f"rm -f {shell_quote(remote_upload)}"], check=False)

        if attempt < TRANSFER_ATTEMPTS:
            safe_print(f"[{name}] {last_error}; retrying in {RETRY_DELAY_SECONDS}s.", file=sys.stderr)
            time.sleep(RETRY_DELAY_SECONDS)

    raise RuntimeError(
        f"[{name}] Could not transfer a complete TAR after {TRANSFER_ATTEMPTS} attempts: {last_error}. "
        "The local TAR is retained."
    )


def process_one_part(
    adb_args: list[str],
    transfer_root: str,
    staging_root: str,
    staging_data: str,
    signature: str,
    part: dict,
    local_path: Path,
) -> str:
    """Resume transfer/extraction safely; retain failed remote TARs and retry extraction."""
    name = part["name"]
    expected_size = int(part["size_bytes"])
    remote_tar = f"{transfer_root}/{name}"
    remote_upload = f"{transfer_root}/{name}.uploading"
    extracted_marker = f"{transfer_root}/.{name}.extracted"
    expected_marker = f"{signature}|{name}|{part['file_count']}|{expected_size}"

    if remote_text(adb_args, extracted_marker) == expected_marker and remote_exists(adb_args, staging_data, "d"):
        safe_print(f"[{name}] Already extracted and verified; skipping.")
        run(adb_args + ["shell", f"rm -f {shell_quote(remote_tar)} {shell_quote(remote_upload)}"], check=False)
        return name

    safe_print(
        f"[{name}] Starting: {format_gib(expected_size)} TAR, {part['file_count']:,} files.",
        flush=True,
    )
    ensure_remote_tar(adb_args, remote_tar, remote_upload, local_path, expected_size, name)

    last_error = "unknown extraction error"
    for attempt in range(1, EXTRACTION_ATTEMPTS + 1):
        safe_print(f"[{name}] Extraction attempt {attempt}/{EXTRACTION_ATTEMPTS}...")
        result = adb_shell(
            adb_args,
            f"tar -xf {shell_quote(remote_tar)} -C {shell_quote(staging_root)}",
            capture=True,
            check=False,
        )
        if result.returncode == 0:
            last_error = ""
            break

        detail = "\n".join(x.strip() for x in (result.stdout, result.stderr) if x and x.strip())
        last_error = detail or f"tar exited with code {result.returncode} and no diagnostic output"
        safe_print(f"[{name}] Extraction failed (exit {result.returncode}):\n{last_error}", file=sys.stderr)
        if attempt < EXTRACTION_ATTEMPTS:
            safe_print(f"[{name}] Retrying extraction without re-uploading the TAR.")
            time.sleep(RETRY_DELAY_SECONDS)

    if last_error:
        raise RuntimeError(
            f"[{name}] Extraction failed after {EXTRACTION_ATTEMPTS} attempts. "
            f"Remote TAR retained at {remote_tar}; staging data and other completed chunks are preserved. "
            f"Last error: {last_error}"
        )

    if not remote_exists(adb_args, staging_data, "d"):
        raise RuntimeError(f"[{name}] Extraction reported success but staging Data is missing; remote TAR retained.")

    sample_rel = part.get("first_file")
    if sample_rel:
        sample_remote = f"{staging_data}/{PurePosixPath(sample_rel).as_posix()}"
        if not remote_exists(adb_args, sample_remote, "f"):
            raise RuntimeError(
                f"[{name}] Extracted sample file is missing: {sample_remote}. Remote TAR retained for retry."
            )

    # Record success only after extraction and the sample check succeed.
    write_remote_text(adb_args, extracted_marker, expected_marker)
    run(adb_args + ["shell", f"rm -f {shell_quote(remote_tar)} {shell_quote(remote_upload)}"])
    safe_print(f"[{name}] Extracted and verified; remote TAR deleted.", flush=True)
    return name


def main() -> int:
    if not ADB.is_file():
        raise FileNotFoundError(f"ADB not found: {ADB}")
    if not APK.is_file():
        raise FileNotFoundError(f"APK not found: {APK}")

    package = get_package_name()
    serial = get_device()
    adb_args = [str(ADB), "-s", serial]

    obb_dir = f"{ANDROID_OBB_ROOT}/{package}"
    app_data = f"{obb_dir}/Data"
    staging_root = f"{obb_dir}/{STAGING_DIR_NAME}"
    staging_data = f"{staging_root}/Data"
    stage_signature_path = f"{obb_dir}/{STAGING_SIGNATURE_NAME}"
    completion_marker = f"{obb_dir}/{COMPLETE_MARKER_NAME}"

    safe_print(f"Unity project: {PROJECT_DIR}")
    safe_print(f"Package name:  {package}")
    safe_print(f"APK:           {APK}")
    safe_print(f"Transfer worker processes: {MAX_PROCESSES}")
    safe_print(f"Automatic upload retries:  {TRANSFER_ATTEMPTS}")
    safe_print(f"Automatic extract retries: {EXTRACTION_ATTEMPTS}")
    safe_print(f"OBB directory: {obb_dir}")
    safe_print(f"Final Data:    {app_data}")

    run(adb_args + ["shell", "df", "-h", "/sdcard"], capture=True)
    # Reinstall/update without uninstalling, preserving the app's external files.
    run(adb_args + ["install", "-r", str(APK)])
    run(adb_args + ["shell", "mkdir", "-p", obb_dir])

    # Only treat an existing final folder as complete if our completion marker exists.
    if remote_exists(adb_args, app_data, "d"):
        marker = remote_text(adb_args, completion_marker)
        if marker == COMPLETE_MARKER_VALUE:
            safe_print("\nData is already installed and has the completion marker; skipping transfer.")
            return 0
        raise RuntimeError(
            f"A final Data folder already exists at {app_data}, but the completion marker is missing or invalid. "
            "To protect existing files, the script will not overwrite it automatically. "
            "Your incomplete transfer staging folder, if any, has not been deleted."
        )

    manifest, parts = validate_manifest_and_parts()
    signature = str(manifest["transfer_id"])

    safe_print(f"TAR parts:      {len(parts)}")
    safe_print(f"Source files:   {manifest['file_count']:,}")
    safe_print(f"Total TAR size: {format_gib(manifest['total_archive_bytes'])}")

    test_device_tar_extraction(adb_args, obb_dir)

    previous_signature = remote_text(adb_args, stage_signature_path)
    if remote_exists(adb_args, staging_root, "d") and previous_signature != signature:
        safe_print("Staging data belongs to a different source version; removing that staging folder and its markers.")
        run(adb_args + ["shell", f"rm -rf {shell_quote(staging_root)}"])
        stale_markers = " ".join(
            shell_quote(f"{obb_dir}/.{part['name']}.extracted") for part, _ in parts
        )
        run(adb_args + ["shell", f"rm -f {stale_markers}"], check=False)
        # A stale TAR may also belong to the old transfer. Remove only this script's named part files.
        stale_archives = " ".join(
            item
            for part, _ in parts
            for item in (
                shell_quote(f"{obb_dir}/{part['name']}"),
                shell_quote(f"{obb_dir}/{part['name']}.uploading"),
            )
        )
        run(adb_args + ["shell", f"rm -f {stale_archives}"], check=False)

    run(adb_args + ["shell", f"mkdir -p {shell_quote(staging_root)}"])
    write_remote_text(adb_args, stage_signature_path, signature)

    # Part 1 prepares the directory tree before the remaining parts extract in parallel.
    first_part, first_path = parts[0]
    safe_print("\nProcessing TAR 1 first to prepare the staging directory tree.")
    with concurrent.futures.ProcessPoolExecutor(max_workers=1) as first_executor:
        first_future = first_executor.submit(
            process_one_part,
            adb_args,
            obb_dir,
            staging_root,
            staging_data,
            signature,
            first_part,
            first_path,
        )
        first_future.result()

    remaining = parts[1:]
    safe_print(f"\nProcessing remaining {len(remaining)} TARs with up to {MAX_PROCESSES} worker processes.")
    completed = 0
    failures: list[tuple[str, str]] = []
    with concurrent.futures.ProcessPoolExecutor(max_workers=MAX_PROCESSES) as executor:
        futures = {
            executor.submit(
                process_one_part,
                adb_args,
                obb_dir,
                staging_root,
                staging_data,
                signature,
                part,
                local_path,
            ): part["name"]
            for part, local_path in remaining
        }
        for future in concurrent.futures.as_completed(futures):
            name = futures[future]
            try:
                future.result()
                completed += 1
                safe_print(f"Overall progress: {completed}/{len(remaining)} parallel TAR jobs completed.")
            except Exception as exc:
                failures.append((name, str(exc)))
                safe_print(f"FAILED: {name}: {exc}", file=sys.stderr, flush=True)

    if failures:
        details = "\n".join(f"  {name}: {message}" for name, message in failures)
        raise RuntimeError(
            f"{len(failures)} TAR part(s) failed. No final Data folder was created.\n{details}\n"
            "Run this script again after fixing the cause; verified parts will be skipped, and complete remote TARs "
            "will be reused instead of uploaded again."
        )

    if not remote_exists(adb_args, staging_data, "d"):
        raise RuntimeError("All TAR steps finished but the staged Data folder is missing.")

    if (DATA_DIR / "Gaia" / "index.bin").is_file():
        index_remote = f"{staging_data}/Gaia/index.bin"
        if not remote_exists(adb_args, index_remote, "f"):
            raise RuntimeError(
                f"The staged data is missing Gaia/index.bin. Staging data is retained for diagnosis: {staging_root}"
            )

    # Promote only after every part has a success marker. Both directories are on the same filesystem.
    if remote_exists(adb_args, app_data, "d"):
        raise RuntimeError(f"Refusing to promote: final Data directory unexpectedly exists at {app_data}.")
    run(adb_args + ["shell", f"mv {shell_quote(staging_data)} {shell_quote(app_data)}"])
    run(adb_args + ["shell", f"rmdir {shell_quote(staging_root)}"], check=False)

    if not remote_exists(adb_args, app_data, "d"):
        raise RuntimeError("Could not verify final Data directory. Local TAR parts and manifest are retained.")

    write_remote_text(adb_args, completion_marker, COMPLETE_MARKER_VALUE)
    run(adb_args + ["shell", f"rm -f {shell_quote(stage_signature_path)}"], check=False)

    # Do not delete markers/TARs until final Data and its completion marker are confirmed.
    if remote_text(adb_args, completion_marker) != COMPLETE_MARKER_VALUE:
        raise RuntimeError(
            "Final Data was moved into place, but the completion marker could not be verified. "
            "The installed data was left in place; rerunning will report this state rather than deleting it."
        )

    stale_remote_files = " ".join(
        item
        for part, _ in parts
        for item in (
            shell_quote(f"{obb_dir}/{part['name']}"),
            shell_quote(f"{obb_dir}/{part['name']}.uploading"),
            shell_quote(f"{obb_dir}/.{part['name']}.extracted"),
        )
    )
    run(adb_args + ["shell", f"rm -f {stale_remote_files}"], check=False)

    safe_print("\nDeployment completed successfully.")
    safe_print(f"Runtime data: {app_data}")
    safe_print("Verified remote TARs were deleted only after successful extraction and final promotion.")
    safe_print("Local TAR parts and the manifest are retained for future reuse.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, FileNotFoundError, OSError, tarfile.TarError, ValueError, KeyError) as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)
