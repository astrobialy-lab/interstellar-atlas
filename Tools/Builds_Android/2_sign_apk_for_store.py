#!/usr/bin/env python3
"""Zipalign, release-sign, and verify a Unity-built Meta Quest APK.

Edit the USER SETTINGS section below, then run this script without arguments:

    python Tools/Builds/sign_meta_quest_apk.py

Passwords are prompted securely (or may be supplied through the environment
variables documented below). They are intentionally not stored in this file.
"""
from __future__ import annotations

import getpass
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Optional, Tuple


# =============================================================================
# USER SETTINGS — edit these values
# =============================================================================

# This script is intended to live at <UnityProject>/Tools/Builds/.
PROJECT_ROOT = Path(__file__).resolve().parents[2]

# The unsigned/debug-signed APK produced by your Unity build script.
INPUT_APK = PROJECT_ROOT / "Builds" / "Android" / "Interstellar Atlas.apk"

# Keep this OUTSIDE your Git repository / Unity project.
KEYSTORE_PATH = PROJECT_ROOT.parent / "interstellar-atlas.keystore"
KEY_ALIAS = "interstellar-atlas"

# None creates "<input-name>-release.apk" beside the input APK.
OUTPUT_APK: Optional[Path] = None

# Set this to your Android SDK directory only if automatic detection fails.
# Example: Path(r"C:\Program Files\Unity\Hub\Editor\6000.4.12f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK")
ANDROID_SDK_ROOT_OVERRIDE: Optional[Path] = None

# Meta Quest release APK signing options.
ENABLE_V1_SIGNATURE = True
ENABLE_V2_SIGNATURE = True
REQUIRE_V2_VERIFICATION = True


# =============================================================================
# PASSWORD HANDLING
# =============================================================================

# By default, the password is requested once with hidden terminal input.
# For automated builds, you can set:
#   META_ANDROID_KEYSTORE_PASSWORD
#
# Do NOT put passwords directly in this script or commit them to Git.

KEYSTORE_PASSWORD_ENV = "META_ANDROID_KEYSTORE_PASSWORD"


# =============================================================================
# SDK FALLBACK LOCATIONS
# =============================================================================

UNITY_SDK_FALLBACKS = [
    Path(
        r"C:\Program Files\Unity\Hub\Editor\6000.4.12f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK"
    ),
    Path.home() / "Unity" / "Hub" / "Editor" / "6000.4.12f1" / "Editor" / "Data" / "PlaybackEngines" / "AndroidPlayer" / "SDK",
    Path("/Applications/Unity/Hub/Editor/6000.4.12f1/Unity.app/Contents/PlaybackEngines/AndroidPlayer/SDK"),
]


# =============================================================================
# IMPLEMENTATION
# =============================================================================

def version_key(path: Path) -> Tuple[int, ...]:
    """Sort Android Build Tools directories by numeric version."""
    numbers = re.findall(r"\d+", path.name)
    return tuple(int(value) for value in numbers) if numbers else (0,)


def find_sdk_root(explicit_sdk: Optional[Path]) -> Path:
    candidates = []

    if explicit_sdk is not None:
        candidates.append(explicit_sdk.expanduser())

    for variable in ("ANDROID_SDK_ROOT", "ANDROID_HOME"):
        value = os.environ.get(variable)
        if value:
            candidates.append(Path(value).expanduser())

    candidates.extend(UNITY_SDK_FALLBACKS)

    for candidate in candidates:
        if (candidate / "build-tools").is_dir():
            return candidate.resolve()

    tried = "\n".join(f"  {path}" for path in candidates) or "  (no SDK locations found)"

    raise FileNotFoundError(
        "Could not find an Android SDK containing build-tools.\n"
        "Set ANDROID_SDK_ROOT / ANDROID_HOME, or edit ANDROID_SDK_ROOT_OVERRIDE "
        "near the top of this script.\n"
        f"Locations checked:\n{tried}"
    )


def find_build_tools(sdk_root: Path) -> Tuple[Path, Path]:
    build_tools_root = sdk_root / "build-tools"

    versions = sorted(
        (p for p in build_tools_root.iterdir() if p.is_dir()),
        key=version_key,
        reverse=True,
    )

    if os.name == "nt":
        apksigner_names = ("apksigner.bat", "apksigner")
        zipalign_names = ("zipalign.exe", "zipalign")
    else:
        apksigner_names = ("apksigner", "apksigner.bat")
        zipalign_names = ("zipalign", "zipalign.exe")

    for version_dir in versions:
        apksigner = next(
            (version_dir / name for name in apksigner_names if (version_dir / name).is_file()),
            None,
        )

        zipalign = next(
            (version_dir / name for name in zipalign_names if (version_dir / name).is_file()),
            None,
        )

        if apksigner and zipalign:
            return apksigner, zipalign

    raise FileNotFoundError(
        f"Could not find apksigner and zipalign under {build_tools_root}. "
        "Install Android SDK Build Tools using Unity Hub or Android Studio."
    )


def run_tool(
    command: list[str],
    *,
    env: dict[str, str],
    description: str,
) -> subprocess.CompletedProcess[str]:

    print(f"\n{description}...")

    try:
        return subprocess.run(
            command,
            check=True,
            text=True,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
        )

    except subprocess.CalledProcessError as exc:
        output = exc.stdout or ""

        if output:
            print(output)

        raise RuntimeError(
            f"{description} failed (exit code {exc.returncode})."
        ) from exc


def get_passwords() -> Tuple[str, str]:
    password = os.environ.get(KEYSTORE_PASSWORD_ENV)

    if password is None:
        password = getpass.getpass("Keystore / key password: ")

    if not password:
        raise ValueError("Password cannot be empty.")

    return password, password


def main() -> int:
    apk = INPUT_APK.expanduser().resolve()
    keystore = KEYSTORE_PATH.expanduser().resolve()

    output = (
        OUTPUT_APK.expanduser().resolve()
        if OUTPUT_APK is not None
        else apk.with_name(f"{apk.stem}-release{apk.suffix}")
    )

    if not apk.is_file():
        print(f"ERROR: input APK not found: {apk}", file=sys.stderr)
        return 2

    if apk.suffix.lower() != ".apk":
        print(f"ERROR: input file must have an .apk extension: {apk}", file=sys.stderr)
        return 2

    if not keystore.is_file():
        print(f"ERROR: keystore not found: {keystore}", file=sys.stderr)
        return 2

    if not KEY_ALIAS.strip():
        print("ERROR: KEY_ALIAS cannot be empty.", file=sys.stderr)
        return 2

    if output.suffix.lower() != ".apk":
        print(f"ERROR: output file must have an .apk extension: {output}", file=sys.stderr)
        return 2

    if output == apk:
        print(
            "ERROR: output APK must differ from input APK; "
            "keep the original build intact.",
            file=sys.stderr,
        )
        return 2

    try:
        sdk_root = find_sdk_root(ANDROID_SDK_ROOT_OVERRIDE)
        apksigner, zipalign = find_build_tools(sdk_root)
        store_password, key_password = get_passwords()

    except (FileNotFoundError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    print("\nMeta Quest APK release signing")
    print(f"Input APK:  {apk}")
    print(f"Keystore:   {keystore}")
    print(f"Key alias:  {KEY_ALIAS}")
    print(f"Output APK: {output}")
    print(f"Build tools:{sdk_root / 'build-tools'}")

    # Passwords are provided to apksigner through its environment-variable
    # interface, not command-line arguments. They are not written to disk.
    child_env = os.environ.copy()

    child_env["META_SIGNING_STORE_PASS"] = store_password
    child_env["META_SIGNING_KEY_PASS"] = key_password

    output.parent.mkdir(parents=True, exist_ok=True)

    try:
        # Align first: any change after apksigner would invalidate the v2 signature.
        with tempfile.TemporaryDirectory(
            prefix=".quest-apk-sign-",
            dir=output.parent,
        ) as temp_name:

            temp_dir = Path(temp_name)

            aligned_apk = temp_dir / "aligned.apk"
            signed_apk = temp_dir / "signed.apk"

            align_result = run_tool(
                [
                    str(zipalign),
                    "-f",
                    "-v",
                    "4",
                    str(apk),
                    str(aligned_apk),
                ],
                env=child_env,
                description="Aligning APK",
            )

            if align_result.stdout.strip():
                print(align_result.stdout.strip())

            sign_command = [
                str(apksigner),
                "sign",
                "--ks", str(keystore),
                "--ks-key-alias", KEY_ALIAS,
                "--ks-pass", "env:META_SIGNING_STORE_PASS",
                "--key-pass", "env:META_SIGNING_KEY_PASS",
                "--v1-signing-enabled",
                "true" if ENABLE_V1_SIGNATURE else "false",
                "--v2-signing-enabled",
                "true" if ENABLE_V2_SIGNATURE else "false",
                "--out", str(signed_apk),
                str(aligned_apk),
            ]

            sign_result = run_tool(
                sign_command,
                env=child_env,
                description="Signing APK with release keystore",
            )

            if sign_result.stdout.strip():
                print(sign_result.stdout.strip())

            # Verify the APK using v2 on Android API 24–27.
            # This explicitly tests the v2 signing scheme.
            verify_result = run_tool(
                [
                    str(apksigner),
                    "verify",
                    "--verbose",
                    "--print-certs",
                    "--min-sdk-version", "24",
                    "--max-sdk-version", "27",
                    str(signed_apk),
                ],
                env=child_env,
                description="Verifying APK v2 signature",
            )

            verify_text = verify_result.stdout
            print(verify_text)

            if not re.search(
                r"Verified using v2 scheme \(APK Signature Scheme v2\):\s*true",
                verify_text,
                re.IGNORECASE,
            ):
                raise RuntimeError(
                    "APK Signature Scheme v2 verification failed."
                )

            # Also verify against the APK's actual supported Android versions.
            run_tool(
                [
                    str(apksigner),
                    "verify",
                    "--verbose",
                    str(signed_apk),
                ],
                env=child_env,
                description="Verifying APK runtime compatibility",
            )

            verify_text = verify_result.stdout
            print(verify_text.strip())

            if REQUIRE_V2_VERIFICATION and not re.search(
                r"Verified using v2 scheme \(APK Signature Scheme v2\):\s*true",
                verify_text,
                re.IGNORECASE,
            ):
                raise RuntimeError(
                    "Signature verification did not confirm APK Signature "
                    "Scheme v2. The output APK will not be installed as "
                    "the release output."
                )

            # Verify alignment after signing too.
            zipalign_check = run_tool(
                [
                    str(zipalign),
                    "-c",
                    "-v",
                    "4",
                    str(signed_apk),
                ],
                env=child_env,
                description="Checking final APK alignment",
            )

            if zipalign_check.stdout.strip():
                print(zipalign_check.stdout.strip())

            # Replace output only after signing and verification both succeed.
            os.replace(signed_apk, output)

        print(f"\nSUCCESS: release-signed APK written to:\n{output}")
        return 0

    except (RuntimeError, OSError) as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        return 1

    finally:
        # Best-effort cleanup of Python references and child process environment.
        store_password = ""
        key_password = ""

        child_env.pop("META_SIGNING_STORE_PASS", None)
        child_env.pop("META_SIGNING_KEY_PASS", None)


if __name__ == "__main__":
    raise SystemExit(main())