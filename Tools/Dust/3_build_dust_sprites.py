from __future__ import annotations

"""Build 20,000 spatial dust sprites from the existing processed dust bricks.

The selection mirrors the older visualization script:
- calculate a 95th-percentile density threshold across the full coarse grid;
- randomly select 20,000 cells at or above that threshold;
- rank the selected densities and remap their ranks through a clipped Gaussian;
- use that visual value for sprite color (in the shader) and alpha.

Positions are taken directly from the processed grid manifest in Unity-space pc.
The output keeps the existing DUSTSPR1 binary layout (32 bytes per sprite), so
DustSpriteRenderer's binary reader does not need a format change.
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
COARSE_FACTOR = 4                 # 4 x 2 pc voxels -> 8 pc sampling cells.
COARSE_PER_CHUNK = CHUNK_SIZE // COARSE_FACTOR
N_SPRITES = 20_000
DENSITY_PERCENTILE = 95.0
RANDOM_SEED = 123                 # Matches the old sampling script's seed.
ALPHA_MIN = 0.03
ALPHA_MAX = 0.65
GAUSS_CLIP = 2.5

FILE_MAGIC = b"DUSTSPR1"
FILE_VERSION = 1
HEADER = struct.Struct("<8sII")   # magic, version, record count
RECORD = np.dtype(
    [
        ("position_size", "<f4", (4,)),  # x, y, z, diameter_pc
        # opacity, rotation radians, aspect ratio, visual-density value (0..1)
        ("parameters", "<f4", (4,)),
    ]
)


def inverse_normal_cdf(p: np.ndarray) -> np.ndarray:
    """Peter J. Acklam approximation to the inverse standard-normal CDF."""
    a = [-3.969683028665376e+01, 2.209460984245205e+02,
         -2.759285104469687e+02, 1.383577518672690e+02,
         -3.066479806614716e+01, 2.506628277459239e+00]
    b = [-5.447609879822406e+01, 1.615858368580409e+02,
         -1.556989798598866e+02, 6.680131188771972e+01,
         -1.328068155288572e+01]
    c = [-7.784894002430293e-03, -3.223964580411365e-01,
         -2.400758277161838e+00, -2.549732539343734e+00,
          4.374664141464968e+00, 2.938163982698783e+00]
    d = [7.784695709041462e-03, 3.224671290700398e-01,
         2.445134137142996e+00, 3.754408661907416e+00]

    p = np.asarray(p, dtype=np.float64)
    q = np.zeros_like(p)
    plow = 0.02425
    phigh = 1.0 - plow

    low = p < plow
    mid = (p >= plow) & (p <= phigh)
    high = p > phigh

    q_low = np.sqrt(-2.0 * np.log(p[low]))
    q[low] = (
        (((((c[0] * q_low + c[1]) * q_low + c[2]) * q_low + c[3]) * q_low + c[4]) * q_low + c[5])
        / ((((d[0] * q_low + d[1]) * q_low + d[2]) * q_low + d[3]) * q_low + 1.0)
    )

    q_mid = p[mid] - 0.5
    r = q_mid * q_mid
    q[mid] = (
        (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q_mid
        / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0)
    )

    q_high = np.sqrt(-2.0 * np.log(1.0 - p[high]))
    q[high] = -(
        (((((c[0] * q_high + c[1]) * q_high + c[2]) * q_high + c[3]) * q_high + c[4]) * q_high + c[5])
        / ((((d[0] * q_high + d[1]) * q_high + d[2]) * q_high + d[3]) * q_high + 1.0)
    )
    return q


def read_manifest() -> dict:
    if not MANIFEST_PATH.exists():
        raise FileNotFoundError(f"Missing {MANIFEST_PATH}. Run 2_process_dust.py first.")
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
        raise RuntimeError(f"Expected a 2 pc voxel grid; found {spacing}.")
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
    expected = CHUNK_SIZE ** 3
    if raw.size != expected:
        raise RuntimeError(
            f"Invalid processed brick {path}: expected {expected} values, got {raw.size}."
        )
    brick = raw.astype(np.float32, copy=False).reshape(CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE)
    return brick.reshape(
        COARSE_PER_CHUNK, COARSE_FACTOR,
        COARSE_PER_CHUNK, COARSE_FACTOR,
        COARSE_PER_CHUNK, COARSE_FACTOR,
    ).mean(axis=(1, 3, 5), dtype=np.float32)


def collect_coarse_grid(manifest: dict) -> np.ndarray:
    """Rebuild the complete coarse grid; missing brick files represent zero density."""
    if not CHUNKS_DIR.exists():
        raise FileNotFoundError(f"Missing processed chunks directory: {CHUNKS_DIR}")
    files = sorted(CHUNKS_DIR.glob("chunk_*.bin"))
    if not files:
        raise RuntimeError(f"No processed dust chunks found in {CHUNKS_DIR}")

    dims = tuple(int(v) for v in manifest["grid_shape_unity_xyz"])
    coarse_dims = tuple((v + COARSE_FACTOR - 1) // COARSE_FACTOR for v in dims)
    max_chunks = tuple((v + CHUNK_SIZE - 1) // CHUNK_SIZE for v in dims)

    # 6.4 million float32 cells is about 25 MiB for the current dust grid.
    grid = np.zeros(coarse_dims, dtype=np.float32)
    print(f"Rebuilding coarse density grid from {len(files):,} bricks...")

    valid_files = 0
    for file_number, path in enumerate(files, start=1):
        coords = parse_chunk(path)
        if coords is None:
            continue
        cx, cy, cz = coords
        if not (0 <= cx < max_chunks[0] and 0 <= cy < max_chunks[1] and 0 <= cz < max_chunks[2]):
            raise RuntimeError(f"Brick is outside the manifest grid: {path}")

        coarse = read_coarse_brick(path)
        x0, y0, z0 = cx * COARSE_PER_CHUNK, cy * COARSE_PER_CHUNK, cz * COARSE_PER_CHUNK
        x1 = min(x0 + COARSE_PER_CHUNK, coarse_dims[0])
        y1 = min(y0 + COARSE_PER_CHUNK, coarse_dims[1])
        z1 = min(z0 + COARSE_PER_CHUNK, coarse_dims[2])
        if x0 < x1 and y0 < y1 and z0 < z1:
            grid[x0:x1, y0:y1, z0:z1] = coarse[:x1-x0, :y1-y0, :z1-z0]

        valid_files += 1
        if file_number % 1000 == 0 or file_number == len(files):
            print(f"  {file_number:,}/{len(files):,} bricks scanned")

    print(f"Scanned valid brick files: {valid_files:,}")
    return grid


def build_sprites(manifest: dict, coarse_grid: np.ndarray) -> np.ndarray:
    rng = np.random.default_rng(RANDOM_SEED)
    finite_values = coarse_grid[np.isfinite(coarse_grid)]
    if finite_values.size == 0:
        raise RuntimeError("The processed coarse grid contains no finite density values.")

    threshold = float(np.percentile(finite_values, DENSITY_PERCENTILE))
    candidate_mask = np.isfinite(coarse_grid) & (coarse_grid >= threshold)
    candidate_indices = np.flatnonzero(candidate_mask)
    candidate_count = int(candidate_indices.size)

    print(f"{DENSITY_PERCENTILE:g}th-percentile density threshold: {threshold:.8g}")
    print(f"Cells at or above threshold: {candidate_count:,}")
    if candidate_count == 0:
        raise RuntimeError("No cells meet the percentile density threshold.")

    # The old script's rejection sampling is uniform among cells passing the
    # threshold. Sample without replacement when possible to avoid duplicate cells.
    replace = candidate_count < N_SPRITES
    sample_count = N_SPRITES
    if replace:
        print("WARNING: fewer than 20,000 eligible cells; sampling with replacement, so positions may repeat.")
    chosen_linear = rng.choice(candidate_indices, size=sample_count, replace=replace)
    chosen_density = coarse_grid.ravel()[chosen_linear].astype(np.float32, copy=False)

    nx, ny, nz = coarse_grid.shape
    gx = chosen_linear // (ny * nz)
    gy = (chosen_linear // nz) % ny
    gz = chosen_linear % nz

    # Manifest coordinates are already in Unity-space pc, so no axis swaps,
    # recentering, or scale transforms are applied.
    edge = np.asarray(manifest["grid_min_edge_pc_xyz"], dtype=np.float32)
    spacing = np.asarray(manifest["voxel_spacing_pc_xyz"], dtype=np.float32)
    cell_size = spacing * COARSE_FACTOR
    positions = edge[None, :] + np.stack(
        [gx + 0.5, gy + 0.5, gz + 0.5], axis=1
    ).astype(np.float32) * cell_size[None, :]

    # Keep the small positional jitter from the current sprite builder.
    positions += rng.uniform(-0.28, 0.28, size=positions.shape).astype(np.float32) * cell_size[None, :]

    # Same visual remap as the older CSV script: rank -> Gaussian z-score -> clip -> 0..1.
    order = np.argsort(chosen_density, kind="stable")
    sorted_density = chosen_density[order]
    ranks = np.empty(sample_count, dtype=np.float64)
    start = 0
    while start < sample_count:
        end = start + 1
        while end < sample_count and sorted_density[end] == sorted_density[start]:
            end += 1
        # Average of the one-based ranks [start + 1, end], matching pandas rank(method="average").
        ranks[order[start:end]] = ((start + 1) + end) * 0.5
        start = end
    p = (ranks - 0.5) / len(ranks)
    z_gauss = inverse_normal_cdf(p)
    z_gauss = np.clip(z_gauss, -GAUSS_CLIP, GAUSS_CLIP)
    density_visual = ((z_gauss + GAUSS_CLIP) / (2.0 * GAUSS_CLIP)).astype(np.float32)

    # Preserve the current billboard sizing while using the new visual-density scale.
    size_pc = (12.0 + 28.0 * np.sqrt(density_visual)).astype(np.float32)
    opacity = (ALPHA_MIN + (ALPHA_MAX - ALPHA_MIN) * density_visual).astype(np.float32)
    rotation = rng.uniform(0.0, math.tau, size=sample_count).astype(np.float32)
    aspect = rng.uniform(0.72, 1.42, size=sample_count).astype(np.float32)

    output = np.zeros(sample_count, dtype=RECORD)
    output["position_size"][:, :3] = positions
    output["position_size"][:, 3] = size_pc
    output["parameters"][:, 0] = opacity
    output["parameters"][:, 1] = rotation
    output["parameters"][:, 2] = aspect
    # The shader maps this value through a magma_r-like palette, preserving the
    # existing 32-byte binary format without adding per-record RGB fields.
    output["parameters"][:, 3] = density_visual

    print(f"Selected sprites: {sample_count:,}")
    print(f"Alpha range: {opacity.min():.4f} .. {opacity.max():.4f}")
    print(f"Visual-density range: {density_visual.min():.4f} .. {density_visual.max():.4f}")
    return output


def main() -> None:
    print("Spatial dust sprite builder (95th-percentile selection)")
    print(f"Input:  {CHUNKS_DIR}")
    print(f"Manifest: {MANIFEST_PATH}")
    print(f"Output: {OUTPUT_PATH}")
    print(f"Target: {N_SPRITES:,} sprites; density percentile: {DENSITY_PERCENTILE:g}")

    manifest = read_manifest()
    coarse_grid = collect_coarse_grid(manifest)
    sprites = build_sprites(manifest, coarse_grid)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    temporary_path = OUTPUT_PATH.with_suffix(OUTPUT_PATH.suffix + ".tmp")
    with temporary_path.open("wb") as handle:
        handle.write(HEADER.pack(FILE_MAGIC, FILE_VERSION, len(sprites)))
        sprites.tofile(handle)
    temporary_path.replace(OUTPUT_PATH)

    print("\nFinished")
    print(f"Sprites: {len(sprites):,}")
    print(f"Record stride: {RECORD.itemsize} bytes")
    print(f"Output size: {OUTPUT_PATH.stat().st_size / (1024**2):.2f} MiB")
    print(f"Output: {OUTPUT_PATH}")


if __name__ == "__main__":
    main()
