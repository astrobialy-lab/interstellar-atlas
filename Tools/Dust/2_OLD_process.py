import numpy as np
import pandas as pd
import matplotlib.cm as cm

# --- Input files ---
dust_path = "n_obs.npy"
x_path = "X.npy"
y_path = "Y.npy"
z_path = "Z.npy"

# --- Output ---
out_csv = "real_dust_sample_20000_magma_gaussian.csv"

# --- Settings ---
N_SAMPLE = 20000
VALUE_SAMPLE_SIZE = 2_000_000
DENSITY_PERCENTILE = 95

UNITY_SCALE = 20.0

X_CENTER = 8.5
Y_CENTER = 0.0
Z_CENTER = 0.0
UNITY_FORWARD_OFFSET = 0.0

ALPHA_MIN = 0.03
ALPHA_MAX = 0.65

GAUSS_CLIP = 2.5

# Approximate inverse normal CDF
def inverse_normal_cdf(p):
    # Peter John Acklam approximation
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

    p = np.asarray(p)
    q = np.zeros_like(p, dtype=float)

    plow = 0.02425
    phigh = 1 - plow

    low = p < plow
    mid = (p >= plow) & (p <= phigh)
    high = p > phigh

    q_low = np.sqrt(-2 * np.log(p[low]))
    q[low] = (((((c[0]*q_low + c[1])*q_low + c[2])*q_low + c[3])*q_low + c[4])*q_low + c[5]) / \
              ((((d[0]*q_low + d[1])*q_low + d[2])*q_low + d[3])*q_low + 1)

    q_mid = p[mid] - 0.5
    r = q_mid * q_mid
    q[mid] = (((((a[0]*r + a[1])*r + a[2])*r + a[3])*r + a[4])*r + a[5]) * q_mid / \
             (((((b[0]*r + b[1])*r + b[2])*r + b[3])*r + b[4])*r + 1)

    q_high = np.sqrt(-2 * np.log(1 - p[high]))
    q[high] = -(((((c[0]*q_high + c[1])*q_high + c[2])*q_high + c[3])*q_high + c[4])*q_high + c[5]) / \
                ((((d[0]*q_high + d[1])*q_high + d[2])*q_high + d[3])*q_high + 1)

    return q

# Load data
dust = np.load(dust_path, mmap_mode="r")
X = np.load(x_path)
Y = np.load(y_path)
Z = np.load(z_path)

print("Dust shape:", dust.shape)
print("X shape:", X.shape)
print("Y shape:", Y.shape)
print("Z shape:", Z.shape)

print("Dust min:", np.nanmin(dust))
print("Dust max:", np.nanmax(dust))

print("X range:", X.min(), X.max())
print("Y range:", Y.min(), Y.max())
print("Z range:", Z.min(), Z.max())

rng = np.random.default_rng(123)
nx, ny, nz = dust.shape

ii = rng.integers(0, nx, VALUE_SAMPLE_SIZE)
jj = rng.integers(0, ny, VALUE_SAMPLE_SIZE)
kk = rng.integers(0, nz, VALUE_SAMPLE_SIZE)

sample_values = dust[ii, jj, kk]
sample_values = sample_values[np.isfinite(sample_values)]

threshold = np.percentile(sample_values, DENSITY_PERCENTILE)
print("Estimated threshold:", threshold)

rows = []
attempts = 0
max_attempts = 200

while len(rows) < N_SAMPLE and attempts < max_attempts:
    attempts += 1
    batch_size = 200_000

    ii = rng.integers(0, nx, batch_size)
    jj = rng.integers(0, ny, batch_size)
    kk = rng.integers(0, nz, batch_size)

    vals = dust[ii, jj, kk]
    keep = np.isfinite(vals) & (vals >= threshold)

    for i, j, k, density in zip(ii[keep], jj[keep], kk[keep], vals[keep]):
        x = (X[i] - X_CENTER) * UNITY_SCALE
        y = -(Z[k] - Z_CENTER) * UNITY_SCALE
        z = (Y[j] - Y_CENTER) * UNITY_SCALE + UNITY_FORWARD_OFFSET

        rows.append([x, y, z, float(density)])

        if len(rows) >= N_SAMPLE:
            break

    print(f"Collected {len(rows)} / {N_SAMPLE} points")

if len(rows) == 0:
    raise RuntimeError("No points collected. Try lowering DENSITY_PERCENTILE.")

df = pd.DataFrame(rows, columns=["x", "y", "z", "density"])

# Original logarithmic density normalization
logd = np.log10(df["density"] + 1e-12)

logmin = logd.min()
logmax = logd.max()

df["density_norm"] = (
    logd - logmin
) / (logmax - logmin + 1e-12)

# --- Gaussian remap of the sampled points ---
# Rank the particles by density.
rank = df["density_norm"].rank(method="average").to_numpy()

# Convert ranks into percentile positions.
p = (rank - 0.5) / len(rank)

# Map percentile positions into Gaussian z-values.
z_gauss = inverse_normal_cdf(p)

# Clip extreme tails, then remap to 0..1.
z_gauss = np.clip(z_gauss, -GAUSS_CLIP, GAUSS_CLIP)
density_visual = (z_gauss + GAUSS_CLIP) / (2 * GAUSS_CLIP)

df["density_visual"] = density_visual

# K3D used magma_r, so use reversed magma here too.
cmap = cm.get_cmap("magma_r")

rgba = cmap(df["density_visual"].to_numpy())

df["r"] = rgba[:, 0]
df["g"] = rgba[:, 1]
df["b"] = rgba[:, 2]

df["alpha"] = ALPHA_MIN + (ALPHA_MAX - ALPHA_MIN) * df["density_visual"]

# Keep raw, normalized, and visual density for debugging.
df = df[[
    "x", "y", "z",
    "density",
    "density_norm",
    "density_visual",
    "r", "g", "b", "alpha"
]]

df.to_csv(out_csv, index=False)

print("Saved:", out_csv)
print(df.head())
print("Exported coordinate ranges:")
print("x:", df["x"].min(), df["x"].max())
print("y:", df["y"].min(), df["y"].max())
print("z:", df["z"].min(), df["z"].max())
print("density_visual:", df["density_visual"].min(), df["density_visual"].max())
print("alpha:", df["alpha"].min(), df["alpha"].max())