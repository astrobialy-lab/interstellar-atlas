from pathlib import Path
import struct
import math


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

PROCESSED_DIR = PROJECT_ROOT / "Data" / "Gaia"
CHUNKS_DIR = PROCESSED_DIR / "chunks"

INDEX_FILE = PROCESSED_DIR / "index.bin"


# ============================================================
# Gaia / HEALPix settings
# ============================================================

HEALPIX_LEVEL = 6


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
    float("inf"),
]


# ============================================================
# Binary formats
# ============================================================

# Index header:
#
# uint32 magic
# uint32 version
# uint32 healpix_level
# uint32 shell_count
# uint32 chunk_count
#
HEADER_STRUCT = struct.Struct(
    "<IIIII"
)


# Chunk:
#
# uint32 chunk_id
# uint32 healpix_id
# uint16 shell_id
# uint16 reserved
#
# uint32 star_count
#
# float64 distance_min
# float64 distance_max
#
# float32 min_x
# float32 min_y
# float32 min_z
#
# float32 max_x
# float32 max_y
# float32 max_z
#
# uint64 path_offset
# uint32 path_length
#
# uint32 reserved
#
CHUNK_STRUCT = struct.Struct(
    "<IIHHIddffffffQII"
)


# Gaia star record:
#
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
#
STAR_STRUCT = struct.Struct(
    "<QffffffffffffffB"
)

STAR_SIZE = STAR_STRUCT.size


# Read this much data from each file at once.
#
# 8 MB is large enough to greatly reduce disk I/O calls
# while keeping memory usage tiny.
READ_BLOCK_SIZE = 8 * 1024 * 1024


MAGIC = 0x47414941  # "GAIA"
VERSION = 2


# ============================================================
# Helpers
# ============================================================

def count_stars(file_path):

    size = file_path.stat().st_size

    if size % STAR_SIZE != 0:

        raise RuntimeError(
            f"Invalid binary size for {file_path}: "
            f"{size} bytes is not divisible by {STAR_SIZE}"
        )

    return size // STAR_SIZE


def parse_healpix_id(directory_name):

    # Example:
    # healpix_12345 -> 12345

    return int(
        directory_name.split("_")[1]
    )


def calculate_bounds(file_path, star_count):

    """
    Calculate the axis-aligned bounding box of a chunk.

    The file is read in large blocks instead of one star
    at a time to reduce disk I/O overhead.
    """

    min_x = math.inf
    min_y = math.inf
    min_z = math.inf

    max_x = -math.inf
    max_y = -math.inf
    max_z = -math.inf

    remaining_stars = star_count

    with file_path.open("rb") as f:

        while remaining_stars > 0:

            # Number of stars that fit into this block.
            stars_in_block = min(
                remaining_stars,
                READ_BLOCK_SIZE // STAR_SIZE
            )

            bytes_to_read = (
                stars_in_block * STAR_SIZE
            )

            data = f.read(bytes_to_read)

            if len(data) != bytes_to_read:

                raise RuntimeError(
                    f"Unexpected end of file: "
                    f"{file_path}"
                )

            # Iterate over every complete star record
            # without creating individual byte slices.
            for values in STAR_STRUCT.iter_unpack(data):

                # Layout:
                #
                # 0 = source_id
                # 1 = x
                # 2 = y
                # 3 = z

                x = values[1]
                y = values[2]
                z = values[3]

                if not (
                    math.isfinite(x)
                    and math.isfinite(y)
                    and math.isfinite(z)
                ):
                    continue

                if x < min_x:
                    min_x = x

                if y < min_y:
                    min_y = y

                if z < min_z:
                    min_z = z

                if x > max_x:
                    max_x = x

                if y > max_y:
                    max_y = y

                if z > max_z:
                    max_z = z

            remaining_stars -= stars_in_block

    if not (
        math.isfinite(min_x)
        and math.isfinite(min_y)
        and math.isfinite(min_z)
        and math.isfinite(max_x)
        and math.isfinite(max_y)
        and math.isfinite(max_z)
    ):
        raise RuntimeError(
            f"No valid positions found in {file_path}"
        )

    return (
        min_x,
        min_y,
        min_z,
        max_x,
        max_y,
        max_z,
    )


# ============================================================
# Main
# ============================================================

if __name__ == "__main__":

    print("======================================")
    print("Generating Gaia chunk index")
    print("======================================")
    print()

    if not CHUNKS_DIR.exists():

        raise RuntimeError(
            f"Chunks directory not found:\n{CHUNKS_DIR}"
        )

    healpix_dirs = sorted(
        path
        for path in CHUNKS_DIR.iterdir()
        if path.is_dir()
        and path.name.startswith("healpix_")
    )

    print(
        f"Found {len(healpix_dirs):,} HEALPix directories."
    )

    print(
        f"Star record size: {STAR_SIZE} bytes"
    )

    print(
        f"Read block size: "
        f"{READ_BLOCK_SIZE / (1024 * 1024):.0f} MB"
    )

    print()

    chunks = []

    chunk_id = 0

    # --------------------------------------------------------
    # Scan every HEALPix directory
    # --------------------------------------------------------

    for i, healpix_dir in enumerate(
        healpix_dirs,
        1
    ):

        healpix_id = parse_healpix_id(
            healpix_dir.name
        )

        for shell_id in range(
            len(DISTANCE_BINS) - 1
        ):

            file_path = (
                healpix_dir
                / f"shell_{shell_id:02d}.bin"
            )

            if not file_path.exists():
                continue

            star_count = count_stars(
                file_path
            )

            # Don't create entries for empty files.
            if star_count == 0:
                continue

            (
                min_x,
                min_y,
                min_z,
                max_x,
                max_y,
                max_z,
            ) = calculate_bounds(
                file_path,
                star_count
            )

            distance_min = (
                DISTANCE_BINS[shell_id]
            )

            distance_max = (
                DISTANCE_BINS[shell_id + 1]
            )

            # Store path relative to processed/
            relative_path = file_path.relative_to(
                PROCESSED_DIR
            )

            chunks.append(
                {
                    "chunk_id": chunk_id,
                    "healpix_id": healpix_id,
                    "shell_id": shell_id,
                    "star_count": star_count,

                    "distance_min": distance_min,
                    "distance_max": distance_max,

                    "min_x": min_x,
                    "min_y": min_y,
                    "min_z": min_z,

                    "max_x": max_x,
                    "max_y": max_y,
                    "max_z": max_z,

                    "file_path": str(relative_path),
                }
            )

            chunk_id += 1

        # Progress only once per 100 HEALPix directories.
        if (
            i % 100 == 0
            or i == len(healpix_dirs)
        ):

            print(
                f"Scanned "
                f"{i:,}/{len(healpix_dirs):,} "
                f"HEALPix directories | "
                f"chunks: {len(chunks):,}"
            )

    print()
    print(
        f"Non-empty chunks: {len(chunks):,}"
    )

    # --------------------------------------------------------
    # Build string table
    # --------------------------------------------------------

    string_table = bytearray()

    path_offsets = []

    for chunk in chunks:

        path = chunk["file_path"].encode("utf-8")

        offset = len(string_table)

        string_table.extend(path)
        string_table.append(0)

        path_offsets.append(offset)

    # --------------------------------------------------------
    # Write index
    # --------------------------------------------------------

    with INDEX_FILE.open("wb") as f:

        # Header

        f.write(
            HEADER_STRUCT.pack(
                MAGIC,
                VERSION,
                HEALPIX_LEVEL,
                len(DISTANCE_BINS) - 1,
                len(chunks),
            )
        )

        # Chunk records

        for chunk, path_offset in zip(
            chunks,
            path_offsets
        ):

            f.write(
                CHUNK_STRUCT.pack(

                    chunk["chunk_id"],

                    chunk["healpix_id"],

                    chunk["shell_id"],

                    0,

                    chunk["star_count"],

                    chunk["distance_min"],

                    chunk["distance_max"],

                    chunk["min_x"],
                    chunk["min_y"],
                    chunk["min_z"],

                    chunk["max_x"],
                    chunk["max_y"],
                    chunk["max_z"],

                    path_offset,

                    len(
                        chunk["file_path"].encode(
                            "utf-8"
                        )
                    ),

                    0,
                )
            )

        # String table

        f.write(string_table)

    print()
    print(
        "Wrote index:"
    )

    print(
        f"  {INDEX_FILE}"
    )

    print(
        f"  Size: "
        f"{INDEX_FILE.stat().st_size:,} bytes"
    )

    print()
    print("======================================")
    print("Done")
    print("======================================")