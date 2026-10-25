#!/usr/bin/env python3
"""Run all four platform build scripts one by one, collecting the results."""
from __future__ import annotations
import subprocess
import sys
from pathlib import Path

# Keep this script and the four platform scripts beside Assets/ and ProjectSettings/.
SCRIPT_DIR = Path(__file__).resolve().parent
BUILD_SCRIPTS = [
    "build_windows.py",
    "build_linux.py",
    "build_macos.py",
    "build_meta_quest.py",
]
# False means continue to the remaining platforms if one build fails.
STOP_AFTER_FIRST_FAILURE = False


def main() -> int:
    results = []
    for script_name in BUILD_SCRIPTS:
        script_path = SCRIPT_DIR / script_name
        if not script_path.is_file():
            print(f"ERROR: missing build script: {script_path}")
            results.append((script_name, False))
            if STOP_AFTER_FIRST_FAILURE:
                break
            continue

        print("\n" + "#" * 72)
        print(f"Running {script_name}")
        print("#" * 72, flush=True)
        try:
            result = subprocess.run([sys.executable, str(script_path)], cwd=SCRIPT_DIR, check=False)
            success = result.returncode == 0
        except OSError as exc:
            print(f"ERROR launching {script_name}: {exc}")
            success = False

        results.append((script_name, success))
        if not success and STOP_AFTER_FIRST_FAILURE:
            break

    print("\nALL-BUILDS SUMMARY")
    for script_name, success in results:
        print(f"  {'OK' if success else 'FAILED'}  {script_name}")
    return 0 if results and all(success for _, success in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
