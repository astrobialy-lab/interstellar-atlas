from pathlib import Path
from urllib.request import Request, urlopen


# ============================================================
# Paths
# ============================================================

# Tools/SpaceBackground/download_milky_way.py
PROJECT_ROOT = Path(__file__).resolve().parents[2]

OUTPUT_DIR = (
    PROJECT_ROOT
    / "Assets"
    / "Textures"
    / "SpaceBackground"
)

OUTPUT_PATH = OUTPUT_DIR / "milky_way_panorama_eso0932a.jpg"
TEMP_PATH = OUTPUT_DIR / "milky_way_panorama_eso0932a.jpg.part"
ATTRIBUTION_PATH = OUTPUT_DIR / "ATTRIBUTION.txt"


# ============================================================
# Source
# ============================================================

IMAGE_URL = "https://cdn.eso.org/images/publicationjpg/eso0932a.jpg"

CHUNK_SIZE = 1024 * 1024
MIN_IMAGE_SIZE = 500_000


def is_valid_jpeg(path: Path) -> bool:
    """Check that the file looks like a complete JPEG download."""
    try:
        if path.stat().st_size < MIN_IMAGE_SIZE:
            return False

        with path.open("rb") as file:
            return file.read(3) == b"\xff\xd8\xff"

    except OSError:
        return False


def download_image() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    if is_valid_jpeg(OUTPUT_PATH):
        print(f"Image already exists: {OUTPUT_PATH}")
    else:
        print(f"Downloading Milky Way panorama:\n{IMAGE_URL}")

        request = Request(
            IMAGE_URL,
            headers={
                "User-Agent": "SpaceSimulationAssetDownloader/1.0"
            },
        )

        try:
            with urlopen(request, timeout=90) as response:
                with TEMP_PATH.open("wb") as output:
                    while True:
                        chunk = response.read(CHUNK_SIZE)

                        if not chunk:
                            break

                        output.write(chunk)

            if not is_valid_jpeg(TEMP_PATH):
                raise ValueError(
                    "The download is incomplete or is not a valid JPEG."
                )

            TEMP_PATH.replace(OUTPUT_PATH)

            size_mb = OUTPUT_PATH.stat().st_size / (1024 * 1024)

            print(f"Downloaded: {OUTPUT_PATH}")
            print(f"File size: {size_mb:.2f} MB")

        except Exception:
            TEMP_PATH.unlink(missing_ok=True)
            raise

    ATTRIBUTION_PATH.write_text(
        "Title: The Milky Way Panorama\n"
        "Credit: ESO/S. Brunier\n"
        "Source: https://www.eso.org/public/images/eso0932a/\n"
        "License: Creative Commons Attribution 4.0 International "
        "(CC BY 4.0)\n"
        "License URL: https://creativecommons.org/licenses/by/4.0/\n",
        encoding="utf-8",
    )

    print(f"Attribution saved: {ATTRIBUTION_PATH}")
    print("Done.")


if __name__ == "__main__":
    try:
        download_image()
    except Exception as error:
        raise SystemExit(f"Download failed: {error}") from error