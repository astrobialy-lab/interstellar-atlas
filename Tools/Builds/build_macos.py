#!/usr/bin/env python3
"""Build only the macOS Unity Build Profile."""
from pathlib import Path
from typing import Optional
from unity_build_common import build_target

PROJECT_ROOT: Optional[Path] = Path(__file__).resolve().parents[2]
# None = automatically find the macOS profile; otherwise specify its .asset path.
PROFILE_PATH: Optional[Path] = None
# Copies the project-root Data/ folder into Builds/macOS/Data after a successful build.
COPY_DATA_TO_BUILD = True
# Optional per-script Unity.exe override. None uses DEFAULT_UNITY_EXE_PATH in unity_build_common.py.
UNITY_EXE_PATH: Optional[Path] = None

if __name__ == "__main__":
    raise SystemExit(build_target(
        "macos", script_file=__file__, project_root=PROJECT_ROOT,
        unity_exe_path=UNITY_EXE_PATH, profile_path=PROFILE_PATH,
        copy_data_to_build=COPY_DATA_TO_BUILD,
    ))
