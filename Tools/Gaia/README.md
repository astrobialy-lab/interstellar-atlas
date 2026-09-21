# Creation

To get Gaia data first create file named gaia_credentials.txt near the project folder.
Then run Tools/Gaia/download_gaia.py, and then Tools/Gaia/process_gaia.py

# Gaia Star Data

This folder contains Gaia star-catalog data used by the Space Simulation project.

## Structure

- `raw/` — Original Gaia Archive downloads.
- `processed/` — Processed binary files used by Unity.
- `config.json` — Configuration describing the dataset and processing settings.

## Data source

Source: ESA Gaia Archive
Mission: Gaia
Data release: Gaia DR3

The raw Gaia data is not stored in Git because the datasets can become very large.

To reproduce the dataset:

1. Run `Tools/Gaia/download_gaia.py`
2. Run `Tools/Gaia/process_gaia.py`
3. The processed files will be placed in `processed/`

## Initial star fields

The processing pipeline uses:

- `source_id`
- `ra`
- `dec`
- `parallax`
- `phot_g_mean_mag`
- `bp_rp`

The processed catalog additionally contains Cartesian coordinates for use by Unity.

## Runtime format

Unity should load the processed binary files rather than the original CSV files.

The final rendering pipeline is:

Gaia Archive
    ↓
raw Gaia data
    ↓
Python preprocessing
    ↓
processed binary catalog
    ↓
Unity
    ↓
GPU buffer
    ↓
star renderer