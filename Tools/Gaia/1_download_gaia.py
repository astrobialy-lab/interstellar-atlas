from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
from astroquery.gaia import Gaia
import time
import os

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "DataRaw" / "Gaia3"
RAW_DIR.mkdir(parents=True, exist_ok=True)

CREDENTIALS_FILE = PROJECT_ROOT.parent / "gaia_credentials.txt"

# HEALPix partition level used to split the Gaia data into manageable files.
# The level doesn't matter because the data is split by cubes later.
LEVEL = 3
NPIXELS = 12 * 4**LEVEL
DIVISOR = 2 ** (59 - 2 * LEVEL)

MAX_WORKERS = 3


def download_healpix(healpix):

    source_id_min = healpix * DIVISOR
    source_id_max = (healpix + 1) * DIVISOR

    # Final file: exists ONLY when the download completed successfully.
    output_file = RAW_DIR / f"healpix_{healpix:05d}.csv"

    # Temporary file: exists while a download is in progress.
    temp_file = RAW_DIR / f"healpix_{healpix:05d}.csv.part"

    # ---------------------------------------------------------
    # Already completed
    # ---------------------------------------------------------

    if output_file.exists():

        if output_file.stat().st_size > 0:
            return

        print(f"WARNING {healpix}: final file is empty, deleting it")
        output_file.unlink()

    # ---------------------------------------------------------
    # Previous interrupted/failed download
    # ---------------------------------------------------------

    if temp_file.exists():

        print(
            f"RETRY {healpix}: removing incomplete "
            f"{temp_file.name}"
        )

        temp_file.unlink()

    # ---------------------------------------------------------
    # Query
    # ---------------------------------------------------------

    query = f"""
    SELECT
        g.source_id,

        -- Gaia observation
        g.ra,
        g.dec,
        g.parallax,
        g.phot_g_mean_mag,
        g.bp_rp,

        -- GSP-Phot stellar estimates
        ap.teff_gspphot,
        ap.logg_gspphot,
        ap.mh_gspphot,

        ap.distance_gspphot,
        ap.radius_gspphot,

        -- Estimated extinction
        ap.azero_gspphot,
        ap.ag_gspphot,
        ap.abp_gspphot,
        ap.arp_gspphot,
        ap.ebpminrp_gspphot,

        -- Absolute magnitude
        ap.mg_gspphot,

        -- Which synthetic stellar library was selected
        ap.libname_gspphot,

        -- FLAME stellar properties
        ap.mass_flame,
        ap.age_flame,
        ap.lum_flame,
        ap.evolstage_flame

    FROM gaiadr3.gaia_source AS g

    JOIN gaiadr3.astrophysical_parameters AS ap
        ON g.source_id = ap.source_id

    WHERE
        g.source_id >= {source_id_min}
        AND g.source_id < {source_id_max}

        AND ap.distance_gspphot IS NOT NULL
        AND ap.lum_flame IS NOT NULL
        AND g.bp_rp IS NOT NULL
        AND ap.ebpminrp_gspphot IS NOT NULL
    """

    # ---------------------------------------------------------
    # Download to temporary file
    # ---------------------------------------------------------

    start_time = time.perf_counter()

    try:

        Gaia.launch_job_async(
            query,
            dump_to_file=True,
            output_format="csv",
            output_file=str(temp_file),
        )

        # -----------------------------------------------------
        # Basic validation
        # -----------------------------------------------------

        if not temp_file.exists():
            raise RuntimeError(
                "Gaia query finished but temporary file was not created"
            )

        file_size = temp_file.stat().st_size

        if file_size == 0:
            raise RuntimeError(
                "Gaia query finished but temporary file is empty"
            )

        # Check that the CSV actually contains a header.
        with open(temp_file, "r", encoding="utf-8") as f:
            header = f.readline().strip()

        if not header:
            raise RuntimeError(
                "Temporary file contains no CSV header"
            )

        # -----------------------------------------------------
        # Mark as COMPLETED
        # -----------------------------------------------------

        os.replace(temp_file, output_file)

        elapsed = (time.perf_counter() - start_time) / 60

        print(
            f"OK {healpix}/{NPIXELS - 1} | "
            f"{file_size / (1024**2):.1f} MB | "
            f"{elapsed:.1f} min"
        )

    except Exception:
        # Keep the .part file so you can see that this partition
        # did NOT finish successfully.
        raise


# -------------------------------------------------------------
# Login
# -------------------------------------------------------------

Gaia.login(credentials_file=str(CREDENTIALS_FILE))


# -------------------------------------------------------------
# Download
# -------------------------------------------------------------

with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:

    futures = [
        executor.submit(download_healpix, healpix)
        for healpix in range(NPIXELS)
    ]

print("Done.")