
from pathlib import Path
import shutil
import struct
import json
import gc

import numpy as np

try:
    from numba import njit, types
    from numba.typed import Dict
except ImportError as exc:
    raise RuntimeError(
        "This Gaia MS-LOD builder requires numba.\n"
        "Install it with:\n"
        "python -m pip install numba"
    ) from exc


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

INPUT_DIR = PROJECT_ROOT / "DataRaw" / "Gaia" / "processed"
OUTPUT_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod"
BUILD_DIR = PROJECT_ROOT / "Data" / "Gaia" / "lod_building"

RUN_DIR = BUILD_DIR / "_sort_runs"
PARTITION_DIR = BUILD_DIR / "_node_partitions"


# ============================================================
# Build settings
# ============================================================

# None = use every processed Gaia chunk.
MAX_FILES = None

N_SIGMA = 2_000
MAX_OCTREE_DEPTH = 21
NODE_INITIAL_CAPACITY = 65_536

# Maximum number of records held while making one sorted run.
SORT_RUN_STARS = 2_000_000

# Number of records loaded from the final sorted stream at once.
PROCESS_BATCH_STARS = 2_000_000

# Input blocks used by the pairwise external merges.
MERGE_BLOCK_STARS = 262_144

# Final output partitioning.
#
# Node IDs are grouped into ranges of this many nodes.
# Each partition is therefore bounded by roughly:
#
#     NODE_PARTITION_SPAN * N_SIGMA * 32 bytes
#
# before accounting for nodes that contain fewer than N_SIGMA stars.
NODE_PARTITION_SPAN = 1_024

PROGRESS_INTERVAL = 5_000_000
FILE_PROGRESS_INTERVAL = 5_000


# ============================================================
# Gaia processed record format
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
    ("flags", "u1"),
])

assert GAIA_DTYPE.itemsize == 65


# ============================================================
# Temporary sorted-run record
# ============================================================

# absolute magnitude + everything needed later
#
# 4 + 8 + 5*4 = 32 bytes/star
SORT_DTYPE = np.dtype([
    ("abs_mag", "<f4"),
    ("source_id", "<u8"),
    ("x", "<f4"),
    ("y", "<f4"),
    ("z", "<f4"),
    ("g", "<f4"),
    ("bp_rp", "<f4"),
])

assert SORT_DTYPE.itemsize == 32


# ============================================================
# Temporary node-partition record
# ============================================================

PARTITION_DTYPE = np.dtype([
    ("node_id", "<u4"),
    ("source_id", "<u8"),
    ("x", "<f4"),
    ("y", "<f4"),
    ("z", "<f4"),
    ("g", "<f4"),
    ("bp_rp", "<f4"),
])

assert PARTITION_DTYPE.itemsize == 32


# ============================================================
# Final Unity rendering record
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

HEADER_STRUCT = struct.Struct("<8sIIQI3ff")

NODE_STRUCT = struct.Struct("<I3f3fIIQI8i")


# ============================================================
# Numba node-key type
# ============================================================

# Both components are uint64 to keep the typed-dict key simple.
#
# key[0] = depth
# key[1] = spatial path
NODE_KEY_TYPE = types.UniTuple(types.uint64, 2)


# ============================================================
# Make one sorted run
# ============================================================

def prepare_sort_run(chunk):
    """
    Validate a Gaia chunk, compute absolute G magnitude,
    sort by absolute magnitude, and return a 32-byte run array.

    Returns:
        run
        valid_count
        minimum xyz
        maximum xyz
        invalid_count
    """

    x = chunk["x"]
    y = chunk["y"]
    z = chunk["z"]

    with np.errstate(
        invalid="ignore",
        divide="ignore",
        over="ignore"
    ):

        distance = np.sqrt(
            x * x + y * y + z * z
        ).astype(
            np.float32,
            copy=False
        )

        g = chunk["g"]
        ag = chunk["ag"]

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

        invalid_count = int(
            np.count_nonzero(~valid)
        )

        if invalid_count == len(chunk):
            return (
                None,
                0,
                None,
                None,
                invalid_count
            )

        valid_records = chunk[valid]
        valid_distance = distance[valid]
        valid_g_intrinsic = g_intrinsic[valid]

        abs_mag = (
            valid_g_intrinsic
            - 5.0 * np.log10(valid_distance)
            + 5.0
        ).astype(
            np.float32,
            copy=False
        )

    run = np.empty(
        len(valid_records),
        dtype=SORT_DTYPE
    )

    run["abs_mag"] = abs_mag
    run["source_id"] = valid_records["source_id"]
    run["x"] = valid_records["x"]
    run["y"] = valid_records["y"]
    run["z"] = valid_records["z"]
    run["g"] = valid_records["g"]
    run["bp_rp"] = valid_records["bp_rp"]

    order = np.argsort(
        run["abs_mag"],
        kind="stable"
    )

    run = run[order]

    minimum = np.array(
        [
            run["x"].min(),
            run["y"].min(),
            run["z"].min(),
        ],
        dtype=np.float32
    )

    maximum = np.array(
        [
            run["x"].max(),
            run["y"].max(),
            run["z"].max(),
        ],
        dtype=np.float32
    )

    return (
        run,
        len(run),
        minimum,
        maximum,
        invalid_count
    )


def create_sorted_runs(files):
    """
    Read input chunks in bounded pieces, sort each piece, and
    write sorted temporary runs to disk.

    Returns:
        run_paths
        total_valid
        global_minimum
        global_maximum
        total_invalid
    """

    RUN_DIR.mkdir(
        parents=True,
        exist_ok=True
    )

    run_paths = []

    total_valid = 0
    total_invalid = 0

    global_minimum = None
    global_maximum = None

    run_id = 0
    file_counter = 0

    for path in files:

        file_counter += 1

        file_size = path.stat().st_size

        if file_size % GAIA_DTYPE.itemsize != 0:
            raise ValueError(
                f"Invalid chunk size:\n"
                f"{path}\n"
                f"Size = {file_size}\n"
                f"Record size = {GAIA_DTYPE.itemsize}"
            )

        remaining = (
            file_size //
            GAIA_DTYPE.itemsize
        )

        with path.open("rb") as f:

            while remaining > 0:

                count = min(
                    remaining,
                    SORT_RUN_STARS
                )

                chunk = np.fromfile(
                    f,
                    dtype=GAIA_DTYPE,
                    count=count
                )

                if len(chunk) != count:
                    raise IOError(
                        f"Could not read entire input piece:\n"
                        f"{path}\n"
                        f"Expected records: {count}\n"
                        f"Read records: {len(chunk)}"
                    )

                remaining -= count

                (
                    run,
                    valid_count,
                    minimum,
                    maximum,
                    invalid_count
                ) = prepare_sort_run(chunk)

                total_invalid += invalid_count

                if valid_count == 0:
                    del chunk
                    continue

                total_valid += valid_count

                if global_minimum is None:
                    global_minimum = minimum.copy()
                    global_maximum = maximum.copy()
                else:
                    global_minimum = np.minimum(
                        global_minimum,
                        minimum
                    )

                    global_maximum = np.maximum(
                        global_maximum,
                        maximum
                    )

                run_path = (
                    RUN_DIR /
                    f"run_{run_id:08d}.bin"
                )

                run.tofile(run_path)

                run_paths.append(run_path)

                # print(
                #     f"  Wrote sorted run "
                #     f"{run_id:,}: "
                #     f"{valid_count:,} stars"
                # )

                run_id += 1

                del run
                del chunk

        if (
            file_counter == 1
            or file_counter == len(files)
            or file_counter % FILE_PROGRESS_INTERVAL == 0
        ):
            print(
                f"Sorted input files: "
                f"{file_counter:,}/{len(files):,}"
            )

    if total_valid == 0:
        raise RuntimeError(
            "No valid Gaia stars remain."
        )

    print()
    print(
        f"Valid stars: {total_valid:,}"
    )

    print(
        f"Invalid stars discarded: "
        f"{total_invalid:,}"
    )

    print(
        f"Sorted runs: {len(run_paths):,}"
    )

    print(
        f"Approximate final sorted data: "
        f"{total_valid * SORT_DTYPE.itemsize / 1e9:.2f} GB"
    )

    return (
        run_paths,
        total_valid,
        global_minimum,
        global_maximum,
        total_invalid
    )


# ============================================================
# Numba two-way merge
# ============================================================

@njit
def merge_until_one_exhausted(
    a,
    b,
    output
):

    i = 0
    j = 0
    k = 0

    while (
        i < a.shape[0]
        and j < b.shape[0]
    ):

        # Left run wins ties.
        if (
            a[i]["abs_mag"]
            <=
            b[j]["abs_mag"]
        ):

            output[k] = a[i]
            i += 1

        else:

            output[k] = b[j]
            j += 1

        k += 1

    return k, i, j


def merge_two_runs(
    left_path,
    right_path,
    output_path
):

    left_size = (
        left_path.stat().st_size
        //
        SORT_DTYPE.itemsize
    )

    right_size = (
        right_path.stat().st_size
        //
        SORT_DTYPE.itemsize
    )

    total_size = (
        left_size +
        right_size
    )

    output_buffer = np.empty(
        MERGE_BLOCK_STARS * 2,
        dtype=SORT_DTYPE
    )

    with (
        left_path.open("rb") as left_file,
        right_path.open("rb") as right_file,
        output_path.open("wb") as output_file
    ):

        a = np.fromfile(
            left_file,
            dtype=SORT_DTYPE,
            count=MERGE_BLOCK_STARS
        )

        b = np.fromfile(
            right_file,
            dtype=SORT_DTYPE,
            count=MERGE_BLOCK_STARS
        )

        written = 0

        while (
            len(a) > 0
            and len(b) > 0
        ):

            count, consumed_a, consumed_b = (
                merge_until_one_exhausted(
                    a,
                    b,
                    output_buffer
                )
            )

            output_buffer[
                :count
            ].tofile(
                output_file
            )

            written += count

            if consumed_a == len(a):

                a = np.fromfile(
                    left_file,
                    dtype=SORT_DTYPE,
                    count=MERGE_BLOCK_STARS
                )

            else:

                a = a[consumed_a:]

            if consumed_b == len(b):

                b = np.fromfile(
                    right_file,
                    dtype=SORT_DTYPE,
                    count=MERGE_BLOCK_STARS
                )

            else:

                b = b[consumed_b:]

        while len(a) > 0:

            a.tofile(output_file)

            written += len(a)

            a = np.fromfile(
                left_file,
                dtype=SORT_DTYPE,
                count=MERGE_BLOCK_STARS
            )

        while len(b) > 0:

            b.tofile(output_file)

            written += len(b)

            b = np.fromfile(
                right_file,
                dtype=SORT_DTYPE,
                count=MERGE_BLOCK_STARS
            )

    if written != total_size:
        raise RuntimeError(
            "Merge size mismatch.\n"
            f"Expected: {total_size:,}\n"
            f"Written:  {written:,}"
        )

    gc.collect()


def merge_all_runs(run_paths):
    """
    Pairwise external merge.

    Each pass approximately doubles run size.
    Old input runs are deleted after their merge succeeds.
    """

    pass_id = 0
    current = list(run_paths)

    while len(current) > 1:

        print()
        print(
            f"Merge pass {pass_id}: "
            f"{len(current):,} runs"
        )

        next_runs = []

        for i in range(
            0,
            len(current),
            2
        ):
            print(i, "/", len(current))

            left = current[i]

            if i + 1 >= len(current):
                next_runs.append(left)
                continue

            right = current[i + 1]

            output = (
                RUN_DIR /
                f"merge_{pass_id:03d}_{i // 2:08d}.bin"
            )

            merge_two_runs(
                left,
                right,
                output
            )

            left.unlink()
            right.unlink()

            next_runs.append(output)

        current = next_runs
        pass_id += 1

    if not current:
        raise RuntimeError(
            "No sorted run remains."
        )

    return current[0]


# ============================================================
# Hierarchy state
# ============================================================

def create_hierarchy_state():
    capacity = max(
        1,
        NODE_INITIAL_CAPACITY
    )

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

    node_lookup = Dict.empty(
        key_type=NODE_KEY_TYPE,
        value_type=types.int32
    )

    node_lookup[
        (
            np.uint64(0),
            np.uint64(0)
        )
    ] = np.int32(0)

    parent_ids[0] = -1

    return (
        1,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children,
        node_lookup
    )


# ============================================================
# Incremental Numba MS-LOD builder
# ============================================================

@njit
def build_hierarchy_batch_numba(
    records,
    root_min,
    root_size,
    n_sigma,
    max_depth,
    progress_interval,
    total_processed,
    node_count,
    parent_ids,
    depths,
    cell_x,
    cell_y,
    cell_z,
    star_counts,
    children,
    node_lookup
):

    batch_count = records.shape[0]

    star_node_ids = np.full(
        batch_count,
        -1,
        dtype=np.int32
    )

    capacity = parent_ids.shape[0]

    scale = 1 << max_depth

    for local_i in range(batch_count):

        global_i = (
            total_processed +
            local_i
        )

        if (
            progress_interval > 0
            and global_i > 0
            and global_i % progress_interval == 0
        ):

            print(
                "Hierarchy progress:",
                global_i
            )

        px = records[local_i]["x"]
        py = records[local_i]["y"]
        pz = records[local_i]["z"]

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
                max_depth -
                depth
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
                np.uint64(depth),
                path
            )

            child_id = np.int32(-1)

            if key in node_lookup:

                child_id = node_lookup[key]

            else:

                if node_count >= capacity:

                    new_capacity = (
                        capacity * 2
                    )

                    new_parent = np.full(
                        new_capacity,
                        -1,
                        dtype=np.int32
                    )

                    new_parent[
                        :capacity
                    ] = parent_ids

                    parent_ids = new_parent

                    new_depths = np.zeros(
                        new_capacity,
                        dtype=np.uint32
                    )

                    new_depths[
                        :capacity
                    ] = depths

                    depths = new_depths

                    new_cell_x = np.zeros(
                        new_capacity,
                        dtype=np.int32
                    )

                    new_cell_x[
                        :capacity
                    ] = cell_x

                    cell_x = new_cell_x

                    new_cell_y = np.zeros(
                        new_capacity,
                        dtype=np.int32
                    )

                    new_cell_y[
                        :capacity
                    ] = cell_y

                    cell_y = new_cell_y

                    new_cell_z = np.zeros(
                        new_capacity,
                        dtype=np.int32
                    )

                    new_cell_z[
                        :capacity
                    ] = cell_z

                    cell_z = new_cell_z

                    new_star_counts = np.zeros(
                        new_capacity,
                        dtype=np.uint32
                    )

                    new_star_counts[
                        :capacity
                    ] = star_counts

                    star_counts = (
                        new_star_counts
                    )

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

                node_lookup[key] = (
                    child_id
                )

                parent_ids[
                    child_id
                ] = parent_id

                depths[
                    child_id
                ] = np.uint32(depth)

                cell_x[
                    child_id
                ] = (
                    cell_x[parent_id] * 2
                    + bx
                )

                cell_y[
                    child_id
                ] = (
                    cell_y[parent_id] * 2
                    + by
                )

                cell_z[
                    child_id
                ] = (
                    cell_z[parent_id] * 2
                    + bz
                )

                children[
                    parent_id,
                    octant
                ] = child_id

            if (
                star_counts[child_id]
                <
                n_sigma
            ):

                star_counts[child_id] += 1

                star_node_ids[
                    local_i
                ] = child_id

                assigned = True

                break

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
                np.int64(global_i)
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
        np.int64(-1)
    )


# ============================================================
# Partition final stars by node-ID range
# ============================================================

def write_partition_batch(
    partition_dir,
    records,
    star_node_ids
):

    partition = np.empty(
        len(records),
        dtype=PARTITION_DTYPE
    )

    partition["node_id"] = (
        np.asarray(
            star_node_ids,
            dtype=np.uint32
        )
    )

    partition["source_id"] = (
        records["source_id"]
    )

    partition["x"] = records["x"]
    partition["y"] = records["y"]
    partition["z"] = records["z"]
    partition["g"] = records["g"]
    partition["bp_rp"] = records["bp_rp"]

    bucket_ids = (
        partition["node_id"]
        //
        NODE_PARTITION_SPAN
    )

    order = np.argsort(
        bucket_ids,
        kind="stable"
    )

    ordered = partition[order]
    ordered_bucket_ids = (
        bucket_ids[order]
    )

    start = 0
    count = len(ordered)

    while start < count:

        bucket_id = int(
            ordered_bucket_ids[start]
        )

        end = start + 1

        while (
            end < count
            and int(
                ordered_bucket_ids[end]
            ) == bucket_id
        ):
            end += 1

        path = (
            partition_dir
            /
            f"bucket_{bucket_id:08d}.bin"
        )

        with path.open("ab") as f:
            ordered[
                start:end
            ].tofile(f)

        start = end


# ============================================================
# Build hierarchy from the final sorted stream
# ============================================================

def build_hierarchy_from_sorted_run(
    sorted_run,
    total_star_count,
    root_min,
    root_size
):

    (
        node_count,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children,
        node_lookup
    ) = create_hierarchy_state()

    PARTITION_DIR.mkdir(
        parents=True,
        exist_ok=True
    )

    processed = 0
    batch_number = 0

    print()
    print(
        "Building MS-LOD hierarchy from "
        "the merged sorted stream..."
    )

    print(
        f"Stars: {total_star_count:,}"
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

    with sorted_run.open("rb") as f:

        while processed < total_star_count:

            batch = np.fromfile(
                f,
                dtype=SORT_DTYPE,
                count=min(
                    PROCESS_BATCH_STARS,
                    total_star_count - processed
                )
            )

            if len(batch) == 0:
                break

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
            ) = build_hierarchy_batch_numba(
                batch,
                root_min,
                root_size,
                N_SIGMA,
                MAX_OCTREE_DEPTH,
                PROGRESS_INTERVAL,
                processed,
                node_count,
                parent_ids,
                depths,
                cell_x,
                cell_y,
                cell_z,
                star_counts,
                children,
                node_lookup
            )

            if failed_index >= 0:

                raise RuntimeError(
                    "A star could not be assigned within "
                    f"MAX_OCTREE_DEPTH={MAX_OCTREE_DEPTH}. "
                    f"First failing star index: "
                    f"{int(failed_index)}"
                )

            write_partition_batch(
                PARTITION_DIR,
                batch,
                star_node_ids
            )

            processed += len(batch)
            batch_number += 1

            if (
                batch_number == 1
                or processed == total_star_count
                or batch_number % 5 == 0
            ):
                print(
                    f"  Processed "
                    f"{processed:,}/{total_star_count:,} "
                    f"stars; nodes: "
                    f"{node_count:,}"
                )

            del batch
            del star_node_ids

    if processed != total_star_count:
        raise RuntimeError(
            "Final sorted stream size mismatch.\n"
            f"Expected: {total_star_count:,}\n"
            f"Processed: {processed:,}"
        )

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

    assigned_count = int(
        np.sum(
            star_counts,
            dtype=np.uint64
        )
    )

    if assigned_count != total_star_count:
        raise RuntimeError(
            "LOD star count mismatch.\n"
            f"Assigned: {assigned_count:,}\n"
            f"Input:    {total_star_count:,}"
        )

    print()
    print(
        f"Total nodes: {node_count:,}"
    )

    print(
        f"Maximum depth: "
        f"{int(depths.max())}"
    )

    depth_counts = np.bincount(
        depths.astype(np.int64)
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
        children
    )


# ============================================================
# Convert partitions into final per-node files
# ============================================================

def write_final_node_files(
    directory,
    node_count,
    star_counts
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

    seen = np.zeros(
        node_count,
        dtype=np.bool_
    )

    bucket_paths = sorted(
        PARTITION_DIR.glob(
            "bucket_*.bin"
        )
    )

    print()
    print(
        f"Converting {len(bucket_paths):,} "
        "node partitions into final files..."
    )

    for bucket_index, bucket_path in enumerate(
        bucket_paths,
        start=1
    ):

        data = np.fromfile(
            bucket_path,
            dtype=PARTITION_DTYPE
        )

        if len(data) == 0:
            bucket_path.unlink()
            continue

        order = np.argsort(
            data["node_id"],
            kind="stable"
        )

        data = data[order]

        start = 0
        data_count = len(data)

        while start < data_count:

            node_id = int(
                data["node_id"][start]
            )

            end = start + 1

            while (
                end < data_count
                and int(
                    data["node_id"][end]
                ) == node_id
            ):
                end += 1

            if node_id < 0 or node_id >= node_count:
                raise RuntimeError(
                    f"Invalid node ID {node_id} "
                    f"in {bucket_path}"
                )

            count = end - start

            expected = int(
                star_counts[node_id]
            )

            if count != expected:
                raise RuntimeError(
                    f"Node {node_id} star-count mismatch.\n"
                    f"Partition: {count}\n"
                    f"Metadata:  {expected}"
                )

            group = data[start:end]

            render = np.empty(
                count,
                dtype=RENDER_DTYPE
            )

            render["x"] = group["x"]
            render["y"] = group["y"]
            render["z"] = group["z"]
            render["g"] = group["g"]
            render["bp_rp"] = group["bp_rp"]

            particle_path = (
                particle_dir
                /
                f"particles_{node_id:08d}.bin"
            )

            source_path = (
                source_id_dir
                /
                f"source_ids_{node_id:08d}.bin"
            )

            render.tofile(
                particle_path
            )

            np.asarray(
                group["source_id"],
                dtype="<u8"
            ).tofile(
                source_path
            )

            seen[node_id] = True

            start = end

        bucket_path.unlink()

        if (
            bucket_index == 1
            or bucket_index == len(bucket_paths)
            or bucket_index % 20 == 0
        ):
            print(
                f"  Buckets: "
                f"{bucket_index:,}/{len(bucket_paths):,}"
            )

        del data
        del order

    expected_seen = star_counts > 0

    if not np.array_equal(seen, expected_seen):

        missing = np.flatnonzero(expected_seen & ~seen)

        raise RuntimeError(
            "Some non-empty hierarchy nodes did not receive "
            "a final particle file.\n"
            f"Missing nodes: {len(missing):,}"
        )

    del seen


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

    if BUILD_DIR.exists():

        shutil.rmtree(
            BUILD_DIR
        )

    BUILD_DIR.mkdir(
        parents=True
    )

    # --------------------------------------------------------
    # Pass 1:
    # make individually sorted runs
    # --------------------------------------------------------

    print()
    print(
        "=========================================="
    )
    print(
        "CREATING SORTED RUNS"
    )
    print(
        "=========================================="
    )

    (
        run_paths,
        total_valid,
        global_minimum,
        global_maximum,
        _
    ) = create_sorted_runs(
        files
    )

    center = (
        global_minimum +
        global_maximum
    ) * 0.5

    root_size = float(
        np.max(
            global_maximum -
            global_minimum
        )
    )

    if root_size <= 0.0:
        raise RuntimeError(
            "All valid stars have identical positions."
        )

    root_min = (
        center -
        root_size * 0.5
    ).astype(
        np.float32
    )

    # --------------------------------------------------------
    # Pass 2:
    # externally merge all sorted runs
    # --------------------------------------------------------

    print()
    print(
        "=========================================="
    )
    print(
        "EXTERNAL MERGE"
    )
    print(
        "=========================================="
    )

    final_sorted_run = (
        merge_all_runs(
            run_paths
        )
    )

    print()
    print(
        f"Final sorted run:\n"
        f"{final_sorted_run}"
    )

    # --------------------------------------------------------
    # Pass 3:
    # build hierarchy incrementally and partition output
    # --------------------------------------------------------

    print()
    print(
        "=========================================="
    )
    print(
        "BUILDING MS-LOD"
    )
    print(
        "=========================================="
    )

    (
        root_min,
        root_size,
        parent_ids,
        depths,
        cell_x,
        cell_y,
        cell_z,
        star_counts,
        children
    ) = build_hierarchy_from_sorted_run(
        final_sorted_run,
        total_valid,
        root_min,
        root_size
    )

    final_sorted_run.unlink()

    # --------------------------------------------------------
    # Convert node partitions into final per-node files
    # --------------------------------------------------------

    write_final_node_files(
        BUILD_DIR,
        len(star_counts),
        star_counts
    )

    # --------------------------------------------------------
    # Metadata + summary
    # --------------------------------------------------------

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
        total_valid
    )

    write_summary(
        BUILD_DIR,
        depths,
        total_valid
    )

    # Temp directories should now be empty.
    if RUN_DIR.exists():
        shutil.rmtree(RUN_DIR)

    if PARTITION_DIR.exists():
        shutil.rmtree(PARTITION_DIR)

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
        f"Stars:       {total_valid:,}"
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
