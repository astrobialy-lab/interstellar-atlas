from pathlib import Path

PROJECT_DIR = Path(__file__).resolve().parents[2]

ARCHIVE_PREFIX = "Data_QuestTransfer_"
MANIFEST_PATH = PROJECT_DIR / "Data_QuestTransfer_manifest.json"
LEGACY_ARCHIVE = PROJECT_DIR / "Data_QuestTransfer.tar"


def main():
    targets = list(PROJECT_DIR.glob(f"{ARCHIVE_PREFIX}*.tar"))
    targets += list(PROJECT_DIR.glob(f"{ARCHIVE_PREFIX}*.tar.part"))
    targets += [
        MANIFEST_PATH,
        MANIFEST_PATH.with_suffix(MANIFEST_PATH.suffix + ".part"),
        LEGACY_ARCHIVE,
    ]

    # Deduplicate paths and delete only existing files.
    targets = sorted(set(targets))

    existing = [path for path in targets if path.is_file()]

    if not existing:
        print("No generated TAR files or manifest found.")
        return

    print(f"Found {len(existing)} files to delete:")
    for path in existing:
        print(f"  {path.name}")

    for path in existing:
        path.unlink()
        print(f"Deleted: {path.name}")

    print(f"\nDeleted {len(existing)} files.")


if __name__ == "__main__":
    main()