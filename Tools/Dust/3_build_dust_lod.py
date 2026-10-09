from pathlib import Path
import json
import math
import shutil
import struct

import numpy as np


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "DataRaw" / "Dust"
PROCESSED_DIR = RAW_DIR / "processed"
INPUT_DIR = PROCESSED_DIR / "chunks"
MANIFEST_PATH = PROCESSED_DIR / "manifest.json"

DATA_DIR = PROJECT_ROOT / "Data" / "Dust"
LOD_DIR = DATA_DIR / "lod"


# ============================================================
# Settings
# ============================================================

CHUNK_SIZE = 32
BASE_VOXEL_SIZE_PC = 2.0
MAX_LOD = 8
CLEAR_OLD_OUTPUT = True


# ============================================================
# Binary density format
# ============================================================

DENSITY_DTYPE = np.dtype("<f2")
BRICK_VOXELS = CHUNK_SIZE ** 3
BRICK_BYTES = BRICK_VOXELS * DENSITY_DTYPE.itemsize


# ============================================================
# Binary index format (must match the Unity reader)
# ============================================================

# Header:
# char[8] magic; uint32 version; uint16 chunk size; uint16 LOD count;
# float32 base voxel size; uint32 base dims[3]; uint32 padded dims[3];
# float64 base origin (center of voxel 0,0,0)[3]; float64 signed step[3].
INDEX_MAGIC = b"DUSTLOD1"
INDEX_VERSION = 1
INDEX_HEADER_STRUCT = struct.Struct("<8sIHHf6I6d")

# uint16 level; uint16 reserved; float32 voxel size; uint32 dims[3];
# uint32 brick-grid dims[3]; uint32 nonempty-brick count.
INDEX_LEVEL_STRUCT = struct.Struct("<HHf7I")

# uint32 bx, by, bz; float32 maximum density.
INDEX_BRICK_STRUCT = struct.Struct("<IIIf")


# ============================================================
# Helpers
# ============================================================

def ceil_div(a, b):
    return (a + b - 1) // b


def chunk_filename(x, y, z):
    return f"chunk_{x:06d}_{y:06d}_{z:06d}.bin"


def parse_chunk_filename(path):
    parts = path.stem.split("_")
    if len(parts) != 4 or parts[0] != "chunk":
        return None
    try:
        return int(parts[1]), int(parts[2]), int(parts[3])
    except ValueError:
        return None


def level_dimensions(base_dims, level):
    scale = 1 << level
    return tuple(ceil_div(int(d), scale) for d in base_dims)


def brick_grid_dimensions(dimensions):
    return tuple(ceil_div(int(d), CHUNK_SIZE) for d in dimensions)


def read_json(path):
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


# ============================================================
# Cleanup
# ============================================================

def clear_old_output():
    if CLEAR_OLD_OUTPUT and LOD_DIR.exists():
        print("Removing previous LOD output...")
        shutil.rmtree(LOD_DIR)


# ============================================================
# Load the geometry produced by the preprocessing script
# ============================================================

def load_source_grid():
    """Load Unity-space geometry from processed/manifest.json.

    The preprocessing script has already translated, rotated/reordered,
    and scaled the dust voxels. Do not recompute the Sun position from the
    raw X/Y/Z arrays here: the manifest is the geometry source of truth.
    """
    if not MANIFEST_PATH.exists():
        raise FileNotFoundError(
            f"Missing preprocessing manifest:\n{MANIFEST_PATH}\n"
            "Run the updated dust-processing script first."
        )

    manifest = read_json(MANIFEST_PATH)

    if int(manifest.get("format_version", -1)) != 1:
        raise RuntimeError(
            "Unsupported dust preprocessing manifest version: "
            f"{manifest.get('format_version')!r}"
        )

    if int(manifest.get("chunk_size_voxels", -1)) != CHUNK_SIZE:
        raise RuntimeError(
            "Preprocessing chunk size does not match this LOD builder: "
            f"manifest={manifest.get('chunk_size_voxels')}, "
            f"LOD builder={CHUNK_SIZE}."
        )

    if manifest.get("unity_units") != "pc (1 Unity unit = 1 pc)":
        raise RuntimeError(
            "Manifest does not declare Unity units as parsecs. "
            f"Found: {manifest.get('unity_units')!r}"
        )

    base_dims = tuple(
        int(v) for v in manifest["grid_shape_unity_xyz"]
    )
    voxel_spacing = tuple(
        float(v) for v in manifest["voxel_spacing_pc_xyz"]
    )
    base_origin = tuple(
        float(v) for v in manifest["first_voxel_center_pc_xyz"]
    )

    if len(base_dims) != 3 or any(d <= 0 for d in base_dims):
        raise RuntimeError(f"Invalid grid_shape_unity_xyz: {base_dims}")
    if len(voxel_spacing) != 3 or any(
        not math.isfinite(v) or v <= 0 for v in voxel_spacing
    ):
        raise RuntimeError(
            f"Invalid voxel_spacing_pc_xyz: {voxel_spacing}"
        )
    if len(base_origin) != 3 or any(
        not math.isfinite(v) for v in base_origin
    ):
        raise RuntimeError(
            f"Invalid first_voxel_center_pc_xyz: {base_origin}"
        )

    # This LOD pyramid uses one isotropic base voxel size.
    for axis, step in zip("XYZ", voxel_spacing):
        if not math.isclose(
            step, BASE_VOXEL_SIZE_PC, rel_tol=1e-6, abs_tol=1e-8
        ):
            raise RuntimeError(
                f"Manifest voxel spacing on {axis} is {step} pc, but this "
                f"LOD builder expects {BASE_VOXEL_SIZE_PC} pc."
            )

    padded_dims = tuple(
        ceil_div(d, CHUNK_SIZE) * CHUNK_SIZE for d in base_dims
    )
    base_step = voxel_spacing  # all axes increase in positive Unity XYZ

    # Bounds are outer volume edges; manifest coordinate is voxel center.
    bounds_min = tuple(
        base_origin[i] - base_step[i] / 2.0 for i in range(3)
    )
    last_center = tuple(
        base_origin[i] + (base_dims[i] - 1) * base_step[i]
        for i in range(3)
    )
    bounds_max = tuple(
        last_center[i] + base_step[i] / 2.0 for i in range(3)
    )

    expected_chunk_grid = brick_grid_dimensions(base_dims)
    manifest_chunk_grid = tuple(
        int(v) for v in manifest["chunk_shape_unity_xyz"]
    )
    if manifest_chunk_grid != expected_chunk_grid:
        raise RuntimeError(
            "Manifest chunk grid is inconsistent with the base dimensions: "
            f"manifest={manifest_chunk_grid}, expected={expected_chunk_grid}."
        )

    print("Loaded Unity-space geometry from:")
    print(f"  {MANIFEST_PATH}")
    print(f"  Base dimensions XYZ: {base_dims}")
    print(f"  Voxel spacing XYZ: {voxel_spacing} pc")
    print(f"  First voxel center XYZ: {base_origin} pc")
    print(f"  Grid edge bounds min: {bounds_min} pc")
    print(f"  Grid edge bounds max: {bounds_max} pc")

    return {
        "base_dims": base_dims,
        "padded_dims": padded_dims,
        "base_origin": base_origin,
        "base_step": base_step,
        "bounds_min": bounds_min,
        "bounds_max": bounds_max,
        "manifest": manifest,
    }


# ============================================================
# Brick readers/writer
# ============================================================

def read_source_brick(path_string):
    path = Path(path_string)
    if not path.exists():
        return None

    data = np.fromfile(path, dtype=DENSITY_DTYPE)
    if data.size != BRICK_VOXELS:
        raise RuntimeError(
            f"Invalid source brick size:\n{path}\n"
            f"Expected {BRICK_VOXELS:,} values / {BRICK_BYTES:,} bytes; "
            f"got {data.size:,} values / {data.nbytes:,} bytes."
        )
    # Preprocessing format is semantic [x, y, z], C order.
    return data.astype(np.float32, copy=False).reshape(
        CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE
    )


def read_runtime_brick(path_string):
    path = Path(path_string)
    if not path.exists():
        return None

    data = np.fromfile(path, dtype=DENSITY_DTYPE)
    if data.size != BRICK_VOXELS:
        raise RuntimeError(
            f"Invalid runtime brick size:\n{path}\n"
            f"Expected {BRICK_VOXELS:,} values / {BRICK_BYTES:,} bytes; "
            f"got {data.size:,} values / {data.nbytes:,} bytes."
        )
    # Runtime format is serialized as [z, y, x]; restore semantic [x,y,z].
    return data.astype(np.float32, copy=False).reshape(
        CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE
    ).transpose(2, 1, 0)


def write_runtime_brick(brick, path):
    # Semantic array brick[x,y,z] -> runtime array [z,y,x].
    np.transpose(brick, (2, 1, 0)).astype(
        DENSITY_DTYPE, copy=False
    ).tofile(path)


# ============================================================
# Generate LOD0
# ============================================================

def generate_lod0(source_grid):
    output_dir = LOD_DIR / "LOD00"
    output_dir.mkdir(parents=True, exist_ok=True)

    base_dims = source_grid["base_dims"]
    base_bricks = brick_grid_dimensions(base_dims)

    print("\n----------------------------------------")
    print("LOD 0")
    print("----------------------------------------")
    print(f"Voxel size: {BASE_VOXEL_SIZE_PC:g} pc")
    print(f"Grid cells: {base_dims[0]} x {base_dims[1]} x {base_dims[2]}")
    print(f"Bricks: {base_bricks[0]} x {base_bricks[1]} x {base_bricks[2]}\n")

    files = sorted(INPUT_DIR.glob("chunk_*.bin"))
    if not files:
        raise RuntimeError(f"No source dust bricks found:\n{INPUT_DIR}")

    brick_entries = []
    global_max = 0.0
    copied = 0
    total_files = len(files)

    for file_index, source_path in enumerate(files, start=1):
        coords = parse_chunk_filename(source_path)
        if coords is None:
            continue

        bx, by, bz = coords
        if (
            bx < 0 or by < 0 or bz < 0
            or bx >= base_bricks[0]
            or by >= base_bricks[1]
            or bz >= base_bricks[2]
        ):
            raise RuntimeError(
                f"Source brick is outside the base brick grid:\n{source_path}\n"
                f"Brick={coords}, grid={base_bricks}"
            )

        brick = read_source_brick(str(source_path))
        if brick is None:
            raise RuntimeError(f"Could not read source brick:\n{source_path}")

        stored_max = float(np.max(brick))
        if stored_max <= 0.0:
            continue

        filename = chunk_filename(bx, by, bz)
        final_path = output_dir / filename
        temp_path = output_dir / (filename + ".tmp")
        write_runtime_brick(brick, temp_path)
        temp_path.replace(final_path)

        brick_entries.append((bx, by, bz, stored_max))
        copied += 1
        global_max = max(global_max, stored_max)

        if file_index % 1000 == 0 or file_index == total_files:
            print(f"LOD0 bricks: {file_index:,}/{total_files:,} | non-empty: {copied:,}")

    if not brick_entries:
        raise RuntimeError("No non-empty LOD0 bricks were found.")

    return {
        "level": 0,
        "voxel_size_pc": BASE_VOXEL_SIZE_PC,
        "dims": base_dims,
        "bricks": base_bricks,
        "nonempty": len(brick_entries),
        "max_density": global_max,
        "entries": brick_entries,
    }


# ============================================================
# Copy a child brick region into an accumulator
# ============================================================

def copy_child_region(
    previous_dir,
    child_bx,
    child_by,
    child_bz,
    child_start,
    child_end,
    accumulator,
    valid_mask,
):
    brick_start = (
        child_bx * CHUNK_SIZE,
        child_by * CHUNK_SIZE,
        child_bz * CHUNK_SIZE,
    )
    brick_end = tuple(v + CHUNK_SIZE for v in brick_start)

    overlap_min = tuple(
        max(brick_start[i], child_start[i]) for i in range(3)
    )
    overlap_max = tuple(
        min(brick_end[i], child_end[i]) for i in range(3)
    )
    if any(overlap_min[i] >= overlap_max[i] for i in range(3)):
        return

    dst0 = tuple(overlap_min[i] - child_start[i] for i in range(3))
    dst1 = tuple(overlap_max[i] - child_start[i] for i in range(3))
    src0 = tuple(overlap_min[i] - brick_start[i] for i in range(3))
    src1 = tuple(overlap_max[i] - brick_start[i] for i in range(3))

    # Missing brick means zero density, but cells inside the physical grid
    # are still valid samples when calculating parent averages.
    valid_mask[dst0[0]:dst1[0], dst0[1]:dst1[1], dst0[2]:dst1[2]] = True

    child_path = previous_dir / chunk_filename(child_bx, child_by, child_bz)
    brick = read_runtime_brick(str(child_path))
    if brick is None:
        return

    accumulator[dst0[0]:dst1[0], dst0[1]:dst1[1], dst0[2]:dst1[2]] = brick[
        src0[0]:src1[0], src0[1]:src1[1], src0[2]:src1[2]
    ]


# ============================================================
# Generate one higher LOD level
# ============================================================

def generate_level(level, previous_level_dir, previous_dims, source_grid):
    current_dims = level_dimensions(source_grid["base_dims"], level)
    current_bricks = brick_grid_dimensions(current_dims)
    output_dir = LOD_DIR / f"LOD{level:02d}"
    output_dir.mkdir(parents=True, exist_ok=True)
    voxel_size_pc = BASE_VOXEL_SIZE_PC * (1 << level)

    print("\n----------------------------------------")
    print(f"LOD {level}")
    print("----------------------------------------")
    print(f"Voxel size: {voxel_size_pc:g} pc")
    print(f"Grid cells: {current_dims[0]} x {current_dims[1]} x {current_dims[2]}")
    print(f"Bricks: {current_bricks[0]} x {current_bricks[1]} x {current_bricks[2]}")

    total_bricks = math.prod(current_bricks)
    print(f"Possible bricks: {total_bricks:,}\n")

    nonempty = 0
    global_max = 0.0
    brick_entries = []

    for bx in range(current_bricks[0]):
        current_x0 = bx * CHUNK_SIZE
        current_x1 = min(current_x0 + CHUNK_SIZE, current_dims[0])
        child_x0 = current_x0 * 2
        child_x1 = min(current_x1 * 2, previous_dims[0])
        child_bx0 = child_x0 // CHUNK_SIZE
        child_bx1 = (child_x1 - 1) // CHUNK_SIZE

        for by in range(current_bricks[1]):
            current_y0 = by * CHUNK_SIZE
            current_y1 = min(current_y0 + CHUNK_SIZE, current_dims[1])
            child_y0 = current_y0 * 2
            child_y1 = min(current_y1 * 2, previous_dims[1])
            child_by0 = child_y0 // CHUNK_SIZE
            child_by1 = (child_y1 - 1) // CHUNK_SIZE

            for bz in range(current_bricks[2]):
                current_z0 = bz * CHUNK_SIZE
                current_z1 = min(current_z0 + CHUNK_SIZE, current_dims[2])
                child_z0 = current_z0 * 2
                child_z1 = min(current_z1 * 2, previous_dims[2])
                child_bz0 = child_z0 // CHUNK_SIZE
                child_bz1 = (child_z1 - 1) // CHUNK_SIZE

                accumulator = np.zeros(
                    (CHUNK_SIZE * 2, CHUNK_SIZE * 2, CHUNK_SIZE * 2),
                    dtype=np.float32,
                )
                valid_mask = np.zeros(accumulator.shape, dtype=np.bool_)

                for cbx in range(child_bx0, child_bx1 + 1):
                    for cby in range(child_by0, child_by1 + 1):
                        for cbz in range(child_bz0, child_bz1 + 1):
                            copy_child_region(
                                previous_level_dir,
                                cbx, cby, cbz,
                                (child_x0, child_y0, child_z0),
                                (child_x1, child_y1, child_z1),
                                accumulator,
                                valid_mask,
                            )

                values = accumulator.reshape(
                    CHUNK_SIZE, 2,
                    CHUNK_SIZE, 2,
                    CHUNK_SIZE, 2,
                )
                masks = valid_mask.reshape(
                    CHUNK_SIZE, 2,
                    CHUNK_SIZE, 2,
                    CHUNK_SIZE, 2,
                )
                density_sum = values.sum(axis=(1, 3, 5), dtype=np.float32)
                sample_count = masks.sum(axis=(1, 3, 5), dtype=np.uint8)

                parent = np.zeros(
                    (CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE),
                    dtype=np.float32,
                )
                np.divide(
                    density_sum,
                    sample_count,
                    out=parent,
                    where=sample_count > 0,
                )

                # Remove padding outside the actual current grid.
                valid_x = current_x1 - current_x0
                valid_y = current_y1 - current_y0
                valid_z = current_z1 - current_z0
                if valid_x < CHUNK_SIZE:
                    parent[valid_x:, :, :] = 0.0
                if valid_y < CHUNK_SIZE:
                    parent[:, valid_y:, :] = 0.0
                if valid_z < CHUNK_SIZE:
                    parent[:, :, valid_z:] = 0.0

                stored_max = float(np.max(parent))
                if stored_max <= 0.0:
                    continue

                filename = chunk_filename(bx, by, bz)
                final_path = output_dir / filename
                temp_path = output_dir / (filename + ".tmp")
                write_runtime_brick(parent, temp_path)
                temp_path.replace(final_path)

                nonempty += 1
                global_max = max(global_max, stored_max)
                brick_entries.append((bx, by, bz, stored_max))

        print(f"X bricks: {bx + 1}/{current_bricks[0]} | non-empty: {nonempty:,}")

    return {
        "level": level,
        "voxel_size_pc": voxel_size_pc,
        "dims": current_dims,
        "bricks": current_bricks,
        "nonempty": nonempty,
        "max_density": global_max,
        "entries": brick_entries,
    }


# ============================================================
# Write binary index
# ============================================================

def write_index(source_grid, level_infos):
    index_path = LOD_DIR / "index.bin"
    with index_path.open("wb") as f:
        f.write(INDEX_HEADER_STRUCT.pack(
            INDEX_MAGIC,
            INDEX_VERSION,
            CHUNK_SIZE,
            len(level_infos),
            float(BASE_VOXEL_SIZE_PC),
            int(source_grid["base_dims"][0]),
            int(source_grid["base_dims"][1]),
            int(source_grid["base_dims"][2]),
            int(source_grid["padded_dims"][0]),
            int(source_grid["padded_dims"][1]),
            int(source_grid["padded_dims"][2]),
            float(source_grid["base_origin"][0]),
            float(source_grid["base_origin"][1]),
            float(source_grid["base_origin"][2]),
            float(source_grid["base_step"][0]),
            float(source_grid["base_step"][1]),
            float(source_grid["base_step"][2]),
        ))

        for info in level_infos:
            dims = info["dims"]
            bricks = info["bricks"]
            f.write(INDEX_LEVEL_STRUCT.pack(
                int(info["level"]),
                0,
                float(info["voxel_size_pc"]),
                int(dims[0]), int(dims[1]), int(dims[2]),
                int(bricks[0]), int(bricks[1]), int(bricks[2]),
                int(info["nonempty"]),
            ))

            for bx, by, bz, max_density in info["entries"]:
                f.write(INDEX_BRICK_STRUCT.pack(
                    int(bx), int(by), int(bz), float(max_density)
                ))

    print(f"\nIndex written:\n  {index_path}")


# ============================================================
# Main
# ============================================================

if __name__ == "__main__":
    print("======================================")
    print("Dust LOD generation: Sun-centered Unity coordinates")
    print("======================================\n")

    if not INPUT_DIR.exists():
        raise RuntimeError(f"Input directory does not exist:\n{INPUT_DIR}")
    if not MANIFEST_PATH.exists():
        raise RuntimeError(
            f"Missing preprocessing manifest:\n{MANIFEST_PATH}\n"
            "Run the updated dust-processing script first."
        )

    # Load/validate geometry before deleting an existing LOD tree.
    SOURCE_GRID = load_source_grid()

    if not any(INPUT_DIR.glob("chunk_*.bin")):
        raise RuntimeError(f"No source dust bricks found:\n{INPUT_DIR}")

    clear_old_output()
    LOD_DIR.mkdir(parents=True, exist_ok=True)

    print("\nPadded base grid:")
    print(f"  {SOURCE_GRID['padded_dims']}")
    print(f"Base voxel size: {BASE_VOXEL_SIZE_PC:g} pc")
    print(f"Base voxel center (index 0,0,0): {SOURCE_GRID['base_origin']} pc")
    print(f"Base world step: {SOURCE_GRID['base_step']} pc")
    print(f"Volume bounds: min={SOURCE_GRID['bounds_min']} pc, max={SOURCE_GRID['bounds_max']} pc")

    print("\nPlanned LOD levels:")
    for level in range(MAX_LOD + 1):
        dims = level_dimensions(SOURCE_GRID["base_dims"], level)
        voxel_size = BASE_VOXEL_SIZE_PC * (1 << level)
        bricks = brick_grid_dimensions(dims)
        print(
            f"  LOD {level:2d}: {voxel_size:6g} pc | "
            f"cells {dims[0]}x{dims[1]}x{dims[2]} | "
            f"bricks {bricks[0]}x{bricks[1]}x{bricks[2]}"
        )

    level_infos = [generate_lod0(SOURCE_GRID)]
    previous_dir = LOD_DIR / "LOD00"
    previous_dims = level_infos[0]["dims"]

    for level in range(1, MAX_LOD + 1):
        info = generate_level(level, previous_dir, previous_dims, SOURCE_GRID)
        level_infos.append(info)
        previous_dir = LOD_DIR / f"LOD{level:02d}"
        previous_dims = info["dims"]

    write_index(SOURCE_GRID, level_infos)

    print("\n======================================")
    print("Finished")
    print("======================================\n")
    total_output_bytes = 0
    for info in level_infos:
        level_bytes = info["nonempty"] * BRICK_BYTES
        total_output_bytes += level_bytes
        print(
            f"LOD {info['level']:2d}: {info['nonempty']:,} bricks = "
            f"{level_bytes / (1024**2):.2f} MiB"
        )

    print(f"\nTotal LOD output: {total_output_bytes / (1024**2):.2f} MiB")
    print(f"Output: {LOD_DIR}")
