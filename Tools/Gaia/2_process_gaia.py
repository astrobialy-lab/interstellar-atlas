from pathlib import Path
import csv
import math
import struct


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "Data" / "Gaia" / "raw"
PROCESSED_DIR = PROJECT_ROOT / "Data" / "Gaia" / "processed"
CHUNKS_DIR = PROCESSED_DIR / "chunks"

CHUNKS_DIR.mkdir(parents=True, exist_ok=True)


# ============================================================
# Distance LOD shells
# ============================================================

# Distances are in parsecs.
#
# Each HEALPix sky region is divided into these radial shells.
#
# Example:
#   shell 0 =   0 - 100 pc
#   shell 1 = 100 - 250 pc
#   shell 2 = 250 - 500 pc
#   ...
#
DISTANCE_BINS = [
    0,
    100,
    250,
    500,
    1_000,
    2_000,
    5_000,
    10_000,
    20_000,
    40_000,
    math.inf,
]

NUM_SHELLS = len(DISTANCE_BINS) - 1


# ============================================================
# Binary star format
# ============================================================

# uint64 source_id
#
# float32 x
# float32 y
# float32 z
#
# float32 G magnitude
# float32 BP-RP
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

STAR_STRUCT = struct.Struct(
    "<QffffffffffffffB"
)


# ============================================================
# Flags
# ============================================================

HAS_GSPPHOT = 1 << 0


# ============================================================
# Helpers
# ============================================================

def safe_float(value):
    if value is None or value == "":
        return math.nan

    try:
        return float(value)
    except ValueError:
        return math.nan


def valid_number(value):
    return math.isfinite(value)


def radec_distance_to_xyz(ra_deg, dec_deg, distance_pc):

    ra = math.radians(ra_deg)
    dec = math.radians(dec_deg)

    cos_dec = math.cos(dec)

    x = distance_pc * cos_dec * math.cos(ra)
    y = distance_pc * cos_dec * math.sin(ra)
    z = distance_pc * math.sin(dec)

    return x, y, z


def get_shell(distance):

    if not valid_number(distance) or distance <= 0:
        return None

    for i in range(NUM_SHELLS):

        lower = DISTANCE_BINS[i]
        upper = DISTANCE_BINS[i + 1]

        if lower <= distance < upper:
            return i

    return None


# ============================================================
# Binary writer
# ============================================================

def pack_star(
    source_id,
    x,
    y,
    z,
    g_mag,
    bp_rp,
    teff,
    logg,
    metallicity,
    radius,
    a0,
    ag,
    abp,
    arp,
    ebp_rp,
    flags,
):

    return STAR_STRUCT.pack(
        source_id,

        float(x),
        float(y),
        float(z),

        float(g_mag),
        float(bp_rp),

        float(teff),
        float(logg),
        float(metallicity),
        float(radius),

        float(a0),
        float(ag),
        float(abp),
        float(arp),
        float(ebp_rp),

        flags,
    )


# ============================================================
# Process one HEALPix CSV
# ============================================================

def process_healpix(csv_file):

    healpix_name = csv_file.stem

    output_dir = CHUNKS_DIR / healpix_name
    output_dir.mkdir(parents=True, exist_ok=True)

    output_files = [
        output_dir / f"shell_{i:02d}.bin"
        for i in range(NUM_SHELLS)
    ]

    # Open all shell files for this HEALPix region.
    files = [
        path.open("wb")
        for path in output_files
    ]

    shell_counts = [0] * NUM_SHELLS

    total_rows = 0
    skipped_position = 0

    try:

        with csv_file.open(
            "r",
            encoding="utf-8",
            newline=""
        ) as f:

            reader = csv.DictReader(f)

            for row in reader:

                total_rows += 1

                # ------------------------------------------------
                # Basic Gaia values
                # ------------------------------------------------

                source_id = int(row["source_id"])

                ra = safe_float(row["ra"])
                dec = safe_float(row["dec"])

                g_mag = safe_float(
                    row["phot_g_mean_mag"]
                )

                bp_rp = safe_float(
                    row["bp_rp"]
                )

                distance = safe_float(
                    row["distance_gspphot"]
                )

                # ------------------------------------------------
                # Position
                # ------------------------------------------------

                if not (
                    valid_number(ra)
                    and valid_number(dec)
                    and valid_number(distance)
                    and distance > 0
                ):

                    skipped_position += 1
                    continue

                # ------------------------------------------------
                # Distance shell
                # ------------------------------------------------

                shell = get_shell(distance)

                if shell is None:
                    skipped_position += 1
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

                ebp_rp = safe_float(
                    row["ebpminrp_gspphot"]
                )

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

                    g_mag,
                    bp_rp,

                    teff,
                    logg,
                    metallicity,
                    radius,

                    a0,
                    ag,
                    abp,
                    arp,
                    ebp_rp,

                    flags,
                )

                files[shell].write(data)

                shell_counts[shell] += 1

    finally:

        for f in files:
            f.close()

    print(f"Finished {csv_file.name}", f"  rows: {total_rows:,}", f"  skipped: {skipped_position:,}")

    return shell_counts


# ============================================================
# Main
# ============================================================

if __name__ == "__main__":

    from concurrent.futures import ThreadPoolExecutor, as_completed

    print("======================================")
    print("Gaia processing")
    print("======================================")
    print()

    csv_files = sorted(
        RAW_DIR.glob("healpix_*.csv")
    )

    if not csv_files:

        raise RuntimeError(
            f"No Gaia CSV files found in:\n{RAW_DIR}"
        )

    print(
        f"Found {len(csv_files):,} HEALPix files."
    )

    print(
        f"Distance shells: {NUM_SHELLS}"
    )

    print()

    total_counts = [0] * NUM_SHELLS

    with ThreadPoolExecutor(max_workers=10) as executor:

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

                counts = future.result()

                for shell, count in enumerate(counts):
                    total_counts[shell] += count

                # print(
                #     f"[{i}/{len(csv_files)}] "
                #     f"finished {csv_file.name}"
                # )

            except Exception as e:

                print(
                    f"[{i}/{len(csv_files)}] "
                    f"ERROR processing {csv_file.name}: {e}"
                )

    print("======================================")
    print("Finished")
    print("======================================")

    print()

    for i, count in enumerate(total_counts):

        print(
            f"shell {i:02d}: "
            f"{DISTANCE_BINS[i]:g} - "
            f"{DISTANCE_BINS[i + 1]:g} pc : "
            f"{count:,}"
        )