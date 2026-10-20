# Cluster Data — Hunt & Reffert (2024)

The simulation uses the **Hunt & Reffert (2024) open-cluster catalogue** to associate Gaia DR3 stars with clusters and let users filter stars by their associated clusters' properties.

Source paper: E. L. Hunt & S. Reffert, *Improving the open cluster census. III. Using cluster masses, radii, and dynamics to create a cleaned open cluster catalogue*, A&A 686, A42 (2024).

- [VizieR catalogue](https://vizier.cds.unistra.fr/viz-bin/VizieR?-source=J/A+A/686/A42)
- [Catalogue documentation](https://cdsarc.cds.unistra.fr/viz-bin/ReadMe/J/A%2BA/686/A42?format=html&tex=true)

## Data pipeline

The download script requests explicit columns from VizieR table `J/A+A/686/A42` and saves the raw tables to:

```text
DataRaw/Clusters/HuntReffert2024/
├── clusters.tsv
└── members.tsv
```

It checks the number of data records before publishing each download:

- `clusters.tsv`: **7,167 clusters**
- `members.tsv`: **1,291,929 membership records**

The processing script converts these tables to compact binary files and writes them to:

```text
Data/Clusters/HuntReffert2024/
├── clusters.bin
├── members.bin
├── cluster_names.json
└── summary.json
```

## Processed files

- **`clusters.bin`** — one record per cluster, sorted by cluster ID. Stores the cluster type and flags, member counts, Galactic coordinates, radii, proper motion, parallax, distance estimates, Cartesian position (`X`, `Y`, `Z`), radial velocity, age and extinction estimates, and mass-related properties. Cluster names are stored separately in `cluster_names.json`, keyed by cluster ID.
- **`members.bin`** — maps Gaia DR3 `source_id` values to cluster IDs. Each record stores the Gaia source ID, cluster ID, membership flags, and membership probability. Records are sorted by `source_id`, making records for the same star contiguous. A star may have more than one membership record.
- **`cluster_names.json`** — maps each cluster ID to its name.
- **`summary.json`** — records catalogue counts, record sizes, unique member-star count, and cluster-type counts.

Both binary files start with a 16-byte header containing the format magic, version, record size, and record count. Current format version: **1**. Cluster records are **136 bytes**; membership records are **16 bytes**.

### Encoded type and flag values

Cluster type codes:

| Code | Catalogue type |
|---:|---|
| 1 | `o` |
| 2 | `m` |
| 3 | `g` |
| 4 | `d` |
| 5 | `r` |
| 0 | Unknown / unrecognized |

Cluster flags: bit 0 = merged cluster; bit 1 = GMM membership flag.

Membership flags: bit 0 = within the Jacobi radius (`inrj`); bit 1 = within the estimated King tidal radius (`inrt`). Membership probability is stored separately as a float.

## Use in the simulation

The membership records connect Gaia DR3 stars to clusters. The simulation can use these links to filter stars according to cluster properties—for example, cluster age, mass, type, or radius—and can also use membership probability and radius flags where appropriate. Cluster-property filtering therefore operates through the cluster catalogue and the star-to-cluster membership table, rather than requiring the full cluster properties to be duplicated in every star record.

## Rebuilding the data

1. Run the download script to create and validate `clusters.tsv` and `members.tsv` under `DataRaw/Clusters/HuntReffert2024/`.
2. Run the processing script to generate the binary files and JSON metadata under `Data/Clusters/HuntReffert2024/`.

The expected record counts in the scripts are validation checks. If the upstream catalogue changes, review those counts and the binary format before regenerating data.
