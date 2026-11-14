from __future__ import annotations

import csv
import math
import os
import time
from pathlib import Path

import numpy as np
from astroquery.vizier import Vizier


# -------------------------------------------------------------
# Paths and filters
# -------------------------------------------------------------

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "DataRaw" / "Hipparcos" / "downloaded"
RAW_DIR.mkdir(parents=True, exist_ok=True)

OUTPUT_FILE = RAW_DIR / "hipparcos2.csv"
TEMP_FILE = RAW_DIR / "hipparcos2.csv.part"

# Keep these names and values consistent with the Gaia downloader.
MAX_DISTANCE_PC = 2000
MIN_PARALLAX_MAS = 1000.0 / MAX_DISTANCE_PC


# -------------------------------------------------------------
# Hipparcos-2 catalogue settings
# -------------------------------------------------------------

HIPPARCOS_CATALOG = "I/311/hip2"
VIZIER_SERVER = "vizier.cfa.harvard.edu"

# Hipparcos-2 positions are at J1991.25. Gaia DR3 positions are at J2016.0.
HIPPARCOS_EPOCH_JYEAR = 1991.25
TARGET_EPOCH_JYEAR = 2016.0

# IMPORTANT: RArad and DErad are in radians in I/311/hip2, not degrees.
# pmRA is mu_alpha*cos(dec), so it maps to Astropy's pm_ra_cosdec.
HIPPARCOS_COLUMNS = [
    "HIP",
    "Sn",
    "So",
    "Nc",
    "RArad",
    "e_RArad",
    "DErad",
    "e_DErad",
    "Plx",
    "e_Plx",
    "pmRA",
    "e_pmRA",
    "pmDE",
    "e_pmDE",
    "Ntr",
    "F2",
    "F1",
    "var",
    "Hpmag",
    "e_Hpmag",
    "sHp",
    "VA",
    "B-V",
    "e_B-V",
    "V-I",
]


# -------------------------------------------------------------
# Output schema
# -------------------------------------------------------------

# Only fields that Hipparcos-2 actually provides, or that can be derived from
# those fields, are written. Gaia-only fields (source_id, G, BP-RP, GSP-Phot,
# FLAME, etc.) are intentionally omitted instead of writing entirely blank
# columns. A later merge can create the union of Gaia and Hipparcos columns.
BASE_OUTPUT_COLUMNS = [
    "source_catalog",
    "hip_id",
    "ra",
    "dec",
    "parallax",
    "distance_parallax_pc",
    "ra_hip1991",
    "dec_hip1991",
    "position_epoch_jyear",
]

# Hipparcos catalogue fields mapped into descriptive output names.
OPTIONAL_SOURCE_FIELDS = {
    "e_ra_cosdec_mas": "e_RArad",
    "e_dec_mas": "e_DErad",
    "e_parallax": "e_Plx",
    "pmra": "pmRA",
    "e_pmra": "e_pmRA",
    "pmdec": "pmDE",
    "e_pmdec": "e_pmDE",
    "hp_mag": "Hpmag",
    "e_hp_mag": "e_Hpmag",
    "s_hp": "sHp",
    "b_v": "B-V",
    "e_b_v": "e_B-V",
    "v_i": "V-I",
    "solution_type_new": "Sn",
    "solution_type_old": "So",
    "num_components": "Nc",
    "num_transits": "Ntr",
    "goodness_of_fit": "F2",
    "rejected_data_percent": "F1",
    "cosmic_dispersion": "var",
    "variability_annex": "VA",
}

INTEGER_OUTPUT_FIELDS = {
    "hip_id",
    "solution_type_new",
    "solution_type_old",
    "num_components",
    "num_transits",
    "variability_annex",
}

# A previous version wrote Gaia-only columns that were entirely empty and
# treated RArad/DErad as degrees. Delete that old output and download it again.
LEGACY_COLUMNS = {
    "source_id",
    "phot_g_mean_mag",
    "bp_rp",
    "teff_gspphot",
    "evolstage_flame",
}
REQUIRED_CURRENT_COLUMNS = {
    "source_catalog",
    "hip_id",
    "ra",
    "dec",
    "parallax",
    "distance_parallax_pc",
    "ra_hip1991",
    "dec_hip1991",
    "position_epoch_jyear",
}


# -------------------------------------------------------------
# Helpers
# -------------------------------------------------------------

def column_as_float(table, column_name: str) -> np.ndarray:
    """Return an Astropy table column as float64, using NaN for masked values."""
    if column_name not in table.colnames:
        raise KeyError(
            f"VizieR did not return required column {column_name!r}. "
            f"Returned columns: {table.colnames}"
        )

    values = np.ma.asarray(table[column_name], dtype=np.float64)
    return np.asarray(values.filled(np.nan), dtype=np.float64)


def finite_or_none(value: float) -> float | None:
    value = float(value)
    return value if math.isfinite(value) else None


def int_or_none(value: float) -> int | None:
    if not math.isfinite(float(value)):
        return None
    return int(round(float(value)))


def csv_value(value):
    """Convert values to CSV-safe strings; missing values become empty cells."""
    if value is None:
        return ""
    if isinstance(value, (float, np.floating)):
        if not math.isfinite(float(value)):
            return ""
        return format(float(value), ".12g")
    if isinstance(value, (int, np.integer)):
        return str(int(value))
    return str(value)


def existing_output_is_current(path: Path) -> bool:
    """Return True only when an existing CSV has the current, non-Gaia schema."""
    try:
        with path.open("r", newline="", encoding="utf-8") as csv_file:
            header = next(csv.reader(csv_file), None)
    except (OSError, UnicodeError, csv.Error):
        return False

    if not header:
        return False

    header_set = set(header)
    return (
        REQUIRED_CURRENT_COLUMNS.issubset(header_set)
        and not (LEGACY_COLUMNS & header_set)
    )


def propagate_positions_to_2016(
    ra_deg: np.ndarray,
    dec_deg: np.ndarray,
    parallax_mas: np.ndarray,
    pmra_masyr: np.ndarray,
    pmdec_masyr: np.ndarray,
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Propagate Hipparcos positions from J1991.25 to J2016.0.

    VizieR returns RArad and DErad in DEGREES.
    Hipparcos pmRA is mu_alpha*cos(dec), matching Astropy's
    pm_ra_cosdec.

    Zero radial velocity is assumed because it is not included
    in this catalogue query.
    """

    import astropy.units as u
    from astropy.coordinates import Distance, SkyCoord
    from astropy.time import Time

    ra_out_deg = ra_deg.copy()
    dec_out_deg = dec_deg.copy()

    # Validate coordinate ranges.
    if np.any((ra_deg < 0.0) | (ra_deg > 360.0)):
        raise ValueError("Hipparcos RA outside [0, 360] degrees.")

    if np.any((dec_deg < -90.0) | (dec_deg > 90.0)):
        raise ValueError("Hipparcos DEC outside [-90, 90] degrees.")

    has_proper_motion = (
        np.isfinite(pmra_masyr)
        & np.isfinite(pmdec_masyr)
    )

    if np.any(has_proper_motion):

        coords = SkyCoord(
            ra=ra_deg[has_proper_motion] * u.deg,
            dec=dec_deg[has_proper_motion] * u.deg,

            distance=Distance(
                parallax=parallax_mas[has_proper_motion] * u.mas
            ),

            pm_ra_cosdec=(
                pmra_masyr[has_proper_motion] * u.mas / u.yr
            ),

            pm_dec=(
                pmdec_masyr[has_proper_motion] * u.mas / u.yr
            ),

            radial_velocity=(
                np.zeros(np.count_nonzero(has_proper_motion))
                * u.km / u.s
            ),

            obstime=Time(
                HIPPARCOS_EPOCH_JYEAR,
                format="jyear"
            ),

            frame="icrs",
        )

        moved = coords.apply_space_motion(
            new_obstime=Time(
                TARGET_EPOCH_JYEAR,
                format="jyear"
            )
        )

        ra_out_deg[has_proper_motion] = moved.ra.deg
        dec_out_deg[has_proper_motion] = moved.dec.deg

    return ra_out_deg, dec_out_deg, has_proper_motion


def has_any_finite_value(values: np.ndarray) -> bool:
    """Whether an optional numeric output column contains at least one value."""
    return bool(np.isfinite(values).any())


# -------------------------------------------------------------
# Download
# -------------------------------------------------------------

def download_hipparcos():
    # ---------------------------------------------------------
    # Existing output
    # ---------------------------------------------------------

    if OUTPUT_FILE.exists():
        if existing_output_is_current(OUTPUT_FILE):
            print(f"Already downloaded: {OUTPUT_FILE}")
            print("Delete this file to force a fresh download.")
            return

        print(
            "Existing output uses the old/incomplete schema or has an invalid "
            "header; deleting it and downloading again."
        )
        OUTPUT_FILE.unlink()

    # ---------------------------------------------------------
    # Previous interrupted/failed output
    # ---------------------------------------------------------

    if TEMP_FILE.exists():
        print(f"RETRY: removing incomplete {TEMP_FILE.name}")
        TEMP_FILE.unlink()

    start_time = time.perf_counter()

    print(
        f"Querying {HIPPARCOS_CATALOG} with parallax >= "
        f"{MIN_PARALLAX_MAS:.3f} mas "
        f"(distance <= {MAX_DISTANCE_PC} pc)..."
    )

    # ---------------------------------------------------------
    # Query VizieR
    # ---------------------------------------------------------

    vizier = Vizier(
        columns=HIPPARCOS_COLUMNS,
        column_filters={"Plx": f">={MIN_PARALLAX_MAS:.12g}"},
        row_limit=-1,
        timeout=180,
        vizier_server=VIZIER_SERVER,
    )

    result = vizier.query_constraints(catalog=HIPPARCOS_CATALOG)

    if not result or len(result) == 0:
        raise RuntimeError(
            "VizieR returned no Hipparcos-2 table. "
            f"Server: {VIZIER_SERVER}; catalogue: {HIPPARCOS_CATALOG}. "
            "Check the connection or try another VizieR mirror."
        )

    table = result[0]

    if len(table) == 0:
        raise RuntimeError(
            "Hipparcos-2 query returned zero rows. "
            "Check the parallax filter and VizieR response."
        )

    print(f"Received {len(table):,} rows from VizieR.")

    # Confirm that the server returned the columns needed for filtering and
    # position propagation before attempting to build the output.
    required_source_columns = {"HIP", "RArad", "DErad", "Plx", "pmRA", "pmDE"}
    missing_source_columns = required_source_columns - set(table.colnames)
    if missing_source_columns:
        raise RuntimeError(
            "VizieR response is missing required columns: "
            f"{sorted(missing_source_columns)}. Returned columns: {table.colnames}"
        )

    # ---------------------------------------------------------
    # Apply the same filters as the Gaia downloader
    # ---------------------------------------------------------

    hip_id = column_as_float(table, "HIP")
    ra_hip1991_deg = column_as_float(table, "RArad")
    dec_hip1991_deg = column_as_float(table, "DErad")
    parallax = column_as_float(table, "Plx")

    valid = (
        np.isfinite(ra_hip1991_deg)
        & np.isfinite(dec_hip1991_deg)
        & np.isfinite(parallax)
        & (parallax >= MIN_PARALLAX_MAS)
    )

    rejected_count = int(len(table) - np.count_nonzero(valid))
    if rejected_count:
        print(
            f"Filtered out {rejected_count:,} rows with invalid coordinates/parallax."
        )

    table = table[valid]
    if len(table) == 0:
        raise RuntimeError("No Hipparcos-2 rows remain after filtering.")

    # Cache every selected catalogue column once, in the filtered row order.
    values = {
        name: column_as_float(table, name)
        for name in HIPPARCOS_COLUMNS
    }

    hip_id = values["HIP"]
    ra_hip1991_deg = values["RArad"]
    dec_hip1991_deg = values["DErad"]
    parallax = values["Plx"]
    pmra = values["pmRA"]
    pmdec = values["pmDE"]

    # ---------------------------------------------------------
    # Propagate positions to Gaia DR3's reference epoch
    # ---------------------------------------------------------

    ra_2016_deg, dec_2016_deg, propagated = propagate_positions_to_2016(
        ra_hip1991_deg,
        dec_hip1991_deg,
        parallax,
        pmra,
        pmdec,
    )

    not_propagated = int(len(table) - np.count_nonzero(propagated))
    if not_propagated:
        print(
            f"WARNING: {not_propagated:,} rows have missing proper motion; "
            "their ra/dec remain at epoch J1991.25."
        )

    # ---------------------------------------------------------
    # Build output values
    # ---------------------------------------------------------

    distance_pc = 1000.0 / parallax

    hp_mag = values["Hpmag"]
    m_hp_abs = np.full(len(table), np.nan, dtype=np.float64)
    has_hp_mag = np.isfinite(hp_mag)
    m_hp_abs[has_hp_mag] = (
        hp_mag[has_hp_mag]
        - 5.0 * np.log10(distance_pc[has_hp_mag])
        + 5.0
    )

    output_data: dict[str, np.ndarray | list[str]] = {
        "source_catalog": ["Hipparcos2"] * len(table),
        "hip_id": hip_id,
        "ra": ra_2016_deg,
        "dec": dec_2016_deg,
        "parallax": parallax,
        "distance_parallax_pc": distance_pc,
        "ra_hip1991": ra_hip1991_deg,
        "dec_hip1991": dec_hip1991_deg,
        "position_epoch_jyear": np.where(
            propagated, TARGET_EPOCH_JYEAR, HIPPARCOS_EPOCH_JYEAR
        ),
        "m_hp_abs": m_hp_abs,
    }

    for output_name, source_name in OPTIONAL_SOURCE_FIELDS.items():
        output_data[output_name] = values[source_name]

    # Omit optional columns that are completely empty in this download. Keep
    # columns with partial missing values; those missing cells remain blank.
    output_columns = list(BASE_OUTPUT_COLUMNS)
    if has_any_finite_value(m_hp_abs):
        output_columns.append("m_hp_abs")

    for output_name in OPTIONAL_SOURCE_FIELDS:
        if has_any_finite_value(np.asarray(output_data[output_name], dtype=np.float64)):
            output_columns.append(output_name)

    # ---------------------------------------------------------
    # Write temporary CSV
    # ---------------------------------------------------------

    print(f"Writing {len(table):,} Hipparcos-2 rows...")
    print(f"Output columns ({len(output_columns)}): {', '.join(output_columns)}")

    with TEMP_FILE.open("w", newline="", encoding="utf-8") as csv_file:
        writer = csv.DictWriter(
            csv_file,
            fieldnames=output_columns,
            extrasaction="raise",
        )
        writer.writeheader()

        for i in range(len(table)):
            row = {}
            for column in output_columns:
                if column == "source_catalog":
                    value = "Hipparcos2"
                else:
                    raw_value = output_data[column][i]
                    if column in INTEGER_OUTPUT_FIELDS:
                        value = int_or_none(raw_value)
                    else:
                        value = finite_or_none(raw_value)
                row[column] = csv_value(value)

            writer.writerow(row)

    # ---------------------------------------------------------
    # Validate temporary CSV
    # ---------------------------------------------------------

    if not TEMP_FILE.exists():
        raise RuntimeError("CSV writing finished but temporary file was not created.")

    file_size = TEMP_FILE.stat().st_size
    if file_size == 0:
        raise RuntimeError("Temporary CSV is empty.")

    with TEMP_FILE.open("r", newline="", encoding="utf-8") as csv_file:
        reader = csv.reader(csv_file)
        header = next(reader, None)
        first_data_row = next(reader, None)

    if header != output_columns:
        raise RuntimeError("Temporary CSV header does not match the expected schema.")

    if first_data_row is None:
        raise RuntimeError("Temporary CSV contains a header but no data rows.")

    if len(first_data_row) != len(output_columns):
        raise RuntimeError(
            "Temporary CSV first data row has a different number of fields "
            "than the header."
        )

    # ---------------------------------------------------------
    # Mark as completed
    # ---------------------------------------------------------

    os.replace(TEMP_FILE, OUTPUT_FILE)

    elapsed = (time.perf_counter() - start_time) / 60.0
    print(
        f"OK | {len(table):,} stars | "
        f"{file_size / (1024 ** 2):.2f} MB | "
        f"{elapsed:.2f} min"
    )
    print(f"Saved: {OUTPUT_FILE}")


if __name__ == "__main__":
    download_hipparcos()