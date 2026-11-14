
import csv
import io
import time
from decimal import Decimal

import requests


# ---------------------------------------------------------
# Settings
# ---------------------------------------------------------

SOURCE_ID = 168726523212358272

TAP_URL = "https://gaia.ari.uni-heidelberg.de/tap/sync"

OUTPUT_FIELDS = [
    "source_id",

    "phot_g_mean_mag",
    "phot_bp_mean_mag",
    "phot_rp_mean_mag",

    "distance_gspphot",
    "lum_flame",
    "bp_rp",
    "ebpminrp_gspphot",
    "ra",
    "dec",
    "mg_gspphot",
]

MAX_ATTEMPTS = 4


# ---------------------------------------------------------
# ADQL Query
# ---------------------------------------------------------

QUERY = f"""
SELECT TOP 2
    g.source_id,

    ap.distance_gspphot,
    ap.lum_flame,

    g.bp_rp,
    ap.ebpminrp_gspphot,

    g.ra,
    g.dec,

    ap.mg_gspphot,

    g.phot_g_mean_mag,
    g.phot_bp_mean_mag,
    g.phot_rp_mean_mag

FROM gaiadr3.gaia_source AS g

LEFT OUTER JOIN gaiadr3.astrophysical_parameters AS ap
    ON g.source_id = ap.source_id

WHERE g.source_id = {SOURCE_ID}
"""


# ---------------------------------------------------------
# Query Gaia
# ---------------------------------------------------------

def query_gaia():

    for attempt in range(1, MAX_ATTEMPTS + 1):

        try:

            response = requests.post(
                TAP_URL,
                data={
                    "REQUEST": "doQuery",
                    "LANG": "ADQL",
                    "FORMAT": "CSV",
                    "QUERY": QUERY,
                },
                timeout=(30, 180),
            )

            if response.status_code in (429, 502, 503, 504):

                if attempt < MAX_ATTEMPTS:

                    delay = 15 * attempt

                    print(
                        f"Server busy (HTTP {response.status_code}). "
                        f"Retrying in {delay}s..."
                    )

                    time.sleep(delay)
                    continue

            response.raise_for_status()

            content = response.content.decode("utf-8-sig").strip()

            if not content:
                raise RuntimeError("Gaia returned an empty response.")

            if content.startswith("<?xml") or "<VOTABLE" in content[:500].upper():
                raise RuntimeError(
                    f"Gaia returned a TAP error:\n{content[:3000]}"
                )

            reader = csv.DictReader(io.StringIO(content))

            if not reader.fieldnames:
                raise RuntimeError("CSV response has no header.")

            # Normalize column names.
            rows = [
                {
                    (key or "").strip().lower(): value
                    for key, value in row.items()
                }
                for row in reader
            ]

            return rows

        except requests.RequestException:

            if attempt >= MAX_ATTEMPTS:
                raise

            delay = 15 * attempt

            print(f"Connection failed. Retrying in {delay}s...")
            time.sleep(delay)

    raise RuntimeError("Gaia query failed after all attempts.")


# ---------------------------------------------------------
# Main
# ---------------------------------------------------------

def main():

    print(f"Querying Gaia DR3...\n")

    rows = query_gaia()

    if not rows:
        print("Source not found in Gaia DR3.")
        return

    if len(rows) != 1:
        raise RuntimeError(
            f"Expected exactly one source, received {len(rows)}."
        )

    row = rows[0]

    if int(Decimal(row["source_id"])) != SOURCE_ID:
        raise RuntimeError("Returned source ID does not match!")

    print("=" * 55)
    print("Gaia DR3")
    print("=" * 55)

    for field in OUTPUT_FIELDS:

        value = row.get(field)

        if value is None or value == "":
            value = "NULL"

        print(f"{field:22s}: {value}")

    print("=" * 55)


if __name__ == "__main__":
    main()
