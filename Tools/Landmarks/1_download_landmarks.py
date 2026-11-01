#!/usr/bin/env python3
"""Build a Sun-centred astronomical landmark CSV from online catalogues.

Output columns:
    name,type,x_pc,y_pc,z_pc,short_description,paragraph_description

Data sources used:
  * SIMBAD through astroquery with Harvard/Strasbourg mirror failover:
    object identity, coordinates, object type, and distance/parallax measurements.
  * VizieR J/A+A/686/A42/clusters (Hunt & Reffert 2024), with multiple
    official VizieR mirror failover: open-cluster coordinates and median distances.
  * Schlafly et al. PS1 cloud-distance FITS catalogues: selected molecular
    cloud sightlines, Galactic l/b, and distance-modulus percentiles.
  * Wikipedia Action API: short lead excerpts for descriptive context.

Important:
  * The script starts from the shortlist in this file. It does not
    discover additional objects beyond that shortlist.
  * The 1,300 pc limit is applied to the best available distance estimate.
  * Objects without a usable distance or position are skipped and reported.
  * The Local Bubble is an extended structure containing the Sun, not a point
    source. It is deliberately represented at (0, 0, 0) pc as a visualization
    anchor, matching the previous landmark convention.
  * The XYZ convention is heliocentric Galactic Cartesian:
        X = d cos(b) cos(l)
        Y = d cos(b) sin(l)
        Z = d sin(b)
    where l and b are Galactic longitude and latitude in degrees.

Install dependencies:
    python -m pip install requests numpy astropy astroquery

Run:
    python 1_download_landmarks_v2.py

You can change OUTPUT_CSV and MAX_DISTANCE_PC below.
"""

from __future__ import annotations

import csv
import io
import math
import re
import sys
import time
import unicodedata
from dataclasses import dataclass
from typing import Any, Iterable

import numpy as np
import requests
from astropy import units as u
from astropy.coordinates import SkyCoord
from astropy.table import Table
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
from pathlib import Path


# ============================================================================
# SETTINGS
# ============================================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]
OUTPUT_CSV = PROJECT_ROOT / "DataRaw" / "Landmarks" / "landmarks.csv"
MAX_DISTANCE_PC = 1300.0
REQUEST_DELAY_SECONDS = 0.20
HTTP_TIMEOUT_SECONDS = 25
SERVICE_CONNECT_TIMEOUT_SECONDS = 15
MINIMUM_CORE_SERVICE_REQUIREMENT = True
WIKIPEDIA_EXCERPT_MAX_WORDS = 24  # Keep only a short attributed excerpt.

SIMBAD_SERVERS = [
    # SIMBAD supports both its Strasbourg primary service and the Harvard mirror.
    "simbad.harvard.edu",
    "simbad.cds.unistra.fr",
]
VIZIER_SERVERS = [
    # Astroquery's documented VizieR mirrors. Start with Harvard because the
    # Strasbourg endpoint timed out for the previous run.
    "vizier.cfa.harvard.edu",
    "vizier.ast.cam.ac.uk",
    "vizier.nao.ac.jp",
    "vizier.hia.nrc.ca",
    "vizier.iucaa.ernet.in",
    "vizier.china-vo.org",
    "vizier.cds.unistra.fr",
]
VIZIER_CLUSTER_CATALOG = "J/A+A/686/A42/clusters"
SCHLAFLY_MAJOR_CLOUDS_URL = "https://e.schlaf.ly/distances/bigcloud_cat_summary.fits"
SCHLAFLY_MBM_CLOUDS_URL = "https://e.schlaf.ly/distances/mbmcloud_cat_summary.fits"
WIKIPEDIA_API_URL = "https://en.wikipedia.org/w/api.php"

USER_AGENT = (
    "InterstellarAtlasLandmarkBuilder/1.1 "
    "(astronomical landmark research; astronomical visualization project)"
)


# ============================================================================
# 1. CANDIDATE LANDMARKS
# Groups are semantic categories. Each group is sorted alphabetically below.
# Keep the display names stable; aliases for catalogue lookups are separate.
# ============================================================================

LANDMARK_GROUPS: dict[str, list[str]] = {
    "Open and embedded star clusters": [
        "Alpha Persei Cluster (Melotte 20)",
        "Butterfly Cluster (M6)",
        "Coma Berenices Cluster (Melotte 111)",
        "Hyades Cluster",
        "IC 2391",
        "IC 2602 (Southern Pleiades)",
        "IC 348",
        "IC 4665",
        "M34 (NGC 1039)",
        "M35 (NGC 2168)",
        "M38 (NGC 1912)",
        "M39 (NGC 7092)",
        "M44 (Praesepe / Beehive Cluster)",
        "M67 (NGC 2682)",
        "NGC 1333",
        "NGC 1502",
        "NGC 2232",
        "NGC 2264 (Christmas Tree Cluster)",
        "NGC 2451",
        "NGC 2516",
        "NGC 2547",
        "NGC 3532 (Wishing Well Cluster)",
        "NGC 6633",
        "NGC 752",
        "Pleiades (M45)",
        "Ptolemy's Cluster (M7)",
    ],
    "Nebulae and supernova remnants": [
        "Barnard's Loop",
        "California Nebula (NGC 1499)",
        "Cat's Eye Nebula (NGC 6543)",
        "Cave Nebula (Sh2-155)",
        "Coalsack Nebula",
        "Cocoon Nebula (IC 5146)",
        "Dumbbell Nebula (M27)",
        "Elephant Trunk Nebula (IC 1396)",
        "Flaming Star Nebula (IC 405)",
        "Gum Nebula",
        "Helix Nebula (NGC 7293)",
        "Horsehead Nebula (Barnard 33) & Flame Nebula",
        "Iris Nebula (NGC 7023)",
        "M78 Reflection Nebula",
        "North America Nebula (NGC 7000)",
        "NGC 2023 Reflection Nebula",
        "Orion Nebula (M42) / Trapezium Cluster",
        "Owl Nebula (M97)",
        "Pelican Nebula (IC 5070)",
        "Pipe Nebula",
        "Ring Nebula (M57)",
        "Running Man Nebula (NGC 1977)",
        "Sh2-216 Planetary Nebula",
        "Skull Nebula (NGC 246)",
        "Spaghetti Nebula (Simeis 147)",
        "Veil Nebula (Cygnus Loop)",
        "Vela Supernova Remnant",
        "Witch Head Nebula (IC 2118)",
    ],
    "Molecular clouds, associations and ISM structures": [
        "Aquila Rift",
        "Barnard 68 Dark Cloud",
        "Cepheus Flare",
        "Cepheus OB2 Association",
        "Chamaeleon Clouds",
        "Corona Australis Cloud",
        "Lambda Orionis Ring",
        "Local Bubble / Local Chimney",
        "Lupus Molecular Clouds",
        "Lynds 1622 (Boogeyman Nebula)",
        "MBM 12 Molecular Cloud",
        "Monoceros R2 Molecular Cloud",
        "Musca Molecular Cloud",
        "Orion A Molecular Cloud",
        "Orion B Molecular Cloud",
        "Orion OB1 Association",
        "Perseus Molecular Cloud",
        "Perseus OB2 Association",
        "Rho Ophiuchi Cloud Complex",
        "Scorpius-Centaurus OB Association",
        "Serpens Molecular Cloud",
        "Taurus Molecular Cloud",
        "Taurus-Auriga Association",
        "Vela Molecular Ridge",
        "Vela OB2 Association",
    ],
}


# Query alternatives for compound/display names that SIMBAD may not resolve
# verbatim. The first successful SIMBAD result is used.
SIMBAD_ALIASES: dict[str, list[str]] = {
    "Alpha Persei Cluster (Melotte 20)": ["Melotte 20", "Alpha Persei Cluster"],
    "Butterfly Cluster (M6)": ["M 6", "NGC 6405"],
    "Coma Berenices Cluster (Melotte 111)": ["Melotte 111", "Coma Star Cluster"],
    "Hyades Cluster": ["Hyades"],
    "IC 2602 (Southern Pleiades)": ["IC 2602"],
    "M34 (NGC 1039)": ["NGC 1039", "M 34"],
    "M35 (NGC 2168)": ["NGC 2168", "M 35"],
    "M38 (NGC 1912)": ["NGC 1912", "M 38"],
    "M39 (NGC 7092)": ["NGC 7092", "M 39"],
    "M44 (Praesepe / Beehive Cluster)": ["Praesepe", "M 44", "NGC 2632"],
    "M67 (NGC 2682)": ["NGC 2682", "M 67"],
    "NGC 2264 (Christmas Tree Cluster)": ["NGC 2264"],
    "NGC 3532 (Wishing Well Cluster)": ["NGC 3532"],
    "Pleiades (M45)": ["M 45", "Pleiades"],
    "Ptolemy's Cluster (M7)": ["M 7", "NGC 6475"],
    "California Nebula (NGC 1499)": ["NGC 1499"],
    "Cat's Eye Nebula (NGC 6543)": ["NGC 6543"],
    "Cave Nebula (Sh2-155)": ["Sh 2-155", "Sh2-155"],
    "Cocoon Nebula (IC 5146)": ["IC 5146"],
    "Dumbbell Nebula (M27)": ["M 27", "NGC 6853"],
    "Elephant Trunk Nebula (IC 1396)": ["IC 1396"],
    "Flaming Star Nebula (IC 405)": ["IC 405"],
    "Helix Nebula (NGC 7293)": ["NGC 7293"],
    "Horsehead Nebula (Barnard 33) & Flame Nebula": ["Barnard 33", "B 33", "Horsehead Nebula"],
    "North America Nebula (NGC 7000)": ["NGC 7000"],
    "Orion Nebula (M42) / Trapezium Cluster": ["M 42", "NGC 1976", "Orion Nebula"],
    "Owl Nebula (M97)": ["M 97", "NGC 3587"],
    "Pelican Nebula (IC 5070)": ["IC 5070"],
    "Ring Nebula (M57)": ["M 57", "NGC 6720"],
    "Running Man Nebula (NGC 1977)": ["NGC 1977"],
    "Sh2-216 Planetary Nebula": ["Sh 2-216", "Sh2-216"],
    "Skull Nebula (NGC 246)": ["NGC 246"],
    "Spaghetti Nebula (Simeis 147)": ["Simeis 147", "Sh 2-240"],
    "Veil Nebula (Cygnus Loop)": ["Cygnus Loop", "NGC 6960"],
    "Witch Head Nebula (IC 2118)": ["IC 2118"],
    "Barnard's Loop": ["Sh 2-276", "Barnard Loop"],
    "Gum Nebula": ["Gum 12"],
    "Aquila Rift": ["Aquila Rift"],
    "Barnard 68 Dark Cloud": ["Barnard 68", "B 68"],
    "Lynds 1622 (Boogeyman Nebula)": ["L 1622", "Lynds 1622"],
    "MBM 12 Molecular Cloud": ["MBM 12", "L 1457"],
    "Monoceros R2 Molecular Cloud": ["Mon R2", "Monoceros R2"],
    "Rho Ophiuchi Cloud Complex": ["Rho Oph", "Ophiuchus Cloud"],
    "Scorpius-Centaurus OB Association": ["Sco-Cen", "Scorpius-Centaurus"],
}


# Additional resolver aliases. Display labels are for people; catalogue aliases
# are the formal identifiers most likely to be understood by SIMBAD.
SIMBAD_ALIASES.update({
    "M78 Reflection Nebula": ["M 78", "NGC 2068"],
    "NGC 2023 Reflection Nebula": ["NGC 2023"],
    "Pipe Nebula": ["Pipe Nebula", "Barnard 59", "B 59"],
    "Coalsack Nebula": ["Coalsack", "Coalsack Nebula", "Caldwell 99"],
    "Pelican Nebula (IC 5070)": ["IC 5070", "Pelican Nebula"],
    "North America Nebula (NGC 7000)": ["NGC 7000", "North America Nebula"],
    "Iris Nebula (NGC 7023)": ["NGC 7023"],
    "Elephant Trunk Nebula (IC 1396)": ["IC 1396", "vdB 142"],
    "Cave Nebula (Sh2-155)": ["Sh 2-155", "LBN 529"],
    "Spaghetti Nebula (Simeis 147)": ["Simeis 147", "Sh 2-240"],
    "Veil Nebula (Cygnus Loop)": ["Cygnus Loop", "NGC 6960", "NGC 6992"],
    "Vela Supernova Remnant": ["Vela SNR", "G263.9-3.3"],
    "Gum Nebula": ["Gum 12", "Gum Nebula"],
    "Aldebaran (Alpha Tauri)": ["alf Tau", "Alpha Tauri", "HD 29139"],
    "Alpha Centauri": ["alf Cen", "Alpha Cen", "HD 128620"],
    "Altair": ["alf Aql", "Alpha Aquilae", "HD 187642"],
    "Antares (Alpha Scorpii)": ["alf Sco", "Alpha Scorpii", "HD 148478"],
    "Arcturus": ["alf Boo", "Alpha Bootis", "HD 124897"],
    "Barnard's Star": ["V2500 Oph", "BD+04 3561a", "Barnard Star"],
    "Bellatrix": ["gam Ori", "Gamma Orionis", "HD 35468"],
    "Betelgeuse (Alpha Orionis)": ["alf Ori", "Alpha Orionis", "HD 39801"],
    "Canopus": ["alf Car", "Alpha Carinae", "HD 45348"],
    "Capella": ["alf Aur", "Alpha Aurigae", "HD 34029"],
    "Castor": ["alf Gem", "Alpha Geminorum", "HD 60179"],
    "Deneb": ["alf Cyg", "Alpha Cygni", "HD 197345"],
    "Fomalhaut": ["alf PsA", "Alpha Piscis Austrini", "HD 216956"],
    "Polaris (Alpha Ursae Minoris)": ["alf UMi", "Alpha Ursae Minoris", "HD 8890"],
    "Pollux": ["bet Gem", "Beta Geminorum", "HD 62509"],
    "Procyon": ["alf CMi", "Alpha Canis Minoris", "HD 61421"],
    "Regulus": ["alf Leo", "Alpha Leonis", "HD 87901"],
    "Rigel (Beta Orionis)": ["bet Ori", "Beta Orionis", "HD 34085"],
    "Sirius (Alpha Canis Majoris)": ["alf CMa", "Alpha Canis Majoris", "HD 48915"],
    "Spica (Alpha Virginis)": ["alf Vir", "Alpha Virginis", "HD 116658"],
    "Vega (Alpha Lyrae)": ["alf Lyr", "Alpha Lyrae", "HD 172167"],
    "Orion A Molecular Cloud": ["Orion A", "L1641", "L 1641"],
    "Orion B Molecular Cloud": ["Orion B", "L 1630", "L1630"],
    "Chamaeleon Clouds": ["Chamaeleon I", "Chamaeleon II", "Chamaeleon III"],
    "Lupus Molecular Clouds": ["Lupus 1", "Lupus 2", "Lupus 3", "Lupus 4"],
    "Corona Australis Cloud": ["Corona Australis", "R CrA Cloud"],
    "Musca Molecular Cloud": ["Musca", "Musca Cloud"],
    "Scorpius-Centaurus OB Association": ["Sco-Cen", "Scorpius-Centaurus", "Lower Centaurus Crux"],
    "Cepheus OB2 Association": ["Cep OB2", "Cepheus OB2"],
    "Orion OB1 Association": ["Orion OB1", "Ori OB1"],
    "Perseus OB2 Association": ["Per OB2", "Perseus OB2"],
    "Vela OB2 Association": ["Vel OB2", "Vela OB2"],
    "Taurus-Auriga Association": ["Tau-Aur", "Taurus-Auriga"],
    "Serpens Molecular Cloud": ["Serpens Cloud", "Serpens South"],
})


# Hunt & Reffert catalogue matching aliases. These are used only against its
# AllNames/Name columns; they do not force the output location.
CLUSTER_CATALOG_ALIASES: dict[str, list[str]] = {
    "Alpha Persei Cluster (Melotte 20)": ["Melotte 20", "Alpha Persei"],
    "Butterfly Cluster (M6)": ["M6", "NGC 6405"],
    "Coma Berenices Cluster (Melotte 111)": ["Melotte 111", "Coma Ber"],
    "Hyades Cluster": ["Hyades"],
    "IC 2391": ["IC 2391"],
    "IC 2602 (Southern Pleiades)": ["IC 2602"],
    "IC 348": ["IC 348"],
    "IC 4665": ["IC 4665"],
    "M34 (NGC 1039)": ["M34", "NGC 1039"],
    "M35 (NGC 2168)": ["M35", "NGC 2168"],
    "M38 (NGC 1912)": ["M38", "NGC 1912"],
    "M39 (NGC 7092)": ["M39", "NGC 7092"],
    "M44 (Praesepe / Beehive Cluster)": ["M44", "Praesepe", "NGC 2632"],
    "M67 (NGC 2682)": ["M67", "NGC 2682"],
    "NGC 1333": ["NGC 1333"],
    "NGC 1502": ["NGC 1502"],
    "NGC 2232": ["NGC 2232"],
    "NGC 2264 (Christmas Tree Cluster)": ["NGC 2264"],
    "NGC 2451": ["NGC 2451"],
    "NGC 2516": ["NGC 2516"],
    "NGC 2547": ["NGC 2547"],
    "NGC 3532 (Wishing Well Cluster)": ["NGC 3532"],
    "NGC 6633": ["NGC 6633"],
    "NGC 752": ["NGC 752"],
    "Pleiades (M45)": ["M45", "Pleiades"],
    "Ptolemy's Cluster (M7)": ["M7", "NGC 6475"],
}


# Schlafly major-cloud FITS table has multiple sightlines for some regions.
# We combine the matching sightlines in Cartesian space to obtain a single
# representative position. Orion A and Orion B are deliberately NOT both
# assigned the broad Orion region because that would conflate the two clouds.
SCHLAFLY_REGION_ALIASES: dict[str, list[str]] = {
    "California Nebula (NGC 1499)": ["California"],
    "Cepheus Flare": ["Cepheus"],
    "Lambda Orionis Ring": ["Orion Lam"],
    "Monoceros R2 Molecular Cloud": ["Mon R2"],
    "Perseus Molecular Cloud": ["Perseus"],
    "Rho Ophiuchi Cloud Complex": ["Ophiuchus"],
    "Taurus Molecular Cloud": ["Taurus"],
    "MBM 12 Molecular Cloud": ["MBM 12"],
}

# ============================================================================
# ZUCKER ET AL. (2021) — TABLE 1: SKELETONIZATION RESULTS
# DOI: 10.3847/1538-4357/ac1f96
#
# Published median Galactic coordinates and median distances.
# All distances are heliocentric.
#
# Columns: Galactic longitude, Galactic latitude, median distance (pc).
# ============================================================================

ZUCKER_TABLE1: dict[str, tuple[float, float, float]] = {
    "Chamaeleon":       (299.0, -15.9, 183.0),
    "Ophiuchus":        (355.4,  17.0, 136.0),
    "Lupus":            (341.7,   5.1, 165.0),
    "Taurus":            (172.6, -15.6, 148.0),
    "Perseus":           (158.9, -20.2, 295.0),
    "Musca":             (301.0,  -9.7, 172.0),
    "Pipe":                (2.1,   4.1, 152.0),
    "Cepheus":           (112.1,  15.9, 345.0),
    "Corona Australis":     (6.5, -24.6, 161.0),
    "Orion A":            (212.5, -19.6, 405.0),
    "Orion B":            (205.5, -14.0, 404.0),
    "Lambda Orionis":     (194.3, -14.6, 386.0),
}


ZUCKER_REGION_ALIASES: dict[str, str] = {
    "Chamaeleon Clouds": "Chamaeleon",
    "Lupus Molecular Clouds": "Lupus",
    "Taurus Molecular Cloud": "Taurus",
    "Perseus Molecular Cloud": "Perseus",
    "Musca Molecular Cloud": "Musca",
    "Pipe Nebula": "Pipe",
    "Cepheus Flare": "Cepheus",
    "Corona Australis Cloud": "Corona Australis",
    "Orion A Molecular Cloud": "Orion A",
    "Orion B Molecular Cloud": "Orion B",
    "Lambda Orionis Ring": "Lambda Orionis",
    "Rho Ophiuchi Cloud Complex": "Ophiuchus",
}


class ZuckerCloudCatalogue:
    """Published median cloud positions from Zucker et al. (2021), Table 1."""

    SOURCE = "Zucker et al. (2021), Table 1"

    def query(self, display_name: str) -> Location | None:
        region = ZUCKER_REGION_ALIASES.get(display_name)

        if region is None:
            return None

        values = ZUCKER_TABLE1.get(region)

        if values is None:
            return None

        l_deg, b_deg, distance_pc = values

        x, y, z = xyz_from_lb(l_deg, b_deg, distance_pc)

        return Location(
            x_pc=x,
            y_pc=y,
            z_pc=z,
            distance_pc=distance_pc,
            gal_l_deg=l_deg,
            gal_b_deg=b_deg,
            source=self.SOURCE,
            source_detail=(
                f"Zucker et al. (2021), Table 1, cloud={region}; "
                f"l={l_deg:.1f} deg; b={b_deg:.1f} deg; "
                f"dmed={distance_pc:.1f} pc. "
                "Representative position calculated from the published "
                "median Galactic coordinates and median heliocentric distance."
            ),
        )

# Preserve original user-supplied classifications for the original landmarks.
# Other objects use a SIMBAD type where possible, with a category fallback.
ORIGINAL_TYPE_OVERRIDES: dict[str, str] = {
    "Orion Nebula (M42) / Trapezium Cluster": "H II region / young cluster",
    "Horsehead Nebula (Barnard 33) & Flame Nebula": "Dark nebula / H II region",
    "Orion B Molecular Cloud": "Giant molecular cloud",
    "Orion A Molecular Cloud": "Giant molecular cloud",
    "Lambda Orionis Ring": "Molecular ring / OB association",
    "Witch Head Nebula (IC 2118)": "Reflection nebula",
    "Taurus Molecular Cloud": "Molecular cloud complex",
    "Pleiades (M45)": "Open star cluster",
    "Hyades Cluster": "Open star cluster",
    "Perseus Molecular Cloud": "Molecular cloud complex",
    "NGC 1333": "Reflection nebula / young cluster",
    "IC 348": "Young open cluster",
    "California Nebula (NGC 1499)": "Emission nebula",
    "Rho Ophiuchi Cloud Complex": "Molecular cloud complex",
    "Lupus Molecular Clouds": "Molecular cloud complex",
    "Chamaeleon Clouds": "Molecular cloud complex",
    "Musca Molecular Cloud": "Molecular cloud",
    "Corona Australis Cloud": "Molecular cloud",
    "Pipe Nebula": "Dark nebula complex",
    "Cepheus Flare": "Molecular cloud complex",
    "Coalsack Nebula": "Dark nebula",
    "Scorpius-Centaurus OB Association": "Young stellar association",
    "Betelgeuse (Alpha Orionis)": "Red supergiant star",
    "Rigel (Beta Orionis)": "Blue supergiant star",
    "Aldebaran (Alpha Tauri)": "Red giant star",
    "Antares (Alpha Scorpii)": "Red supergiant star",
    "Polaris (Alpha Ursae Minoris)": "Cepheid variable star",
    "Spica (Alpha Virginis)": "Binary giant/subgiant star",
    "Sirius (Alpha Canis Majoris)": "Binary main-sequence + white dwarf",
    "Vega (Alpha Lyrae)": "Main-sequence star",
    "Local Bubble / Local Chimney": "Cavity in the ISM",
}

# Sun and Galactic Center are reference points, not ordinary nearby candidates.
# The Galactic Center is deliberately exempt from MAX_DISTANCE_PC because the
# user explicitly wants it retained as a map reference (about 8.2 kpc away).
REFERENCE_POINTS: dict[str, dict[str, Any]] = {
    "Sun": {
        "type": "Point",
        "xyz": (0.0, 0.0, 0.0),
        "short": "Sun of the solar system.",
        "paragraph": "The Sun is the origin of this heliocentric coordinate system. Its position is (0, 0, 0) pc by definition.",
    },
    "Galaxy Center": {
        "type": "Point",
        "xyz": (8200.0, 0.0, 0.0),
        "short": "Center of the Milky Way galaxy.",
        "paragraph": "The Galactic Center is retained as a reference point at approximately 8,200 pc from the Sun. It is intentionally exempt from the 1,300 pc nearby-landmark filter.",
    },
}


SIMBAD_OTYPE_LABELS = {
    "*": "Star",
    "**": "Double or multiple star",
    "V*": "Variable star",
    "OpC": "Open star cluster",
    "GlC": "Globular cluster",
    "Cl*": "Star cluster",
    "Ass*": "Stellar association",
    "HII": "H II region",
    "RNe": "Reflection nebula",
    "DNe": "Dark nebula",
    "Neb": "Nebula",
    "SNR": "Supernova remnant",
    "PN": "Planetary nebula",
    "ISM": "Interstellar matter",
    "Cld": "Interstellar cloud",
}


# The Local Bubble surrounds the Sun and has no single point coordinate.
# This explicit visualization anchor is the sole manual coordinate exception.
SPECIAL_ANCHORS: dict[str, tuple[float, float, float]] = {
    "Local Bubble / Local Chimney": (0.0, 0.0, 0.0),
}


# ============================================================================
# DATA TYPES AND HELPERS
# ============================================================================

@dataclass
class Location:
    x_pc: float
    y_pc: float
    z_pc: float
    distance_pc: float
    gal_l_deg: float
    gal_b_deg: float
    source: str
    simbad_otype: str = ""
    spectral_type: str = ""
    source_detail: str = ""


def make_session() -> requests.Session:
    session = requests.Session()
    retry = Retry(
        total=3,
        connect=3,
        read=3,
        backoff_factor=0.8,
        status_forcelist=(429, 500, 502, 503, 504),
        allowed_methods=("GET", "POST"),
    )
    adapter = HTTPAdapter(max_retries=retry)
    session.mount("https://", adapter)
    session.mount("http://", adapter)
    session.headers.update({"User-Agent": USER_AGENT})
    return session


def natural_sort_key(value: str) -> list[tuple[int, Any]]:
    """Natural alphabetical sort: M6 appears before M34."""
    parts = re.split(r"(\d+)", value.casefold())
    return [(1, int(p)) if p.isdigit() else (0, p) for p in parts]


def normalize_name(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        value = value.decode("utf-8", errors="replace")
    text = unicodedata.normalize("NFKD", str(value))
    text = "".join(ch for ch in text if not unicodedata.combining(ch))
    return re.sub(r"[^a-z0-9]+", "", text.casefold())


def clean_text(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        value = value.decode("utf-8", errors="replace")
    text = str(value).strip()
    if text in {"", "--", "None", "nan", "masked"}:
        return ""
    return text


def to_float(value: Any) -> float | None:
    if value is None:
        return None
    try:
        if np.ma.is_masked(value):
            return None
    except Exception:
        pass
    try:
        result = float(value)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(result):
        return None
    return result


def get_row_value(row: Any, colnames: Iterable[str], *wanted: str) -> Any:
    """Fetch a table/row value with case-insensitive column-name matching."""
    lookup = {str(name).casefold(): str(name) for name in colnames}
    for candidate in wanted:
        actual = lookup.get(candidate.casefold())
        if actual is not None:
            try:
                value = row[actual]
                if np.ma.is_masked(value):
                    continue
                return value
            except Exception:
                continue
    return None


def xyz_from_lb(l_deg: float, b_deg: float, distance_pc: float) -> tuple[float, float, float]:
    """Convert heliocentric Galactic l,b,d to the project's XYZ convention."""
    l_rad = math.radians(l_deg)
    b_rad = math.radians(b_deg)
    x = distance_pc * math.cos(b_rad) * math.cos(l_rad)
    y = distance_pc * math.cos(b_rad) * math.sin(l_rad)
    z = distance_pc * math.sin(b_rad)
    return x, y, z


def radec_to_location(
    ra_deg: float,
    dec_deg: float,
    distance_pc: float,
    source: str,
    otype: str = "",
    spectral_type: str = "",
    source_detail: str = "",
) -> Location:
    sky = SkyCoord(ra=ra_deg * u.deg, dec=dec_deg * u.deg, frame="icrs")
    gal = sky.galactic
    l_deg = float(gal.l.deg)
    b_deg = float(gal.b.deg)
    x, y, z = xyz_from_lb(l_deg, b_deg, distance_pc)
    return Location(
        x_pc=x,
        y_pc=y,
        z_pc=z,
        distance_pc=distance_pc,
        gal_l_deg=l_deg,
        gal_b_deg=b_deg,
        source=source,
        simbad_otype=otype,
        spectral_type=spectral_type,
        source_detail=source_detail,
    )


def is_within_limit(location: Location) -> bool:
    return 0.0 < location.distance_pc <= MAX_DISTANCE_PC


def word_limit(text: str, max_words: int) -> str:
    words = text.split()
    if len(words) <= max_words:
        return text.strip()
    return " ".join(words[:max_words]).rstrip(" ,;:-") + "…"


# ============================================================================
# 2. SIMBAD
# ============================================================================

class SimbadClient:
    """SIMBAD client with explicit mirror failover.

    SIMBAD mirror failover is performed before checking fields. Each object
    lookup also tries all known aliases until it finds a distance measurement.
    """

    def __init__(self) -> None:
        from astroquery.simbad import Simbad

        self.client = None
        self.server = ""
        last_error: Exception | None = None

        for server in SIMBAD_SERVERS:
            try:
                client = Simbad()
                client.server = server
                client.timeout = HTTP_TIMEOUT_SECONDS
                client.ROW_LIMIT = 100

                # IMPORTANT: use the instance method after setting its mirror.
                available = client.list_votable_fields()
                available_names = {
                    clean_text(value).casefold()
                    for value in available["name"]
                }

                # Current astroquery uses 'sp' for the spectral-type bundle,
                # not the older guessed field name 'sp_type'.
                requested = ["otype", "sp", "mesDistance", "mesplx", "parallax"]
                for field in requested:
                    if field.casefold() in available_names:
                        try:
                            client.add_votable_fields(field)
                        except Exception as exc:
                            print(f"  SIMBAD ({server}): could not add {field}: {exc}")

                # Test the mirror using standard SIMBAD identifiers.
                probe = None

                for test_identifier in ("Sirius", "HD 48915"):
                    try:
                        probe = client.query_object(test_identifier)
                        if probe is not None and len(probe) > 0:
                            break
                    except Exception:
                        probe = None

                if probe is None or len(probe) == 0:
                    raise RuntimeError("SIMBAD mirror failed its test query")

                self.client = client
                self.server = server
                print(f"  Connected to SIMBAD mirror: {server}")
                break
            except Exception as exc:
                last_error = exc
                print(f"  SIMBAD mirror unavailable ({server}): {exc}")

        if self.client is None:
            raise RuntimeError(f"All SIMBAD mirrors failed. Last error: {last_error}")

    def query(self, display_name: str) -> tuple[Location | None, str, str]:
        """Try every known identifier until one has a usable distance.

        Important difference from v1: resolving a name is not enough. Many
        nebulae/clouds have a SIMBAD object record but no distance attached to
        that particular record. The previous implementation stopped at the
        first resolvable alias and never tried the remaining aliases.
        """
        candidates = [display_name] + SIMBAD_ALIASES.get(display_name, [])
        tried: set[str] = set()
        resolved_without_distance: list[str] = []
        last_otype = ""

        for candidate in candidates:
            candidate_key = normalize_name(candidate)
            if not candidate_key or candidate_key in tried:
                continue
            tried.add(candidate_key)

            try:
                result = self.client.query_object(candidate)
                time.sleep(REQUEST_DELAY_SECONDS)
            except Exception as exc:
                # A failed alias should not prevent trying its alternatives.
                continue

            if result is None or len(result) == 0:
                continue

            columns = result.colnames
            first = result[0]
            ra = to_float(get_row_value(first, columns, "ra"))
            dec = to_float(get_row_value(first, columns, "dec"))
            if ra is None or dec is None:
                resolved_without_distance.append(f"{candidate} (no RA/Dec)")
                continue

            otype = clean_text(get_row_value(first, columns, "otype"))
            spectral_type = clean_text(
                get_row_value(first, columns, "sp_type", "sp", "sp_type_string")
            )
            if otype:
                last_otype = otype

            distance_pc, distance_detail = self._best_distance(result)
            if distance_pc is None:
                resolved_without_distance.append(candidate)
                continue

            location = radec_to_location(
                ra,
                dec,
                distance_pc,
                source="SIMBAD",
                otype=otype,
                spectral_type=spectral_type,
                source_detail=distance_detail,
            )
            return (
                location,
                otype,
                f"resolved as {candidate}; {distance_detail}",
            )

        if resolved_without_distance:
            tried_text = ", ".join(resolved_without_distance[:5])
            extra = " ..." if len(resolved_without_distance) > 5 else ""
            return (
                None,
                last_otype,
                "SIMBAD resolved candidate(s) but none had a reliable distance: "
                f"{tried_text}{extra}",
            )

        return None, "", "SIMBAD did not resolve any known name/identifier"

    @staticmethod
    def _best_distance(table: Any) -> tuple[float | None, str]:
        columns = table.colnames
        candidates: list[tuple[tuple[int, int, int], float, str]] = []

        quality_rank = {"A": 0, "B": 1, "C": 2, "D": 3, "E": 4, "": 5}
        method_rank = {"paral": 0, "gaia": 0, "cep": 1, "rrlyr": 2, "st-l": 3}

        # Literature distance measurements (pc, kpc or Mpc).
        for row in table:
            value = to_float(get_row_value(row, columns, "mesdistance.distance"))
            unit = clean_text(get_row_value(row, columns, "mesdistance.unit")).casefold()
            if value is None or value <= 0:
                continue
            if unit == "pc":
                distance = value
            elif unit == "kpc":
                distance = value * 1000.0
            elif unit == "mpc":
                distance = value * 1_000_000.0
            else:
                continue

            quality = clean_text(get_row_value(row, columns, "mesdistance.q")).upper()
            method = clean_text(get_row_value(row, columns, "mesdistance.method")).casefold()
            bibcode = clean_text(get_row_value(row, columns, "mesdistance.bibcode"))
            year_match = re.match(r"(\d{4})", bibcode)
            year = int(year_match.group(1)) if year_match else 0
            score = (quality_rank.get(quality, 5), method_rank.get(method, 4), -year)
            detail = (
                f"SIMBAD literature distance={distance:.1f} pc, "
                f"method={method or 'unspecified'}, ref={bibcode or 'not listed'}"
            )
            candidates.append((score, distance, detail))

        if candidates:
            candidates.sort(key=lambda item: item[0])
            return candidates[0][1], candidates[0][2]

        # Preferred fallback: the current best SIMBAD parallax value, generally
        # sourced from Gaia/Hipparcos. This is distinct from the historical list.
        for row in table:
            plx = to_float(get_row_value(row, columns, "plx_value"))
            plx_err = to_float(get_row_value(row, columns, "plx_err"))
            if plx is not None and plx > 0 and (plx_err is None or plx_err / plx <= 0.20):
                distance = 1000.0 / plx
                bibcode = clean_text(get_row_value(row, columns, "plx_bibcode"))
                detail = (
                    f"SIMBAD adopted parallax={plx:.4f} mas; "
                    f"inverse-parallax distance={distance:.1f} pc; "
                    f"ref={bibcode or 'not listed'}"
                )
                return distance, detail

        # Last fallback: best-quality historical parallax measurement.
        plx_candidates: list[tuple[int, float, str]] = []
        for row in table:
            plx = to_float(get_row_value(row, columns, "mesplx.plx"))
            plx_err = to_float(get_row_value(row, columns, "mesplx.plx_err"))
            if plx is None or plx <= 0:
                continue
            if plx_err is not None and plx_err / plx > 0.20:
                continue
            distance = 1000.0 / plx
            bibcode = clean_text(get_row_value(row, columns, "mesplx.bibcode"))
            year_match = re.match(r"(\d{4})", bibcode)
            year = int(year_match.group(1)) if year_match else 0
            detail = (
                f"SIMBAD parallax={plx:.4f} mas; "
                f"inverse-parallax distance={distance:.1f} pc; "
                f"ref={bibcode or 'not listed'}"
            )
            plx_candidates.append((-year, distance, detail))

        if plx_candidates:
            plx_candidates.sort(key=lambda item: item[0])
            return plx_candidates[0][1], plx_candidates[0][2]

        return None, ""


# ============================================================================
# 3. VIZIER OPEN-CLUSTER CATALOGUE — Hunt & Reffert 2024
# ============================================================================

class ClusterCatalogue:
    def __init__(self) -> None:
        self.rows: list[Any] = []
        self.colnames: list[str] = []
        self.server = ""
        last_error: Exception | None = None

        from astroquery.vizier import Vizier

        for server in VIZIER_SERVERS:
            try:
                print(f"Trying Hunt & Reffert 2024 catalogue on VizieR mirror {server}...")
                vizier = Vizier(
                    columns=["*"],
                    row_limit=-1,
                    timeout=SERVICE_CONNECT_TIMEOUT_SECONDS,
                    vizier_server=server,
                )
                catalogues = vizier.get_catalogs(VIZIER_CLUSTER_CATALOG)
                if catalogues and len(catalogues) > 0 and len(catalogues[0]) > 0:
                    table = catalogues[0]
                    self.rows = list(table)
                    self.colnames = list(table.colnames)
                    self.server = server
                    print(f"  Loaded {len(self.rows):,} cluster rows from {server}.")
                    break
                raise RuntimeError("VizieR returned no rows for the cluster table")
            except Exception as exc:
                last_error = exc
                print(f"  VizieR mirror failed ({server}): {exc}")

        if not self.rows:
            print(f"  WARNING: all VizieR mirrors failed; last error: {last_error}")

    @property
    def available(self) -> bool:
        return bool(self.rows)

    def query(self, display_name: str) -> Location | None:
        if not self.rows:
            return None

        aliases = CLUSTER_CATALOG_ALIASES.get(display_name, [display_name])
        alias_keys = {normalize_name(alias) for alias in aliases if normalize_name(alias)}
        matches: list[tuple[int, Any]] = []

        for row in self.rows:
            name = clean_text(get_row_value(row, self.colnames, "Name"))
            all_names = clean_text(get_row_value(row, self.colnames, "AllNames"))
            values = {normalize_name(name)}
            values.update(
                normalize_name(part)
                for part in re.split(r"[,;|]", all_names)
                if normalize_name(part)
            )
            common = alias_keys.intersection(values)
            if common:
                exact = 0 if normalize_name(display_name) in values else 1
                matches.append((exact, row))

        if not matches:
            return None

        matches.sort(key=lambda item: item[0])
        row = matches[0][1]
        distance = to_float(get_row_value(row, self.colnames, "dist50"))
        ra = to_float(get_row_value(row, self.colnames, "RA_ICRS"))
        dec = to_float(get_row_value(row, self.colnames, "DE_ICRS"))
        catalogue_name = clean_text(get_row_value(row, self.colnames, "Name"))
        if distance is None or distance <= 0 or ra is None or dec is None:
            return None

        ctype = clean_text(get_row_value(row, self.colnames, "Type"))
        detail = (
            f"Hunt & Reffert 2024 VizieR catalogue ({catalogue_name}); "
            f"dist50={distance:.1f} pc; catalogue type={ctype or 'not listed'}; "
            f"mirror={self.server}"
        )
        return radec_to_location(
            ra, dec, distance,
            source="VizieR: Hunt & Reffert 2024",
            otype="OpC",
            source_detail=detail,
        )


# ============================================================================
# 4. SCHLAFLY MOLECULAR-CLOUD DISTANCE CATALOGUE
# ============================================================================

class SchlaflyCloudCatalogue:
    def __init__(self, session: requests.Session) -> None:
        self.session = session
        self.rows: list[dict[str, Any]] = []
        self._download(SCHLAFLY_MAJOR_CLOUDS_URL, "major clouds")
        self._download(SCHLAFLY_MBM_CLOUDS_URL, "MBM clouds")

    def _download(self, url: str, label: str) -> None:
        try:
            print(f"Downloading Schlafly et al. {label} FITS catalogue...")
            response = self.session.get(url, timeout=HTTP_TIMEOUT_SECONDS)
            response.raise_for_status()
            table = Table.read(io.BytesIO(response.content), format="fits")
            columns = table.colnames
            count_before = len(self.rows)
            for row in table:
                record = {
                    "name": clean_text(get_row_value(row, columns, "name")),
                    "l": to_float(get_row_value(row, columns, "l")),
                    "b": to_float(get_row_value(row, columns, "b")),
                    "m50": to_float(get_row_value(row, columns, "m50")),
                    "dist_lit": to_float(get_row_value(row, columns, "dist_lit")),
                }
                if record["name"] and record["l"] is not None and record["b"] is not None:
                    self.rows.append(record)
            print(f"  Loaded {len(self.rows) - count_before:,} rows from {label}.")
        except Exception as exc:
            print(f"  Could not load {label}: {exc}")

    def query(self, display_name: str) -> Location | None:
        aliases = SCHLAFLY_REGION_ALIASES.get(display_name, [])
        if not aliases:
            return None
        alias_keys = {normalize_name(alias) for alias in aliases}
        selected = [
            row for row in self.rows
            if normalize_name(row["name"]) in alias_keys
        ]
        if not selected:
            return None

        xyz_samples: list[tuple[float, float, float]] = []
        used_distances: list[float] = []
        for row in selected:
            dm = row.get("m50")
            if dm is not None:
                # Schlafly m50 is the median distance modulus:
                # mu = 5 log10(d_pc) - 5.
                distance = 10.0 ** ((dm + 5.0) / 5.0)
            else:
                distance = row.get("dist_lit")
            if distance is None or distance <= 0 or distance > MAX_DISTANCE_PC:
                continue
            x, y, z = xyz_from_lb(row["l"], row["b"], distance)
            xyz_samples.append((x, y, z))
            used_distances.append(distance)

        if not xyz_samples:
            return None

        # A robust median over the Cartesian sightline positions provides a
        # single representative marker for a spatially extended cloud.
        median_xyz = np.median(np.asarray(xyz_samples, dtype=float), axis=0)
        x, y, z = (float(v) for v in median_xyz)
        distance = math.sqrt(x * x + y * y + z * z)
        if distance <= 0:
            return None
        l_deg = math.degrees(math.atan2(y, x)) % 360.0
        b_deg = math.degrees(math.asin(max(-1.0, min(1.0, z / distance))))
        region = selected[0]["name"]
        detail = (
            f"Schlafly et al. PS1 cloud-distance catalogue, region={region}; "
            f"combined {len(used_distances)} sightline(s), median m50-derived distance "
            f"{float(np.median(used_distances)):.1f} pc"
        )
        return Location(
            x_pc=x,
            y_pc=y,
            z_pc=z,
            distance_pc=distance,
            gal_l_deg=l_deg,
            gal_b_deg=b_deg,
            source="Schlafly et al.",
            source_detail=detail,
        )


# ============================================================================
# 5. WIKIPEDIA DESCRIPTIVE CONTEXT
# Only a short, attributed lead excerpt is included. The remainder of the
# paragraph is generated from catalogue metadata and coordinate information.
# ============================================================================

STOPWORDS = {
    "the", "of", "and", "in", "nebula", "cluster", "cloud", "molecular",
    "star", "stars", "association", "region", "remnant", "open", "dark",
    "reflection", "emission", "planetary", "supernova", "complex", "object",
}


def title_match_score(target_name: str, article_title: str) -> float:
    target_tokens = {
        token for token in re.findall(r"[a-z0-9]+", target_name.casefold())
        if token not in STOPWORDS and len(token) > 1
    }
    title_tokens = {
        token for token in re.findall(r"[a-z0-9]+", article_title.casefold())
        if token not in STOPWORDS and len(token) > 1
    }
    if not target_tokens or not title_tokens:
        return 0.0
    overlap = len(target_tokens.intersection(title_tokens))
    return overlap / max(1, min(len(target_tokens), len(title_tokens)))



def wikipedia_intro(session: requests.Session, display_name: str) -> tuple[str, str]:

    # Search specifically for astronomical articles when names are ambiguous.
    search_overrides = {
        "Capella": "Capella star",
        "Canopus": "Canopus star",
    }

    search_query = search_overrides.get(display_name, display_name)

    params = {
        "action": "query",
        "generator": "search",
        "gsrsearch": search_query,
        "gsrnamespace": 0,
        "gsrlimit": 10,
        "prop": "extracts",
        "exintro": 1,
        "explaintext": 1,
        "exchars": 1600,
        "format": "json",
        "formatversion": 2,
    }

    try:
        response = session.get(
            WIKIPEDIA_API_URL,
            params=params,
            timeout=HTTP_TIMEOUT_SECONDS
        )
        response.raise_for_status()

        payload = response.json()
        pages = payload.get("query", {}).get("pages", [])

        if isinstance(pages, dict):
            pages = list(pages.values())

        candidates = []

        for page in pages:
            title = clean_text(page.get("title"))
            extract = clean_text(page.get("extract"))

            if not title or not extract:
                continue

            # Reject disambiguation articles.
            if "(disambiguation)" in title.casefold():
                continue

            if "may also refer to:" in extract.casefold():
                continue

            score = title_match_score(display_name, title)

            if score < 0.50:
                continue

            # Remove HTML breaks and normalize all whitespace.
            extract = re.sub(
                r"<br\s*/?>",
                " ",
                extract,
                flags=re.IGNORECASE
            )
            extract = re.sub(r"\s+", " ", extract).strip()

            candidates.append((score, title, extract))

        if not candidates:
            return "", ""

        candidates.sort(key=lambda item: item[0], reverse=True)

        score, title, extract = candidates[0]

        return title, word_limit(extract, WIKIPEDIA_EXCERPT_MAX_WORDS)

    except Exception as exc:
        print(f"    Wikipedia description lookup failed: {exc}")
        return "", ""


# ============================================================================
# 6. CSV BUILDING
# ============================================================================


def category_fallback_type(group_name: str) -> str:
    if group_name == "Stars":
        return "Star"
    if group_name == "Open and embedded star clusters":
        return "Open / young star cluster"
    if group_name == "Nebulae and supernova remnants":
        return "Nebula / supernova remnant"
    return "Molecular cloud / stellar association / ISM structure"


def select_display_type(
    display_name: str,
    group_name: str,
    simbad_otype: str,
    spectral_type: str,
) -> str:
    if display_name in ORIGINAL_TYPE_OVERRIDES:
        return ORIGINAL_TYPE_OVERRIDES[display_name]

    if simbad_otype:
        mapped = SIMBAD_OTYPE_LABELS.get(simbad_otype)
        if mapped:
            if group_name == "Stars" and spectral_type:
                return f"{mapped} ({spectral_type})"
            return mapped

    if group_name == "Stars" and spectral_type:
        return f"Star ({spectral_type})"
    return category_fallback_type(group_name)


def make_descriptions(
    session: requests.Session,
    name: str,
    type_label: str,
    location: Location,
) -> tuple[str, str]:
    distance = location.distance_pc
    short = f"{type_label}; approximately {distance:,.0f} pc from the Sun."

    wiki_title, wiki_excerpt = wikipedia_intro(session, name)
    paragraph_parts: list[str] = []
    if wiki_excerpt and wiki_title:
        paragraph_parts.append(f"Wikipedia ({wiki_title}) describes it as: {wiki_excerpt}")

    if location.simbad_otype:
        paragraph_parts.append(f"SIMBAD object classification: {location.simbad_otype}.")
    if location.spectral_type:
        paragraph_parts.append(f"Spectral type: {location.spectral_type}.")

    paragraph_parts.append(
        f"Adopted heliocentric distance: {distance:,.1f} pc "
        f"(position/distance source: {location.source})."
    )
    paragraph_parts.append(
        f"Galactic coordinates: l={location.gal_l_deg:.3f}°, "
        f"b={location.gal_b_deg:.3f}°; converted to Sun-centred Galactic XYZ."
    )
    if location.source_detail:
        paragraph_parts.append(f"Catalogue detail: {location.source_detail}.")

    paragraph = " ".join(paragraph_parts)
    return short, paragraph


def main() -> int:
    candidate_entries = [
        (group_name, name)
        for group_name, names in LANDMARK_GROUPS.items()
        for name in sorted(names, key=natural_sort_key)
    ]
    candidate_entries.sort(key=lambda item: (item[0].casefold(), natural_sort_key(item[1])))

    candidate_names = [name for _, name in candidate_entries]

    if len(candidate_names) != len(set(candidate_names)):
        print("ERROR: duplicate landmark names found in LANDMARK_GROUPS.")
        return 2

    reference_entries = [("Reference points", name) for name in REFERENCE_POINTS]
    all_entries = candidate_entries + reference_entries
    all_entries.sort(key=lambda item: (item[0].casefold(), natural_sort_key(item[1])))

    print(f"Candidate landmarks: {len(candidate_entries)}")
    print(f"Reference points: {len(reference_entries)} (Sun and Galaxy Center)")
    print(f"Maximum heliocentric distance: {MAX_DISTANCE_PC:.0f} pc")
    print()

    session = make_session()

    # Load the specialist catalogues once rather than making one VizieR/FITS
    # request for each candidate.
    cluster_catalogue = ClusterCatalogue()
    cloud_catalogue = SchlaflyCloudCatalogue(session)
    zucker_catalogue = ZuckerCloudCatalogue()

    try:
        simbad = SimbadClient()
    except Exception as exc:
        print(f"WARNING: all SIMBAD mirrors failed: {exc}")
        simbad = None

    # Continue even if the online catalogues are unavailable.
    # Unresolved objects will be skipped; no previous CSV data is reused.
    if simbad is None and not cluster_catalogue.available:
        print("\nWARNING: SIMBAD and VizieR are both unavailable.")
        print("Catalogue-dependent landmarks may be skipped.")

    output_rows: list[dict[str, str]] = []
    skipped: list[tuple[str, str]] = []
    counts_by_source: dict[str, int] = {}

    for index, (group_name, name) in enumerate(all_entries, start=1):
        print(f"[{index:03d}/{len(all_entries)}] {name}")
        location: Location | None = None
        simbad_otype = ""
        spectral_type = ""
        resolution_note = ""

        # Sun and Galactic Center are explicit reference points. They are not
        # subject to the nearby-object distance cut (the Galactic Center is ~8.2 kpc).
        if name in REFERENCE_POINTS:
            reference = REFERENCE_POINTS[name]
            x, y, z = reference["xyz"]
            output_rows.append({
                "name": name,
                "type": reference["type"],
                "x_pc": f"{x:.3f}",
                "y_pc": f"{y:.3f}",
                "z_pc": f"{z:.3f}",
                "short_description": reference["short"],
                "paragraph_description": reference["paragraph"],
            })
            counts_by_source["reference point (explicit coordinate)"] = (
                counts_by_source.get("reference point (explicit coordinate)", 0) + 1
            )
            print("    INCLUDED: explicit reference point (exempt from distance filter).")
            continue

        # Special treatment: the Local Bubble is an extended cavity containing
        # the Sun, so use its explicitly defined visualization anchor.
        if name in SPECIAL_ANCHORS:
            x, y, z = SPECIAL_ANCHORS[name]
            location = Location(
                x_pc=x,
                y_pc=y,
                z_pc=z,
                distance_pc=0.0,
                gal_l_deg=0.0,
                gal_b_deg=0.0,
                source="manual visualization anchor (not a measured centre)",
                source_detail=(
                    "Extended structure containing the Sun; marker placed at "
                    "the Sun as a visualization anchor, not a measured centroid"
                ),
            )
            type_label = ORIGINAL_TYPE_OVERRIDES[name]
            short = "Nearby low-density interstellar cavity containing the Sun."
            paragraph = (
                "The Local Bubble is an extended low-density cavity in the "
                "interstellar medium that surrounds the Sun. It has no single "
                "point distance or unique centre; this marker is deliberately "
                "anchored at (0, 0, 0) pc for visualization."
            )
            output_rows.append({
                "name": name,
                "type": type_label,
                "x_pc": f"{x:.3f}",
                "y_pc": f"{y:.3f}",
                "z_pc": f"{z:.3f}",
                "short_description": short,
                "paragraph_description": paragraph,
            })
            counts_by_source[location.source] = counts_by_source.get(location.source, 0) + 1
            print("    Included using the documented origin anchor (not a distance measurement).")
            continue

        # 1. Open clusters: Hunt & Reffert 2024.
        if group_name == "Open and embedded star clusters":
            location = cluster_catalogue.query(name)

        # 2. Molecular clouds: Zucker et al. 2021, Table 1.
        if location is None:
            location = zucker_catalogue.query(name)

        # 3. Additional cloud distances: Schlafly et al.
        if location is None:
            location = cloud_catalogue.query(name)

        # 4. General astronomical objects: SIMBAD.
        if location is None and simbad is not None:
            location, simbad_otype, resolution_note = simbad.query(name)

            if location:
                spectral_type = location.spectral_type

        if location is None:
            reason = resolution_note or "no suitable online catalogue match"
            skipped.append((name, reason))
            print(f"    SKIPPED: {reason}")
            continue

        # The maximum-distance cut applies to ordinary nearby objects. The
        # explicit Sun/Galactic Center reference points were handled above.
        if not is_within_limit(location):
            reason = (
                f"best available distance is {location.distance_pc:.1f} pc, "
                f"outside the {MAX_DISTANCE_PC:.0f} pc limit ({location.source})"
            )
            skipped.append((name, reason))
            print(f"    SKIPPED: {reason}")
            continue

        type_label = select_display_type(name, group_name, simbad_otype, spectral_type)
        short, paragraph = make_descriptions(
            session,
            name,
            type_label,
            location,
        )

        output_rows.append({
            "name": name,
            "type": type_label,
            "x_pc": f"{location.x_pc:.3f}",
            "y_pc": f"{location.y_pc:.3f}",
            "z_pc": f"{location.z_pc:.3f}",
            "short_description": short,
            "paragraph_description": paragraph,
        })
        counts_by_source[location.source] = counts_by_source.get(location.source, 0) + 1
        print(
            f"    INCLUDED: d={location.distance_pc:.1f} pc; "
            f"l={location.gal_l_deg:.2f}°, b={location.gal_b_deg:.2f}°; "
            f"source={location.source}"
        )

        # Be gentle with public astronomy services.
        time.sleep(REQUEST_DELAY_SECONDS)

    fieldnames = [
        "name",
        "type",
        "x_pc",
        "y_pc",
        "z_pc",
        "short_description",
        "paragraph_description",
    ]

    # Sort CSV rows:
    # 1. Sun always first.
    # 2. All other landmarks alphabetically by type, then by name.
    # 3. Galaxy Center always last.
    output_rows.sort(
        key=lambda row: (
            0 if row["name"] == "Sun"
            else 2 if row["name"] == "Galaxy Center"
            else 1,
            row["type"].casefold(),
            natural_sort_key(row["name"]),
        )
    )

    OUTPUT_CSV.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8-sig") as csv_file:
        writer = csv.DictWriter(csv_file, fieldnames=fieldnames, quoting=csv.QUOTE_MINIMAL)
        writer.writeheader()
        writer.writerows(output_rows)

    print("\n" + "=" * 72)
    print(f"Finished: {OUTPUT_CSV}")
    print(f"Nearby candidate names: {len(candidate_entries)}")
    print(f"Explicit reference points: {len(reference_entries)}")
    print(f"CSV rows written: {len(output_rows)}")
    print(f"Skipped: {len(skipped)}")
    print(f"SIMBAD mirror: {simbad.server if simbad else 'unavailable'}")
    print(f"VizieR mirror: {cluster_catalogue.server if cluster_catalogue.available else 'unavailable'}")
    print("\nRows by location source:")
    for source, count in sorted(counts_by_source.items()):
        print(f"  {source}: {count}")

    if skipped:
        print("\nSkipped objects (not written to the CSV):")
        for name, reason in skipped:
            print(f"  - {name}: {reason}")

    print("\nNotes:")
    print("  * No previous CSV coordinates or descriptions are used.")
    print("  * Local Bubble is represented by an explicit visualization anchor.")
    print("  * Sun and Galaxy Center are explicit reference points.")
    print("  * Galaxy Center is exempt from the 1,300 pc distance limit.")
    print("  * Zucker cloud positions use published Table 1 median values.")
    print("  * Orion A and Orion B have additional uncertainties noted in the paper.")
    print("  * Objects without a valid catalogue position and distance are skipped.")
    print("  * CSV contains only the seven requested columns.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nInterrupted by user.")
        raise SystemExit(130)
