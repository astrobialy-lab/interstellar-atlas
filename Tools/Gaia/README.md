# Gaia Scripts

This folder contains the scripts used to download, process, and prepare Gaia DR3 star-catalog data for the Space Simulation project.

## Creation

To download Gaia data, first create:

`gaia_credentials.txt`

near the project folder, containing the Gaia Archive credentials.

Then run:

1. `Tools/Gaia/download_gaia.py`
2. `Tools/Gaia/process_gaia.py`
3. `Tools/Gaia/4_build_gaia_ms_lod.py`

The first two steps download and process the Gaia catalogue. The final step builds the multi-scale LOD dataset used for efficient large-scale star rendering.

## Data Structure

* `DataRaw/Gaia/` — Original Gaia Archive downloads.
* `Data/Gaia/chunks/` — Processed Gaia star data divided into HEALPix and distance-shell chunks.
* `Data/Gaia/index.bin` — Index describing the processed chunks.
* `Data/Gaia/lod/` — Multi-scale LOD hierarchy used by Unity.
* `config.json` — Dataset and processing configuration.

The raw Gaia data is not stored in Git because of its very large size.

## Data Source

Source: ESA Gaia Archive

Mission: Gaia

Data release: Gaia DR3

## Gaia Star Data

The processing pipeline uses Gaia fields including:

* `source_id`
* `ra`
* `dec`
* `parallax`
* `phot_g_mean_mag`
* `bp_rp`

The processed catalogue additionally contains Cartesian coordinates and other stellar parameters used by the simulation.

## Runtime Format

Unity loads the processed binary data rather than the original downloaded tables.

The main processing pipeline is:

Gaia Archive

↓

raw Gaia data

↓

Python preprocessing

↓

processed binary chunks

↓

MS-LOD generation

↓

Unity

↓

GPU buffers

↓

star renderer

## MS-LOD

The MS-LOD dataset provides the same Gaia stars at multiple spatial levels of detail.

LOD particle records contain:

* Cartesian position
* G magnitude
* BP-RP colour

The corresponding Gaia `source_id` values are stored separately in aligned sidecar files, allowing additional catalogue information to be associated with rendered stars without increasing the GPU particle record size.

## Far-Star Cache

The runtime also uses a far-star cache for stars beyond the live rendering distance.

The cache stores a world-space equirectangular panorama of distant stars. It is regenerated around the player's position when the cache becomes sufficiently outdated.

The old cache remains visible while a new cache is generated.
