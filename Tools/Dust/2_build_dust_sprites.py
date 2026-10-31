from __future__ import annotations

"""Build 20,000 spatial dust sprites directly from Edenhofer et al. HEALPix data.

Input:
    DataRaw/Dust/Edenhofer2024/mean_and_std_healpix.fits

Output:
    Data/Dust/dust_sprites.bin

The output is compatible with the existing Unity DustSpriteRenderer:
    DUSTSPR1 header + 32-byte records.

Dependencies:
    numpy
    astropy

No healpy, compiled extensions, intermediate Cartesian grid, or runtime
network access are required.

Selection/visualization logic:
- Calculate a global 95th-percentile threshold over finite map voxels.
- Keep voxels at or above the threshold.
- Select 20,000 candidate voxels.
- Account for the larger physical volume of HEALPix radial shells when
  allocating sprites between shells.
- Rank selected extinction values and map their ranks through a clipped
  Gaussian distribution to visual density, size, and alpha.

Important data interpretation:
The map stores differential extinction density (E pc^-1), not dust mass
 density. The integrated inner map inside the first radial boundary is not
used because it does not provide 3D voxel positions within that region.
"""

import math
import struct
from pathlib import Path

import numpy as np
from astropy.io import fits


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]
INPUT_DIR = PROJECT_ROOT / "DataRaw" / "Dust" / "Edenhofer2024"
INPUT_PATH = INPUT_DIR / "mean_and_std_healpix.fits"

OUTPUT_DIR = PROJECT_ROOT / "Data" / "Dust"
OUTPUT_PATH = OUTPUT_DIR / "dust_sprites.bin"


# ============================================================
# Settings
# ============================================================

N_SPRITES = 20_000
DENSITY_PERCENTILE = 95.0
RANDOM_SEED = 123

ALPHA_MIN = 0.03
ALPHA_MAX = 0.65
GAUSS_CLIP = 2.5

# HEALPix pixel position jitter. This preserves the small random offset used
# by the old Cartesian-grid builder, scaled to the approximate HEALPix cell.
POSITION_JITTER_FRACTION = 0.28

# Unity coordinate convention in this project:
# Unity X = Galactic X
# Unity Y = Galactic Z (north Galactic pole)
# Unity Z = -Galactic Y


# ============================================================
# Binary output format -- unchanged
# ============================================================

FILE_MAGIC = b"DUSTSPR1"
FILE_VERSION = 1

HEADER = struct.Struct("<8sII")

RECORD = np.dtype(
    [
        ("position_size", "<f4", (4,)),
        # opacity, rotation radians, aspect ratio, visual density
        ("parameters", "<f4", (4,)),
    ]
)


# ============================================================
# Gaussian inverse CDF
# ============================================================

def inverse_normal_cdf(p: np.ndarray) -> np.ndarray:
    """Peter J. Acklam approximation to the inverse standard-normal CDF."""

    a = [
        -3.969683028665376e+01,
         2.209460984245205e+02,
        -2.759285104469687e+02,
         1.383577518672690e+02,
        -3.066479806614716e+01,
         2.506628277459239e+00,
    ]

    b = [
        -5.447609879822406e+01,
         1.615858368580409e+02,
        -1.556989798598866e+02,
         6.680131188771972e+01,
        -1.328068155288572e+01,
    ]

    c = [
        -7.784894002430293e-03,
        -3.223964580411365e-01,
        -2.400758277161838e+00,
        -2.549732539343734e+00,
         4.374664141464968e+00,
         2.938163982698783e+00,
    ]

    d = [
         7.784695709041462e-03,
         3.224671290700398e-01,
         2.445134137142996e+00,
         3.754408661907416e+00,
    ]

    p = np.asarray(p, dtype=np.float64)
    q = np.zeros_like(p)

    plow = 0.02425
    phigh = 1.0 - plow

    low = p < plow
    mid = (p >= plow) & (p <= phigh)
    high = p > phigh

    q_low = np.sqrt(-2.0 * np.log(p[low]))
    q[low] = (
        (((((c[0] * q_low + c[1]) * q_low + c[2])
            * q_low + c[3]) * q_low + c[4]) * q_low + c[5])
        /
        ((((d[0] * q_low + d[1]) * q_low + d[2])
            * q_low + d[3]) * q_low + 1.0)
    )

    q_mid = p[mid] - 0.5
    r = q_mid * q_mid

    q[mid] = (
        (((((a[0] * r + a[1]) * r + a[2])
            * r + a[3]) * r + a[4]) * r + a[5]) * q_mid
        /
        (((((b[0] * r + b[1]) * r + b[2])
            * r + b[3]) * r + b[4]) * r + 1.0)
    )

    q_high = np.sqrt(-2.0 * np.log(1.0 - p[high]))
    q[high] = -(
        (((((c[0] * q_high + c[1]) * q_high + c[2])
            * q_high + c[3]) * q_high + c[4]) * q_high + c[5])
        /
        ((((d[0] * q_high + d[1]) * q_high + d[2])
            * q_high + d[3]) * q_high + 1.0)
    )

    return q


# ============================================================
# Pure NumPy HEALPix pixel-center conversion
# ============================================================

def healpix_pix2vec(
    nside: int,
    pixels: np.ndarray,
    nested: bool,
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Return HEALPix pixel-center unit vectors for RING or NESTED ordering.

    The returned x/y/z axes are Galactic Cartesian axes:
        x = cos(b) cos(l)
        y = cos(b) sin(l)
        z = sin(b)

    This is a NumPy implementation of the standard HEALPix pixel-center
    equations. NESTED ordering requires nside to be a power of two.
    """

    pixels = np.asarray(pixels, dtype=np.int64)
    npix = 12 * nside * nside

    if nside <= 0:
        raise ValueError(f"Invalid HEALPix NSIDE: {nside}")

    if np.any(pixels < 0) or np.any(pixels >= npix):
        raise ValueError("HEALPix pixel index out of range.")

    if nested and (nside & (nside - 1)) != 0:
        raise ValueError(
            "NESTED HEALPix ordering requires NSIDE to be a power of two."
        )

    z = np.empty(len(pixels), dtype=np.float64)
    phi = np.empty(len(pixels), dtype=np.float64)

    if not nested:
        # ----------------------------------------------------
        # RING ordering
        # ----------------------------------------------------

        ncap = 2 * nside * (nside - 1)
        nl2 = 2 * nside
        nl4 = 4 * nside

        north = pixels < ncap
        equatorial = (pixels >= ncap) & (pixels < npix - ncap)
        south = pixels >= npix - ncap

        if np.any(north):
            p = pixels[north]

            iring = (
                0.5 * (1.0 + np.sqrt(1.0 + 2.0 * p))
            ).astype(np.int64)

            iphi = p + 1 - 2 * iring * (iring - 1)

            z[north] = (
                1.0
                - iring.astype(np.float64) ** 2
                / (3.0 * nside**2)
            )

            phi[north] = (
                (iphi - 0.5) * math.pi / (2.0 * iring)
            )

        if np.any(equatorial):
            p = pixels[equatorial] - ncap

            iring = p // nl4 + nside
            iphi = p % nl4 + 1

            # Alternating half-pixel phase between adjacent rings.
            fodd = np.where((iring + nside) & 1, 1.0, 0.5)

            z[equatorial] = (
                (nl2 - iring) * (2.0 / (3.0 * nside))
            )

            phi[equatorial] = (
                (iphi - fodd) * math.pi / nl2
            )

        if np.any(south):
            p = npix - pixels[south]

            iring = (
                0.5 * (1.0 + np.sqrt(2.0 * p - 1.0))
            ).astype(np.int64)

            iphi = (
                4 * iring + 1
                - (p - 2 * iring * (iring - 1))
            )

            z[south] = (
                -1.0
                + iring.astype(np.float64) ** 2
                / (3.0 * nside**2)
            )

            phi[south] = (
                (iphi - 0.5) * math.pi / (2.0 * iring)
            )

    else:
        # ----------------------------------------------------
        # NESTED ordering
        # ----------------------------------------------------

        order = nside.bit_length() - 1
        face_size = nside * nside

        face = pixels // face_size
        intra = pixels % face_size

        # De-interleave the Morton/Z-order bits into face-local x and y.
        ix = np.zeros(len(pixels), dtype=np.int64)
        iy = np.zeros(len(pixels), dtype=np.int64)

        for bit in range(order):
            ix |= ((intra >> (2 * bit)) & 1) << bit
            iy |= ((intra >> (2 * bit + 1)) & 1) << bit

        jrll = np.array(
            [2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4],
            dtype=np.int64,
        )

        jpll = np.array(
            [1, 3, 5, 7, 0, 2, 4, 6, 1, 3, 5, 7],
            dtype=np.int64,
        )

        jr = jrll[face] * nside - ix - iy - 1

        nr = np.full(len(pixels), nside, dtype=np.int64)
        kshift = np.zeros(len(pixels), dtype=np.int64)

        north = jr < nside
        south = jr > 3 * nside

        nr[north] = jr[north]
        nr[south] = 4 * nside - jr[south]

        equatorial = ~(north | south)
        kshift[equatorial] = (
            jr[equatorial] - nside
        ) & 1

        z[north] = (
            1.0
            - nr[north].astype(np.float64) ** 2
            / (3.0 * nside**2)
        )

        z[south] = (
            -1.0
            + nr[south].astype(np.float64) ** 2
            / (3.0 * nside**2)
        )

        z[equatorial] = (
            (2 * nside - jr[equatorial])
            * (2.0 / (3.0 * nside))
        )

        jp = (
            jpll[face] * nr + ix - iy + 1 + kshift
        ) // 2

        jp = np.where(jp > 4 * nr, jp - 4 * nr, jp)
        jp = np.where(jp < 1, jp + 4 * nr, jp)

        phi = (
            jp - (kshift + 1) * 0.5
        ) * (math.pi / (2.0 * nr))

    xy = np.sqrt(np.maximum(0.0, 1.0 - z * z))

    x = xy * np.cos(phi)
    y = xy * np.sin(phi)

    return x, y, z


# ============================================================
# Read original HEALPix FITS data
# ============================================================

def read_healpix_data(hdul):
    """Find the mean extinction map and radial center/boundary tables."""

    mean_data = None
    nside = None
    nested = None

    radial_centers = None
    radial_boundaries = None

    for hdu in hdul:
        name = hdu.name.lower().strip()

        # Mean reconstruction. The published dustmaps reader recognizes
        # these names and reads NSIDE/ORDERING from the image header.
        if (
            hdu.data is not None
            and name in ("mean", "primary", "healpix times distance")
        ):
            if getattr(hdu.data, "ndim", 0) != 2:
                continue

            if "NSIDE" not in hdu.header:
                raise RuntimeError(
                    f"Mean-map HDU {hdu.name!r} has no NSIDE header value."
                )

            if "ORDERING" not in hdu.header:
                raise RuntimeError(
                    f"Mean-map HDU {hdu.name!r} has no ORDERING header value."
                )

            if mean_data is not None:
                raise RuntimeError(
                    "Found more than one 2D mean-map HDU; refusing to guess."
                )

            mean_data = hdu.data
            nside = int(hdu.header["NSIDE"])

            ordering = str(hdu.header["ORDERING"]).strip().upper()
            if ordering == "RING":
                nested = False
            elif ordering.startswith("NEST"):
                nested = True
            else:
                raise RuntimeError(
                    f"Unsupported HEALPix ORDERING value: {ordering!r}"
                )

        # Radial metadata tables.
        if isinstance(hdu, fits.BinTableHDU):
            columns = list(hdu.columns.names or [])

            if columns == ["radial pixel centers"]:
                radial_centers = np.asarray(
                    hdu.data["radial pixel centers"],
                    dtype=np.float64,
                ).squeeze()

            elif columns == ["radial pixel boundaries"]:
                radial_boundaries = np.asarray(
                    hdu.data["radial pixel boundaries"],
                    dtype=np.float64,
                ).squeeze()

    if mean_data is None:
        raise RuntimeError(
            "Could not locate the 2D mean HEALPix map. "
            "Expected an HDU named MEAN, PRIMARY, or HEALPIX TIMES DISTANCE."
        )

    if radial_centers is None or radial_boundaries is None:
        raise RuntimeError(
            "Could not find the radial pixel centers and boundaries tables."
        )

    npix = 12 * nside**2

    if mean_data.shape != (len(radial_centers), npix):
        raise RuntimeError(
            f"Unexpected mean-map shape {mean_data.shape}; expected "
            f"({len(radial_centers)}, {npix})."
        )

    if len(radial_boundaries) != len(radial_centers) + 1:
        raise RuntimeError(
            "Expected exactly one more radial boundary than radial centers."
        )

    if not np.all(np.isfinite(radial_centers)):
        raise RuntimeError("Radial centers contain non-finite values.")

    if not np.all(np.isfinite(radial_boundaries)):
        raise RuntimeError("Radial boundaries contain non-finite values.")

    if np.any(np.diff(radial_centers) <= 0):
        raise RuntimeError("Radial centers are not strictly increasing.")

    if np.any(np.diff(radial_boundaries) <= 0):
        raise RuntimeError("Radial boundaries are not strictly increasing.")

    if radial_boundaries[0] < 0:
        raise RuntimeError("Radial boundaries cannot be negative.")

    if np.any(radial_centers <= radial_boundaries[:-1]) or np.any(
        radial_centers >= radial_boundaries[1:]
    ):
        raise RuntimeError(
            "At least one radial center is outside its corresponding shell."
        )

    return (
        mean_data,
        nside,
        nested,
        radial_centers,
        radial_boundaries,
    )


# ============================================================
# Percentile and shell allocation
# ============================================================

def calculate_density_threshold(mean_data) -> float:
    """Calculate the same finite-value global percentile used by old builder."""

    print("Calculating global density percentile...")
    print("This scans the full mean map and may take a little while.")

    finite_mask = np.isfinite(mean_data)
    finite_count = int(np.count_nonzero(finite_mask))

    if finite_count == 0:
        raise RuntimeError("The mean HEALPix map contains no finite values.")

    # The old Cartesian builder computed np.percentile over finite cells.
    # Keep that same percentile definition here.
    finite_values = np.asarray(
        mean_data[finite_mask],
        dtype=np.float32,
    )
    del finite_mask

    threshold = float(
        np.percentile(finite_values, DENSITY_PERCENTILE)
    )

    del finite_values

    print(f"Finite map cells: {finite_count:,}")
    print(
        f"{DENSITY_PERCENTILE:g}th-percentile threshold: "
        f"{threshold:.8g}"
    )

    return threshold


def allocate_sprites_to_shells(
    candidate_counts: np.ndarray,
    shell_volumes: np.ndarray,
    rng: np.random.Generator,
) -> tuple[np.ndarray, bool]:
    """Allocate sprites proportional to eligible physical volume per shell.

    All HEALPix pixels have the same solid angle. A pixel in radial shell i
    therefore has volume proportional to (r_outer^3 - r_inner^3) / 3.
    The common solid-angle factor cancels when computing probabilities.

    When there are enough candidates, no candidate pixel is selected twice.
    When there are fewer than N_SPRITES candidates, replacement is enabled,
    matching the fallback behavior of the original Cartesian builder.
    """

    total_candidates = int(candidate_counts.sum())

    if total_candidates == 0:
        raise RuntimeError("No cells passed the density threshold.")

    shell_weights = (
        candidate_counts.astype(np.float64) * shell_volumes
    )
    shell_weights[candidate_counts == 0] = 0.0

    total_weight = float(shell_weights.sum())
    if not math.isfinite(total_weight) or total_weight <= 0:
        raise RuntimeError("No eligible shell has positive physical volume.")

    probabilities = shell_weights / total_weight

    if total_candidates < N_SPRITES:
        allocation = rng.multinomial(N_SPRITES, probabilities)
        return allocation.astype(np.int64), True

    allocation = rng.multinomial(N_SPRITES, probabilities).astype(np.int64)

    # A multinomial draw can request more pixels from a shell than it has.
    # Cap those shells, then redistribute the excess over remaining eligible
    # cells, still weighted by their physical volume.
    while True:
        over = allocation > candidate_counts
        if not np.any(over):
            break

        excess = int(
            np.sum(allocation[over] - candidate_counts[over])
        )
        allocation[over] = candidate_counts[over]

        capacity = candidate_counts - allocation
        remaining_weights = (
            capacity.astype(np.float64) * shell_volumes
        )
        remaining_weights[capacity <= 0] = 0.0

        weight_sum = float(remaining_weights.sum())
        if not math.isfinite(weight_sum) or weight_sum <= 0:
            raise RuntimeError(
                "Could not redistribute the requested sprites across shells."
            )

        allocation += rng.multinomial(
            excess,
            remaining_weights / weight_sum,
        )

    return allocation, False


# ============================================================
# Build sprite records
# ============================================================

def build_sprites(
    mean_data,
    nside: int,
    nested: bool,
    radial_centers: np.ndarray,
    radial_boundaries: np.ndarray,
) -> np.ndarray:

    rng = np.random.default_rng(RANDOM_SEED)

    nradii, npix = mean_data.shape

    print()
    print(f"HEALPix NSIDE: {nside}")
    print(f"Ordering: {'NESTED' if nested else 'RING'}")
    print(f"Angular pixels per shell: {npix:,}")
    print(f"Radial shells: {nradii:,}")
    print(
        f"Radial coverage: {radial_boundaries[0]:.2f} - "
        f"{radial_boundaries[-1]:.2f} pc"
    )

    # --------------------------------------------------------
    # Global threshold
    # --------------------------------------------------------

    threshold = calculate_density_threshold(mean_data)

    # --------------------------------------------------------
    # Collect eligible pixels in every radial shell
    # --------------------------------------------------------

    candidates_by_shell: list[np.ndarray] = []
    candidate_counts = np.zeros(nradii, dtype=np.int64)

    print()
    print("Finding eligible dust cells...")

    for ri in range(nradii):
        layer = mean_data[ri]

        mask = np.isfinite(layer) & (layer >= threshold)

        pixels = np.flatnonzero(mask).astype(np.int32, copy=False)
        candidates_by_shell.append(pixels)
        candidate_counts[ri] = pixels.size

        if (ri + 1) % 25 == 0 or ri + 1 == nradii:
            print(
                f"\rRadial shells: {ri + 1:,}/{nradii:,}",
                end="",
                flush=True,
            )

    print()

    total_candidates = int(candidate_counts.sum())
    print(f"Eligible cells: {total_candidates:,}")

    if total_candidates == 0:
        raise RuntimeError(
            "No cells passed the density threshold. "
            "Check the FITS data and DENSITY_PERCENTILE."
        )

    # --------------------------------------------------------
    # Physical volume of one pixel-shell cell, up to a common
    # HEALPix solid-angle factor.
    # --------------------------------------------------------

    inner = radial_boundaries[:-1]
    outer = radial_boundaries[1:]

    shell_volumes = (outer**3 - inner**3) / 3.0

    if not np.all(np.isfinite(shell_volumes)) or np.any(shell_volumes <= 0):
        raise RuntimeError("Invalid radial shell volumes.")

    # --------------------------------------------------------
    # Allocate sprites to shells and sample eligible pixels
    # --------------------------------------------------------

    allocation, replace = allocate_sprites_to_shells(
        candidate_counts,
        shell_volumes,
        rng,
    )

    if replace:
        print(
            "WARNING: fewer than 20,000 eligible cells; "
            "sampling with replacement, as in the original builder."
        )

    positions_list = []
    density_list = []

    pixel_solid_angle = 4.0 * math.pi / npix
    angular_pixel_width = math.sqrt(pixel_solid_angle)

    print()
    print("Selecting sprites and calculating positions...")

    for ri, count_value in enumerate(allocation):
        count = int(count_value)
        if count == 0:
            continue

        pixels = candidates_by_shell[ri]

        selected_pixels = rng.choice(
            pixels,
            size=count,
            replace=replace,
        )

        radius = float(radial_centers[ri])

        # Galactic Cartesian direction vectors from HEALPix.
        gx, gy, gz = healpix_pix2vec(
            nside,
            selected_pixels,
            nested,
        )

        # Existing project convention:
        # Unity (x, y, z) = Galactic (x, z, -y)
        positions = np.column_stack(
            (
                gx * radius,
                gz * radius,
                gy * radius,
            )
        ).astype(np.float32)

        # Approximate physical size of one HEALPix cell at this radius.
        radial_width = float(
            radial_boundaries[ri + 1] - radial_boundaries[ri]
        )
        transverse_width = radius * angular_pixel_width
        jitter_scale = (
            POSITION_JITTER_FRACTION
            * max(radial_width, transverse_width)
        )

        positions += rng.uniform(
            -jitter_scale,
            jitter_scale,
            size=positions.shape,
        ).astype(np.float32)

        densities = np.asarray(
            mean_data[ri, selected_pixels],
            dtype=np.float32,
        )

        positions_list.append(positions)
        density_list.append(densities)

    positions = np.concatenate(positions_list, axis=0)
    chosen_density = np.concatenate(density_list, axis=0)

    if positions.shape != (N_SPRITES, 3):
        raise RuntimeError(
            f"Internal sprite-count error: produced {positions.shape[0]} "
            f"positions, expected {N_SPRITES}."
        )

    # --------------------------------------------------------
    # Rank selected densities and remap ranks through a
    # clipped Gaussian, as in the original sprite builder.
    # --------------------------------------------------------

    order = np.argsort(chosen_density, kind="stable")
    sorted_density = chosen_density[order]

    ranks = np.empty(N_SPRITES, dtype=np.float64)

    start = 0
    while start < N_SPRITES:
        end = start + 1

        while (
            end < N_SPRITES
            and sorted_density[end] == sorted_density[start]
        ):
            end += 1

        # Average one-based ranks for tied values, matching pandas
        # rank(method="average").
        ranks[order[start:end]] = (
            (start + 1) + end
        ) * 0.5

        start = end

    p = (ranks - 0.5) / N_SPRITES
    z_gauss = inverse_normal_cdf(p)
    z_gauss = np.clip(z_gauss, -GAUSS_CLIP, GAUSS_CLIP)

    density_visual = (
        (z_gauss + GAUSS_CLIP) / (2.0 * GAUSS_CLIP)
    ).astype(np.float32)

    # --------------------------------------------------------
    # Existing sprite appearance settings
    # --------------------------------------------------------

    size_pc = (
        12.0 + 28.0 * np.sqrt(density_visual)
    ).astype(np.float32)

    opacity = (
        ALPHA_MIN
        + (ALPHA_MAX - ALPHA_MIN) * density_visual
    ).astype(np.float32)

    rotation = rng.uniform(
        0.0,
        math.tau,
        size=N_SPRITES,
    ).astype(np.float32)

    aspect = rng.uniform(
        0.72,
        1.42,
        size=N_SPRITES,
    ).astype(np.float32)

    # --------------------------------------------------------
    # Write existing binary record structure
    # --------------------------------------------------------

    output = np.zeros(N_SPRITES, dtype=RECORD)

    output["position_size"][:, :3] = positions
    output["position_size"][:, 3] = size_pc

    output["parameters"][:, 0] = opacity
    output["parameters"][:, 1] = rotation
    output["parameters"][:, 2] = aspect
    output["parameters"][:, 3] = density_visual

    # Randomize the order of complete sprite records.
    rng.shuffle(output)

    print()
    print(f"Selected sprites: {N_SPRITES:,}")
    print(
        f"Sprite X range: {positions[:, 0].min():.1f} .. "
        f"{positions[:, 0].max():.1f} pc"
    )
    print(
        f"Sprite Y range: {positions[:, 1].min():.1f} .. "
        f"{positions[:, 1].max():.1f} pc"
    )
    print(
        f"Sprite Z range: {positions[:, 2].min():.1f} .. "
        f"{positions[:, 2].max():.1f} pc"
    )
    print(
        f"Alpha range: {opacity.min():.4f} .. {opacity.max():.4f}"
    )
    print(
        f"Visual-density range: {density_visual.min():.4f} .. "
        f"{density_visual.max():.4f}"
    )

    return output


# ============================================================
# Main
# ============================================================

def main() -> None:
    if not INPUT_PATH.exists():
        raise FileNotFoundError(
            f"Missing HEALPix input file:\n{INPUT_PATH}"
        )

    print("======================================")
    print("Edenhofer 2024 HEALPix Dust Sprites")
    print("======================================")
    print(f"Input:  {INPUT_PATH}")
    print(f"Output: {OUTPUT_PATH}")
    print(f"Target: {N_SPRITES:,} sprites")
    print(f"Threshold: {DENSITY_PERCENTILE:g}th percentile")

    # Keep the FITS file memory-mapped while the map is processed.
    with fits.open(
        INPUT_PATH,
        mode="readonly",
        memmap=True,
    ) as hdul:
        (
            mean_data,
            nside,
            nested,
            radial_centers,
            radial_boundaries,
        ) = read_healpix_data(hdul)

        sprites = build_sprites(
            mean_data,
            nside,
            nested,
            radial_centers,
            radial_boundaries,
        )

        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

        temporary_path = OUTPUT_PATH.with_suffix(
            OUTPUT_PATH.suffix + ".tmp"
        )

        with temporary_path.open("wb") as handle:
            handle.write(
                HEADER.pack(
                    FILE_MAGIC,
                    FILE_VERSION,
                    len(sprites),
                )
            )
            sprites.tofile(handle)

        temporary_path.replace(OUTPUT_PATH)

    expected_size = HEADER.size + N_SPRITES * RECORD.itemsize
    actual_size = OUTPUT_PATH.stat().st_size

    if actual_size != expected_size:
        raise RuntimeError(
            f"Output size check failed: got {actual_size} bytes, "
            f"expected {expected_size}."
        )

    print()
    print("======================================")
    print("Finished")
    print(f"Sprites: {N_SPRITES:,}")
    print(f"Record stride: {RECORD.itemsize} bytes")
    print(f"Output size: {actual_size / (1024**2):.2f} MiB")
    print(f"Output: {OUTPUT_PATH}")
    print("======================================")


if __name__ == "__main__":
    main()
