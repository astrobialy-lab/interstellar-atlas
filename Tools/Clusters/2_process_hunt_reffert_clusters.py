from pathlib import Path
import csv
import json
import math
import struct

import numpy as np


# ============================================================
# Paths
# ============================================================

PROJECT_ROOT = Path(__file__).resolve().parents[2]

INPUT_DIR = (
    PROJECT_ROOT
    / "DataRaw"
    / "Clusters"
    / "HuntReffert2024"
)

OUTPUT_DIR = (
    PROJECT_ROOT
    / "Data"
    / "Clusters"
    / "HuntReffert2024"
)

OUTPUT_DIR.mkdir(
    parents=True,
    exist_ok=True
)

CLUSTERS_FILE = INPUT_DIR / "clusters.tsv"
MEMBERS_FILE = INPUT_DIR / "members.tsv"


# ============================================================
# Cluster binary format
# ============================================================

CLUSTER_MAGIC = 0x434C5354  # "CLST"
CLUSTER_VERSION = 1

CLUSTER_DTYPE = np.dtype([
    ("id", "<u2"),
    ("type", "u1"),
    ("flags", "u1"),

    ("N", "<u4"),
    ("Nt", "<u4"),

    ("GLON", "<f4"),
    ("GLAT", "<f4"),

    ("r50pc", "<f4"),
    ("rcpc", "<f4"),
    ("rtpc", "<f4"),
    ("rtotpc", "<f4"),

    ("pmRA", "<f4"),
    ("pmDE", "<f4"),
    ("Plx", "<f4"),

    ("dist16", "<f4"),
    ("dist50", "<f4"),
    ("dist84", "<f4"),

    ("X", "<f4"),
    ("Y", "<f4"),
    ("Z", "<f4"),

    ("RV", "<f4"),

    ("logAge16", "<f4"),
    ("logAge50", "<f4"),
    ("logAge84", "<f4"),

    ("AV16", "<f4"),
    ("AV50", "<f4"),
    ("AV84", "<f4"),

    ("r50Jpc", "<f4"),
    ("rJpc", "<f4"),
    ("probJ", "<f4"),

    ("NJ", "<i4"),

    ("MassJ", "<f4"),
    ("e_MassJ", "<f4"),
    ("MassTot", "<f4"),
    ("e_MassTot", "<f4"),

    ("minClSize", "<f4"),
])


CLUSTER_RECORD_SIZE = CLUSTER_DTYPE.itemsize

CLUSTER_HEADER = struct.Struct(
    "<IIII"
)


# ============================================================
# Membership binary format
# ============================================================

MEMBER_MAGIC = 0x4D454D42  # "MEMB"
MEMBER_VERSION = 1


MEMBER_DTYPE = np.dtype([
    ("source_id", "<u8"),
    ("cluster_id", "<u2"),
    ("flags", "<u2"),
    ("probability", "<f4"),
])


MEMBER_RECORD_SIZE = MEMBER_DTYPE.itemsize

MEMBER_HEADER = struct.Struct(
    "<IIII"
)


# ============================================================
# Cluster types
# ============================================================

CLUSTER_TYPES = {
    "o": 1,
    "m": 2,
    "g": 3,
    "d": 4,
    "r": 5,
}


# ============================================================
# Flags
# ============================================================

CLUSTER_FLAG_MERGED = 1 << 0
CLUSTER_FLAG_GMM = 1 << 1

MEMBER_FLAG_INRJ = 1 << 0
MEMBER_FLAG_INRT = 1 << 1


# ============================================================
# Expected catalogue sizes
# ============================================================

EXPECTED_CLUSTERS = 7_167
EXPECTED_MEMBERS = 1_291_929


# ============================================================
# Helpers
# ============================================================

def safe_float(value):
    value = value.strip()

    if not value:
        return math.nan

    try:
        return float(value)
    except ValueError:
        return math.nan


def safe_int(value):
    value = value.strip()

    if not value:
        return -1

    try:
        return int(value)
    except ValueError:
        return -1


def is_comment_or_empty(line):
    stripped = line.lstrip()

    return (
        not stripped
        or stripped.startswith("#")
    )


def read_vizier_header(reader):
    """
    VizieR TSV output contains:

        comments
        column-name row
        units row
        data rows
    """

    header = None
    units = None

    for row in reader:

        if not row:
            continue

        if row[0].lstrip().startswith("#"):
            continue

        row = [
            value.strip()
            for value in row
        ]

        if not any(row):
            continue

        if header is None:
            header = row
            continue

        units = row
        break

    if header is None:
        raise RuntimeError(
            "Could not find VizieR column header."
        )

    if units is None:
        raise RuntimeError(
            "Could not find VizieR units row."
        )

    return header


def get_column_indices(
    header,
    required
):
    indices = {}

    for name in required:

        try:
            indices[name] = header.index(name)

        except ValueError:

            raise RuntimeError(
                f"Required column '{name}' was not found.\n"
                f"Available columns:\n"
                + "\n".join(header)
            )

    return indices


# ============================================================
# Process clusters
# ============================================================

def process_clusters():

    print()
    print("======================================")
    print("Processing cluster catalogue")
    print("======================================")
    print()

    if not CLUSTERS_FILE.exists():

        raise FileNotFoundError(
            f"Missing:\n{CLUSTERS_FILE}"
        )

    # --------------------------------------------------------
    # Read header
    # --------------------------------------------------------

    with CLUSTERS_FILE.open(
        "r",
        encoding="utf-8"
    ) as f:

        reader = csv.reader(
            f,
            delimiter="\t"
        )

        header = read_vizier_header(
            reader
        )

    required = [
        "Name",
        "ID",
        "Type",
        "N",
        "Nt",
        "GLON",
        "GLAT",
        "r50pc",
        "rcpc",
        "rtpc",
        "rtotpc",
        "pmRA",
        "pmDE",
        "Plx",
        "dist16",
        "dist50",
        "dist84",
        "X",
        "Y",
        "Z",
        "RV",
        "logAge16",
        "logAge50",
        "logAge84",
        "AV16",
        "AV50",
        "AV84",
        "r50Jpc",
        "rJpc",
        "probJ",
        "NJ",
        "MassJ",
        "e_MassJ",
        "MassTot",
        "e_MassTot",
        "minClSize",
        "isMerged",
        "isGMMMemb",
    ]

    indices = get_column_indices(
        header,
        required
    )

    records = []

    names = []

    # --------------------------------------------------------
    # Read data
    # --------------------------------------------------------

    with CLUSTERS_FILE.open(
        "r",
        encoding="utf-8"
    ) as f:

        reader = csv.reader(
            f,
            delimiter="\t"
        )

        read_vizier_header(
            reader
        )

        for row in reader:

            if not row:
                continue

            if row[0].lstrip().startswith("#"):
                continue

            if len(row) < len(header):
                continue

            cluster_id = safe_int(
                row[indices["ID"]]
            )

            if cluster_id < 0:
                continue

            if cluster_id > 65535:
                raise RuntimeError(
                    f"Cluster ID exceeds uint16: "
                    f"{cluster_id}"
                )

            cluster_type = (
                row[indices["Type"]]
                .strip()
                .lower()
            )

            type_code = CLUSTER_TYPES.get(
                cluster_type,
                0
            )

            flags = 0

            if safe_int(
                row[indices["isMerged"]]
            ) == 1:
                flags |= CLUSTER_FLAG_MERGED

            if safe_int(
                row[indices["isGMMMemb"]]
            ) == 1:
                flags |= CLUSTER_FLAG_GMM

            record = (
                cluster_id,
                type_code,
                flags,

                safe_int(row[indices["N"]]),
                safe_int(row[indices["Nt"]]),

                safe_float(row[indices["GLON"]]),
                safe_float(row[indices["GLAT"]]),

                safe_float(row[indices["r50pc"]]),
                safe_float(row[indices["rcpc"]]),
                safe_float(row[indices["rtpc"]]),
                safe_float(row[indices["rtotpc"]]),

                safe_float(row[indices["pmRA"]]),
                safe_float(row[indices["pmDE"]]),
                safe_float(row[indices["Plx"]]),

                safe_float(row[indices["dist16"]]),
                safe_float(row[indices["dist50"]]),
                safe_float(row[indices["dist84"]]),

                safe_float(row[indices["X"]]),
                safe_float(row[indices["Y"]]),
                safe_float(row[indices["Z"]]),

                safe_float(row[indices["RV"]]),

                safe_float(row[indices["logAge16"]]),
                safe_float(row[indices["logAge50"]]),
                safe_float(row[indices["logAge84"]]),

                safe_float(row[indices["AV16"]]),
                safe_float(row[indices["AV50"]]),
                safe_float(row[indices["AV84"]]),

                safe_float(row[indices["r50Jpc"]]),
                safe_float(row[indices["rJpc"]]),
                safe_float(row[indices["probJ"]]),

                safe_int(row[indices["NJ"]]),

                safe_float(row[indices["MassJ"]]),
                safe_float(row[indices["e_MassJ"]]),
                safe_float(row[indices["MassTot"]]),
                safe_float(row[indices["e_MassTot"]]),

                safe_float(row[indices["minClSize"]]),
            )

            records.append(record)

            names.append(
                row[indices["Name"]].strip()
            )

    if len(records) != EXPECTED_CLUSTERS:
        raise RuntimeError(
            f"Cluster row count mismatch.\n"
            f"Expected: {EXPECTED_CLUSTERS:,}\n"
            f"Found:    {len(records):,}"
        )

    cluster_array = np.array(
        records,
        dtype=CLUSTER_DTYPE
    )

    del records

    # --------------------------------------------------------
    # Sort by cluster ID
    # --------------------------------------------------------

    order = np.argsort(
        cluster_array["id"],
        kind="stable"
    )

    cluster_array = cluster_array[order]

    names = [
        names[i]
        for i in order
    ]

    del order

    # --------------------------------------------------------
    # Check duplicate IDs
    # --------------------------------------------------------

    if len(cluster_array) > 1:

        duplicate_mask = (
            cluster_array["id"][1:]
            ==
            cluster_array["id"][:-1]
        )

        if np.any(duplicate_mask):

            duplicate_id = int(
                cluster_array["id"][1:][
                    duplicate_mask
                ][0]
            )

            raise RuntimeError(
                f"Duplicate cluster ID: "
                f"{duplicate_id}"
            )

    # --------------------------------------------------------
    # Write binary
    # --------------------------------------------------------

    output_path = (
        OUTPUT_DIR /
        "clusters.bin"
    )

    with output_path.open(
        "wb"
    ) as f:

        f.write(
            CLUSTER_HEADER.pack(
                CLUSTER_MAGIC,
                CLUSTER_VERSION,
                CLUSTER_RECORD_SIZE,
                len(cluster_array)
            )
        )

        cluster_array.tofile(f)

    print(
        f"Clusters: {len(cluster_array):,}"
    )

    print(
        f"Cluster record size: "
        f"{CLUSTER_RECORD_SIZE} bytes"
    )

    print(
        f"Wrote: {output_path}"
    )

    return cluster_array, names


# ============================================================
# Process memberships
# ============================================================

def process_members():

    print()
    print("======================================")
    print("Processing cluster memberships")
    print("======================================")
    print()

    if not MEMBERS_FILE.exists():

        raise FileNotFoundError(
            f"Missing:\n{MEMBERS_FILE}"
        )

    # --------------------------------------------------------
    # Read header
    # --------------------------------------------------------

    with MEMBERS_FILE.open(
        "r",
        encoding="utf-8"
    ) as f:

        reader = csv.reader(
            f,
            delimiter="\t"
        )

        header = read_vizier_header(
            reader
        )

    required = [
        "Seq",
        "Name",
        "ID",
        "GaiaDR3",
        "inrj",
        "inrt",
        "Prob",
    ]

    indices = get_column_indices(
        header,
        required
    )

    # --------------------------------------------------------
    # Preallocate using the known catalogue size.
    #
    # This avoids repeated Python list growth and keeps memory
    # predictable.
    # --------------------------------------------------------

    source_ids = np.empty(
        EXPECTED_MEMBERS,
        dtype="<u8"
    )

    cluster_ids = np.empty(
        EXPECTED_MEMBERS,
        dtype="<u2"
    )

    flags = np.empty(
        EXPECTED_MEMBERS,
        dtype="<u2"
    )

    probabilities = np.empty(
        EXPECTED_MEMBERS,
        dtype="<f4"
    )

    count = 0

    # --------------------------------------------------------
    # Read memberships
    # --------------------------------------------------------

    with MEMBERS_FILE.open(
        "r",
        encoding="utf-8"
    ) as f:

        reader = csv.reader(
            f,
            delimiter="\t"
        )

        read_vizier_header(
            reader
        )

        for row in reader:

            if not row:
                continue

            if row[0].lstrip().startswith("#"):
                continue

            if len(row) < len(header):
                continue

            cluster_id = safe_int(
                row[indices["ID"]]
            )

            source_id = safe_int(
                row[indices["GaiaDR3"]]
            )

            if cluster_id < 0:
                continue

            if cluster_id > 65535:
                raise RuntimeError(
                    f"Cluster ID exceeds uint16: "
                    f"{cluster_id}"
                )

            if source_id < 0:
                continue

            if count >= EXPECTED_MEMBERS:
                raise RuntimeError(
                    "More membership records were found "
                    "than expected."
                )

            member_flags = 0

            if safe_int(
                row[indices["inrj"]]
            ) == 1:
                member_flags |= MEMBER_FLAG_INRJ

            if safe_int(
                row[indices["inrt"]]
            ) == 1:
                member_flags |= MEMBER_FLAG_INRT

            source_ids[count] = source_id
            cluster_ids[count] = cluster_id
            flags[count] = member_flags
            probabilities[count] = (
                safe_float(
                    row[indices["Prob"]]
                )
            )

            count += 1

    if count != EXPECTED_MEMBERS:
        raise RuntimeError(
            f"Membership row count mismatch.\n"
            f"Expected: {EXPECTED_MEMBERS:,}\n"
            f"Found:    {count:,}"
        )

    print(
        f"Membership rows: {count:,}"
    )

    # --------------------------------------------------------
    # Sort by Gaia source_id.
    #
    # This is the important runtime optimization:
    #
    # all memberships for the same star are contiguous.
    # --------------------------------------------------------

    print(
        "Sorting memberships by Gaia source_id..."
    )

    order = np.lexsort(
        (
            cluster_ids,
            source_ids,
        )
    )

    members = np.empty(
        count,
        dtype=MEMBER_DTYPE
    )

    members["source_id"] = (
        source_ids[order]
    )

    members["cluster_id"] = (
        cluster_ids[order]
    )

    members["flags"] = (
        flags[order]
    )

    members["probability"] = (
        probabilities[order]
    )

    del source_ids
    del cluster_ids
    del flags
    del probabilities
    del order

    # --------------------------------------------------------
    # Write binary
    # --------------------------------------------------------

    output_path = (
        OUTPUT_DIR /
        "members.bin"
    )

    with output_path.open(
        "wb"
    ) as f:

        f.write(
            MEMBER_HEADER.pack(
                MEMBER_MAGIC,
                MEMBER_VERSION,
                MEMBER_RECORD_SIZE,
                count
            )
        )

        members.tofile(f)

    print(
        f"Membership record size: "
        f"{MEMBER_RECORD_SIZE} bytes"
    )

    print(
        f"Wrote: {output_path}"
    )

    return members


# ============================================================
# Cluster names
# ============================================================

def write_cluster_names(
    clusters,
    names
):

    output_path = (
        OUTPUT_DIR /
        "cluster_names.json"
    )

    result = {}

    for cluster_id, name in zip(
        clusters["id"],
        names
    ):

        result[str(int(cluster_id))] = name

    with output_path.open(
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            result,
            f,
            indent=2,
            ensure_ascii=False
        )

    print(
        f"Wrote: {output_path}"
    )


# ============================================================
# Summary
# ============================================================

def write_summary(
    clusters,
    members
):

    unique_source_count = (
        np.unique(
            members["source_id"]
        ).size
    )

    reverse_types = {
        value: key
        for key, value
        in CLUSTER_TYPES.items()
    }

    type_counts = {}

    for value in clusters["type"]:

        type_name = reverse_types.get(
            int(value),
            "unknown"
        )

        type_counts[type_name] = (
            type_counts.get(
                type_name,
                0
            )
            + 1
        )

    summary = {
        "version": 1,

        "clusters": int(
            len(clusters)
        ),

        "memberships": int(
            len(members)
        ),

        "unique_member_stars": int(
            unique_source_count
        ),

        "cluster_record_bytes":
            CLUSTER_RECORD_SIZE,

        "membership_record_bytes":
            MEMBER_RECORD_SIZE,

        "membership_sorted_by":
            "GaiaDR3/source_id",

        "cluster_types":
            type_counts,
    }

    path = (
        OUTPUT_DIR /
        "summary.json"
    )

    with path.open(
        "w",
        encoding="utf-8"
    ) as f:

        json.dump(
            summary,
            f,
            indent=2
        )

    print(
        f"Wrote: {path}"
    )


# ============================================================
# Main
# ============================================================

def main():

    clusters, names = (
        process_clusters()
    )

    members = (
        process_members()
    )

    write_cluster_names(
        clusters,
        names
    )

    write_summary(
        clusters,
        members
    )

    print()
    print("======================================")
    print("Cluster processing complete")
    print("======================================")
    print()

    print(
        f"Output directory:\n{OUTPUT_DIR}"
    )


if __name__ == "__main__":
    main()