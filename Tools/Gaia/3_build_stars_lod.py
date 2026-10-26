from __future__ import annotations

import gc
import json
import shutil
import struct
from pathlib import Path

import numpy as np


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

# Processed Gaia records created by the Gaia processing script.
INPUT_DIR = PROJECT_ROOT / "DataRaw" / "Gaia" / "processed"

# Installed/runtime data directory.
OUTPUT_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod"

# Build into a separate directory and replace OUTPUT_DIR only on success.
BUILD_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod_building"
BACKUP_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod_previous"


# ============================================================
# Build settings
# ============================================================

# The first layer contains the brightest N stars. Each following layer
# multiplies the previous layer's star count, until all valid stars are included.
# Example: 100 and 2.0 => 100, 200, 400, 800, ...; the final
# layer is capped at the exact total if the next value would exceed it.
FIRST_LAYER_STAR_COUNT = 100
LAYER_STAR_MULTIPLIER = 2.0

# A leaf node stores at most this many stars. If a node exceeds this number,
# its stars are redistributed into children and the parent stores no particles.
MAX_STARS_PER_NODE = 2_000
MAX_OCTREE_DEPTH = 20

# Number of processed records read from disk at once.
READ_BLOCK_STARS = 1_000_000

FILE_PROGRESS_INTERVAL = 25


# ============================================================
# Processed Gaia input record format (77 bytes)
# ============================================================

GAIA_DTYPE = np.dtype([
    ("source_id", "<u8"),
    ("x", "<f4"),
    ("y", "<f4"),
    ("z", "<f4"),
    ("g", "<f4"),
    ("bp_rp", "<f4"),
    ("teff", "<f4"),
    ("logg", "<f4"),
    ("metallicity", "<f4"),
    ("radius", "<f4"),
    ("a0", "<f4"),
    ("ag", "<f4"),
    ("abp", "<f4"),
    ("arp", "<f4"),
    ("ebp_rp", "<f4"),
    ("mass", "<f4"),
    ("age", "<f4"),
    ("evolstage", "<i4"),
    ("flags", "u1"),
])
assert GAIA_DTYPE.itemsize == 77


# ============================================================
# Final Unity particle record format (36 bytes)
# ============================================================

RENDER_DTYPE = np.dtype([
    ("x", "<f4"),
    ("y", "<f4"),
    ("z", "<f4"),
    ("g", "<f4"),
    ("bp_rp", "<f4"),
    ("teff", "<f4"),
    ("mass", "<f4"),
    ("age", "<f4"),
    ("evolstage", "<i4"),
])
assert RENDER_DTYPE.itemsize == 36

SOURCE_ID_DTYPE = np.dtype("<u8")


# ============================================================
# Metadata format used by the current MSLOD001 Unity loader
# ============================================================

METADATA_MAGIC = b"MSLOD001"
METADATA_VERSION = 1
HEADER_STRUCT = struct.Struct("<8sIIQI3ff")
NODE_STRUCT = struct.Struct("<I3f3fIIQI8i")


# ============================================================
# Input scan and globally brightness-sorted star collection
# ============================================================

def scan_input(files: list[Path]):
    """Count valid records and find spatial bounds without retaining the data."""
    global_minimum = None
    global_maximum = None
    valid_total = 0
    invalid_total = 0
    records_read = 0

    for file_index, path in enumerate(files, start=1):
        size = path.stat().st_size
        if size % GAIA_DTYPE.itemsize != 0:
            raise ValueError(
                f"Invalid Gaia chunk size:\n{path}\n"
                f"Size={size:,}; record size={GAIA_DTYPE.itemsize}"
            )

        remaining = size // GAIA_DTYPE.itemsize
        with path.open("rb") as source:
            while remaining > 0:
                requested = min(remaining, READ_BLOCK_STARS)
                block = np.fromfile(source, dtype=GAIA_DTYPE, count=requested)
                if len(block) != requested:
                    raise IOError(
                        f"Short read in {path}: expected {requested:,}, got {len(block):,}."
                    )
                remaining -= len(block)
                records_read += len(block)

                valid_mask = (
                    np.isfinite(block["x"])
                    & np.isfinite(block["y"])
                    & np.isfinite(block["z"])
                    & np.isfinite(block["g"])
                )
                valid = block[valid_mask]
                valid_total += len(valid)
                invalid_total += len(block) - len(valid)

                if len(valid):
                    block_min = np.array(
                        [valid["x"].min(), valid["y"].min(), valid["z"].min()],
                        dtype=np.float64,
                    )
                    block_max = np.array(
                        [valid["x"].max(), valid["y"].max(), valid["z"].max()],
                        dtype=np.float64,
                    )
                    if global_minimum is None:
                        global_minimum = block_min
                        global_maximum = block_max
                    else:
                        global_minimum = np.minimum(global_minimum, block_min)
                        global_maximum = np.maximum(global_maximum, block_max)

                del block, valid

        if (
            file_index == 1
            or file_index == len(files)
            or file_index % FILE_PROGRESS_INTERVAL == 0
        ):
            print(
                f"Scanned files: {file_index:,}/{len(files):,}; "
                f"records read: {records_read:,}; "
                f"valid stars: {valid_total:,}"
            )

    if valid_total == 0 or global_minimum is None or global_maximum is None:
        raise RuntimeError("No valid Gaia stars were found in the processed input.")

    center = (global_minimum + global_maximum) * 0.5
    root_size = float(np.max(global_maximum - global_minimum))
    if not np.isfinite(root_size) or root_size <= 0.0:
        raise RuntimeError("Cannot construct a spatial root cube from the input coordinates.")
    root_min = (center - root_size * 0.5).astype(np.float32)

    print()
    print(f"Input records read:        {records_read:,}")
    print(f"Valid records:             {valid_total:,}")
    print(f"Invalid records discarded: {invalid_total:,}")
    print(f"Spatial root minimum:      {root_min.tolist()}")
    print(f"Spatial root size:          {root_size:.6g} pc")

    return valid_total, invalid_total, root_min, root_size


def load_and_sort_all_valid_stars(files: list[Path], expected_count: int) -> np.ndarray:
    """Load all valid records once into a preallocated array and sort brightest-first."""
    stars = np.empty(expected_count, dtype=GAIA_DTYPE)
    written = 0

    for file_index, path in enumerate(files, start=1):
        remaining = path.stat().st_size // GAIA_DTYPE.itemsize
        with path.open("rb") as source:
            while remaining > 0:
                requested = min(remaining, READ_BLOCK_STARS)
                block = np.fromfile(source, dtype=GAIA_DTYPE, count=requested)
                if len(block) != requested:
                    raise IOError(
                        f"Short read in {path}: expected {requested:,}, got {len(block):,}."
                    )
                remaining -= len(block)

                valid_mask = (
                    np.isfinite(block["x"])
                    & np.isfinite(block["y"])
                    & np.isfinite(block["z"])
                    & np.isfinite(block["g"])
                )
                valid = block[valid_mask]
                count = len(valid)
                if count:
                    end = written + count
                    if end > expected_count:
                        raise RuntimeError(
                            "Input changed between scan and load: more valid stars were found."
                        )
                    stars[written:end] = valid
                    written = end

                del block, valid

        if (
            file_index == 1
            or file_index == len(files)
            or file_index % FILE_PROGRESS_INTERVAL == 0
        ):
            print(
                f"Loaded files: {file_index:,}/{len(files):,}; "
                f"valid stars loaded: {written:,}/{expected_count:,}"
            )

    if written != expected_count:
        raise RuntimeError(
            "Input changed between scan and load.\n"
            f"Expected valid stars: {expected_count:,}\n"
            f"Loaded valid stars:   {written:,}"
        )

    # g stores absolute G magnitude: lower values are brighter.
    # source_id deterministically breaks equal-magnitude ties.
    stars.sort(order=["g", "source_id"], kind="stable")

    print()
    print(f"Brightness-sorted stars: {len(stars):,}")
    print(
        f"Brightness range (absolute G): "
        f"{float(stars['g'][0]):.6g} to {float(stars['g'][-1]):.6g}"
    )
    print(f"Approximate in-memory star data: {stars.nbytes / 1e9:.3f} GB")
    return stars


def calculate_layer_counts(total_stars: int) -> list[int]:
    """Generate geometric layer sizes, capping the final layer at total_stars."""
    if total_stars < 1:
        raise ValueError("total_stars must be at least 1.")
    if FIRST_LAYER_STAR_COUNT < 1:
        raise ValueError("FIRST_LAYER_STAR_COUNT must be at least 1.")
    if not np.isfinite(LAYER_STAR_MULTIPLIER) or LAYER_STAR_MULTIPLIER <= 1.0:
        raise ValueError("LAYER_STAR_MULTIPLIER must be greater than 1.")

    counts = [min(int(FIRST_LAYER_STAR_COUNT), total_stars)]
    while counts[-1] < total_stars:
        next_count = int(round(counts[-1] * float(LAYER_STAR_MULTIPLIER)))
        if next_count <= counts[-1]:
            next_count = counts[-1] + 1
        if next_count >= total_stars:
            next_count = total_stars
        counts.append(next_count)

    return counts


# ============================================================
# Spatial hierarchy
# ============================================================

def make_node(node_id: int, parent_id: int, depth: int, cx: int, cy: int, cz: int):
    return {
        "node_id": node_id,
        "parent_id": parent_id,
        "depth": depth,
        "cx": cx,
        "cy": cy,
        "cz": cz,
        "children": [-1] * 8,
        # Only leaves hold stars. Nodes that split remain empty spatial links.
        "star_indices": [],
    }


def build_layer_hierarchy(
    stars: np.ndarray,
    root_min: np.ndarray,
    root_size: float,
    max_stars_per_node: int,
    max_depth: int,
):
    """Build a spatial octree whose particle records are stored only in leaves."""
    nodes = [make_node(0, -1, 0, 0, 0, 0)]
    scale = 1 << max_depth

    # Quantize positions once on the finest grid. These integer coordinates are
    # then used to split each node by the next octree bit, avoiding repeated float math.
    qx = np.floor(
        (stars["x"].astype(np.float64) - float(root_min[0])) / root_size * scale
    ).astype(np.int64)
    qy = np.floor(
        (stars["y"].astype(np.float64) - float(root_min[1])) / root_size * scale
    ).astype(np.int64)
    qz = np.floor(
        (stars["z"].astype(np.float64) - float(root_min[2])) / root_size * scale
    ).astype(np.int64)

    np.clip(qx, 0, scale - 1, out=qx)
    np.clip(qy, 0, scale - 1, out=qy)
    np.clip(qz, 0, scale - 1, out=qz)

    initial_indices = np.arange(len(stars), dtype=np.int64)
    assigned_count = 0

    def split_or_store(node_id: int, indices: np.ndarray):
        nonlocal assigned_count
        node = nodes[node_id]

        # This node is a leaf: it is the only kind of node that stores particles.
        if len(indices) <= max_stars_per_node:
            node["star_indices"] = indices.tolist()
            assigned_count += len(indices)
            return

        if node["depth"] >= max_depth:
            raise RuntimeError(
                f"Node {node_id} still contains {len(indices):,} stars at "
                f"MAX_OCTREE_DEPTH={max_depth}, above the leaf limit "
                f"{max_stars_per_node:,}. Increase MAX_OCTREE_DEPTH or the limit."
            )

        next_depth = node["depth"] + 1
        shift = max_depth - next_depth
        bx = (qx[indices] >> shift) & 1
        by = (qy[indices] >> shift) & 1
        bz = (qz[indices] >> shift) & 1
        octants = bx | (by << 1) | (bz << 2)

        # The parent deliberately keeps no stars when it has children.
        for octant in range(8):
            child_mask = octants == octant
            if not np.any(child_mask):
                continue

            child_indices = indices[child_mask]
            child_id = len(nodes)
            child = make_node(
                child_id,
                node_id,
                next_depth,
                node["cx"] * 2 + (octant & 1),
                node["cy"] * 2 + ((octant >> 1) & 1),
                node["cz"] * 2 + ((octant >> 2) & 1),
            )
            nodes.append(child)
            nodes[node_id]["children"][octant] = child_id
            split_or_store(child_id, child_indices)

    split_or_store(0, initial_indices)

    if assigned_count != len(stars):
        raise RuntimeError(
            f"Hierarchy assigned {assigned_count:,} stars; expected {len(stars):,}."
        )

    # Validate the intended invariant: internal nodes contain no particles;
    # leaves contain no more than MAX_STARS_PER_NODE.
    for node in nodes:
        has_children = any(child_id >= 0 for child_id in node["children"])
        local_count = len(node["star_indices"])
        if has_children and local_count != 0:
            raise RuntimeError(f"Internal node {node['node_id']} unexpectedly stores stars.")
        if not has_children and local_count > max_stars_per_node:
            raise RuntimeError(f"Leaf node {node['node_id']} exceeds the star limit.")

    print(f"  Hierarchy nodes: {len(nodes):,}; leaf stars assigned: {assigned_count:,}")
    return nodes


def node_cumulative_counts(nodes: list[dict]) -> list[int]:
    """Count a node's own stars plus all stars in its descendants."""
    cumulative = [len(node["star_indices"]) for node in nodes]
    # Nodes are appended after their parents, so reverse ID order propagates
    # each child's cumulative count to its parent after processing descendants.
    for node_id in range(len(nodes) - 1, 0, -1):
        parent_id = nodes[node_id]["parent_id"]
        cumulative[parent_id] += cumulative[node_id]
    return cumulative


# ============================================================
# Layer files
# ============================================================

def write_layer_metadata(
    layer_dir: Path,
    nodes: list[dict],
    root_min: np.ndarray,
    root_size: float,
    n_sigma: int,
    total_stars: int,
):
    metadata_path = layer_dir / "metadata.bin"
    cumulative = node_cumulative_counts(nodes)

    with metadata_path.open("wb") as output:
        output.write(
            HEADER_STRUCT.pack(
                METADATA_MAGIC,
                METADATA_VERSION,
                len(nodes),
                int(total_stars),
                int(n_sigma),
                float(root_min[0]),
                float(root_min[1]),
                float(root_min[2]),
                float(root_size),
            )
        )

        for node_id, node in enumerate(nodes):
            depth = node["depth"]
            if depth == 0:
                node_size = root_size
                minimum = root_min.astype(np.float64)
            else:
                cells_per_axis = 1 << depth
                node_size = root_size / cells_per_axis
                minimum = root_min.astype(np.float64) + np.array(
                    [node["cx"], node["cy"], node["cz"]], dtype=np.float64
                ) * node_size

            center = minimum + node_size * 0.5
            half_size = node_size * 0.5
            children = node["children"]
            child_count = sum(child_id >= 0 for child_id in children)

            output.write(
                NODE_STRUCT.pack(
                    node_id,
                    float(center[0]),
                    float(center[1]),
                    float(center[2]),
                    float(half_size),
                    float(half_size),
                    float(half_size),
                    int(depth),
                    len(node["star_indices"]),
                    int(cumulative[node_id]),
                    child_count,
                    *children,
                )
            )

    expected_size = HEADER_STRUCT.size + len(nodes) * NODE_STRUCT.size
    actual_size = metadata_path.stat().st_size
    if actual_size != expected_size:
        raise RuntimeError(
            f"Metadata size mismatch in {metadata_path}: "
            f"expected {expected_size:,}, got {actual_size:,}."
        )


def write_layer_particles(layer_dir: Path, stars: np.ndarray, nodes: list[dict]):
    particle_dir = layer_dir / "particles"
    source_id_dir = layer_dir / "source_ids"
    particle_dir.mkdir(parents=True, exist_ok=True)
    source_id_dir.mkdir(parents=True, exist_ok=True)

    total_written = 0
    for node_id, node in enumerate(nodes):
        indices = node["star_indices"]
        if not indices:
            # Internal nodes are metadata-only; only leaves have particle/source-ID files.
            continue

        group = stars[np.asarray(indices, dtype=np.int64)]
        render = np.empty(len(group), dtype=RENDER_DTYPE)
        render["x"] = group["x"]
        render["y"] = group["y"]
        render["z"] = group["z"]
        render["g"] = group["g"]
        render["bp_rp"] = group["bp_rp"]
        render["teff"] = group["teff"]
        render["mass"] = group["mass"]
        render["age"] = group["age"]
        render["evolstage"] = group["evolstage"]

        render.tofile(particle_dir / f"particles_{node_id:08d}.bin")
        np.asarray(group["source_id"], dtype=SOURCE_ID_DTYPE).tofile(
            source_id_dir / f"source_ids_{node_id:08d}.bin"
        )
        total_written += len(group)

    if total_written != len(stars):
        raise RuntimeError(
            f"Layer particle count mismatch: wrote {total_written:,}, "
            f"expected {len(stars):,}."
        )

    print(f"  Particle records written: {total_written:,}")


def write_layer_summary(
    layer_dir: Path,
    layer_index: int,
    requested_top_n: int,
    stars: np.ndarray,
    nodes: list[dict],
):
    nodes_by_depth: dict[str, int] = {}
    for node in nodes:
        key = str(node["depth"])
        nodes_by_depth[key] = nodes_by_depth.get(key, 0) + 1

    summary = {
        "metadata_version": METADATA_VERSION,
        "layer_index": layer_index,
        "requested_top_stars": int(requested_top_n),
        "actual_stars": int(len(stars)),
        "brightness_field": "absolute_g_magnitude",
        "sort_order": "ascending; lower magnitude is brighter",
        "brightest_absolute_g_magnitude": float(stars[0]["g"]),
        "faintest_absolute_g_magnitude": float(stars[-1]["g"]),
        "max_stars_per_node": MAX_STARS_PER_NODE,
        "max_octree_depth": MAX_OCTREE_DEPTH,
        "total_nodes": len(nodes),
        "nodes_by_depth": nodes_by_depth,
    }
    with (layer_dir / "summary.json").open("w", encoding="utf-8") as output:
        json.dump(summary, output, indent=2)


def build_one_layer(
    layer_index: int,
    requested_top_n: int,
    all_top_stars: np.ndarray,
    root_min: np.ndarray,
    root_size: float,
):
    actual_count = min(requested_top_n, len(all_top_stars))
    stars = all_top_stars[:actual_count]
    layer_name = f"layer_{requested_top_n}"
    layer_dir = BUILD_DIR / layer_name
    layer_dir.mkdir(parents=True, exist_ok=False)

    print()
    print("============================================================")
    print(f"BUILDING LAYER {layer_index}: TOP {requested_top_n:,} STARS")
    print("============================================================")
    print(f"Actual stars in layer: {actual_count:,}")
    print(f"Maximum stars per leaf: {MAX_STARS_PER_NODE:,}")

    nodes = build_layer_hierarchy(
        stars,
        root_min,
        root_size,
        MAX_STARS_PER_NODE,
        MAX_OCTREE_DEPTH,
    )

    write_layer_particles(layer_dir, stars, nodes)
    write_layer_metadata(
        layer_dir,
        nodes,
        root_min,
        root_size,
        MAX_STARS_PER_NODE,
        actual_count,
    )
    write_layer_summary(layer_dir, layer_index, requested_top_n, stars, nodes)

    return {
        "layer_index": layer_index,
        "directory": layer_name,
        "requested_top_stars": int(requested_top_n),
        "actual_stars": int(actual_count),
        "total_nodes": int(len(nodes)),
        "max_depth_used": int(max(node["depth"] for node in nodes)),
    }


# ============================================================
# Main
# ============================================================

def main():
    if MAX_STARS_PER_NODE < 1:
        raise ValueError("MAX_STARS_PER_NODE must be at least 1.")
    if MAX_OCTREE_DEPTH < 1 or MAX_OCTREE_DEPTH > 20:
        raise ValueError("MAX_OCTREE_DEPTH must be between 1 and 20.")
    if READ_BLOCK_STARS < 1:
        raise ValueError("READ_BLOCK_STARS must be at least 1.")
    if FIRST_LAYER_STAR_COUNT < 1:
        raise ValueError("FIRST_LAYER_STAR_COUNT must be at least 1.")
    if not np.isfinite(LAYER_STAR_MULTIPLIER) or LAYER_STAR_MULTIPLIER <= 1.0:
        raise ValueError("LAYER_STAR_MULTIPLIER must be greater than 1.")

    if not INPUT_DIR.exists():
        raise FileNotFoundError(f"Input directory does not exist:\n{INPUT_DIR}")

    files = sorted(INPUT_DIR.rglob("*.bin"))
    if not files:
        raise RuntimeError(f"No .bin files found in:\n{INPUT_DIR}")

    print("============================================================")
    print("GAIA BRIGHTNESS-BASED LOD BUILDER")
    print("============================================================")
    print(f"Input:                    {INPUT_DIR}")
    print(f"Output:                   {OUTPUT_DIR}")
    print(f"Input chunk files:        {len(files):,}")
    print(f"First layer stars:        {FIRST_LAYER_STAR_COUNT:,}")
    print(f"Layer multiplier:         {LAYER_STAR_MULTIPLIER:g}")
    print(f"Maximum stars per leaf:   {MAX_STARS_PER_NODE:,}")
    print(f"Maximum octree depth:     {MAX_OCTREE_DEPTH}")

    if BUILD_DIR.exists():
        shutil.rmtree(BUILD_DIR)
    BUILD_DIR.mkdir(parents=True, exist_ok=True)

    # First pass finds the total valid-star count and common spatial cube.
    valid_total, invalid_total, root_min, root_size = scan_input(files)
    layer_counts = calculate_layer_counts(valid_total)
    print(f"Layer star counts:        {', '.join(f'{n:,}' for n in layer_counts)}")

    # Second pass stores all valid stars once and sorts them by absolute G magnitude.
    all_stars = load_and_sort_all_valid_stars(files, valid_total)

    layer_entries = []
    for layer_index, top_n in enumerate(layer_counts, start=0):
        layer_entries.append(
            build_one_layer(
                layer_index,
                top_n,
                all_stars,
                root_min,
                root_size,
            )
        )
        gc.collect()

    manifest = {
        "metadata_magic": METADATA_MAGIC.decode("ascii"),
        "metadata_version": METADATA_VERSION,
        "brightness_field": "absolute_g_magnitude",
        "sort_order": "ascending; lower magnitude is brighter",
        "input_valid_stars": int(valid_total),
        "input_invalid_stars_discarded": int(invalid_total),
        "first_layer_star_count": int(FIRST_LAYER_STAR_COUNT),
        "layer_star_multiplier": float(LAYER_STAR_MULTIPLIER),
        "max_stars_per_node": int(MAX_STARS_PER_NODE),
        "max_octree_depth": int(MAX_OCTREE_DEPTH),
        "root_min": [float(v) for v in root_min],
        "root_size": float(root_size),
        "layers": layer_entries,
    }
    with (BUILD_DIR / "manifest.json").open("w", encoding="utf-8") as output:
        json.dump(manifest, output, indent=2)

    # Replace the old output only after every layer has built successfully.
    if BACKUP_DIR.exists():
        shutil.rmtree(BACKUP_DIR)

    had_previous_output = OUTPUT_DIR.exists()
    if had_previous_output:
        OUTPUT_DIR.rename(BACKUP_DIR)

    try:
        BUILD_DIR.rename(OUTPUT_DIR)
    except Exception:
        if had_previous_output and BACKUP_DIR.exists() and not OUTPUT_DIR.exists():
            BACKUP_DIR.rename(OUTPUT_DIR)
        raise

    if BACKUP_DIR.exists():
        shutil.rmtree(BACKUP_DIR)

    print()
    print("============================================================")
    print("GAIA LOD BUILD COMPLETE")
    print("============================================================")
    print(f"Valid input stars: {valid_total:,}")
    print(f"Invalid input stars discarded: {invalid_total:,}")
    for entry in layer_entries:
        print(
            f"{entry['directory']}: {entry['actual_stars']:,} stars, "
            f"{entry['total_nodes']:,} nodes, "
            f"max depth {entry['max_depth_used']}"
        )
    print(f"Manifest: {OUTPUT_DIR / 'manifest.json'}")
    print(f"Output:   {OUTPUT_DIR}")


if __name__ == "__main__":
    main()