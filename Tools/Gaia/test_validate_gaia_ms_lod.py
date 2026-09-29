from pathlib import Path
import struct
import json


PROJECT_ROOT = Path(__file__).resolve().parents[2]

LOD_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod_test"

PARTICLE_DIR = LOD_DIR / "particles"
SOURCE_ID_DIR = LOD_DIR / "source_ids"

NODE_STRUCT = struct.Struct("<I3f3fIIQI8i")
HEADER_STRUCT = struct.Struct("<8sIIQI3ff")


RENDER_RECORD_SIZE = 20
SOURCE_ID_SIZE = 8


def main():

    metadata_path = LOD_DIR / "metadata.bin"
    summary_path = LOD_DIR / "summary.json"

    print("Checking:", LOD_DIR)
    print()

    # --------------------------------------------------------
    # Summary
    # --------------------------------------------------------

    with summary_path.open("r", encoding="utf-8") as f:
        summary = json.load(f)

    expected_stars = summary["total_stars"]
    expected_nodes = summary["total_nodes"]

    print(f"Expected stars: {expected_stars:,}")
    print(f"Expected nodes: {expected_nodes:,}")

    # --------------------------------------------------------
    # Metadata
    # --------------------------------------------------------

    with metadata_path.open("rb") as f:

        header = f.read(HEADER_STRUCT.size)

        if len(header) != HEADER_STRUCT.size:
            raise RuntimeError("Metadata header is truncated.")

        (
            magic,
            version,
            node_count,
            total_star_count,
            n_sigma,
            root_x,
            root_y,
            root_z,
            root_size,
        ) = HEADER_STRUCT.unpack(header)

        if magic != b"MSLOD001":
            raise RuntimeError(
                f"Bad metadata magic: {magic!r}"
            )

        print(f"Metadata version: {version}")
        print(f"Metadata nodes:   {node_count:,}")
        print(f"Metadata stars:   {total_star_count:,}")
        print(f"N_SIGMA:          {n_sigma:,}")

        if node_count != expected_nodes:
            raise RuntimeError(
                "Metadata node count does not match summary."
            )

        if total_star_count != expected_stars:
            raise RuntimeError(
                "Metadata star count does not match summary."
            )

        nodes = []

        for _ in range(node_count):

            raw = f.read(NODE_STRUCT.size)

            if len(raw) != NODE_STRUCT.size:
                raise RuntimeError(
                    "Metadata ended before all nodes were read."
                )

            values = NODE_STRUCT.unpack(raw)

            node_id = values[0]

            depth = values[7]
            star_count = values[8]
            cumulative_count = values[9]
            child_count = values[10]

            children = values[11:19]

            nodes.append({
                "id": node_id,
                "depth": depth,
                "star_count": star_count,
                "cumulative_count": cumulative_count,
                "child_count": child_count,
                "children": children,
            })

    # --------------------------------------------------------
    # Node IDs
    # --------------------------------------------------------

    for expected_id, node in enumerate(nodes):

        if node["id"] != expected_id:
            raise RuntimeError(
                f"Node ID mismatch: expected {expected_id}, "
                f"got {node['id']}"
            )

    # --------------------------------------------------------
    # Root
    # --------------------------------------------------------

    if nodes[0]["depth"] != 0:
        raise RuntimeError(
            "Node 0 is not depth 0."
        )

    if nodes[0]["star_count"] != min(
        n_sigma,
        expected_stars
    ):
        raise RuntimeError(
            "Root star count is incorrect."
        )

    # --------------------------------------------------------
    # Parent / child references
    # --------------------------------------------------------

    for node in nodes:

        for child_id in node["children"]:

            if child_id < 0:
                continue

            if child_id >= len(nodes):
                raise RuntimeError(
                    f"Node {node['id']} references invalid "
                    f"child {child_id}."
                )

            child = nodes[child_id]

            if child["depth"] != node["depth"] + 1:
                raise RuntimeError(
                    f"Depth error: node {node['id']} -> "
                    f"child {child_id}"
                )

    # --------------------------------------------------------
    # Direct star count
    # --------------------------------------------------------

    direct_total = sum(
        node["star_count"]
        for node in nodes
    )

    print(
        f"Direct stars across nodes: "
        f"{direct_total:,}"
    )

    if direct_total != expected_stars:
        raise RuntimeError(
            "Direct node star counts do not sum "
            "to total star count."
        )

    # --------------------------------------------------------
    # Particle files
    # --------------------------------------------------------

    particle_files = sorted(
        PARTICLE_DIR.glob("particles_*.bin")
    )

    if len(particle_files) != expected_nodes:
        raise RuntimeError(
            f"Expected {expected_nodes} particle files, "
            f"found {len(particle_files)}."
        )

    particle_stars = 0

    for node_id, path in enumerate(particle_files):

        size = path.stat().st_size

        if size % RENDER_RECORD_SIZE != 0:
            raise RuntimeError(
                f"Invalid particle file size:\n{path}"
            )

        count = size // RENDER_RECORD_SIZE

        if count != nodes[node_id]["star_count"]:
            raise RuntimeError(
                f"Particle count mismatch for node {node_id}: "
                f"metadata={nodes[node_id]['star_count']}, "
                f"file={count}"
            )

        particle_stars += count

    print(
        f"Stars in particle files: "
        f"{particle_stars:,}"
    )

    if particle_stars != expected_stars:
        raise RuntimeError(
            "Particle files do not contain "
            "the expected number of stars."
        )

    # --------------------------------------------------------
    # Source ID files
    # --------------------------------------------------------

    source_files = sorted(
        SOURCE_ID_DIR.glob("source_ids_*.bin")
    )

    if len(source_files) != expected_nodes:
        raise RuntimeError(
            f"Expected {expected_nodes} source-ID files, "
            f"found {len(source_files)}."
        )

    source_stars = 0

    for node_id, path in enumerate(source_files):

        size = path.stat().st_size

        if size % SOURCE_ID_SIZE != 0:
            raise RuntimeError(
                f"Invalid source ID file size:\n{path}"
            )

        count = size // SOURCE_ID_SIZE

        if count != nodes[node_id]["star_count"]:
            raise RuntimeError(
                f"Source ID count mismatch for node {node_id}: "
                f"metadata={nodes[node_id]['star_count']}, "
                f"file={count}"
            )

        source_stars += count

    print(
        f"Stars in source-ID files: "
        f"{source_stars:,}"
    )

    if source_stars != expected_stars:
        raise RuntimeError(
            "Source-ID files do not contain "
            "the expected number of stars."
        )

    # --------------------------------------------------------
    # Final
    # --------------------------------------------------------

    print()
    print("==========================================")
    print("LOD VALIDATION PASSED")
    print("==========================================")
    print("Hierarchy:       OK")
    print("Star counts:     OK")
    print("Particle files:  OK")
    print("Source IDs:      OK")
    print()

if __name__ == "__main__":
    main()