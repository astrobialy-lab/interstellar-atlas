from pathlib import Path
import csv
import math
import struct
import shutil
from concurrent.futures import ProcessPoolExecutor, as_completed

# Big stars filtering
STAR_MIN_MASS_SOLAR = 2.0

# Dust bounding box, centered on the Sun (Unity origin), in pc.
BOX_HALF_X = 1010.0
BOX_HALF_Y = 410.0
BOX_HALF_Z = 1010.0

MAX_WORKERS = 20

# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "DataRaw" / "Gaia" / "downloaded"
PROCESSED_DIR = PROJECT_ROOT / "DataRaw" / "Gaia"

# Current working data used by the LOD builder.
CHUNKS_DIR = PROCESSED_DIR / "processed"

# New data is built here first.
BUILD_CHUNKS_DIR = PROCESSED_DIR / "processed_building"

# Temporary backup during replacement.
BACKUP_CHUNKS_DIR = PROCESSED_DIR / "processed_previous"


# ============================================================
# Binary star format
# ============================================================

# uint64 source_id
#
# float32 x
# float32 y
# float32 z
#
# float32 absolute_g_mag (mg_gspphot)
# float32 intrinsic BP-RP
#
# float32 Teff
# float32 logg
# float32 metallicity
# float32 radius
#
# float32 A0
# float32 AG
# float32 ABP
# float32 ARP
# float32 E(BP-RP)
#
# uint8 flags

STAR_STRUCT = struct.Struct("<Q16fiB")

assert STAR_STRUCT.size == 77


# ============================================================
# Flags
# ============================================================

HAS_GSPPHOT = 1 << 0


# ============================================================
# Helpers
# ============================================================

def safe_int(value, default=-1):
    if value is None or value == "":
        return default

    try:
        return int(value)
    except (ValueError, TypeError):
        return default

def safe_float(value):
    if value is None or value == "":
        return math.nan

    try:
        return float(value)
    except (ValueError, TypeError):
        return math.nan


def valid_number(value):
    return math.isfinite(value)


def radec_distance_to_xyz(
    ra_deg,
    dec_deg,
    distance_pc
):
    """
    Convert Gaia ICRS RA/Dec into Sun-centered
    Galactic Cartesian coordinates, in parsecs.

    Axes:
        +X: Galactic center (l = 0°, b = 0°)
        +Y: Galactic longitude l = 90°
        +Z: North Galactic Pole (b = 90°)
    """

    ra = math.radians(ra_deg)
    dec = math.radians(dec_deg)

    cos_dec = math.cos(dec)

    # ICRS equatorial Cartesian unit vector
    x_eq = cos_dec * math.cos(ra)
    y_eq = cos_dec * math.sin(ra)
    z_eq = math.sin(dec)

    # Rotate from ICRS equatorial axes to Galactic axes
    x_gal = (
        -0.0548755604162154 * x_eq
        -0.8734370902348850 * y_eq
        -0.4838350155487132 * z_eq
    )

    y_gal = (
        +0.4941094278755837 * x_eq
        -0.4448296299600112 * y_eq
        +0.7469822444972180 * z_eq
    )

    z_gal = (
        -0.8676661490190047 * x_eq
        -0.1980763734312015 * y_eq
        +0.4559837761750669 * z_eq
    )

    # Scale the unit vector by the distance in parsecs
    return (
        distance_pc * x_gal,
        distance_pc * z_gal,
        -distance_pc * y_gal
    )

# ============================================================
# Binary writer
# ============================================================

def pack_star(
    source_id,
    x,
    y,
    z,
    absolute_g_mag,
    bp_rp_intrinsic,
    teff,
    logg,
    metallicity,
    radius,
    a0,
    ag,
    abp,
    arp,
    ebp_rp,
    mass,
    age,
    evolstage,
    flags,
):
    return STAR_STRUCT.pack(
        source_id,

        float(x),
        float(y),
        float(z),

        float(absolute_g_mag),
        float(bp_rp_intrinsic),

        float(teff),
        float(logg),
        float(metallicity),
        float(radius),

        float(a0),
        float(ag),
        float(abp),
        float(arp),
        float(ebp_rp),

        float(mass),
        float(age),

        int(evolstage),
        flags,
    )

# ============================================================
# Process one HEALPix CSV
# ============================================================

def process_healpix(csv_file):

    healpix_name = csv_file.stem

    # Example:
    # healpix_00000.csv
    #
    # becomes:
    # healpix_00000.bin
    output_file = (
        BUILD_CHUNKS_DIR /
        f"{healpix_name}.bin"
    )

    total_rows = 0
    written_rows = 0
    skipped = 0

    try:

        with csv_file.open(
            "r",
            encoding="utf-8",
            newline=""
        ) as f:

            reader = csv.DictReader(f)

            with output_file.open("wb") as out:

                for row in reader:

                    total_rows += 1

                    # ------------------------------------------------
                    # Basic Gaia values
                    # ------------------------------------------------

                    source_id = int(row["source_id"])
                    ra = safe_float(row["ra"])
                    dec = safe_float(row["dec"])
                    absolute_g_mag = safe_float(row["mg_gspphot"])
                    bp_rp = safe_float(row["bp_rp"])
                    ebp_rp = safe_float(row["ebpminrp_gspphot"])
                    distance = safe_float(row["distance_gspphot"])
                    mass = safe_float(row["mass_flame"])
                    age = safe_float(row["age_flame"])
                    evolstage = safe_int(row["evolstage_flame"])

                    # ------------------------------------------------
                    # Massive-star filter
                    # ------------------------------------------------

                    if (
                        not valid_number(mass)
                        or mass < STAR_MIN_MASS_SOLAR
                    ):
                        skipped += 1
                        continue

                    # ------------------------------------------------
                    # Brightness
                    # ------------------------------------------------

                    if not valid_number(
                        absolute_g_mag
                    ):
                        skipped += 1
                        continue

                    # ------------------------------------------------
                    # Color
                    #
                    # Require BOTH observed BP-RP and
                    # E(BP-RP) so intrinsic color can be computed.
                    # ------------------------------------------------

                    if not (valid_number(bp_rp) and valid_number(ebp_rp)):
                        skipped += 1
                        continue

                    bp_rp_intrinsic = bp_rp - ebp_rp

                    if not valid_number(bp_rp_intrinsic):
                        skipped += 1
                        continue

                    # ------------------------------------------------
                    # Cartesian position
                    # ------------------------------------------------

                    x, y, z = radec_distance_to_xyz(
                        ra,
                        dec,
                        distance
                    )

                    # ------------------------------------------------
                    # Dust bounding-box filter
                    # ------------------------------------------------

                    if not (
                        -BOX_HALF_X <= x <= BOX_HALF_X
                        and -BOX_HALF_Y <= y <= BOX_HALF_Y
                        and -BOX_HALF_Z <= z <= BOX_HALF_Z
                    ):
                        skipped += 1
                        continue

                    # ------------------------------------------------
                    # GSP-Phot
                    # ------------------------------------------------

                    teff = safe_float(
                        row["teff_gspphot"]
                    )

                    logg = safe_float(
                        row["logg_gspphot"]
                    )

                    metallicity = safe_float(
                        row["mh_gspphot"]
                    )

                    radius = safe_float(
                        row["radius_gspphot"]
                    )

                    a0 = safe_float(
                        row["azero_gspphot"]
                    )

                    ag = safe_float(
                        row["ag_gspphot"]
                    )

                    abp = safe_float(
                        row["abp_gspphot"]
                    )

                    arp = safe_float(
                        row["arp_gspphot"]
                    )

                    # ebp_rp was already read above because it is
                    # needed to calculate the intrinsic color.

                    # ------------------------------------------------
                    # Flags
                    # ------------------------------------------------

                    flags = 0

                    if valid_number(teff):
                        flags |= HAS_GSPPHOT

                    # ------------------------------------------------
                    # Write immediately
                    # ------------------------------------------------

                    data = pack_star(
                        source_id,

                        x,
                        y,
                        z,

                        absolute_g_mag,
                        bp_rp_intrinsic,

                        teff,
                        logg,
                        metallicity,
                        radius,

                        a0,
                        ag,
                        abp,
                        arp,
                        ebp_rp,

                        mass,
                        age,
                        evolstage,

                        flags,
                    )

                    out.write(data)

                    written_rows += 1

    except Exception:

        # Remove incomplete output if this HEALPix failed.
        if output_file.exists():
            output_file.unlink()

        raise

    print(
        f"Finished {csv_file.name}"
        f"  rows: {total_rows:,}"
        f"  written: {written_rows:,}"
        f"  skipped: {skipped:,}"
    )

    return written_rows, skipped


# ============================================================
# Main
# ============================================================

if __name__ == "__main__":

    print("======================================")
    print("Gaia processing")
    print("======================================")
    print()

    csv_files = sorted(
        RAW_DIR.glob("healpix_*.csv")
    )

    if not csv_files:
        raise RuntimeError(
            f"No Gaia CSV files found in:\n"
            f"{RAW_DIR}"
        )

    print(f"Found {len(csv_files):,} HEALPix files.")
    print(f"Output staging directory:\n{BUILD_CHUNKS_DIR}")
    print()

    # --------------------------------------------------------
    # Create a fresh staging directory.
    # Do not modify the existing processed data yet.
    # --------------------------------------------------------

    if BUILD_CHUNKS_DIR.exists():
        shutil.rmtree(BUILD_CHUNKS_DIR)

    BUILD_CHUNKS_DIR.mkdir(
        parents=True,
        exist_ok=True
    )

    total_written = 0
    total_skipped = 0
    failed_files = []

    # --------------------------------------------------------
    # Process all CSV files
    # --------------------------------------------------------

    with ProcessPoolExecutor(
        max_workers=MAX_WORKERS
    ) as executor:

        futures = {
            executor.submit(
                process_healpix,
                csv_file
            ): (i, csv_file)
            for i, csv_file in enumerate(csv_files, 1)
        }

        for future in as_completed(futures):

            i, csv_file = futures[future]

            try:
                written, skipped = future.result()

                total_written += written
                total_skipped += skipped

            except Exception as e:
                print(
                    f"[{i}/{len(csv_files)}] "
                    f"ERROR processing {csv_file.name}: {e}"
                )

                failed_files.append(
                    (csv_file.name, str(e))
                )

    # --------------------------------------------------------
    # Print processing results
    # --------------------------------------------------------

    print()
    print("======================================")
    print("PROCESSING RESULTS")
    print("======================================")
    print(f"Stars written: {total_written:,}")
    print(f"Stars skipped: {total_skipped:,}")
    print(f"Files failed:  {len(failed_files):,}")

    # --------------------------------------------------------
    # Abort if any CSV failed.
    # Preserve the previous processed data.
    # --------------------------------------------------------

    if failed_files:
        shutil.rmtree(
            BUILD_CHUNKS_DIR,
            ignore_errors=True
        )

        print()
        print("FAILED. Previous processed data was preserved.")

        raise RuntimeError(
            f"{len(failed_files):,} CSV files failed. "
            f"First failed file: {failed_files[0][0]}"
        )

    # --------------------------------------------------------
    # Verify that every CSV produced an output file
    # --------------------------------------------------------

    output_files = list(
        BUILD_CHUNKS_DIR.glob("healpix_*.bin")
    )

    if len(output_files) != len(csv_files):
        actual_count = len(output_files)

        shutil.rmtree(
            BUILD_CHUNKS_DIR,
            ignore_errors=True
        )

        raise RuntimeError(
            "Output file count mismatch.\n"
            f"Expected: {len(csv_files):,}\n"
            f"Found: {actual_count:,}\n"
            "Previous processed data was preserved."
        )

    # --------------------------------------------------------
    # Replace existing processed data
    # --------------------------------------------------------

    print()
    print("All files processed successfully.")
    print("Replacing old processed data...")

    if CHUNKS_DIR.exists():

        # Remove any leftover backup first.
        if BACKUP_CHUNKS_DIR.exists():
            shutil.rmtree(BACKUP_CHUNKS_DIR)

        # Keep the current version as a backup.
        CHUNKS_DIR.rename(BACKUP_CHUNKS_DIR)

    try:
        # Install the newly processed data.
        BUILD_CHUNKS_DIR.rename(CHUNKS_DIR)

    except Exception:
        # Restore the backup if installation fails.
        if (
            not CHUNKS_DIR.exists()
            and BACKUP_CHUNKS_DIR.exists()
        ):
            BACKUP_CHUNKS_DIR.rename(CHUNKS_DIR)

        raise

    # --------------------------------------------------------
    # Delete old data only after successful replacement
    # --------------------------------------------------------

    if BACKUP_CHUNKS_DIR.exists():
        shutil.rmtree(BACKUP_CHUNKS_DIR)

    # --------------------------------------------------------
    # Final report
    # --------------------------------------------------------

    print()
    print("======================================")
    print("GAIA PROCESSING COMPLETE")
    print("======================================")
    print(f"CSV files processed: {len(csv_files):,}")
    print(f"Output files:        {len(output_files):,}")
    print(f"Stars written:       {total_written:,}")
    print(f"Stars skipped:       {total_skipped:,}")
    print(f"Final output:        {CHUNKS_DIR}")