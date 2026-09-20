from pathlib import Path
import struct
import math

from astropy_healpix import HEALPix
import astropy.units as u
from astropy.coordinates import Longitude, Latitude


# ------------------------------------------------------------
# Paths
# ------------------------------------------------------------

PROJECT_ROOT = Path(__file__).resolve().parents[2]

CHUNKS_DIR = (
    PROJECT_ROOT
    / "Data"
    / "Gaia"
    / "processed"
    / "chunks"
)


# ------------------------------------------------------------
# HEALPix configuration
# ------------------------------------------------------------

LEVEL = 6
NSIDE = 2 ** LEVEL

healpix = HEALPix(
    nside=NSIDE,
    order="nested",
    frame=None,
)


# ------------------------------------------------------------
# Gaia star binary format
# ------------------------------------------------------------

STAR_STRUCT = struct.Struct(
    "<QffffffffffffffB"
)


# ------------------------------------------------------------
# Read stars from one chunk
# ------------------------------------------------------------

def read_stars(healpix_id, shell_id, count=5):

    path = (
        CHUNKS_DIR
        / f"healpix_{healpix_id:05d}"
        / f"shell_{shell_id:02d}.bin"
    )

    print()
    print(f"Reading: {path}")

    if not path.exists():
        raise FileNotFoundError(path)

    with path.open("rb") as f:

        for i in range(count):

            data = f.read(STAR_STRUCT.size)

            if not data:
                break

            if len(data) != STAR_STRUCT.size:
                raise RuntimeError(
                    "Incomplete star record"
                )

            values = STAR_STRUCT.unpack(data)

            source_id = values[0]

            x = values[1]
            y = values[2]
            z = values[3]

            yield i, source_id, x, y, z


# ------------------------------------------------------------
# Convert Cartesian position -> nested HEALPix
# ------------------------------------------------------------

def position_to_healpix(x, y, z):

    r = math.sqrt(
        x * x +
        y * y +
        z * z
    )

    if r == 0:
        raise ValueError(
            "Cannot calculate HEALPix for origin."
        )

    # Gaia coordinates:
    #
    # x, y, z
    #
    # Convert to spherical coordinates.
    #
    # longitude: 0 .. 360 degrees
    # latitude:  -90 .. +90 degrees

    lon = math.degrees(
        math.atan2(y, x)
    )

    if lon < 0:
        lon += 360.0

    lat = math.degrees(
        math.asin(z / r)
    )

    pixel = healpix.lonlat_to_healpix(
        Longitude(lon, unit=u.deg),
        Latitude(lat, unit=u.deg),
    )

    return int(pixel)


# ------------------------------------------------------------
# Test known chunks
# ------------------------------------------------------------

def test_chunk(healpix_id, shell_id, count=5):

    print()
    print("=" * 60)
    print(
        f"Testing HEALPix={healpix_id}, "
        f"shell={shell_id}"
    )
    print("=" * 60)

    stars = list(
        read_stars(
            healpix_id,
            shell_id,
            count
        )
    )

    for i, source_id, x, y, z in stars:

        calculated = position_to_healpix(
            x, y, z
        )

        print()
        print(f"Star {i}")
        print(f"  source_id: {source_id}")
        print(
            f"  position: "
            f"({x:.6f}, {y:.6f}, {z:.6f})"
        )
        print(f"  expected HEALPix: {healpix_id}")
        print(f"  calculated HEALPix: {calculated}")

        if calculated == healpix_id:
            print("  RESULT: PASS")
        else:
            print("  RESULT: FAIL")


# ------------------------------------------------------------
# Main
# ------------------------------------------------------------

if __name__ == "__main__":

    print("Gaia HEALPix test")
    print()
    print(f"HEALPix level: {LEVEL}")
    print(f"NSIDE: {NSIDE}")

    # Test several real chunks.
    #
    # The directory itself tells us the expected HEALPix.

    test_chunk(0, 0, 4)
    test_chunk(0, 1, 5)
    test_chunk(0, 2, 5)

    # A completely different HEALPix.
    test_chunk(19114, 0, 5)

    print()
    print("=" * 60)
    print("Finished")
    print("=" * 60)