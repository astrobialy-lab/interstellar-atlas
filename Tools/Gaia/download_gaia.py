from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
from astroquery.gaia import Gaia


PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "Data" / "Gaia" / "raw"
RAW_DIR.mkdir(parents=True, exist_ok=True)

CREDENTIALS_FILE = PROJECT_ROOT.parent / "gaia_credentials.txt"

LEVEL = 3
NPIXELS = 12 * 4**LEVEL
DIVISOR = 2 ** (59 - 2 * LEVEL)

MAX_WORKERS = 5


def download_healpix(healpix):

    source_id_min = healpix * DIVISOR
    source_id_max = (healpix + 1) * DIVISOR

    output_file = RAW_DIR / f"healpix_{healpix:05d}.csv"

    if output_file.exists():
        return f"Skipping {healpix}: already downloaded"

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
        ap.teff_gspphot_lower,
        ap.teff_gspphot_upper,

        ap.logg_gspphot,
        ap.logg_gspphot_lower,
        ap.logg_gspphot_upper,

        ap.mh_gspphot,
        ap.mh_gspphot_lower,
        ap.mh_gspphot_upper,

        ap.distance_gspphot,
        ap.distance_gspphot_lower,
        ap.distance_gspphot_upper,

        ap.radius_gspphot,
        ap.radius_gspphot_lower,
        ap.radius_gspphot_upper,

        -- Estimated extinction
        ap.azero_gspphot,

        ap.ag_gspphot,
        ap.ag_gspphot_lower,
        ap.ag_gspphot_upper,

        ap.abp_gspphot,
        ap.abp_gspphot_lower,
        ap.abp_gspphot_upper,

        ap.arp_gspphot,
        ap.arp_gspphot_lower,
        ap.arp_gspphot_upper,

        ap.ebpminrp_gspphot,
        ap.ebpminrp_gspphot_lower,
        ap.ebpminrp_gspphot_upper,

        -- Absolute magnitude
        ap.mg_gspphot,
        ap.mg_gspphot_lower,
        ap.mg_gspphot_upper,

        -- Which synthetic stellar library was selected
        ap.libname_gspphot

    FROM gaiadr3.gaia_source AS g

    JOIN gaiadr3.astrophysical_parameters AS ap
        ON g.source_id = ap.source_id

    WHERE
        g.source_id >= {source_id_min}
        AND g.source_id < {source_id_max}

        AND g.phot_g_mean_mag IS NOT NULL
        AND g.bp_rp IS NOT NULL
    """

    Gaia.launch_job_async(
        query,
        dump_to_file=True,
        output_format="csv",
        output_file=str(output_file),
    )

    return f"Downloaded {healpix}/{NPIXELS - 1}"


Gaia.login(credentials_file=str(CREDENTIALS_FILE))


with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:

    futures = [
        executor.submit(download_healpix, healpix)
        for healpix in range(NPIXELS)
    ]

    for future in as_completed(futures):
        try:
            print(future.result())
        except Exception as e:
            print(f"ERROR: {e}")


print("Done.")