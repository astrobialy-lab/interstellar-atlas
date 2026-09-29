from pathlib import Path
import re


# ============================================================
# Configuration
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "DataRaw" / "Gaia"

LEVEL = 6
NPIXELS = 12 * 4**LEVEL

FILENAME_RE = re.compile(
    r"^healpix_(\d{5})\.csv$"
)


# ============================================================
# Main
# ============================================================

def main():

    print("==========================================")
    print("Gaia download validation")
    print("==========================================")
    print()

    print(f"Directory:       {RAW_DIR}")
    print(f"HEALPix level:   {LEVEL}")
    print(f"Expected files:  {NPIXELS:,}")
    print()

    if not RAW_DIR.exists():
        raise FileNotFoundError(
            f"Directory does not exist:\n{RAW_DIR}"
        )

    # --------------------------------------------------------
    # Find existing HEALPix files
    # --------------------------------------------------------

    existing = set()

    for path in RAW_DIR.iterdir():

        match = FILENAME_RE.match(path.name)

        if match:
            healpix = int(match.group(1))

            if 0 <= healpix < NPIXELS:
                existing.add(healpix)

    # --------------------------------------------------------
    # Missing
    # --------------------------------------------------------

    missing = [
        healpix
        for healpix in range(NPIXELS)
        if healpix not in existing
    ]

    # --------------------------------------------------------
    # Results
    # --------------------------------------------------------

    print(
        f"Files found:      {len(existing):,}"
    )

    print(
        f"Files MISSING:    {len(missing):,}"
    )

    print()

    if not missing:

        print("==========================================")
        print("GAIA DOWNLOAD VALIDATION PASSED")
        print("==========================================")

    else:

        print("First 10 missing HEALPix files:")
        print("------------------------------------------")

        for healpix in missing[:10]:

            print(
                f"healpix_{healpix:05d}.csv"
            )

        print()

        print("==========================================")
        print("GAIA DOWNLOAD VALIDATION FAILED")
        print("==========================================")

        print(
            f"MISSING {len(missing):,} "
            f"of {NPIXELS:,} files."
        )


if __name__ == "__main__":
    main()