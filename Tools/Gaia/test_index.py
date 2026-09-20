import struct
from pathlib import Path


INDEX_PATH = (
    Path(__file__).resolve().parents[2]
    / "Data/Gaia/processed/index.bin"
)


# Must exactly match generate_index.py
HEADER_STRUCT = struct.Struct("<IIIII")

CHUNK_STRUCT = struct.Struct("<IIHHIddQII")


with INDEX_PATH.open("rb") as f:

    # --------------------------------------------------------
    # Header
    # --------------------------------------------------------

    header = f.read(HEADER_STRUCT.size)

    (
        magic,
        version,
        level,
        shell_count,
        chunk_count,
    ) = HEADER_STRUCT.unpack(header)

    print("Index:", INDEX_PATH)
    print("Magic:", hex(magic))
    print("Version:", version)
    print("HEALPix level:", level)
    print("Shell count:", shell_count)
    print("Chunk count:", chunk_count)


    # --------------------------------------------------------
    # First 5 chunks
    # --------------------------------------------------------

    print("\nFirst 5 chunks:")

    for i in range(min(5, chunk_count)):

        data = f.read(CHUNK_STRUCT.size)

        values = CHUNK_STRUCT.unpack(data)

        (
            chunk_id,
            healpix_id,
            shell_id,
            reserved1,
            star_count,
            dmin,
            dmax,
            path_offset,
            path_length,
            reserved2,
        ) = values

        print(
            f"  chunk={chunk_id:6d} "
            f"healpix={healpix_id:5d} "
            f"shell={shell_id:2d} "
            f"stars={star_count:10d} "
            f"distance={dmin:g}-{dmax:g} pc "
            f"path_offset={path_offset}"
        )


print("\nDone")