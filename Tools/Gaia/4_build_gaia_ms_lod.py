from pathlib import Path
import shutil
import struct
import json

import numpy as np

try:
    from numba import njit, types
    from numba.typed import Dict
except ImportError as exc:
    raise RuntimeError(
        "This full Gaia MS-LOD builder requires numba.\n"
        "Install it with:\n"
        "python -m pip install numba"
    ) from exc


# Numba typed-dict key type.
#
# Key:
#   (depth, spatial_path)
#
# depth  = uint32
# path   = uint64
NODE_KEY_TYPE = types.Tuple((
    types.uint32,
    types.uint64
))

# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

INPUT_DIR = PROJECT_ROOT / "Data" / "Gaia" / "chunks"

# Final full-dataset output.
OUTPUT_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod"

# Temporary output while building.
BUILD_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod_building"


# ============================================================
# Build settings
# ============================================================

# None = use every processed Gaia chunk.
MAX_FILES = None

# Gaia Sky MS-LOD parameter.
N_SIGMA = 2_000

# 21 spatial bits per axis is far more than we should need.
#
# 2^21 = 2,097,152 cells per axis.
MAX_OCTREE_DEPTH = 21

# Initial number of node slots.
# The arrays automatically grow if needed.
NODE_INITIAL_CAPACITY = 65_536

# Hierarchy progress message interval.
PROGRESS_INTERVAL = 5_000_000

# Input-file progress message interval.
FILE_PROGRESS_INTERVAL = 5_000


# ============================================================
# Gaia processed record format
# ============================================================
#
# 65 bytes:
#
# uint64 source_id
# float  x
# float  y
# float  z
# float  G
# float  BP-RP
# float  Teff
# float  logg
# float  metallicity
# float  radius
# float  A0
# float  AG
# float  ABP
# float  ARP
# float  E(BP-RP)
# byte   flags
#

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
    ("flags", "u1"),
])

assert GAIA_DTYPE.itemsize == 65


# ============================================================
# Unity rendering record
# ============================================================

RENDER_DTYPE = np.dtype([
    ("x", "<f4"),
    ("y", "<f4"),
    ("z", "<f4"),
    ("g", "<f4"),
    ("bp_rp", "<f4"),
])

assert RENDER_DTYPE.itemsize == 20


# ============================================================
# Metadata format
# ============================================================

METADATA_MAGIC = b"MSLOD001"
METADATA_VERSION = 1

HEADER_STRUCT = struct.Struct(
    "<8sIIQI3ff"
)

NODE_STRUCT = struct.Struct(
    "<I3f3fIIQI8i"
)


# ============================================================
# Count input records
# ============================================================

def count_input_records(files):

    counts = np.empty(
        len(files),
        dtype=np.int64
    )

    total_records = 0

    for i, path in enumerate(files):

        file_size = path.stat().st_size

        if file_size % GAIA_DTYPE.itemsize != 0:
            raise ValueError(
                f"Invalid chunk size:\n"
                f"{path}\n"
                f"Size = {file_size}\n"
                f"Record size = {GAIA_DTYPE.itemsize}"
            )

        count = (
            file_size //
            GAIA_DTYPE.itemsize
        )

        counts[i] = count
        total_records += count

    return total_records, counts


# ============================================================
# Load all input files
# ============================================================

def load_input_files(files):

    total_records, record_counts = (
        count_input_records(files)
    )

    if total_records <= 0:
        raise RuntimeError(
            "No Gaia records found."
        )

    print(
        f"Total records to load: "
        f"{total_records:,}"
    )

    print(
        f"Approximate Gaia record memory: "
        f"{total_records * GAIA_DTYPE.itemsize / 1e9:.2f} GB"
    )

    records = np.empty(
        total_records,
        dtype=GAIA_DTYPE
    )

    offset = 0

    for i, path in enumerate(
        files,
        start=1
    ):

        if (
            i == 1
            or i == len(files)
            or i % FILE_PROGRESS_INTERVAL == 0
        ):
            print(
                f"Reading chunks: "
                f"{i:,}/{len(files):,}"
            )

        expected_count = int(
            record_counts[i - 1]
        )

        chunk = np.fromfile(
            path,
            dtype=GAIA_DTYPE
        )

        if len(chunk) != expected_count:
            raise IOError(
                f"Could not read entire file:\n"
                f"{path}\n"
                f"Expected records: {expected_count}\n"
                f"Read records: {len(chunk)}"
            )

        end = offset + len(chunk)

        records[offset:end] = chunk

        offset = end

    if offset != total_records:
        raise RuntimeError(
            "Input record count mismatch."
        )

    print(
        f"Loaded records: "
        f"{len(records):,}"
    )

    return records


# ============================================================
# Prepare stars
# ============================================================

def prepare_stars(records):

    x = records["x"]
    y = records["y"]
    z = records["z"]

    distance = np.sqrt(
        x * x +
        y * y +
        z * z,
        dtype=np.float32
    )

    g = records["g"]
    ag = records["ag"]

    # Extinction-corrected G when AG exists.
    g_intrinsic = np.where(
        np.isfinite(ag),
        g - ag,
        g
    )

    valid = (
        np.isfinite(x)
        & np.isfinite(y)
        & np.isfinite(z)
        & np.isfinite(g_intrinsic)
        & np.isfinite(distance)
        & (distance > 0.0)
    )

    invalid_count = np.count_nonzero(
        ~valid
    )

    if invalid_count:
        print(
            f"Discarding invalid records: "
            f"{invalid_count:,}"
        )

        records = records[valid]
        distance = distance[valid]
        g_intrinsic = g_intrinsic[valid]

    else:
        print(
            "No invalid records found."
        )

    # Absolute G magnitude:
    #
    # M = m - 5 log10(d) + 5
    #
    # d in parsecs.
    abs_mag = (
        g_intrinsic
        - 5.0 * np.log10(distance)
        + 5.0
    ).astype(
        np.float32,
        copy=False
    )

    print(
        "Sorting by absolute magnitude..."
    )

    order = np.argsort(
        abs_mag,
        kind="stable"
    )

    records = records[order]

    del order
    del abs_mag
    del distance
    del g_intrinsic
    del valid

    print(
        f"Stars after validation: "
        f"{len(records):,}"
    )

    print(
        "Sorted by absolute magnitude "
        "(brightest first)."
    )

    return records


# ============================================================
# Numba MS-LOD hierarchy construction
# ============================================================
#
# The input records are already sorted brightest -> faintest.
#
# First N_SIGMA stars:
#     root
#
# Every later star:
#
#     root
#       ↓
#     child
#       ↓
#     child
#       ↓
#     ...
#
# Continue until the first spatial node containing fewer than
# N_SIGMA stars is found.
#
# This is equivalent to the prototype hierarchy construction,
# but the expensive loop is compiled with Numba.
#
# node_lookup key:
#
#     (depth, base-8 spatial path)
#
# The depth is part of the key so paths consisting of zero
# octants cannot collide between different depths.
#

@njit
def build_hierarchy_numba(
    records,
    root_min,
    root_size,
    n_sigma,
    max_depth,
    initial_capacity,
    progress_interval
):

    star_count = records.shape[0]

    root_count = n_sigma

    if root_count > star_count:
        root_count = star_count

    capacity = initial_capacity

    if capacity < 1:
        capacity = 1

    parent_ids = np.full(
        capacity,
        -1,
        dtype=np.int32
    )

    depths = np.zeros(
        capacity,
        dtype=np.uint32
    )

    cell_x = np.zeros(
        capacity,
        dtype=np.int32
    )

    cell_y = np.zeros(
        capacity,
        dtype=np.int32
    )

    cell_z = np.zeros(
        capacity,
        dtype=np.int32
    )

    star_counts = np.zeros(
        capacity,
        dtype=np.uint32
    )

    children = np.full(
        (capacity, 8),
        -1,
        dtype=np.int32
    )

    # For every globally sorted star, store the LOD node
    # that owns that star.
    star_node_ids = np.full(
        star_count,
        -1,
        dtype=np.int32
    )

    node_lookup = Dict.empty(
        key_type=NODE_KEY_TYPE,
        value_type=types.int32
    )

    # --------------------------------------------------------
    # Root
    # --------------------------------------------------------

    node_count = 1

    node_lookup[
        (
            np.uint32(0),
            np.uint64(0)
        )
    ] = np.int32(0)

    star_counts[0] = root_count

    star_node_ids[:root_count] = 0

    scale = 1 << max_depth

    # --------------------------------------------------------
    # Remaining stars
    # --------------------------------------------------------

    for i in range(
        root_count,
        star_count
    ):

        if (
            progress_interval > 0
            and i > root_count
            and i % progress_interval == 0
        ):
            print(
                "Hierarchy progress:",
                i,
                "/",
                star_count,
                "nodes:",
                node_count
            )

        px = records[i]["x"]
        py = records[i]["y"]
        pz = records[i]["z"]

        # Quantize the position once to max-depth grid
        # coordinates. Individual octant bits are extracted
        # from these integers while descending the tree.

        qx = int(
            (px - root_min[0])
            / root_size
            * scale
        )

        qy = int(
            (py - root_min[1])
            / root_size
            * scale
        )

        qz = int(
            (pz - root_min[2])
            / root_size
            * scale
        )

        if qx < 0:
            qx = 0
        elif qx >= scale:
            qx = scale - 1

        if qy < 0:
            qy = 0
        elif qy >= scale:
            qy = scale - 1

        if qz < 0:
            qz = 0
        elif qz >= scale:
            qz = scale - 1

        path = np.uint64(0)

        parent_id = np.int32(0)

        assigned = False

        for depth in range(
            1,
            max_depth + 1
        ):

            shift = (
                max_depth - depth
            )

            bx = (
                qx >> shift
            ) & 1

            by = (
                qy >> shift
            ) & 1

            bz = (
                qz >> shift
            ) & 1

            octant = (
                bx
                | (by << 1)
                | (bz << 2)
            )

            path = (
                path << np.uint64(3)
            ) | np.uint64(octant)

            key = (
                np.uint32(depth),
                path
            )

            child_id = np.int32(-1)

            if key in node_lookup:
                child_id = node_lookup[key]

            else:

                # Grow arrays when needed.
                if node_count >= capacity:

                    new_capacity = (
                        capacity * 2
                    )

                    new_parent = np.full(
                        new_capacity,
                        -1,
                        dtype=np.int32
                    )

                    new_parent[:capacity] = (
                        parent_ids
                    )

                    parent_ids = new_parent

                    new_depths = np.zeros(
                        new_capacity,
                        dtype=np.uint32
                    )

                    new_depths[:capacity] = (
                        depths
                    )

                    depths = new_depths

                    new_cell_x = np.zeros(
                        new_capacity,
                        dtype=np.int32
                    )

                    new_cell_x[:capacity] = (
                        cell_x
                    )

                    cell_x = new_cell_x

                    new_cell_y = np.zeros(
                        new_capacity,
                        dtype=np.int32
                    )

                    new_cell_y[:capacity] = (
                        cell_y
                    )

                    cell_y = new_cell_y

                    new_cell_z = np.zeros(
                        new_capacity,
                        dtype=np.int32
                    )

                    new_cell_z[:capacity] = (
                        cell_z
                    )

                    cell_z = new_cell_z

                    new_star_counts = np.zeros(
                        new_capacity,
                        dtype=np.uint32
                    )

                    new_star_counts[:capacity] = (
                        star_counts
                    )

                    star_counts = new_star_counts

                    new_children = np.full(
                        (new_capacity, 8),
                        -1,
                        dtype=np.int32
                    )

                    new_children[
                        :capacity,
                        :
                    ] = children

                    children = new_children

                    capacity = new_capacity

                child_id = np.int32(
                    node_count
                )

                node_count += 1

                node_lookup[key] = child_id

                parent_ids[child_id] = (
                    parent_id
                )

                depths[child_id] = np.uint32(
                    depth
                )

                cell_x[child_id] = (
                    cell_x[parent_id] * 2
                    + bx
                )

                cell_y[child_id] = (
                    cell_y[parent_id] * 2
                    + by
                )

                cell_z[child_id] = (
                    cell_z[parent_id] * 2
                    + bz
                )

                children[
                    parent_id,
                    octant
                ] = child_id

            # This node still has space.
            if star_counts[child_id] < n_sigma:

                star_counts[child_id] += 1

                star_node_ids[i] = child_id

                assigned = True

                break

            # Full node: continue into its child.
            parent_id = child_id

        if not assigned:

            return (
                node_count,
                parent_ids,
                depths,
                cell_x,
                cell_y,
                cell_z,
                star_counts,
                children,
                star_node_ids,
                np.int32(i)
            )

    return (
        node_count,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children,
        star_node_ids,
        np.int32(-1)
    )


# ============================================================
# Build hierarchy
# ============================================================

def build_ms_lod(records):

    if len(records) == 0:
        raise RuntimeError(
            "No stars remain after validation."
        )

    minimum = np.array(
        [
            records["x"].min(),
            records["y"].min(),
            records["z"].min(),
        ],
        dtype=np.float32
    )

    maximum = np.array(
        [
            records["x"].max(),
            records["y"].max(),
            records["z"].max(),
        ],
        dtype=np.float32
    )

    center = (
        minimum + maximum
    ) * 0.5

    root_size = float(
        np.max(maximum - minimum)
    )

    if root_size <= 0.0:
        raise RuntimeError(
            "All stars have identical positions."
        )

    root_min = (
        center
        - root_size * 0.5
    ).astype(
        np.float32
    )

    print()
    print(
        "Building MS-LOD hierarchy..."
    )

    print(
        f"Stars: {len(records):,}"
    )

    print(
        f"N_SIGMA: {N_SIGMA:,}"
    )

    print(
        f"Root size: {root_size}"
    )

    print(
        f"Max octree depth: "
        f"{MAX_OCTREE_DEPTH}"
    )

    (
        node_count,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children,
        star_node_ids,
        failed_index
    ) = build_hierarchy_numba(
        records,
        root_min,
        root_size,
        N_SIGMA,
        MAX_OCTREE_DEPTH,
        NODE_INITIAL_CAPACITY,
        PROGRESS_INTERVAL
    )

    if failed_index >= 0:

        raise RuntimeError(
            "A star could not be assigned within "
            f"MAX_OCTREE_DEPTH={MAX_OCTREE_DEPTH}. "
            f"First failing star index: "
            f"{failed_index}"
        )

    # Trim node arrays.
    parent_ids = (
        parent_ids[:node_count]
    )

    depths = (
        depths[:node_count]
    )

    cell_x = (
        cell_x[:node_count]
    )

    cell_y = (
        cell_y[:node_count]
    )

    cell_z = (
        cell_z[:node_count]
    )

    star_counts = (
        star_counts[:node_count]
    )

    children = (
        children[:node_count]
    )

    if np.any(
        star_node_ids < 0
    ):
        raise RuntimeError(
            "Some stars were not assigned "
            "to an LOD node."
        )

    assigned_count = int(
        np.sum(
            star_counts,
            dtype=np.uint64
        )
    )

    if assigned_count != len(records):

        raise RuntimeError(
            "LOD star count mismatch.\n"
            f"Assigned: {assigned_count:,}\n"
            f"Input:    {len(records):,}"
        )

    depth_counts = np.bincount(
        depths.astype(np.int64)
    )

    print()
    print(
        f"Total nodes: {node_count:,}"
    )

    print(
        f"Maximum depth: "
        f"{int(depths.max())}"
    )

    for depth, count in enumerate(
        depth_counts
    ):

        if count > 0:

            print(
                f"  depth {depth}: "
                f"{int(count):,} nodes"
            )

    return (
        root_min,
        root_size,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children,
        star_node_ids
    )


# ============================================================
# Cumulative star counts
# ============================================================

def compute_cumulative_counts(
    parent_ids,
    star_counts
):

    cumulative = (
        star_counts.astype(
            np.uint64,
            copy=True
        )
    )

    # Child IDs are always greater than parent IDs.
    for node_id in range(
        len(parent_ids) - 1,
        0,
        -1
    ):

        parent_id = int(
            parent_ids[node_id]
        )

        if parent_id >= 0:

            cumulative[parent_id] += (
                cumulative[node_id]
            )

    return cumulative


# ============================================================
# Write particle/source files
# ============================================================

def write_particles(
    directory,
    records,
    star_counts,
    star_node_ids
):

    particle_dir = (
        directory / "particles"
    )

    source_id_dir = (
        directory / "source_ids"
    )

    particle_dir.mkdir(
        parents=True,
        exist_ok=True
    )

    source_id_dir.mkdir(
        parents=True,
        exist_ok=True
    )

    print()
    print(
        "Grouping stars by LOD node..."
    )

    # Stable sort means stars remain in global
    # brightness order within each node.
    sort_order = np.argsort(
        star_node_ids,
        kind="stable"
    )

    offsets = np.empty(
        len(star_counts) + 1,
        dtype=np.int64
    )

    offsets[0] = 0

    offsets[1:] = np.cumsum(
        star_counts,
        dtype=np.int64
    )

    print(
        "Writing particle/source files..."
    )

    node_count = len(
        star_counts
    )

    for node_id in range(
        node_count
    ):

        start = int(
            offsets[node_id]
        )

        end = int(
            offsets[node_id + 1]
        )

        indices = (
            sort_order[start:end]
        )

        render = np.empty(
            len(indices),
            dtype=RENDER_DTYPE
        )

        render["x"] = (
            records["x"][indices]
        )

        render["y"] = (
            records["y"][indices]
        )

        render["z"] = (
            records["z"][indices]
        )

        render["g"] = (
            records["g"][indices]
        )

        render["bp_rp"] = (
            records["bp_rp"][indices]
        )

        particle_path = (
            particle_dir
            / f"particles_{node_id:08d}.bin"
        )

        render.tofile(
            particle_path
        )

        source_ids = np.asarray(
            records["source_id"][indices],
            dtype="<u8"
        )

        source_path = (
            source_id_dir
            / f"source_ids_{node_id:08d}.bin"
        )

        source_ids.tofile(
            source_path
        )

        if (
            node_id > 0
            and node_id % 10_000 == 0
        ):

            print(
                f"  Wrote "
                f"{node_id:,}/{node_count:,} nodes"
            )

    del sort_order
    del offsets


# ============================================================
# Write metadata
# ============================================================

def write_metadata(
    directory,
    root_min,
    root_size,
    parent_ids,
    depths,
    cell_x,
    cell_y,
    cell_z,
    star_counts,
    children,
    total_star_count
):

    path = (
        directory /
        "metadata.bin"
    )

    cumulative = (
        compute_cumulative_counts(
            parent_ids,
            star_counts
        )
    )

    with path.open("wb") as f:

        f.write(
            HEADER_STRUCT.pack(
                METADATA_MAGIC,
                METADATA_VERSION,
                len(star_counts),
                int(total_star_count),
                N_SIGMA,
                float(root_min[0]),
                float(root_min[1]),
                float(root_min[2]),
                float(root_size),
            )
        )

        for node_id in range(
            len(star_counts)
        ):

            depth = int(
                depths[node_id]
            )

            if depth == 0:

                node_size = root_size
                minimum = root_min

            else:

                cells_per_axis = (
                    1 << depth
                )

                node_size = (
                    root_size /
                    cells_per_axis
                )

                minimum = (
                    root_min
                    + np.array(
                        [
                            cell_x[node_id],
                            cell_y[node_id],
                            cell_z[node_id],
                        ],
                        dtype=np.float32
                    )
                    * node_size
                )

            center = (
                minimum
                + node_size * 0.5
            )

            half_size = (
                node_size * 0.5
            )

            child_ids = (
                children[node_id]
            )

            child_count = int(
                np.count_nonzero(
                    child_ids >= 0
                )
            )

            f.write(
                NODE_STRUCT.pack(
                    node_id,

                    float(center[0]),
                    float(center[1]),
                    float(center[2]),

                    float(half_size),
                    float(half_size),
                    float(half_size),

                    depth,

                    int(
                        star_counts[node_id]
                    ),

                    int(
                        cumulative[node_id]
                    ),

                    child_count,

                    *child_ids.tolist()
                )
            )

    print(
        f"Metadata written: {path}"
    )


# ============================================================
# Write summary
# ============================================================

def write_summary(
    directory,
    depths,
    total_star_count
):

    nodes_by_depth = {}

    for depth in depths:

        key = str(
            int(depth)
        )

        nodes_by_depth[key] = (
            nodes_by_depth.get(
                key,
                0
            )
            + 1
        )

    summary = {
        "version": METADATA_VERSION,
        "n_sigma": N_SIGMA,
        "total_stars": int(
            total_star_count
        ),
        "total_nodes": int(
            len(depths)
        ),
        "max_depth": int(
            depths.max()
        ),
        "nodes_by_depth": nodes_by_depth,
    }

    path = (
        directory /
        "summary.json"
    )

    with path.open(
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            summary,
            f,
            indent=2
        )


# ============================================================
# Main
# ============================================================

def main():

    # --------------------------------------------------------
    # Input validation
    # --------------------------------------------------------

    if not INPUT_DIR.exists():

        raise FileNotFoundError(
            f"Input directory does not exist:\n"
            f"{INPUT_DIR}"
        )

    files = sorted(
        INPUT_DIR.rglob("*.bin")
    )

    if not files:

        raise RuntimeError(
            f"No .bin files found in:\n"
            f"{INPUT_DIR}"
        )

    if MAX_FILES is not None:

        files = files[:MAX_FILES]

    print(
        f"Using {len(files):,} input chunk files."
    )

    print(
        f"Input:\n{INPUT_DIR}"
    )

    print(
        f"Output:\n{OUTPUT_DIR}"
    )

    # --------------------------------------------------------
    # Clean temporary build directory
    # --------------------------------------------------------

    if BUILD_DIR.exists():

        shutil.rmtree(
            BUILD_DIR
        )

    BUILD_DIR.mkdir(
        parents=True
    )

    # --------------------------------------------------------
    # Load
    # --------------------------------------------------------

    records = load_input_files(
        files
    )

    # --------------------------------------------------------
    # Validate + sort
    # --------------------------------------------------------

    records = prepare_stars(
        records
    )

    # --------------------------------------------------------
    # Build hierarchy
    # --------------------------------------------------------

    (
        root_min,
        root_size,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children,
        star_node_ids
    ) = build_ms_lod(
        records
    )

    # --------------------------------------------------------
    # Write files
    # --------------------------------------------------------

    write_particles(
        BUILD_DIR,
        records,
        star_counts,
        star_node_ids
    )

    write_metadata(
        BUILD_DIR,
        root_min,
        root_size,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children,
        len(records)
    )

    write_summary(
        BUILD_DIR,
        depths,
        len(records)
    )

    # --------------------------------------------------------
    # Replace old full output only after success.
    # --------------------------------------------------------

    if OUTPUT_DIR.exists():

        shutil.rmtree(
            OUTPUT_DIR
        )

    BUILD_DIR.rename(
        OUTPUT_DIR
    )

    # --------------------------------------------------------
    # Final report
    # --------------------------------------------------------

    print()
    print(
        "=========================================="
    )
    print(
        "FULL MS-LOD BUILD COMPLETE"
    )
    print(
        "=========================================="
    )

    print(
        f"Stars:       {len(records):,}"
    )

    print(
        f"Nodes:       {len(star_counts):,}"
    )

    print(
        f"Max depth:   "
        f"{int(depths.max())}"
    )

    print(
        f"Metadata:    "
        f"{OUTPUT_DIR / 'metadata.bin'}"
    )

    print(
        f"Particles:   "
        f"{OUTPUT_DIR / 'particles'}"
    )

    print(
        f"Source IDs:  "
        f"{OUTPUT_DIR / 'source_ids'}"
    )


if __name__ == "__main__":
    main()