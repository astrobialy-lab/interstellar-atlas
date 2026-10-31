from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError
import hashlib
import shutil
import time


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

OUTPUT_DIR = (
    PROJECT_ROOT
    / "DataRaw"
    / "Dust"
    / "Edenhofer2024"
)

HEALPIX_FILENAME = "mean_and_std_healpix.fits"

HEALPIX_PATH = OUTPUT_DIR / HEALPIX_FILENAME
TEMP_PATH = OUTPUT_DIR / (HEALPIX_FILENAME + ".part")


# ============================================================
# Zenodo
# ============================================================

ZENODO_RECORD = "10658339"

DOWNLOAD_URL = (
    f"https://zenodo.org/records/{ZENODO_RECORD}"
    f"/files/{HEALPIX_FILENAME}?download=1"
)

EXPECTED_SIZE = 3_252_715_200

EXPECTED_MD5 = "10c823a5fcf81b47b6e15530bcdf54dc"

BLOCK_SIZE = 8 * 1024 * 1024


# ============================================================
# MD5 verification
# ============================================================

def calculate_md5(path: Path) -> str:

    md5 = hashlib.md5()

    with path.open("rb") as f:

        while True:

            chunk = f.read(BLOCK_SIZE)

            if not chunk:
                break

            md5.update(chunk)

    return md5.hexdigest()


def verify_file(path: Path) -> bool:

    if not path.exists():
        return False

    if path.stat().st_size != EXPECTED_SIZE:
        return False

    print("Verifying MD5...")

    actual_md5 = calculate_md5(path)

    if actual_md5 != EXPECTED_MD5:

        print("MD5 verification failed.")

        return False

    return True


# ============================================================
# Download
# ============================================================

def download():

    OUTPUT_DIR.mkdir(
        parents=True,
        exist_ok=True
    )

    print("======================================")
    print("Edenhofer et al. 2024 dust map")
    print("Primary 1.25 kpc HEALPix data")
    print("======================================")

    print(f"Output: {HEALPIX_PATH}")
    print(f"Expected size: {EXPECTED_SIZE / 1e9:.2f} GB")
    print()

    # --------------------------------------------------------
    # Skip existing valid download
    # --------------------------------------------------------

    if HEALPIX_PATH.exists():

        if verify_file(HEALPIX_PATH):

            print("File already downloaded and verified.")
            return

        raise RuntimeError(
            f"Existing file failed verification: {HEALPIX_PATH}\n"
            "Move or delete it before downloading again."
        )

    # --------------------------------------------------------
    # Resume interrupted download
    # --------------------------------------------------------

    downloaded = (
        TEMP_PATH.stat().st_size
        if TEMP_PATH.exists()
        else 0
    )

    if downloaded > EXPECTED_SIZE:

        TEMP_PATH.unlink()
        downloaded = 0

    if downloaded == EXPECTED_SIZE:

        if verify_file(TEMP_PATH):

            TEMP_PATH.replace(HEALPIX_PATH)

            print("Download verified.")
            return

        TEMP_PATH.unlink()
        downloaded = 0

    print(
        f"Already downloaded: "
        f"{downloaded / 1e9:.2f} GB"
    )

    headers = {
        "User-Agent": "Interstellar Atlas Dust Downloader"
    }

    if downloaded > 0:

        headers["Range"] = f"bytes={downloaded}-"

    request = Request(
        DOWNLOAD_URL,
        headers=headers
    )

    start_time = time.time()

    try:

        with urlopen(request, timeout=120) as response:

            status = response.status

            if downloaded > 0 and status == 206:

                content_range = response.headers.get(
                    "Content-Range", ""
                )

                if not content_range.startswith(
                    f"bytes {downloaded}-"
                ):

                    raise RuntimeError(
                        f"Unexpected Content-Range: {content_range}"
                    )

                mode = "ab"

            elif status == 200:

                # Server ignored the Range request.
                # Restart from the beginning.

                downloaded = 0
                mode = "wb"

            else:

                raise RuntimeError(
                    f"Unexpected HTTP status: {status}"
                )

            print("Downloading...")

            with TEMP_PATH.open(mode) as f:

                while True:

                    chunk = response.read(
                        BLOCK_SIZE
                    )

                    if not chunk:
                        break

                    f.write(chunk)
                    downloaded += len(chunk)

                    elapsed = max(
                        time.time() - start_time,
                        0.001
                    )

                    speed = downloaded / elapsed

                    progress = (
                        downloaded / EXPECTED_SIZE * 100
                    )

                    print(
                        f"\r"
                        f"{progress:6.2f}%  "
                        f"{downloaded / 1e9:.2f} / "
                        f"{EXPECTED_SIZE / 1e9:.2f} GB  "
                        f"{speed / 1e6:.2f} MB/s",
                        end="",
                        flush=True
                    )

        print()

        # ----------------------------------------------------
        # Validate size
        # ----------------------------------------------------

        actual_size = TEMP_PATH.stat().st_size

        if actual_size != EXPECTED_SIZE:

            raise RuntimeError(
                f"Incomplete download.\n"
                f"Expected: {EXPECTED_SIZE:,} bytes\n"
                f"Received: {actual_size:,} bytes\n"
                "Partial file preserved. Run again to resume."
            )

        # ----------------------------------------------------
        # Validate checksum
        # ----------------------------------------------------

        print("Verifying MD5...")

        actual_md5 = calculate_md5(TEMP_PATH)

        if actual_md5 != EXPECTED_MD5:

            TEMP_PATH.unlink()

            raise RuntimeError(
                "MD5 mismatch. Corrupted download removed."
            )

        # ----------------------------------------------------
        # Publish
        # ----------------------------------------------------

        TEMP_PATH.replace(HEALPIX_PATH)

        print()
        print("======================================")
        print("Download complete!")
        print(f"File: {HEALPIX_PATH}")
        print(f"Size: {actual_size / 1e9:.2f} GB")
        print(f"MD5:  {actual_md5}")
        print("======================================")


    except Exception:

        print()
        print("Download failed.")
        print("Any partial download has been preserved.")

        raise


# ============================================================
# Main
# ============================================================

if __name__ == "__main__":

    download()
