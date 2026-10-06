import argparse
from pathlib import Path

import numpy as np
import pandas as pd


# These match dust2unity3.py.
UNITY_SCALE = 20.0
X_CENTER = 8.5
Y_CENTER = 0.0
Z_CENTER = 0.0
DENSITY_PERCENTILE = 95.0
THRESHOLD_SAMPLE_SIZE = 2_000_000
RANDOM_SEED = 123

# A 0.1 kpc search radius corresponds to 100 pc or 2 Unity units.
SEARCH_RADIUS_KPC = 0.1
MIN_VISIBLE_CELLS = 20

DUST_KEYWORDS = (
    "cloud",
    "molecular",
    "nebula",
    "h ii",
    "dark nebula",
    "reflection nebula",
    "emission nebula",
    "molecular ring",
)


def parse_args():
    parser = argparse.ArgumentParser(
        description=(
            "Move dust-associated landmark markers to local "
            "dust-density-weighted centers."
        )
    )
    parser.add_argument("--dust", default="n_obs.npy")
    parser.add_argument("--x", default="X.npy")
    parser.add_argument("--y", default="Y.npy")
    parser.add_argument("--z", default="Z.npy")
    parser.add_argument(
        "--landmarks",
        default="2local_neighborhood_landmarks_semicolons.csv",
    )
    parser.add_argument(
        "--output",
        default="landmarks_dust_weighted.csv",
    )
    parser.add_argument(
        "--report",
        default="landmarks_dust_weighted_report.csv",
    )
    parser.add_argument(
        "--radius-pc",
        type=float,
        default=SEARCH_RADIUS_KPC * 1000.0,
    )
    parser.add_argument(
        "--minimum-cells",
        type=int,
        default=MIN_VISIBLE_CELLS,
    )
    return parser.parse_args()


def is_dust_landmark(type_description):
    description = str(type_description).strip().lower()
    return any(keyword in description for keyword in DUST_KEYWORDS)


def unity_to_physical(unity_position):
    """Return physical (X, Y, Z) in kpc from corrected Unity coordinates."""
    ux, uy, uz = unity_position
    return np.array(
        [
            ux / UNITY_SCALE + X_CENTER,
            uz / UNITY_SCALE + Y_CENTER,
            -uy / UNITY_SCALE + Z_CENTER,
        ],
        dtype=float,
    )


def physical_to_unity(physical_position):
    """Return corrected Unity (x, y, z) from physical (X, Y, Z) in kpc."""
    x, y, z = physical_position
    return np.array(
        [
            (x - X_CENTER) * UNITY_SCALE,
            -(z - Z_CENTER) * UNITY_SCALE,
            (y - Y_CENTER) * UNITY_SCALE,
        ],
        dtype=float,
    )


def estimate_visibility_threshold(dust):
    rng = np.random.default_rng(RANDOM_SEED)
    nx, ny, nz = dust.shape

    ii = rng.integers(0, nx, THRESHOLD_SAMPLE_SIZE)
    jj = rng.integers(0, ny, THRESHOLD_SAMPLE_SIZE)
    kk = rng.integers(0, nz, THRESHOLD_SAMPLE_SIZE)

    values = np.asarray(dust[ii, jj, kk])
    values = values[np.isfinite(values)]

    if values.size == 0:
        raise RuntimeError("Could not find any finite dust-density values.")

    return float(np.percentile(values, DENSITY_PERCENTILE))


def local_weighted_center(
    dust,
    x_axis,
    y_axis,
    z_axis,
    seed,
    radius_kpc,
    threshold,
    minimum_cells,
):
    x_indices = np.flatnonzero(np.abs(x_axis - seed[0]) <= radius_kpc)
    y_indices = np.flatnonzero(np.abs(y_axis - seed[1]) <= radius_kpc)
    z_indices = np.flatnonzero(np.abs(z_axis - seed[2]) <= radius_kpc)

    if not (x_indices.size and y_indices.size and z_indices.size):
        return None, 0, "outside_grid"

    local_density = np.asarray(
        dust[np.ix_(x_indices, y_indices, z_indices)],
        dtype=float,
    )

    local_x = x_axis[x_indices][:, None, None]
    local_y = y_axis[y_indices][None, :, None]
    local_z = z_axis[z_indices][None, None, :]

    distance_squared = (
        (local_x - seed[0]) ** 2
        + (local_y - seed[1]) ** 2
        + (local_z - seed[2]) ** 2
    )

    visible = (
        np.isfinite(local_density)
        & (local_density >= threshold)
        & (distance_squared <= radius_kpc**2)
    )

    cell_count = int(np.count_nonzero(visible))

    if cell_count < minimum_cells:
        return None, cell_count, "too_few_visible_cells"

    weights = np.where(visible, local_density, 0.0)
    total_weight = float(weights.sum())

    if not np.isfinite(total_weight) or total_weight <= 0.0:
        return None, cell_count, "invalid_weight"

    center = np.array(
        [
            np.sum(weights * local_x) / total_weight,
            np.sum(weights * local_y) / total_weight,
            np.sum(weights * local_z) / total_weight,
        ]
    )

    return center, cell_count, "moved"


def main():
    args = parse_args()
    radius_kpc = args.radius_pc / 1000.0

    if radius_kpc <= 0.0:
        raise ValueError("--radius-pc must be positive.")

    dust = np.load(args.dust, mmap_mode="r")
    x_axis = np.asarray(np.load(args.x), dtype=float)
    y_axis = np.asarray(np.load(args.y), dtype=float)
    z_axis = np.asarray(np.load(args.z), dtype=float)

    expected_shape = (len(x_axis), len(y_axis), len(z_axis))
    if dust.shape != expected_shape:
        raise ValueError(
            f"Dust shape {dust.shape} does not match axis lengths "
            f"{expected_shape}."
        )

    landmarks = pd.read_csv(args.landmarks)
    required_columns = {
        "name",
        "type",
        "x_pc",
        "y_pc",
        "z_pc",
    }
    missing = required_columns.difference(landmarks.columns)
    if missing:
        raise ValueError(
            "Landmark CSV is missing columns: " + ", ".join(sorted(missing))
        )

    threshold = estimate_visibility_threshold(dust)
    print(f"Estimated 95th-percentile visibility threshold: {threshold:.6g}")
    print(f"Search radius: {args.radius_pc:.1f} pc")

    report_rows = []

    for row_index, row in landmarks.iterrows():
        old_unity = row[["x_pc", "y_pc", "z_pc"]].to_numpy(dtype=float)
        new_unity = old_unity.copy()
        cell_count = 0
        status = "not_a_dust_landmark"

        if is_dust_landmark(row["type"]):
            seed = unity_to_physical(old_unity)
            center, cell_count, status = local_weighted_center(
                dust,
                x_axis,
                y_axis,
                z_axis,
                seed,
                radius_kpc,
                threshold,
                args.minimum_cells,
            )

            if center is not None:
                new_unity = physical_to_unity(center)
                landmarks.loc[
                    row_index,
                    ["x_pc", "y_pc", "z_pc"],
                ] = new_unity

        displacement_unity = float(np.linalg.norm(new_unity - old_unity))
        displacement_pc = displacement_unity / UNITY_SCALE * 1000.0

        report_rows.append(
            {
                "name": row["name"],
                "type": row["type"],
                "status": status,
                "visible_cell_count": cell_count,
                "old_x": old_unity[0],
                "old_y": old_unity[1],
                "old_z": old_unity[2],
                "new_x": new_unity[0],
                "new_y": new_unity[1],
                "new_z": new_unity[2],
                "displacement_pc": displacement_pc,
            }
        )

        print(
            f"{row['name']}: {status}; cells={cell_count}; "
            f"shift={displacement_pc:.1f} pc"
        )

    output_path = Path(args.output)
    report_path = Path(args.report)
    landmarks.to_csv(output_path, index=False)
    pd.DataFrame(report_rows).to_csv(report_path, index=False)

    print(f"Saved revised landmarks: {output_path}")
    print(f"Saved displacement report: {report_path}")


if __name__ == "__main__":
    main()
