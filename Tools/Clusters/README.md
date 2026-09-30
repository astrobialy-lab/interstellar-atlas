# Hunt & Reffert 2024 Cluster Catalogue

This folder contains the open-cluster catalogue and Gaia DR3 cluster-membership data used by the Space Simulation project.

## Data Source

Source: Hunt & Reffert (2024)

Catalogue: `J/A+A/686/A42`

Paper: *Improving the open cluster census. III.*

The catalogue contains cluster properties and Gaia DR3 membership information. The VizieR catalogue provides 7,167 clusters and 1,291,929 cluster-membership records.

## Raw Data

The original downloaded tables are stored in:

`DataRaw/Clusters/HuntReffert2024/`

* `clusters.tsv` — Cluster catalogue.
* `members.tsv` — Gaia DR3 cluster memberships.

## Processed Data

The processed binary data is stored in:

`Data/Clusters/HuntReffert2024/`

* `clusters.bin` — Fixed-size cluster records.
* `members.bin` — Gaia source IDs and their cluster memberships.
* `cluster_names.json` — Cluster names indexed by cluster ID.
* `summary.json` — Dataset and processing information.

The membership records are sorted by Gaia DR3 `source_id`, allowing efficient lookup of all known cluster memberships for a star.

## Cluster Properties

The processed cluster catalogue contains properties including:

* Cluster type
* Position and distance
* Proper motion and parallax
* Cluster radii
* Radial velocity
* Age estimates
* Extinction
* Cluster mass
* Membership-related flags

## Runtime Use

The cluster data is an auxiliary catalogue for the Gaia star dataset.

Gaia DR3 star

↓

Gaia `source_id`

↓

cluster membership lookup

↓

cluster properties

↓

filter / analysis

↓

existing Gaia star renderer

The cluster catalogue does not have a separate rendering system. It is used to identify and filter the existing Gaia stars according to cluster membership and cluster properties.
