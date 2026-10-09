from __future__ import annotations

import csv
import struct
from pathlib import Path


# Binary format:
# Header: 8-byte magic, uint32 version, uint32 record count.
# Record: uint16 name length, uint16 type length, uint16 short-description
# length, uint32 paragraph-description length, then float32 Unity X/Y/Z;
# followed by the UTF-8 strings in that same order.
MAGIC = b"LMARK001"
VERSION = 1
HEADER = struct.Struct("<8sII")
RECORD_HEADER = struct.Struct("<HHHIfff")

INPUT_RELATIVE = Path("DataRaw") / "Landmarks" / "local_neighborhood_landmarks.csv"
OUTPUT_RELATIVE = Path("Data") / "Landmarks" / "landmarks.bin"

REQUIRED_COLUMNS = (
    "name",
    "type",
    "x_pc",
    "y_pc",
    "z_pc",
    "short_description",
    "paragraph_description",
)


def find_project_root() -> Path:
    """Find a project root from this script's ancestors or the current directory."""
    candidates = [Path.cwd(), Path(__file__).resolve().parent, *Path(__file__).resolve().parents]
    seen: set[Path] = set()
    for candidate in candidates:
        candidate = candidate.resolve()
        if candidate in seen:
            continue
        seen.add(candidate)
        if (candidate / INPUT_RELATIVE).is_file():
            return candidate
    raise FileNotFoundError(
        f"Could not locate {INPUT_RELATIVE}. Run this script from the Unity project "
        "or place it somewhere inside the project."
    )


def encode_string(value: str, field: str, max_length: int) -> bytes:
    encoded = (value or "").encode("utf-8")
    if len(encoded) > max_length:
        raise ValueError(
            f"{field} is {len(encoded):,} UTF-8 bytes; maximum is {max_length:,}."
        )
    return encoded


def parse_float(row: dict[str, str], column: str, line_number: int) -> float:
    raw = (row.get(column) or "").strip()
    try:
        value = float(raw)
    except ValueError as exc:
        raise ValueError(
            f"CSV line {line_number}: invalid {column} value {raw!r}."
        ) from exc
    if not (-float("inf") < value < float("inf")):
        raise ValueError(
            f"CSV line {line_number}: {column} must be finite, got {raw!r}."
        )
    return value


def convert(project_root: Path) -> tuple[Path, int]:
    input_path = project_root / INPUT_RELATIVE
    output_path = project_root / OUTPUT_RELATIVE

    if not input_path.is_file():
        raise FileNotFoundError(f"Landmarks CSV not found: {input_path}")

    output_path.parent.mkdir(parents=True, exist_ok=True)

    temporary_path = output_path.with_suffix(output_path.suffix + ".tmp")
    count = 0

    with input_path.open("r", encoding="utf-8-sig", newline="") as csv_file:
        reader = csv.DictReader(csv_file)
        if reader.fieldnames is None:
            raise ValueError(f"CSV has no header: {input_path}")

        # Normalize header names to make a UTF-8 BOM and stray spaces harmless.
        reader.fieldnames = [name.strip() if name else "" for name in reader.fieldnames]
        missing = [column for column in REQUIRED_COLUMNS if column not in reader.fieldnames]
        if missing:
            raise ValueError(
                "CSV is missing required columns: " + ", ".join(missing)
                + f"\nFound: {reader.fieldnames}"
            )

        with temporary_path.open("wb") as output:
            # Reserve the header; record count is patched after all rows are read.
            output.write(HEADER.pack(MAGIC, VERSION, 0))

            for line_number, row in enumerate(reader, start=2):
                if not row or all(value is None or not value.strip() for value in row.values()):
                    continue

                name = (row.get("name") or "").strip()
                landmark_type = (row.get("type") or "").strip()
                short_description = (row.get("short_description") or "").strip()
                paragraph_description = (row.get("paragraph_description") or "").strip()

                if not name:
                    raise ValueError(f"CSV line {line_number}: landmark name is empty.")

                # Input XYZ is standard Sun-centered Galactic Cartesian, pc.
                # Output XYZ matches the Unity world axes (Unity Y is up):
                # Unity X = Galactic X; Unity Y = Galactic Z; Unity Z = -Galactic Y.
                gal_x = parse_float(row, "x_pc", line_number)
                gal_y = parse_float(row, "y_pc", line_number)
                gal_z = parse_float(row, "z_pc", line_number)
                unity_x, unity_y, unity_z = gal_x, gal_z, -gal_y

                name_bytes = encode_string(name, "name", 0xFFFF)
                type_bytes = encode_string(landmark_type, "type", 0xFFFF)
                short_bytes = encode_string(short_description, "short_description", 0xFFFF)
                paragraph_bytes = encode_string(
                    paragraph_description, "paragraph_description", 0xFFFFFFFF
                )

                output.write(
                    RECORD_HEADER.pack(
                        len(name_bytes),
                        len(type_bytes),
                        len(short_bytes),
                        len(paragraph_bytes),
                        unity_x,
                        unity_y,
                        unity_z,
                    )
                )
                output.write(name_bytes)
                output.write(type_bytes)
                output.write(short_bytes)
                output.write(paragraph_bytes)
                count += 1

            output.seek(0)
            output.write(HEADER.pack(MAGIC, VERSION, count))

    temporary_path.replace(output_path)

    return output_path, count


def main() -> None:
    root = find_project_root()
    output_path, count = convert(root)
    print(f"Landmarks written: {count:,}")
    print(f"Output:            {output_path}")
    print("Coordinates: Unity X=Galactic X, Unity Y=Galactic Z, Unity Z=-Galactic Y")


if __name__ == "__main__":
    main()
