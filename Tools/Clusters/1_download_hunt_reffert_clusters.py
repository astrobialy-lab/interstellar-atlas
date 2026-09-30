from pathlib import Path
from urllib.parse import urlencode
from urllib.request import Request, urlopen
import shutil
import time


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

OUTPUT_DIR = (
    PROJECT_ROOT
    / "DataRaw"
    / "Clusters"
    / "HuntReffert2024"
)


# ============================================================
# VizieR
# ============================================================

VIZIER_URL = (
    "https://vizier.cds.unistra.fr/viz-bin/asu-tsv"
)

CATALOGUE = "J/A+A/686/A42"


# ============================================================
# Explicit columns
#
# We request only the columns we actually need.
# This avoids depending on VizieR's default output columns.
# ============================================================

CLUSTER_COLUMNS = [
    "Name",
    "ID",
    "Type",

    "N",
    "Nt",

    "RAdeg",
    "DEdeg",
    "GLON",
    "GLAT",

    "r50pc",
    "rcpc",
    "rtpc",
    "rtotpc",

    "pmRA",
    "pmDE",
    "Plx",

    "dist16",
    "dist50",
    "dist84",

    "X",
    "Y",
    "Z",

    "RV",

    "logAge16",
    "logAge50",
    "logAge84",

    "AV16",
    "AV50",
    "AV84",

    "r50Jpc",
    "rJpc",
    "probJ",
    "NJ",

    "MassJ",
    "e_MassJ",
    "MassTot",
    "e_MassTot",

    "minClSize",

    "isMerged",
    "isGMMMemb",

    "NXmatches",
    "XmatchType",
    "Note",
]


MEMBER_COLUMNS = [
    "Seq",
    "Name",
    "ID",
    "GaiaDR3",
    "inrj",
    "inrt",
    "Prob",
]


TABLES = {
    "clusters": {
        "output": "clusters.tsv",
        "expected_rows": 7_167,
        "columns": CLUSTER_COLUMNS,
    },

    "members": {
        "output": "members.tsv",
        "expected_rows": 1_291_929,
        "columns": MEMBER_COLUMNS,
    },
}


# ============================================================
# Data-row validation
# ============================================================

def is_data_row(
    table_name: str,
    line: str,
) -> bool:

    line = line.strip()

    if not line:
        return False

    if line.startswith("#"):
        return False

    fields = line.split()

    # --------------------------------------------------------
    # The first two non-comment rows are:
    #
    #   column names
    #   units
    #
    # They are rejected by requiring actual numeric fields.
    # --------------------------------------------------------

    if table_name == "clusters":

        # Requested columns:
        #
        # Name ID Type N Nt ...
        #
        if len(fields) < 4:
            return False

        try:
            int(fields[1])
            int(fields[3])

            return True

        except ValueError:
            return False


    if table_name == "members":

        # Requested columns:
        #
        # Seq Name ID GaiaDR3 inrj inrt Prob
        #
        if len(fields) < 7:
            return False

        try:
            int(fields[0])
            int(fields[2])
            int(fields[3])
            float(fields[6])

            return True

        except ValueError:
            return False


    return False


# ============================================================
# Download one table
# ============================================================

def download_table(
    table_name: str,
    config: dict,
) -> None:

    output_name = config["output"]
    expected_rows = config["expected_rows"]
    columns = config["columns"]


    output_path = (
        OUTPUT_DIR /
        output_name
    )

    temp_path = (
        OUTPUT_DIR /
        (output_name + ".part")
    )


    # --------------------------------------------------------
    # Explicit column selection
    # --------------------------------------------------------

    params = {
        "-source":
            f"{CATALOGUE}/{table_name}",

        "-out":
            ",".join(columns),

        "-out.max":
            "unlimited",
    }


    url = (
        VIZIER_URL
        + "?"
        + urlencode(params)
    )


    print(
        f"Downloading: {table_name}"
    )

    print(
        f"URL: {url}"
    )

    print(
        f"Output: {output_path}"
    )

    print()


    request = Request(
        url,
        headers={
            "User-Agent":
                "Gaia VR space simulation data downloader"
        }
    )


    try:

        # ====================================================
        # Download
        # ====================================================

        with urlopen(
            request,
            timeout=120
        ) as response:

            status = getattr(
                response,
                "status",
                200
            )

            if status != 200:

                raise RuntimeError(
                    f"HTTP status {status}"
                )


            downloaded = 0
            start_time = time.time()


            with temp_path.open(
                "wb"
            ) as f:

                while True:

                    chunk = response.read(
                        1024 * 1024
                    )

                    if not chunk:
                        break

                    f.write(chunk)

                    downloaded += len(
                        chunk
                    )


                    elapsed = max(
                        time.time()
                        - start_time,
                        1e-6
                    )


                    speed = (
                        downloaded /
                        elapsed
                    )


                    print(
                        f"\r"
                        f"{downloaded / 1e6:9.2f} MB  "
                        f"{speed / 1e6:7.2f} MB/s",
                        end="",
                        flush=True
                    )


            print()


        # ====================================================
        # Basic validation
        # ====================================================

        actual_size = (
            temp_path.stat().st_size
        )


        if actual_size == 0:

            raise RuntimeError(
                f"VizieR returned an empty file "
                f"for {table_name}."
            )


        # ====================================================
        # Count actual data records
        # ====================================================

        data_rows = 0


        with temp_path.open(
            "r",
            encoding="utf-8",
            errors="replace"
        ) as f:

            for line in f:

                if is_data_row(
                    table_name,
                    line
                ):
                    data_rows += 1


        if data_rows != expected_rows:

            raise RuntimeError(
                f"Data-row count mismatch for "
                f"{table_name}.\n"
                f"Expected: {expected_rows:,}\n"
                f"Found:    {data_rows:,}"
            )


        # ====================================================
        # Publish
        # ====================================================

        shutil.move(
            temp_path,
            output_path
        )


        print(
            f"Finished: {output_name}"
        )

        print(
            f"Size: {actual_size / 1e6:.2f} MB"
        )

        print(
            f"Rows: {data_rows:,}"
        )

        print()


    except Exception:

        if temp_path.exists():
            temp_path.unlink()

        raise


# ============================================================
# Main
# ============================================================

def main():

    OUTPUT_DIR.mkdir(
        parents=True,
        exist_ok=True
    )


    print(
        "======================================"
    )

    print(
        "Hunt & Reffert 2024 cluster catalogue"
    )

    print(
        "======================================"
    )

    print()


    print(
        f"Output directory:\n{OUTPUT_DIR}"
    )

    print()


    for table_name, config in TABLES.items():

        download_table(
            table_name,
            config
        )


    print(
        "======================================"
    )

    print(
        "Download complete."
    )

    print(
        "======================================"
    )


if __name__ == "__main__":
    main()