#!/usr/bin/env python3
"""Build only the standalone Meta Quest (Android APK) Unity Build Profile."""
from pathlib import Path
from typing import Optional
from unity_build_common import build_target

PROJECT_ROOT: Optional[Path] = Path(__file__).resolve().parents[2]
# None = automatically find a Quest profile, falling back to an Android profile.
PROFILE_PATH: Optional[Path] = None
# Quest data is not copied by this script; keep using your existing data-install process.
COPY_DATA_TO_BUILD = False
# Optional per-script Unity.exe override. None uses DEFAULT_UNITY_EXE_PATH in unity_build_common.py.
UNITY_EXE_PATH: Optional[Path] = None

if __name__ == "__main__":
    raise SystemExit(build_target(
        "quest", script_file=__file__, project_root=PROJECT_ROOT,
        unity_exe_path=UNITY_EXE_PATH, profile_path=PROFILE_PATH,
        copy_data_to_build=COPY_DATA_TO_BUILD,
    ))
