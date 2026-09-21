from pathlib import Path
import struct


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
# uint64 file_offset
# uint32 file_size
#
# uint32 reserved
#
CHUNK_STRUCT = struct.Struct(
    "<IIHHIddQII"
)


MAGIC = 0x47414941  # "GAIA"
VERSION = 1


# ============================================================
# Helpers
# ============================================================

def count_stars(file_path):
    """
    Determine the number of stars in a binary chunk.

    The star record size is 65 bytes.
    """

    STAR_SIZE = 65

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
                    "file_path": str(relative_path),
                }
            )

            chunk_id += 1

        if (
            i % 100 == 0
            or i == len(healpix_dirs)
        ):

            print(
                f"Scanned "
                f"{i:,}/{len(healpix_dirs):,} "
                f"HEALPix directories"
            )

    print()
    print(
        f"Non-empty chunks: {len(chunks):,}"
    )

    # --------------------------------------------------------
    # Build string table
    # --------------------------------------------------------

    # We don't want variable-length strings inside each
    # chunk record.
    #
    # Instead:
    #
    # chunk record -> path offset
    # string table -> actual path
    #
    # For now we use a simple UTF-8 string table.

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
        f"Wrote index:"
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
