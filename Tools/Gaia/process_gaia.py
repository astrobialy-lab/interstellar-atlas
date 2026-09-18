from pathlib import Path
from collections import defaultdict
import csv
import math
import struct


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

RAW_DIR = PROJECT_ROOT / "Data" / "Gaia" / "raw"
PROCESSED_DIR = PROJECT_ROOT / "Data" / "Gaia" / "processed"
NODES_DIR = PROCESSED_DIR / "nodes"

PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
NODES_DIR.mkdir(parents=True, exist_ok=True)


# ============================================================
# Octree settings
# ============================================================

# A node is subdivided if it contains more stars than this.
MAX_STARS_PER_NODE = 10_000

# Safety limit so the tree cannot subdivide forever.
MAX_DEPTH = 12


# ============================================================
# Binary formats
# ============================================================

# Node:
#
#   uint64 node_id
#   uint32 parent_id
#   uint32 first_child
#   uint32 child_count
#   uint32 star_count
#   float64 center_x
#   float64 center_y
#   float64 center_z
#   float64 half_size
#
NODE_STRUCT = struct.Struct("<QIIIIdddd")


# Star:
#
#   uint64 source_id
#
#   float32 x
#   float32 y
#   float32 z
#
#   float32 G magnitude
#   float32 BP-RP
#
#   float32 Teff
#   float32 logg
#   float32 metallicity
#   float32 radius
#
#   float32 A0
#   float32 AG
#   float32 ABP
#   float32 ARP
#   float32 E(BP-RP)
#
#   float32 distance
#
#   uint8 flags
#
STAR_STRUCT = struct.Struct("<QfffffffffffffffB")


# Flags
HAS_GSPPHOT = 1 << 0


# ============================================================
# Helpers
# ============================================================

def safe_float(value):
    """
    Convert a CSV value to float.

    Empty Gaia fields become NaN.
    """
    if value is None or value == "":
        return math.nan

    try:
        return float(value)
    except ValueError:
        return math.nan


def valid_number(value):
    return math.isfinite(value)


def radec_distance_to_xyz(ra_deg, dec_deg, distance_pc):
    """
    Convert Gaia RA/Dec + distance into Cartesian coordinates.

    x axis: RA = 0 deg, Dec = 0 deg
    y axis: RA = 90 deg, Dec = 0 deg
    z axis: Dec = +90 deg

    Units: parsecs.
    """

    ra = math.radians(ra_deg)
    dec = math.radians(dec_deg)

    cos_dec = math.cos(dec)

    x = distance_pc * cos_dec * math.cos(ra)
    y = distance_pc * cos_dec * math.sin(ra)
    z = distance_pc * math.sin(dec)

    return x, y, z


# ============================================================
# Read Gaia files
# ============================================================

def get_csv_files():
    files = sorted(RAW_DIR.glob("healpix_*.csv"))

    if not files:
        raise RuntimeError(
            f"No Gaia CSV files found in:\n{RAW_DIR}"
        )

    print(f"Found {len(files)} Gaia CSV files.")

    return files


def read_stars(csv_files):
    """
    Read all usable stars into memory.

    This is intentionally simple for the current 44-file test.
    We can replace this with a streaming implementation later
    when processing the full Gaia catalog.
    """

    stars = []

    skipped_no_position = 0
    total_rows = 0

    for file_index, csv_file in enumerate(csv_files, 1):

        print(
            f"Reading {file_index}/{len(csv_files)}: "
            f"{csv_file.name}"
        )

        with csv_file.open(
            "r",
            encoding="utf-8",
            newline=""
        ) as f:

            reader = csv.DictReader(f)

            for row in reader:

                total_rows += 1

                source_id = int(row["source_id"])

                ra = safe_float(row["ra"])
                dec = safe_float(row["dec"])
                parallax = safe_float(row["parallax"])

                g_mag = safe_float(row["phot_g_mean_mag"])
                bp_rp = safe_float(row["bp_rp"])

                distance = safe_float(row["distance_gspphot"])

                # ------------------------------------------------
                # Position
                # ------------------------------------------------

                if valid_number(distance) and distance > 0:

                    x, y, z = radec_distance_to_xyz(
                        ra,
                        dec,
                        distance
                    )

                    has_position = True

                else:

                    # We keep the star in the dataset, but cannot
                    # currently place it in 3D space.
                    x = math.nan
                    y = math.nan
                    z = math.nan

                    has_position = False
                    skipped_no_position += 1

                # ------------------------------------------------
                # GSP-Phot
                # ------------------------------------------------

                teff = safe_float(row["teff_gspphot"])
                logg = safe_float(row["logg_gspphot"])
                metallicity = safe_float(row["mh_gspphot"])
                radius = safe_float(row["radius_gspphot"])

                a0 = safe_float(row["azero_gspphot"])
                ag = safe_float(row["ag_gspphot"])
                abp = safe_float(row["abp_gspphot"])
                arp = safe_float(row["arp_gspphot"])
                ebp_rp = safe_float(row["ebpminrp_gspphot"])

                flags = 0

                if valid_number(teff):
                    flags |= HAS_GSPPHOT

                stars.append(
                    {
                        "source_id": source_id,

                        "x": x,
                        "y": y,
                        "z": z,

                        "g_mag": g_mag,
                        "bp_rp": bp_rp,

                        "teff": teff,
                        "logg": logg,
                        "metallicity": metallicity,
                        "radius": radius,

                        "a0": a0,
                        "ag": ag,
                        "abp": abp,
                        "arp": arp,
                        "ebp_rp": ebp_rp,

                        "distance": distance,

                        "flags": flags,

                        "has_position": has_position,
                    }
                )

    print()
    print(f"Total CSV rows:       {total_rows:,}")
    print(f"Stars loaded:         {len(stars):,}")
    print(f"Without 3D position:  {skipped_no_position:,}")

    return stars


# ============================================================
# Build initial octree
# ============================================================

class Node:

    def __init__(
        self,
        node_id,
        center_x,
        center_y,
        center_z,
        half_size,
        depth
    ):

        self.node_id = node_id

        self.center_x = center_x
        self.center_y = center_y
        self.center_z = center_z

        self.half_size = half_size

        self.depth = depth

        self.star_indices = []

        self.children = []


def calculate_bounds(stars):
    """
    Find a cubic bounding box containing every star.
    """

    positioned = [
        star
        for star in stars
        if star["has_position"]
    ]

    if not positioned:
        raise RuntimeError(
            "No stars have valid GSP-Phot distances."
        )

    min_x = min(star["x"] for star in positioned)
    max_x = max(star["x"] for star in positioned)

    min_y = min(star["y"] for star in positioned)
    max_y = max(star["y"] for star in positioned)

    min_z = min(star["z"] for star in positioned)
    max_z = max(star["z"] for star in positioned)

    center_x = (min_x + max_x) / 2
    center_y = (min_y + max_y) / 2
    center_z = (min_z + max_z) / 2

    size_x = max_x - min_x
    size_y = max_y - min_y
    size_z = max_z - min_z

    size = max(size_x, size_y, size_z)

    # Make the cube slightly larger so stars on the boundary
    # are safely inside.
    size *= 1.001

    half_size = size / 2

    return (
        center_x,
        center_y,
        center_z,
        half_size,
    )


def child_index(node, star):
    """
    Determine which of the 8 octants contains a star.

    Bit 0 = X
    Bit 1 = Y
    Bit 2 = Z
    """

    index = 0

    if star["x"] >= node.center_x:
        index |= 1

    if star["y"] >= node.center_y:
        index |= 2

    if star["z"] >= node.center_z:
        index |= 4

    return index


def create_children(node, next_node_id):
    """
    Split one node into 8 children.
    """

    quarter = node.half_size / 2

    children = []

    for i in range(8):

        offset_x = quarter if (i & 1) else -quarter
        offset_y = quarter if (i & 2) else -quarter
        offset_z = quarter if (i & 4) else -quarter

        child = Node(
            node_id=next_node_id,
            center_x=node.center_x + offset_x,
            center_y=node.center_y + offset_y,
            center_z=node.center_z + offset_z,
            half_size=quarter,
            depth=node.depth + 1,
        )

        children.append(child)

        next_node_id += 1

    return children, next_node_id


def build_octree(stars):
    """
    Build the adaptive octree.

    Every star remains in exactly one leaf.
    """

    (
        center_x,
        center_y,
        center_z,
        half_size,
    ) = calculate_bounds(stars)

    root = Node(
        node_id=0,
        center_x=center_x,
        center_y=center_y,
        center_z=center_z,
        half_size=half_size,
        depth=0,
    )

    for index, star in enumerate(stars):

        if star["has_position"]:
            root.star_indices.append(index)

    all_nodes = [root]

    next_node_id = 1

    queue = [root]

    while queue:

        node = queue.pop()

        if (
            len(node.star_indices) <= MAX_STARS_PER_NODE
            or node.depth >= MAX_DEPTH
        ):
            continue

        children, next_node_id = create_children(
            node,
            next_node_id
        )

        for index in node.star_indices:

            star = stars[index]

            child = children[
                child_index(node, star)
            ]

            child.star_indices.append(index)

        node.children = children

        # Parent no longer needs its star list.
        node.star_indices = []

        all_nodes.extend(children)

        queue.extend(children)

    print()
    print(f"Octree nodes: {len(all_nodes):,}")

    leaf_nodes = [
        node
        for node in all_nodes
        if not node.children
    ]

    print(f"Leaf nodes:   {len(leaf_nodes):,}")

    max_depth = max(
        node.depth
        for node in all_nodes
    )

    print(f"Tree depth:   {max_depth}")

    return root, all_nodes


# ============================================================
# Collect leaf stars
# ============================================================

def collect_leaf_nodes(root):
    leaves = []

    stack = [root]

    while stack:

        node = stack.pop()

        if node.children:

            stack.extend(node.children)

        else:

            leaves.append(node)

    return leaves


# ============================================================
# Write star binary
# ============================================================

def pack_star(star):

    # NaN is intentionally preserved for unavailable GSP-Phot
    # parameters.

    return STAR_STRUCT.pack(
        star["source_id"],

        float(star["x"]),
        float(star["y"]),
        float(star["z"]),

        float(star["g_mag"]),
        float(star["bp_rp"]),

        float(star["teff"]),
        float(star["logg"]),
        float(star["metallicity"]),
        float(star["radius"]),

        float(star["a0"]),
        float(star["ag"]),
        float(star["abp"]),
        float(star["arp"]),
        float(star["ebp_rp"]),

        float(star["distance"]),

        star["flags"],
    )


def write_node_file(node, stars):

    filename = NODES_DIR / f"node_{node.node_id:08d}.bin"

    with filename.open("wb") as f:

        for index in node.star_indices:

            f.write(
                pack_star(stars[index])
            )

    return filename


# ============================================================
# Write index
# ============================================================

def write_index(all_nodes):

    index_file = PROCESSED_DIR / "index.bin"

    with index_file.open("wb") as f:

        # Header
        #
        # uint32 magic
        # uint32 version
        # uint32 node_count
        #
        f.write(
            struct.pack(
                "<III",
                0x47414941,  # "GAIA"
                1,
                len(all_nodes),
            )
        )

        for node in all_nodes:

            if node.children:

                first_child = node.children[0].node_id
                child_count = 8

            else:

                first_child = 0
                child_count = 0

            f.write(
                NODE_STRUCT.pack(
                    node.node_id,

                    0xFFFFFFFF,  # parent not needed yet

                    first_child,
                    child_count,

                    len(node.star_indices),

                    node.center_x,
                    node.center_y,
                    node.center_z,

                    node.half_size,
                )
            )

    print(f"Wrote index: {index_file}")


# ============================================================
# Main
# ============================================================

def main():

    print("======================================")
    print("Gaia processing")
    print("======================================")
    print()

    csv_files = get_csv_files()

    stars = read_stars(csv_files)

    print()
    print("Building octree...")

    root, all_nodes = build_octree(stars)

    leaves = collect_leaf_nodes(root)

    print()
    print("Writing node binaries...")

    for i, node in enumerate(leaves, 1):

        write_node_file(node, stars)

        if i % 100 == 0 or i == len(leaves):

            print(
                f"Written {i}/{len(leaves)} nodes"
            )

    print()
    print("Writing index...")

    write_index(all_nodes)

    print()
    print("======================================")
    print("Done.")
    print("======================================")


if __name__ == "__main__":
    main()