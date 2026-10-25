from pathlib import Path
import json
import math
import shutil

import numpy as np


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "DataRaw" / "Dust"
DOWNLOADED_DIR = RAW_DIR / "downloaded"

DUST_PATH = DOWNLOADED_DIR / "n_obs.npy"
X_PATH = DOWNLOADED_DIR / "X.npy"
Y_PATH = DOWNLOADED_DIR / "Y.npy"
Z_PATH = DOWNLOADED_DIR / "Z.npy"

PROCESSED_DIR = RAW_DIR / "processed"
CHUNKS_DIR = PROCESSED_DIR / "chunks"
MANIFEST_PATH = PROCESSED_DIR / "manifest.json"


# ============================================================
# Source coordinate system assumptions
# ============================================================

# Assumptions that MUST match the source dataset documentation:
#
#   Source origin: Galactic center.
#   Source +X: from the Galactic center outward, toward the Sun.
#   Source +Y: direction of Galactic rotation (Galactic l = +90 deg
#              in the local Sun-centered Galactic Cartesian frame).
#   Source +Z: toward Galactic north.
#   Source coordinate units: kpc.
#
# If the original dataset uses a different axis convention, adjust the
# coordinate equations and the array reordering below accordingly.

# Practical modern estimate for the Sun's distance from the Galactic
# center. Different methods disagree slightly; 8.23 kpc is an adopted
# reference value, not an exact value. See Leung et al. (2023), who found
# R0 = 8.5 kpc from Galactic-bar kinematics (for this specific data).
SUN_X_KPC = 8.5

# The Sun is above the Galactic disk mid-plane. Bennett & Bovy (2019)
# measured z_sun = 20.8 +/- 0.3 pc. This assumes the source Z=0 plane is
# the physical Galactic disk mid-plane.
SUN_Y_KPC = 0.0
SUN_Z_KPC = 0.0208

SOURCE_TO_PC = 1000.0


# ============================================================
# Source grid validation
# ============================================================

# Expected source grid spacing. The coordinates are expected to be
# samples/voxel centers separated by 0.002 kpc = 2 pc.
EXPECTED_SPACING_KPC = 0.002
GRID_SPACING_RTOL = 1e-6
GRID_SPACING_ATOL = 1e-12


# ============================================================
# Chunk format
# ============================================================

CHUNK_SIZE = 32
DENSITY_DTYPE = np.dtype("<f2")
CLEAR_OLD_OUTPUT = True


# ============================================================
# Cleanup
# ============================================================

def clear_old_output():
    """Remove old processed chunks/metadata before a fresh build."""
    if not CLEAR_OLD_OUTPUT:
        return

    if CHUNKS_DIR.exists():
        for path in CHUNKS_DIR.glob("*.bin"):
            path.unlink()

    # Remove output from the older coordinate format, if present.
    axes_dir = PROCESSED_DIR / "axes"
    if axes_dir.exists():
        shutil.rmtree(axes_dir)

    if MANIFEST_PATH.exists():
        MANIFEST_PATH.unlink()


# ============================================================
# Load source data
# ============================================================

def load_data():
    print("Loading dust data...\n")

    for path in (DUST_PATH, X_PATH, Y_PATH, Z_PATH):
        if not path.exists():
            raise FileNotFoundError(f"Required input file not found:\n{path}")

    dust = np.load(DUST_PATH, mmap_mode="r")
    X = np.asarray(np.load(X_PATH), dtype=np.float64)
    Y = np.asarray(np.load(Y_PATH), dtype=np.float64)
    Z = np.asarray(np.load(Z_PATH), dtype=np.float64)

    print("Dust shape:", dust.shape)
    print("X shape:", X.shape)
    print("Y shape:", Y.shape)
    print("Z shape:", Z.shape)
    print("Dust min:", float(np.nanmin(dust)))
    print("Dust max:", float(np.nanmax(dust)))

    if dust.ndim != 3:
        raise RuntimeError(f"Expected a 3D dust array, got ndim={dust.ndim}")

    nx, ny, nz = dust.shape
    if len(X) != nx:
        raise RuntimeError(f"X length {len(X)} != dust axis 0 {nx}")
    if len(Y) != ny:
        raise RuntimeError(f"Y length {len(Y)} != dust axis 1 {ny}")
    if len(Z) != nz:
        raise RuntimeError(f"Z length {len(Z)} != dust axis 2 {nz}")

    return dust, X, Y, Z


# ============================================================
# Validate coordinate grid and report the Sun's nearest voxel
# ============================================================

def validate_coordinates(X, Y, Z):
    print("\nValidating coordinate grid...")
    spacings = {}

    for name, axis in (("X", X), ("Y", Y), ("Z", Z)):
        if len(axis) < 2:
            raise RuntimeError(f"{name} axis has fewer than 2 values")
        if not np.all(np.isfinite(axis)):
            raise RuntimeError(f"{name} axis contains non-finite values")

        differences = np.diff(axis)
        if not np.all(differences > 0):
            raise RuntimeError(f"{name} axis must be strictly increasing")

        spacing = float(np.median(differences))
        spacings[name] = spacing

        print(f"{name}: {axis[0]} -> {axis[-1]} kpc")
        print(f"  median spacing: {spacing:.9g} kpc")

        if not np.allclose(
            differences,
            EXPECTED_SPACING_KPC,
            rtol=GRID_SPACING_RTOL,
            atol=GRID_SPACING_ATOL,
        ):
            raise RuntimeError(
                f"{name} spacing is not consistently "
                f"{EXPECTED_SPACING_KPC} kpc."
            )

    for name, coordinate, axis in (
        ("Sun X", SUN_X_KPC, X),
        ("Sun Y", SUN_Y_KPC, Y),
        ("Sun Z", SUN_Z_KPC, Z),
    ):
        if coordinate < axis[0] or coordinate > axis[-1]:
            raise RuntimeError(
                f"{name}={coordinate} kpc falls outside the source grid "
                f"[{axis[0]}, {axis[-1]}] kpc."
            )

    ix_sun = int(np.argmin(np.abs(X - SUN_X_KPC)))
    iy_sun = int(np.argmin(np.abs(Y - SUN_Y_KPC)))
    iz_sun = int(np.argmin(np.abs(Z - SUN_Z_KPC)))

    print("\nAdopted Sun source position:")
    print(f"  X = {SUN_X_KPC:.6f} kpc")
    print(f"  Y = {SUN_Y_KPC:.6f} kpc")
    print(f"  Z = {SUN_Z_KPC:.6f} kpc")
    print("Nearest source-grid indices (not necessarily exactly at the Sun):")
    print(f"  ix = {ix_sun}, X[ix] = {X[ix_sun]:.6f} kpc")
    print(f"  iy = {iy_sun}, Y[iy] = {Y[iy_sun]:.6f} kpc")
    print(f"  iz = {iz_sun}, Z[iz] = {Z[iz_sun]:.6f} kpc")

    return spacings


# ============================================================
# Source-grid -> Sun-centered Unity coordinates
# ============================================================

# The processed array is arranged in Unity X,Y,Z order, with each axis
# increasing in the positive Unity direction:
#
#   Unity +X = toward Galactic center
#   Unity +Y = toward Galactic north
#   Unity +Z = opposite source +Y, matching the Gaia-to-Unity mapping
#             (x_unity=x_gal, y_unity=z_gal, z_unity=-y_gal).
#
# Coordinate mapping, with all outputs in parsecs:
#
#   unity_x_pc = (SUN_X_KPC - source_X_KPC) * 1000
#   unity_y_pc = (source_Z_KPC - SUN_Z_KPC) * 1000
#   unity_z_pc = -(source_Y_KPC - SUN_Y_KPC) * 1000
#
# Since X and Y output directions reverse their corresponding source
# array directions, source X and source Y are reversed in each brick.


def output_grid_geometry(X, Y, Z, nx, ny, nz):
    """Return output voxel spacing/origins for Unity placement."""
    dx_pc = float(np.median(np.diff(X)) * SOURCE_TO_PC)
    dy_pc = float(np.median(np.diff(Z)) * SOURCE_TO_PC)
    dz_pc = float(np.median(np.diff(Y)) * SOURCE_TO_PC)

    # First output voxel centers map to source (X[-1], Z[0], Y[0]).
    first_center_pc = [
        (SUN_X_KPC - float(X[-1])) * SOURCE_TO_PC,
        (float(Z[0]) - SUN_Z_KPC) * SOURCE_TO_PC,
        (float(Y[0]) - SUN_Y_KPC) * SOURCE_TO_PC,
    ]

    voxel_spacing_pc = [dx_pc, dy_pc, dz_pc]
    grid_min_edge_pc = [
        first_center_pc[i] - voxel_spacing_pc[i] / 2.0
        for i in range(3)
    ]

    return {
        "first_voxel_center_pc": first_center_pc,
        "grid_min_edge_pc": grid_min_edge_pc,
        "voxel_spacing_pc": voxel_spacing_pc,
        "grid_shape_unity_xyz": [int(nx), int(nz), int(ny)],
    }


# ============================================================
# Process chunks
# ============================================================

def process_chunks(dust, X, Y, Z):
    nx, ny, nz = dust.shape

    # Output array dimensions are Unity X,Y,Z = source X,Z,Y.
    chunk_nx = math.ceil(nx / CHUNK_SIZE)
    chunk_ny = math.ceil(nz / CHUNK_SIZE)
    chunk_nz = math.ceil(ny / CHUNK_SIZE)
    total_possible = chunk_nx * chunk_ny * chunk_nz

    voxels_per_chunk = CHUNK_SIZE ** 3
    bytes_per_chunk = voxels_per_chunk * DENSITY_DTYPE.itemsize
    max_output_bytes = total_possible * bytes_per_chunk

    print("\nOutput chunk grid (Unity X,Y,Z):")
    print(f"  {chunk_nx} x {chunk_ny} x {chunk_nz}")
    print(f"  Possible chunks: {total_possible:,}")
    print(f"  Full chunk size: {bytes_per_chunk:,} bytes")
    print(f"  Maximum output size: {max_output_bytes / (1024**3):.2f} GiB\n")

    nonempty_chunks = 0
    empty_chunks = 0
    positive_voxels = 0
    invalid_voxels = 0
    global_max_density = -np.inf
    global_min_positive = np.inf

    for cx in range(chunk_nx):
        # Output +Unity X corresponds to decreasing source X.
        out_x0 = cx * CHUNK_SIZE
        out_x1 = min(out_x0 + CHUNK_SIZE, nx)
        source_x0 = nx - out_x1
        source_x1 = nx - out_x0

        for cy in range(chunk_ny):
            # Output +Unity Y corresponds to increasing source Z.
            source_z0 = cy * CHUNK_SIZE
            source_z1 = min(source_z0 + CHUNK_SIZE, nz)

            for cz in range(chunk_nz):
                # Output +Unity Z corresponds to decreasing source Y.
                out_z0 = cz * CHUNK_SIZE
                out_z1 = min(out_z0 + CHUNK_SIZE, ny)
                # Reverse the Galactic Y direction.
                source_y0 = out_z0
                source_y1 = out_z1

                # Source block order: source X,Y,Z.
                source_block = np.asarray(
                    dust[
                        source_x0:source_x1,
                        source_y0:source_y1,
                        source_z0:source_z1,
                    ]
                )

                # Reverse source X and Y, then put the axes in Unity X,Y,Z order.
                # Output shape: (source-X count, source-Z count, source-Y count).
                block = source_block[::-1, :, :].transpose(0, 2, 1)

                valid = np.isfinite(block) & (block > 0)
                if not np.any(valid):
                    empty_chunks += 1
                    continue

                valid_values = block[valid]
                positive_voxels += int(valid_values.size)
                invalid_voxels += int(block.size - valid_values.size)

                chunk_min = float(np.min(valid_values))
                chunk_max = float(np.max(valid_values))
                global_min_positive = min(global_min_positive, chunk_min)
                global_max_density = max(global_max_density, chunk_max)

                if chunk_max > np.finfo(np.float16).max:
                    raise RuntimeError(
                        f"Density exceeds float16 range in chunk ({cx},{cy},{cz}): "
                        f"{chunk_max}"
                    )

                brick = np.zeros(
                    (CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE),
                    dtype=DENSITY_DTYPE,
                )
                sx, sy, sz = block.shape
                brick[:sx, :sy, :sz] = np.where(valid, block, 0.0).astype(
                    DENSITY_DTYPE,
                    copy=False,
                )

                output_file = CHUNKS_DIR / f"chunk_{cx:06d}_{cy:06d}_{cz:06d}.bin"
                brick.tofile(output_file)
                nonempty_chunks += 1

        print(
            f"X chunks: {cx + 1}/{chunk_nx}"
            f" | non-empty: {nonempty_chunks:,}"
        )

    if positive_voxels == 0:
        raise RuntimeError("No finite positive density values were found in the grid.")

    return {
        "chunk_nx": chunk_nx,
        "chunk_ny": chunk_ny,
        "chunk_nz": chunk_nz,
        "total_possible_chunks": total_possible,
        "nonempty_chunks": nonempty_chunks,
        "empty_chunks": empty_chunks,
        "positive_voxels": positive_voxels,
        "invalid_voxels": invalid_voxels,
        "global_min_positive": float(global_min_positive),
        "global_max_density": float(global_max_density),
        "bytes_per_chunk": bytes_per_chunk,
    }


# ============================================================
# Write metadata required to position the chunk grid in Unity
# ============================================================

def write_manifest(X, Y, Z, dust_shape, spacings, stats):
    nx, ny, nz = dust_shape
    geometry = output_grid_geometry(X, Y, Z, nx, ny, nz)

    chunk_shape = [
        stats["chunk_nx"],
        stats["chunk_ny"],
        stats["chunk_nz"],
    ]
    voxel_spacing_pc = geometry["voxel_spacing_pc"]
    chunk_size_pc = [CHUNK_SIZE * spacing for spacing in voxel_spacing_pc]

    manifest = {
        "format_version": 1,
        "description": "Sun-centered dust density bricks, ordered in Unity X,Y,Z.",
        "source_units": "kpc",
        "unity_units": "pc (1 Unity unit = 1 pc)",
        "density_dtype": "float16 little-endian",
        "chunk_size_voxels": CHUNK_SIZE,
        "chunk_shape_unity_xyz": chunk_shape,
        "chunk_size_pc_xyz": chunk_size_pc,
        "source_grid_shape_xyz": [int(nx), int(ny), int(nz)],
        "grid_shape_unity_xyz": geometry["grid_shape_unity_xyz"],
        "source_spacing_kpc_xyz": {
            "x": float(spacings["X"]),
            "y": float(spacings["Y"]),
            "z": float(spacings["Z"]),
        },
        "voxel_spacing_pc_xyz": voxel_spacing_pc,
        "first_voxel_center_pc_xyz": geometry["first_voxel_center_pc"],
        "grid_min_edge_pc_xyz": geometry["grid_min_edge_pc"],
        "sun_source_position_kpc_xyz": [SUN_X_KPC, SUN_Y_KPC, SUN_Z_KPC],
        "sun_unity_position_pc_xyz": [0.0, 0.0, 0.0],
        "assumed_source_axes": {
            "source_x_positive": "outward from Galactic center toward the Sun",
            "source_y_positive": "direction of Galactic rotation; maps to positive Unity Z and negative Galactic Y in the HUD",
            "source_z_positive": "toward Galactic north; maps to positive Unity Y",
        },
        "mapping_source_kpc_to_unity_pc": {
            "unity_x": "(SUN_X_KPC - source_X_KPC) * 1000",
            "unity_y": "(source_Z_KPC - SUN_Z_KPC) * 1000",
            "unity_z": "(source_Y_KPC - SUN_Y_KPC) * 1000",
        },
        "unity_chunk_position_rule": (
            "For chunk (cx,cy,cz), its geometric center in Unity units is "
            "grid_min_edge_pc_xyz + ([cx,cy,cz] + 0.5) * chunk_size_pc_xyz. "
            "This treats grid_min_edge_pc_xyz as the outer edge before voxel 0."
        ),
        "stats": stats,
    }

    with MANIFEST_PATH.open("w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, allow_nan=False)

    print(f"\nManifest written: {MANIFEST_PATH}")
    print("First voxel center in Unity pc:", geometry["first_voxel_center_pc"])
    print("Grid outer minimum edge in Unity pc:", geometry["grid_min_edge_pc"])
    print("Voxel spacing Unity units (pc):", voxel_spacing_pc)
    print("Chunk physical size Unity units (pc):", chunk_size_pc)


# ============================================================
# Main
# ============================================================

if __name__ == "__main__":
    print("======================================")
    print("Dust processing: Sun-centered Unity coordinates")
    print("======================================\n")
    print("Input:")
    print(f"  {DUST_PATH}")
    print("\nOutput:")
    print(f"  {CHUNKS_DIR}")
    print(f"  {MANIFEST_PATH}")
    print("\nCoordinate assumptions:")
    print("  Source: Galactocentric Cartesian, kpc")
    print(f"  Adopted Sun X: {SUN_X_KPC:.3f} kpc")
    print(f"  Adopted Sun Z: {SUN_Z_KPC * SOURCE_TO_PC:.1f} pc above disk mid-plane")
    print("  Output: Sun-centered, Unity Y-up, pc")
    print("  Unity +X: toward Galactic center")
    print("  Unity +Y: toward Galactic north")
    print("  Unity +Z: opposite source +Y")
    print("  Density bricks: 32 x 32 x 32 float16 values")

    # Validate before deleting old outputs.
    dust, X, Y, Z = load_data()
    spacings = validate_coordinates(X, Y, Z)

    clear_old_output()
    CHUNKS_DIR.mkdir(parents=True, exist_ok=True)

    stats = process_chunks(dust, X, Y, Z)
    write_manifest(X, Y, Z, dust.shape, spacings, stats)

    print("\n======================================")
    print("Finished")
    print("======================================")
    print(f"Non-empty chunks: {stats['nonempty_chunks']:,}")
    print(f"Empty chunks: {stats['empty_chunks']:,}")
    print(f"Positive voxels: {stats['positive_voxels']:,}")
    print(f"Invalid/zero voxels: {stats['invalid_voxels']:,}")
    print(f"Minimum positive density: {stats['global_min_positive']:.8g}")
    print(f"Maximum density: {stats['global_max_density']:.8g}")
    print(f"Output chunks: {CHUNKS_DIR}")
    print(f"Manifest: {MANIFEST_PATH}")
