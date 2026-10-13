from __future__ import annotations

"""Build a compact spatial-billboard dataset from the existing processed dust bricks.

This script only reads DataRaw/Dust/processed and writes the dedicated output file
Data/Dust/dust_sprites.bin. It does not edit Unity scripts, shaders, scenes, or settings.
"""

import json
import math
import struct
from pathlib import Path

import numpy as np

PROJECT_ROOT = Path(__file__).resolve().parents[2]
PROCESSED_DIR = PROJECT_ROOT / "DataRaw" / "Dust" / "processed"
CHUNKS_DIR = PROCESSED_DIR / "chunks"
MANIFEST_PATH = PROCESSED_DIR / "manifest.json"
OUTPUT_DIR = PROJECT_ROOT / "Data" / "Dust"
OUTPUT_PATH = OUTPUT_DIR / "dust_sprites.bin"

CHUNK_SIZE = 32
DENSITY_DTYPE = np.dtype("<f2")
COARSE_FACTOR = 4                 # 4 source voxels x 2 pc = an 8 pc sampling cell.
COARSE_PER_CHUNK = CHUNK_SIZE // COARSE_FACTOR
MAX_SPRITES = 12_000
DENSITY_THRESHOLD = 0.01          # Ignore extremely low/near-zero averaged cells.
RANDOM_SEED = 20261013

FILE_MAGIC = b"DUSTSPR1"
FILE_VERSION = 1
HEADER = struct.Struct("<8sII")   # magic, version, record count
RECORD = np.dtype(
    [
        ("position_size", "<f4", (4,)),  # x, y, z, diameter_pc
        ("parameters", "<f4", (4,)),     # opacity, rotation, aspect, normalized_density
    ]
)


def read_manifest() -> dict:
    if not MANIFEST_PATH.exists():
        raise FileNotFoundError(
            f"Missing {MANIFEST_PATH}. Run 2_process_dust.py first."
        )
    with MANIFEST_PATH.open("r", encoding="utf-8") as handle:
        manifest = json.load(handle)

    if int(manifest.get("format_version", -1)) != 1:
        raise RuntimeError("Unsupported dust manifest format version.")
    if int(manifest.get("chunk_size_voxels", -1)) != CHUNK_SIZE:
        raise RuntimeError("Manifest chunk size does not match 32-voxel bricks.")
    if manifest.get("unity_units") != "pc (1 Unity unit = 1 pc)":
        raise RuntimeError("Manifest must use pc as Unity world units.")

    dims = tuple(int(v) for v in manifest["grid_shape_unity_xyz"])
    spacing = tuple(float(v) for v in manifest["voxel_spacing_pc_xyz"])
    origin = tuple(float(v) for v in manifest["grid_min_edge_pc_xyz"])
    if len(dims) != 3 or any(v <= 0 for v in dims):
        raise RuntimeError(f"Invalid Unity grid dimensions: {dims}")
    if len(spacing) != 3 or any(not math.isfinite(v) or v <= 0 for v in spacing):
        raise RuntimeError(f"Invalid voxel spacing: {spacing}")
    if len(origin) != 3 or any(not math.isfinite(v) for v in origin):
        raise RuntimeError(f"Invalid grid minimum edge: {origin}")
    if any(abs(v - 2.0) > 1e-5 for v in spacing):
        raise RuntimeError(
            f"This sprite builder expects the current 2 pc voxel grid; found {spacing}."
        )
    return manifest


def parse_chunk(path: Path) -> tuple[int, int, int] | None:
    parts = path.stem.split("_")
    if len(parts) != 4 or parts[0] != "chunk":
        return None
    try:
        return int(parts[1]), int(parts[2]), int(parts[3])
    except ValueError:
        return None


def read_coarse_brick(path: Path) -> np.ndarray:
    raw = np.fromfile(path, dtype=DENSITY_DTYPE)
    expected = CHUNK_SIZE**3
    if raw.size != expected:
        raise RuntimeError(
            f"Invalid processed brick {path}: expected {expected} values, got {raw.size}."
        )
    brick = raw.astype(np.float32, copy=False).reshape(
        CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE
    )
    # The processed format is semantic [x, y, z] in C order.
    # Average each 4x4x4 block; this softens voxel-level noise before placement.
    coarse = brick.reshape(
        COARSE_PER_CHUNK, COARSE_FACTOR,
        COARSE_PER_CHUNK, COARSE_FACTOR,
        COARSE_PER_CHUNK, COARSE_FACTOR,
    ).mean(axis=(1, 3, 5), dtype=np.float32)
    return coarse


def collect_candidates(manifest: dict) -> tuple[np.ndarray, np.ndarray, tuple[int, int, int]]:
    if not CHUNKS_DIR.exists():
        raise FileNotFoundError(f"Missing processed chunks directory: {CHUNKS_DIR}")

    files = sorted(CHUNKS_DIR.glob("chunk_*.bin"))
    if not files:
        raise RuntimeError(f"No processed dust chunks found in {CHUNKS_DIR}")

    dims = tuple(int(v) for v in manifest["grid_shape_unity_xyz"])
    coarse_dims = tuple((v + COARSE_FACTOR - 1) // COARSE_FACTOR for v in dims)
    max_x_chunks = (dims[0] + CHUNK_SIZE - 1) // CHUNK_SIZE
    max_y_chunks = (dims[1] + CHUNK_SIZE - 1) // CHUNK_SIZE
    max_z_chunks = (dims[2] + CHUNK_SIZE - 1) // CHUNK_SIZE

    index_parts: list[np.ndarray] = []
    density_parts: list[np.ndarray] = []
    valid_files = 0

    print(f"Reading {len(files):,} processed dust bricks...")
    for file_number, path in enumerate(files, start=1):
        coords = parse_chunk(path)
        if coords is None:
            continue
        cx, cy, cz = coords
        if not (0 <= cx < max_x_chunks and 0 <= cy < max_y_chunks and 0 <= cz < max_z_chunks):
            raise RuntimeError(f"Brick is outside the manifest grid: {path}")

        coarse = read_coarse_brick(path)
        mask = np.isfinite(coarse) & (coarse > DENSITY_THRESHOLD)
        if np.any(mask):
            local_x, local_y, local_z = np.nonzero(mask)
            global_x = cx * COARSE_PER_CHUNK + local_x
            global_y = cy * COARSE_PER_CHUNK + local_y
            global_z = cz * COARSE_PER_CHUNK + local_z
            valid = (
                (global_x < coarse_dims[0]) &
                (global_y < coarse_dims[1]) &
                (global_z < coarse_dims[2])
            )
            if np.any(valid):
                global_x = global_x[valid].astype(np.int32, copy=False)
                global_y = global_y[valid].astype(np.int32, copy=False)
                global_z = global_z[valid].astype(np.int32, copy=False)
                linear = (global_x * coarse_dims[1] + global_y) * coarse_dims[2] + global_z
                density = coarse[local_x[valid], local_y[valid], local_z[valid]].astype(
                    np.float32, copy=False
                )
                index_parts.append(linear.astype(np.int32, copy=False))
                density_parts.append(density)

        valid_files += 1
        if file_number % 1000 == 0 or file_number == len(files):
            print(f"  {file_number:,}/{len(files):,} bricks scanned")

    if not index_parts:
        raise RuntimeError(
            f"No averaged dust cells exceeded DENSITY_THRESHOLD={DENSITY_THRESHOLD}."
        )

    indices = np.concatenate(index_parts)
    densities = np.concatenate(density_parts)
    print(f"Candidate cells above threshold: {indices.size:,}")
    print(f"Scanned valid brick files: {valid_files:,}")
    return indices, densities, coarse_dims


def select_sprites(
    manifest: dict,
    linear_indices: np.ndarray,
    densities: np.ndarray,
    coarse_dims: tuple[int, int, int],
) -> np.ndarray:
    rng = np.random.default_rng(RANDOM_SEED)
    count = min(MAX_SPRITES, int(linear_indices.size))

    # Weighted random sampling without replacement. The sublinear density weight
    # favors actual clouds while retaining weaker structures rather than selecting
    # only the absolute peaks.
    weights = np.power(np.maximum(densities, 1e-6), 0.35, dtype=np.float32)
    random_values = np.maximum(rng.random(linear_indices.size), 1e-12)
    scores = np.log(random_values).astype(np.float32) / weights

    if count < linear_indices.size:
        selected = np.argpartition(scores, -count)[-count:]
    else:
        selected = np.arange(count, dtype=np.int64)

    chosen_indices = linear_indices[selected]
    chosen_density = densities[selected]
    nx, ny, nz = coarse_dims
    gx = chosen_indices // (ny * nz)
    gy = (chosen_indices // nz) % ny
    gz = chosen_indices % nz

    edge = np.asarray(manifest["grid_min_edge_pc_xyz"], dtype=np.float32)
    spacing = np.asarray(manifest["voxel_spacing_pc_xyz"], dtype=np.float32)
    cell_size = spacing * COARSE_FACTOR

    positions = edge[None, :] + np.stack(
        [gx + 0.5, gy + 0.5, gz + 0.5], axis=1
    ).astype(np.float32) * cell_size[None, :]

    # A small deterministic jitter prevents a visible regular grid while keeping
    # each point near the coarse cell that supplied its density.
    jitter = rng.uniform(-0.28, 0.28, size=positions.shape).astype(np.float32)
    positions += jitter * cell_size[None, :]

    density_reference = max(float(np.percentile(chosen_density, 99.0)), 1e-6)
    normalized_density = np.clip(
        np.log1p(chosen_density) / np.log1p(density_reference), 0.0, 1.0
    ).astype(np.float32)

    # Deliberately stylized billboards: the sprite shader supplies the soft cloud
    # texture and the point's 3D position supplies parallax while the viewer moves.
    size_pc = (12.0 + 28.0 * np.sqrt(normalized_density)).astype(np.float32)
    opacity = (0.08 + 0.22 * normalized_density).astype(np.float32)
    rotation = rng.uniform(0.0, math.tau, size=count).astype(np.float32)
    aspect = rng.uniform(0.72, 1.42, size=count).astype(np.float32)

    output = np.zeros(count, dtype=RECORD)
    output["position_size"][:, :3] = positions
    output["position_size"][:, 3] = size_pc
    output["parameters"][:, 0] = opacity
    output["parameters"][:, 1] = rotation
    output["parameters"][:, 2] = aspect
    output["parameters"][:, 3] = normalized_density
    return output


def main() -> None:
    print("Spatial dust sprite builder")
    print(f"Input:  {CHUNKS_DIR}")
    print(f"Output: {OUTPUT_PATH}")
    print("This script only creates/replaces dust_sprites.bin; it does not modify project code.")

    manifest = read_manifest()
    indices, densities, coarse_dims = collect_candidates(manifest)
    sprites = select_sprites(manifest, indices, densities, coarse_dims)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    temporary_path = OUTPUT_PATH.with_suffix(OUTPUT_PATH.suffix + ".tmp")
    with temporary_path.open("wb") as handle:
        handle.write(HEADER.pack(FILE_MAGIC, FILE_VERSION, len(sprites)))
        sprites.tofile(handle)
    temporary_path.replace(OUTPUT_PATH)

    print("\nFinished")
    print(f"Sprites: {len(sprites):,}")
    print(f"Output size: {OUTPUT_PATH.stat().st_size / (1024**2):.2f} MiB")
    print(f"Output: {OUTPUT_PATH}")


if __name__ == "__main__":
    main()
