from pathlib import Path
import csv
import math
import struct
import shutil
from concurrent.futures import ProcessPoolExecutor, as_completed


# ============================================================
# Filtering
# ============================================================

# Keep stars with absolute G magnitude <= this value.
# Lower (more negative) magnitudes represent brighter stars.
#
# This limit is applied to both Gaia and Hipparcos stars.
MAX_ABSOLUTE_G_MAG = 2.0

# Maximum spherical distance from the Sun, in pc.
MAX_DISTANCE_PC = 1250.0

MAX_WORKERS = 20


# ============================================================
# Gaia HEALPix configuration
# ============================================================

# MUST match the Gaia downloader:
#
# LEVEL = 3
# NPIXELS = 12 * 4**LEVEL
# DIVISOR = 2 ** (59 - 2 * LEVEL)
#
# Gaia uses NESTED HEALPix ordering.
#
# 12 * 4^3 = 768 partitions.
#
# Hipparcos stars are assigned to these same partitions using RA/Dec.

GAIA_HEALPIX_LEVEL = 3
GAIA_HEALPIX_NSIDE = 2 ** GAIA_HEALPIX_LEVEL
GAIA_HEALPIX_COUNT = 12 * GAIA_HEALPIX_NSIDE ** 2


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

GAIA_RAW_DIR = PROJECT_ROOT / "DataRaw" / "Gaia" / "downloaded"

HIPPARCOS_FILE = (
    PROJECT_ROOT / "DataRaw" / "Hipparcos" / "downloaded" / "hipparcos2.csv"
)

GAIA_HIPPARCOS_CROSSMATCH_FILE = (
    HIPPARCOS_FILE.parent / "gaia_dr3_hipparcos2_best_neighbour.csv"
)

# All processed star data is kept separately from the raw catalogues.
STARS_DIR = PROJECT_ROOT / "DataRaw" / "Stars"
STARS_DIR.mkdir(parents=True, exist_ok=True)

CHUNKS_DIR = STARS_DIR / "processed"
BUILD_CHUNKS_DIR = STARS_DIR / "processed_building"
BACKUP_CHUNKS_DIR = STARS_DIR / "processed_previous"


# ============================================================
# Gaia - Hipparcos cross-match
# ============================================================

def ensure_gaia_hipparcos_crossmatch():
    """Download the official Gaia DR3 Hipparcos-2 cross-match if missing."""

    if (
        GAIA_HIPPARCOS_CROSSMATCH_FILE.exists()
        and GAIA_HIPPARCOS_CROSSMATCH_FILE.stat().st_size > 0
    ):
        return

    print("Downloading Gaia DR3 Hipparcos-2 cross-match...")

    temp_file = GAIA_HIPPARCOS_CROSSMATCH_FILE.with_name(
        GAIA_HIPPARCOS_CROSSMATCH_FILE.stem + ".download.csv"
    )

    if temp_file.exists():
        temp_file.unlink()

    query = """
        SELECT
            original_ext_source_id,
            source_id
        FROM gaiadr3.hipparcos2_best_neighbour
    """

    try:
        from astroquery.gaia import Gaia

        Gaia.launch_job_async(
            query,
            output_file=str(temp_file),
            output_format="csv",
            dump_to_file=True,
            verbose=True,
        )

        if not temp_file.exists() or temp_file.stat().st_size == 0:
            raise RuntimeError(
                "Gaia cross-match download produced no output."
            )

        with temp_file.open(
            "r",
            encoding="utf-8-sig",
            newline="",
        ) as f:

            reader = csv.DictReader(f)

            require_columns(
                reader,
                {
                    "original_ext_source_id",
                    "source_id",
                },
                temp_file,
            )

            row_count = sum(1 for _ in reader)

        if row_count == 0:
            raise RuntimeError("Gaia cross-match table is empty.")

        temp_file.replace(GAIA_HIPPARCOS_CROSSMATCH_FILE)

        print(
            f"Downloaded {row_count:,} Gaia-Hipparcos matches."
        )

    except Exception:
        if temp_file.exists():
            temp_file.unlink()
        raise


def load_gaia_hipparcos_crossmatch():
    """Return a mapping: Hipparcos ID -> Gaia DR3 source_id."""

    ensure_gaia_hipparcos_crossmatch()

    hipparcos_to_gaia = {}

    with GAIA_HIPPARCOS_CROSSMATCH_FILE.open(
        "r",
        encoding="utf-8-sig",
        newline="",
    ) as f:

        reader = csv.DictReader(f)

        require_columns(
            reader,
            {
                "original_ext_source_id",
                "source_id",
            },
            GAIA_HIPPARCOS_CROSSMATCH_FILE,
        )

        for row in reader:

            hip_id = safe_int(
                row.get("original_ext_source_id"),
                default=-1,
            )

            gaia_id = safe_int(
                row.get("source_id"),
                default=0,
            )

            if hip_id <= 0 or gaia_id <= 0:
                raise RuntimeError(
                    "Invalid identifier in Gaia-Hipparcos cross-match: "
                    f"HIP={hip_id}, Gaia={gaia_id}"
                )

            existing = hipparcos_to_gaia.get(hip_id)

            if existing is not None and existing != gaia_id:
                raise RuntimeError(
                    f"Hipparcos ID {hip_id} has multiple best-neighbour "
                    f"Gaia IDs: {existing}, {gaia_id}"
                )

            hipparcos_to_gaia[hip_id] = gaia_id

    if not hipparcos_to_gaia:
        raise RuntimeError("No Gaia-Hipparcos matches were loaded.")

    return hipparcos_to_gaia


def find_hipparcos_ids_already_in_gaia(hipparcos_to_gaia):
    """Find Hipparcos stars whose Gaia counterpart was actually written.

    This is important because a Gaia counterpart might exist in the
    official catalogue but fail our Gaia processing filters.

    Only counterparts present in the processed Gaia binaries count
    as duplicates.
    """

    # Reverse the cross-match:
    # Gaia source_id -> one or more Hipparcos IDs.
    gaia_to_hipparcos = {}

    for hip_id, gaia_id in hipparcos_to_gaia.items():

        gaia_to_hipparcos.setdefault(
            gaia_id, []
        ).append(hip_id)

    # IDs still waiting to be found in the Gaia binaries.
    pending = dict(gaia_to_hipparcos)

    matched_hipparcos_ids = set()

    source_id_struct = struct.Struct("<Q")

    gaia_binary_files = sorted(
        BUILD_CHUNKS_DIR.glob("healpix_*.bin")
    )

    print()
    print("Checking processed Gaia binaries for cross-matches...")

    records_per_block = 8192
    block_size = STAR_STRUCT.size * records_per_block

    for binary_file in gaia_binary_files:

        if not pending:
            break

        if binary_file.stat().st_size % STAR_STRUCT.size != 0:
            raise RuntimeError(
                f"Invalid Gaia binary size: {binary_file.name}"
            )

        with binary_file.open("rb") as f:

            while pending:

                block = f.read(block_size)

                if not block:
                    break

                if len(block) % STAR_STRUCT.size != 0:
                    raise RuntimeError(
                        f"Invalid record boundary in {binary_file.name}"
                    )

                for offset in range(
                    0,
                    len(block),
                    STAR_STRUCT.size,
                ):

                    gaia_id = source_id_struct.unpack_from(
                        block,
                        offset,
                    )[0]

                    hip_ids = pending.pop(gaia_id, None)

                    if hip_ids is not None:
                        matched_hipparcos_ids.update(hip_ids)

    print(
        f"Matched Hipparcos IDs present in processed Gaia: "
        f"{len(matched_hipparcos_ids):,}"
    )

    return matched_hipparcos_ids


# ============================================================
# Binary star format
# ============================================================

# Keep this layout compatible with the existing Unity loader.
#
# uint64 source_id
#   Gaia source_id for Gaia stars; 0 means Hipparcos.
#
# float32 x, y, z
# float32 absolute_g_mag
# float32 bp_rp_intrinsic
# float32 Teff, logg, metallicity, radius
# float32 A0, AG, ABP, ARP, E(BP-RP)
# float32 mass, age
# int32 evolstage
# uint8 flags
#
# For Hipparcos:
#   source_id = 0
#
# Both catalogues use exactly the same 77-byte record format.

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
    except (ValueError, TypeError, OverflowError):
        return default


def safe_float(value):
    if value is None or value == "":
        return math.nan

    try:
        return float(value)
    except (ValueError, TypeError, OverflowError):
        return math.nan


def valid_number(value):
    return math.isfinite(value)


def require_columns(reader, required, csv_file):
    actual = set(reader.fieldnames or [])
    missing = set(required) - actual

    if missing:
        raise RuntimeError(
            f"{csv_file.name} is missing required columns: {sorted(missing)}"
        )


# ============================================================
# HEALPix NESTED coordinate conversion
# ============================================================

def spread_bits(value):
    """Interleave the bits of an integer into even bit positions."""

    result = 0
    bit = 0

    while value:
        result |= (value & 1) << (2 * bit)
        value >>= 1
        bit += 1

    return result


def radec_to_gaia_healpix(ra_deg, dec_deg):
    """Convert ICRS RA/Dec into Gaia's NESTED HEALPix partition.

    Uses the same HEALPix level and ordering as the Gaia downloader.

    Returns:
        Integer HEALPix index in the range [0, GAIA_HEALPIX_COUNT - 1].
    """

    if not (valid_number(ra_deg) and valid_number(dec_deg)):
        raise ValueError("Invalid RA/Dec")

    if not -90.0 <= dec_deg <= 90.0:
        raise ValueError(f"Declination outside valid range: {dec_deg}")

    nside = GAIA_HEALPIX_NSIDE

    # Longitude in four quadrants.
    tt = (ra_deg % 360.0) / 90.0

    # z = cos(theta) = sin(declination).
    z = math.sin(math.radians(dec_deg))
    za = abs(z)

    # --------------------------------------------------------
    # Equatorial region
    # --------------------------------------------------------

    if za <= 2.0 / 3.0:

        temp1 = nside * (0.5 + tt)
        temp2 = nside * z * 0.75

        jp = int(temp1 - temp2)
        jm = int(temp1 + temp2)

        ifp = jp // nside
        ifm = jm // nside

        if ifp == ifm:
            face_num = (ifp & 3) + 4

        elif ifp < ifm:
            face_num = ifp & 3

        else:
            face_num = (ifm & 3) + 8

        ix = jm % nside
        iy = nside - (jp % nside) - 1

    # --------------------------------------------------------
    # Polar regions
    # --------------------------------------------------------

    else:

        ntt = min(3, int(tt))
        tp = tt - ntt

        tmp = nside * math.sqrt(3.0 * (1.0 - za))

        jp = min(nside - 1, int(tp * tmp))
        jm = min(nside - 1, int((1.0 - tp) * tmp))

        if z >= 0:

            face_num = ntt

            ix = nside - jm - 1
            iy = nside - jp - 1

        else:

            face_num = ntt + 8

            ix = jp
            iy = jm

    # --------------------------------------------------------
    # Convert face coordinates to NESTED pixel index
    # --------------------------------------------------------

    pixel_in_face = spread_bits(ix) + (spread_bits(iy) << 1)

    healpix = (
        face_num * nside * nside
        + pixel_in_face
    )

    if not 0 <= healpix < GAIA_HEALPIX_COUNT:
        raise RuntimeError(
            f"Invalid HEALPix index generated: {healpix}"
        )

    return healpix


# ============================================================
# Coordinate conversion
# ============================================================

def radec_distance_to_xyz(ra_deg, dec_deg, distance_pc):
    """Convert ICRS RA/Dec to Sun-centered Galactic Cartesian coordinates.

    Axes:
        +X: Galactic center (l = 0 deg, b = 0 deg)
        +Y: North Galactic Pole (Unity Y)
        +Z: Galactic longitude l = 90 deg (Unity Z)

    The returned coordinate order matches the existing Unity convention:
    (Galactic X, Galactic Z, Galactic Y).
    """

    ra = math.radians(ra_deg)
    dec = math.radians(dec_deg)

    cos_dec = math.cos(dec)

    # ICRS equatorial Cartesian unit vector.
    x_eq = cos_dec * math.cos(ra)
    y_eq = cos_dec * math.sin(ra)
    z_eq = math.sin(dec)

    # Rotate from ICRS equatorial axes to Galactic axes.
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

    return (
        distance_pc * x_gal,
        distance_pc * z_gal,
        distance_pc * y_gal,
    )


# ============================================================
# Binary packing
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
        int(source_id),
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
        int(flags),
    )


# ============================================================
# Gaia processing
# ============================================================

def process_gaia_healpix(csv_file):
    output_file = BUILD_CHUNKS_DIR / f"{csv_file.stem}.bin"

    total_rows = 0
    written_rows = 0
    skipped = 0

    try:

        with csv_file.open("r", encoding="utf-8", newline="") as f:

            reader = csv.DictReader(f)

            require_columns(
                reader,
                {
                    "source_id",
                    "ra",
                    "dec",
                    "parallax",
                    "phot_g_mean_mag",
                    "bp_rp",
                    "teff_gspphot",
                    "logg_gspphot",
                    "mh_gspphot",
                    "distance_gspphot",
                    "radius_gspphot",
                    "azero_gspphot",
                    "ag_gspphot",
                    "abp_gspphot",
                    "arp_gspphot",
                    "ebpminrp_gspphot",
                    "mg_gspphot",
                    "age_flame",
                    "evolstage_flame",
                },
                csv_file,
            )

            with output_file.open("wb") as out:

                for row in reader:

                    total_rows += 1

                    source_id = safe_int(row.get("source_id"), default=0)

                    ra = safe_float(row.get("ra"))
                    dec = safe_float(row.get("dec"))
                    parallax = safe_float(row.get("parallax"))

                    apparent_g_mag = safe_float(row.get("phot_g_mean_mag"))
                    absolute_g_mag = safe_float(row.get("mg_gspphot"))

                    bp_rp = safe_float(row.get("bp_rp"))
                    ebp_rp = safe_float(row.get("ebpminrp_gspphot"))

                    distance = safe_float(row.get("distance_gspphot"))
                    ag = safe_float(row.get("ag_gspphot"))

                    # Mass is optional and is no longer used for filtering.
                    # Keep it in the binary when available.
                    mass = safe_float(row.get("mass_flame"))

                    age = safe_float(row.get("age_flame"))
                    evolstage = safe_int(row.get("evolstage_flame"))

                    def skip_star():
                        nonlocal skipped
                        skipped += 1

                    # A Gaia source ID must be present and nonzero.
                    if source_id <= 0:
                        skip_star()
                        continue

                    # Calculate absolute magnitude if GSP-Phot did not provide it.
                    if not valid_number(absolute_g_mag):

                        if (
                            valid_number(apparent_g_mag)
                            and valid_number(parallax)
                            and parallax > 0
                        ):

                            distance_parallax = 1000.0 / parallax

                            absolute_g_mag = (
                                apparent_g_mag
                                - 5.0 * math.log10(distance_parallax / 10.0)
                            )

                            if valid_number(ag):
                                absolute_g_mag -= ag

                    # Prefer GSP-Phot distance; fall back to inverse parallax.
                    if not valid_number(distance) or distance <= 0:

                        if valid_number(parallax) and parallax > 0:
                            distance = 1000.0 / parallax

                    if not valid_number(distance) or distance <= 0:
                        skip_star()
                        continue

                    if distance > MAX_DISTANCE_PC:
                        skip_star()
                        continue

                    if not (
                        valid_number(ra)
                        and valid_number(dec)
                        and -90.0 <= dec <= 90.0
                    ):
                        skip_star()
                        continue

                    # Require a valid absolute magnitude.
                    if not valid_number(absolute_g_mag):
                        skip_star()
                        continue

                    # Absolute magnitude selection.
                    # Keep stars brighter than or equal to the configured limit.
                    if absolute_g_mag > MAX_ABSOLUTE_G_MAG:
                        skip_star()
                        continue

                    # BP-RP is required.
                    if not valid_number(bp_rp):
                        skip_star()
                        continue

                    # Calculate intrinsic BP-RP when reddening is available.
                    # Otherwise, fall back to the observed BP-RP.
                    if valid_number(ebp_rp):

                        bp_rp_intrinsic = bp_rp - ebp_rp

                    else:

                        bp_rp_intrinsic = bp_rp

                    x, y, z = radec_distance_to_xyz(
                        ra,
                        dec,
                        distance,
                    )

                    teff = safe_float(row.get("teff_gspphot"))
                    logg = safe_float(row.get("logg_gspphot"))
                    metallicity = safe_float(row.get("mh_gspphot"))
                    radius = safe_float(row.get("radius_gspphot"))

                    a0 = safe_float(row.get("azero_gspphot"))
                    abp = safe_float(row.get("abp_gspphot"))
                    arp = safe_float(row.get("arp_gspphot"))

                    flags = 0

                    if valid_number(teff):
                        flags |= HAS_GSPPHOT

                    out.write(
                        pack_star(
                            source_id,
                            x, y, z,
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
                    )

                    written_rows += 1

    except Exception:

        if output_file.exists():
            output_file.unlink()

        raise

    print(
        f"Finished Gaia {csv_file.name}"
        f" | rows: {total_rows:,}"
        f" | written: {written_rows:,}"
        f" | skipped: {skipped:,}"
    )

    return written_rows, skipped


# ============================================================
# Hipparcos photometric transformations
# ============================================================

def hipparcos_to_gaia_photometry(hp_mag, b_v):
    """Approximate Gaia G and BP-RP from Hipparcos Hp and Johnson B-V.

    Polynomial transformations:
        G - Hp = -0.02392 - 0.4069*(B-V)
                 + 0.04569*(B-V)^2 - 0.0452*(B-V)^3

        BP-RP = -0.01612 + 1.274*(B-V) - 0.08143*(B-V)^2

    These are empirical transformations, not Gaia measurements.
    """

    bv2 = b_v * b_v
    bv3 = bv2 * b_v

    g_minus_hp = (
        -0.02392
        - 0.4069 * b_v
        + 0.04569 * bv2
        - 0.0452 * bv3
    )

    bp_rp = -0.01612 + 1.274 * b_v - 0.08143 * bv2

    apparent_g_mag = hp_mag + g_minus_hp

    return apparent_g_mag, bp_rp, g_minus_hp


# ============================================================
# Hipparcos processing and merging
# ============================================================

def process_hipparcos(csv_file, hipparcos_ids_already_in_gaia):
    """Process Hipparcos and append its stars to Gaia HEALPix binaries.

    Each Hipparcos star is assigned to a Gaia HEALPix partition using
    its ICRS RA/Dec.

    No separate Hipparcos binary is created.
    """

    total_rows = 0
    written_rows = 0
    duplicate_skipped = 0
    skipped = 0

    # Buffer records by HEALPix so each binary file is opened only once.
    records_by_healpix = {}

    try:

        with csv_file.open("r", encoding="utf-8", newline="") as f:

            reader = csv.DictReader(f)

            require_columns(
                reader,
                {
                    "hip_id",
                    "ra",
                    "dec",
                    "parallax",
                    "distance_parallax_pc",
                    "hp_mag",
                    "b_v",
                },
                csv_file,
            )

            for row in reader:

                total_rows += 1

                hip_id = safe_int(row.get("hip_id"), default=-1)

                # Skip Hipparcos stars whose Gaia counterparts were
                # successfully included in the processed Gaia binaries.

                if hip_id in hipparcos_ids_already_in_gaia:
                    skipped += 1
                    duplicate_skipped += 1
                    continue

                ra = safe_float(row.get("ra"))
                dec = safe_float(row.get("dec"))

                parallax = safe_float(row.get("parallax"))
                distance = safe_float(row.get("distance_parallax_pc"))

                hp_mag = safe_float(row.get("hp_mag"))
                b_v = safe_float(row.get("b_v"))

                m_hp_abs = safe_float(row.get("m_hp_abs"))

                # The binary source_id field is a uint64.
                # Reserve 0 for Hipparcos.
                source_id = 0

                apparent_g_mag = math.nan
                absolute_g_mag = math.nan
                bp_rp_observed = math.nan
                g_minus_hp = math.nan

                def skip_star():
                    nonlocal skipped
                    skipped += 1

                # ------------------------------------------------
                # Distance
                # ------------------------------------------------

                if not valid_number(distance) or distance <= 0:

                    if valid_number(parallax) and parallax > 0:
                        distance = 1000.0 / parallax

                if not valid_number(distance) or distance <= 0:
                    skip_star()
                    continue

                if distance > MAX_DISTANCE_PC:
                    skip_star()
                    continue

                # ------------------------------------------------
                # Coordinates
                # ------------------------------------------------

                if not (
                    valid_number(ra)
                    and valid_number(dec)
                    and -90.0 <= dec <= 90.0
                ):
                    skip_star()
                    continue

                # ------------------------------------------------
                # Photometry
                # ------------------------------------------------

                if not valid_number(hp_mag):
                    skip_star()
                    continue

                if not valid_number(b_v):
                    skip_star()
                    continue

                # Convert Hipparcos Hp and B-V into approximate Gaia-system
                # apparent G and BP-RP values.

                apparent_g_mag, bp_rp_observed, g_minus_hp = (
                    hipparcos_to_gaia_photometry(hp_mag, b_v)
                )

                if not valid_number(apparent_g_mag):
                    skip_star()
                    continue

                if not valid_number(bp_rp_observed):
                    skip_star()
                    continue

                # ------------------------------------------------
                # Absolute magnitude
                # ------------------------------------------------

                # Prefer the absolute Hp magnitude supplied by the downloader.
                # Fall back to calculating it from Hp and parallax distance.

                if not valid_number(m_hp_abs):

                    m_hp_abs = (
                        hp_mag
                        - 5.0 * math.log10(distance / 10.0)
                    )

                # M_G = M_Hp + (G - Hp).
                # No extinction correction is available in this dataset.

                absolute_g_mag = m_hp_abs + g_minus_hp

                if not valid_number(absolute_g_mag):
                    skip_star()
                    continue

                # ------------------------------------------------
                # Absolute magnitude selection
                # ------------------------------------------------

                if absolute_g_mag > MAX_ABSOLUTE_G_MAG:
                    skip_star()
                    continue

                # ------------------------------------------------
                # Determine Gaia HEALPix partition
                # ------------------------------------------------

                healpix = radec_to_gaia_healpix(ra, dec)

                # ------------------------------------------------
                # Calculate Unity coordinates
                # ------------------------------------------------

                x, y, z = radec_distance_to_xyz(
                    ra,
                    dec,
                    distance,
                )

                # ------------------------------------------------
                # Missing Hipparcos physical parameters
                # ------------------------------------------------

                # Hipparcos has no GSP-Phot/FLAME measurements or extinction
                # estimates. Store NaN for those values.

                nan = math.nan

                # The transformed color is observed, not intrinsic.
                bp_rp_intrinsic_proxy = bp_rp_observed

                flags = 0

                # ------------------------------------------------
                # Pack the record
                # ------------------------------------------------

                record = pack_star(
                    source_id,
                    x, y, z,
                    absolute_g_mag,
                    bp_rp_intrinsic_proxy,
                    nan,       # Teff
                    nan,       # logg
                    nan,       # metallicity
                    nan,       # radius
                    nan,       # A0
                    nan,       # AG
                    nan,       # ABP
                    nan,       # ARP
                    nan,       # E(BP-RP)
                    nan,       # mass
                    nan,       # age
                    -1,        # evolstage unavailable
                    flags,
                )

                # Store in the matching Gaia HEALPix partition.
                records_by_healpix.setdefault(healpix, []).append(record)

                written_rows += 1

        # --------------------------------------------------------
        # Append Hipparcos records to Gaia binaries
        # --------------------------------------------------------

        print()
        print("Merging Hipparcos stars into Gaia HEALPix binaries...")

        for healpix, records in sorted(records_by_healpix.items()):

            output_file = (
                BUILD_CHUNKS_DIR
                / f"healpix_{healpix:05d}.bin"
            )

            # Append to an existing Gaia binary.
            # Create the file if its Gaia CSV was not downloaded.
            mode = "ab" if output_file.exists() else "wb"

            with output_file.open(mode) as out:
                out.writelines(records)

            print(
                f"  HEALPix {healpix:05d}: "
                f"merged {len(records):,} Hipparcos stars"
            )

    except Exception:
        raise

    touched_healpix = sorted(records_by_healpix.keys())

    print(
        f"Finished Hipparcos {csv_file.name}"
        f" | rows: {total_rows:,}"
        f" | written: {written_rows:,}"
        f" | skipped: {skipped:,}"
        f" | HEALPix chunks updated: {len(touched_healpix):,}"
    )

    return (
        written_rows,
        skipped,
        touched_healpix,
        duplicate_skipped,
    )


# ============================================================
# Main
# ============================================================

if __name__ == "__main__":

    print("======================================")
    print("Gaia + Hipparcos processing")
    print("======================================")
    print()

    if not GAIA_RAW_DIR.exists():

        raise RuntimeError(
            f"Gaia download directory not found:\n{GAIA_RAW_DIR}"
        )

    gaia_csv_files = sorted(GAIA_RAW_DIR.glob("healpix_*.csv"))

    if not gaia_csv_files:

        raise RuntimeError(
            f"No Gaia HEALPix CSV files found in:\n{GAIA_RAW_DIR}"
        )

    if not HIPPARCOS_FILE.exists() or HIPPARCOS_FILE.stat().st_size == 0:

        raise RuntimeError(
            "Hipparcos-2 CSV is missing or empty:\n"
            f"{HIPPARCOS_FILE}"
        )

    hipparcos_to_gaia = load_gaia_hipparcos_crossmatch()

    print(
        f"Official Gaia-Hipparcos cross-matches: "
        f"{len(hipparcos_to_gaia):,}"
    )

    print(f"Found {len(gaia_csv_files):,} Gaia HEALPix files.")
    print(f"Found Hipparcos catalogue: {HIPPARCOS_FILE.name}")
    print(f"Gaia HEALPix level: {GAIA_HEALPIX_LEVEL}")
    print(f"Gaia HEALPix partitions: {GAIA_HEALPIX_COUNT:,}")
    print(f"Maximum distance: {MAX_DISTANCE_PC:,.0f} pc")
    print(f"Maximum absolute G magnitude: {MAX_ABSOLUTE_G_MAG}")
    print()
    print("Hipparcos will be merged into the corresponding Gaia binaries.")
    print(f"Output staging directory:\n{BUILD_CHUNKS_DIR}")
    print()

    # --------------------------------------------------------
    # Create a fresh staging directory
    # --------------------------------------------------------

    # Existing processed data remains untouched until the new processing
    # has completed and passed validation.

    if BUILD_CHUNKS_DIR.exists():
        shutil.rmtree(BUILD_CHUNKS_DIR)

    BUILD_CHUNKS_DIR.mkdir(parents=True, exist_ok=True)

    total_gaia_written = 0
    total_hipparcos_written = 0

    total_skipped = 0
    hipparcos_duplicate_skipped = 0

    failed_files = []

    # --------------------------------------------------------
    # Process Gaia HEALPix files in parallel
    # --------------------------------------------------------

    with ProcessPoolExecutor(max_workers=MAX_WORKERS) as executor:

        futures = {}

        for csv_file in gaia_csv_files:

            future = executor.submit(
                process_gaia_healpix,
                csv_file,
            )

            futures[future] = csv_file

        for future in as_completed(futures):

            csv_file = futures[future]

            try:

                written, skipped = future.result()

                total_gaia_written += written
                total_skipped += skipped

            except Exception as exc:

                print(
                    f"ERROR processing Gaia / {csv_file.name}: {exc}"
                )

                failed_files.append(
                    (csv_file.name, str(exc))
                )

    # --------------------------------------------------------
    # Process Hipparcos and merge it into Gaia binaries
    # --------------------------------------------------------

    # Find which cross-matched Gaia stars actually survived our filters.

    hipparcos_ids_already_in_gaia = set()

    if not failed_files:

        try:
            hipparcos_ids_already_in_gaia = (
                find_hipparcos_ids_already_in_gaia(
                    hipparcos_to_gaia
                )
            )

        except Exception as exc:

            print(f"ERROR checking Gaia cross-matches: {exc}")

            failed_files.append(
                ("Gaia-Hipparcos cross-match", str(exc))
            )

    hipparcos_healpix = []

    if not failed_files:

        print()
        print("======================================")
        print("PROCESSING HIPPARCOS")
        print("======================================")
        print()

        try:

            (
                hip_written,
                hip_skipped,
                hipparcos_healpix,
                hipparcos_duplicate_skipped,
            ) = process_hipparcos(
                HIPPARCOS_FILE,
                hipparcos_ids_already_in_gaia,
            )

            total_hipparcos_written += hip_written
            total_skipped += hip_skipped

        except Exception as exc:

            print(
                f"ERROR processing Hipparcos / "
                f"{HIPPARCOS_FILE.name}: {exc}"
            )

            failed_files.append(
                (HIPPARCOS_FILE.name, str(exc))
            )

    # --------------------------------------------------------
    # Processing results
    # --------------------------------------------------------

    total_written = (
        total_gaia_written
        + total_hipparcos_written
    )

    print()
    print("======================================")
    print("PROCESSING RESULTS")
    print("======================================")

    print(f"Gaia stars written: {total_gaia_written:,}")
    print(f"Hipparcos stars merged: {total_hipparcos_written:,}")
    print(f"Total stars written: {total_written:,}")

    print(f"Stars skipped: {total_skipped:,}")

    print(f"Hipparcos HEALPix chunks updated: {len(hipparcos_healpix):,}")
    print(f"Files failed: {len(failed_files):,}")

    print(
        f"Hipparcos duplicates skipped (already in Gaia): "
        f"{hipparcos_duplicate_skipped:,}"
    )

    # Abort if any input failed; preserve previous processed data.
    if failed_files:

        shutil.rmtree(BUILD_CHUNKS_DIR, ignore_errors=True)

        print()
        print("FAILED. Previous processed data was preserved.")

        raise RuntimeError(
            f"{len(failed_files):,} input files failed. "
            f"First failed file: {failed_files[0][0]}"
        )

    # --------------------------------------------------------
    # Verify expected output files
    # --------------------------------------------------------

    # Every Gaia input produces its corresponding binary.
    expected_output_names = {
        f"{path.stem}.bin"
        for path in gaia_csv_files
    }

    # Hipparcos may also create binaries for partitions that do not
    # currently have a corresponding Gaia CSV file.
    expected_output_names.update(
        f"healpix_{hp:05d}.bin"
        for hp in hipparcos_healpix
    )

    actual_output_names = {
        path.name
        for path in BUILD_CHUNKS_DIR.glob("*.bin")
    }

    if actual_output_names != expected_output_names:

        missing = sorted(
            expected_output_names - actual_output_names
        )

        unexpected = sorted(
            actual_output_names - expected_output_names
        )

        shutil.rmtree(BUILD_CHUNKS_DIR, ignore_errors=True)

        raise RuntimeError(
            "Output file set mismatch.\n"
            f"Missing: {missing}\n"
            f"Unexpected: {unexpected}\n"
            "Previous processed data was preserved."
        )

    # --------------------------------------------------------
    # Validate binary sizes
    # --------------------------------------------------------

    for output_file in BUILD_CHUNKS_DIR.glob("*.bin"):

        if output_file.stat().st_size % STAR_STRUCT.size != 0:

            shutil.rmtree(BUILD_CHUNKS_DIR, ignore_errors=True)

            raise RuntimeError(
                f"Invalid binary size for {output_file.name}: "
                f"{output_file.stat().st_size} bytes is not divisible by "
                f"{STAR_STRUCT.size}. Previous processed data was preserved."
            )

    # --------------------------------------------------------
    # Replace existing processed data
    # --------------------------------------------------------

    print()
    print("All files processed successfully.")
    print("Replacing old processed data...")

    if CHUNKS_DIR.exists():

        if BACKUP_CHUNKS_DIR.exists():
            shutil.rmtree(BACKUP_CHUNKS_DIR)

        CHUNKS_DIR.rename(BACKUP_CHUNKS_DIR)

    try:

        BUILD_CHUNKS_DIR.rename(CHUNKS_DIR)

    except Exception:

        if (
            not CHUNKS_DIR.exists()
            and BACKUP_CHUNKS_DIR.exists()
        ):

            BACKUP_CHUNKS_DIR.rename(CHUNKS_DIR)

        raise

    # Delete the old version only after the new version is installed.
    if BACKUP_CHUNKS_DIR.exists():
        shutil.rmtree(BACKUP_CHUNKS_DIR)

    # --------------------------------------------------------
    # Final report
    # --------------------------------------------------------

    output_files = list(CHUNKS_DIR.glob("*.bin"))

    print()
    print("======================================")
    print("GAIA + HIPPARCOS PROCESSING COMPLETE")
    print("======================================")

    print(f"Gaia files processed: {len(gaia_csv_files):,}")
    print("Hipparcos catalogues processed: 1")

    print(f"Output HEALPix binaries: {len(output_files):,}")
    print(f"Hipparcos HEALPix chunks updated: {len(hipparcos_healpix):,}")

    print()
    print(f"Gaia stars written: {total_gaia_written:,}")
    print(f"Hipparcos stars merged: {total_hipparcos_written:,}")
    print(f"Total stars written: {total_written:,}")

    print(f"Stars skipped: {total_skipped:,}")

    print()
    print(f"Final output:\n{CHUNKS_DIR}")