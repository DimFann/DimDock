import os
import json
import mysql.connector
import logging
import argparse

from file_store import create_file_store, validate_key

# 🔧 Configuration
logging.basicConfig(level=logging.INFO, format='%(levelname)s: %(message)s')
DB_HOST = os.environ['DB_HOST']
DB_USER = os.environ['DB_USER']
DB_PASSWORD = os.environ['DB_PASSWORD']
DB_NAME = os.environ['DB_NAME']
DATA_ROOT = os.environ.get('DIMDOCK_DATA_ROOT', '/data')
STORE = create_file_store()
SHORTCUTS_FILENAME = '.shortcuts.json'
LEGACY_SHORTCUTS_FILENAME = 'shortcuts.json'

def ensure_directory_shortcuts_table(cursor):
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS DirectoryShortcuts (
            id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
            target_path_id BIGINT UNSIGNED NOT NULL,
            parent_path_id BIGINT UNSIGNED NOT NULL,
            name VARCHAR(255) NOT NULL,
            CONSTRAINT fk_directory_shortcuts_target
                FOREIGN KEY (target_path_id) REFERENCES Paths(id) ON DELETE CASCADE,
            CONSTRAINT fk_directory_shortcuts_parent
                FOREIGN KEY (parent_path_id) REFERENCES Paths(id) ON DELETE CASCADE
        )
    """)


def process_directory_shortcuts(cursor, data):
    directories = data.get('directory_shortcuts', {})
    if not isinstance(directories, dict):
        raise ValueError('directory_shortcuts must be an object mapping destination paths to lists.')
    records = []
    seen = set()
    for parent_path, entries in directories.items():
        validate_key(parent_path, allow_root=False)
        if len(parent_path) > 1024 or not isinstance(entries, list):
            raise ValueError(f'Invalid directory shortcut destination: {parent_path!r}')
        cursor.execute("SELECT id FROM Paths WHERE path = %s", (parent_path,))
        parent = cursor.fetchone()
        if parent is None:
            raise ValueError(f'Directory shortcut destination is not indexed: {parent_path}')
        for entry in entries:
            if not isinstance(entry, dict):
                raise ValueError(f'Directory shortcut in {parent_path} must be an object.')
            name, target = entry.get('name'), entry.get('target_path')
            validate_key(name, allow_root=False)
            validate_key(target, allow_root=False)
            if '/' in name or len(name) > 255 or len(target) > 1024:
                raise ValueError(f'Invalid directory shortcut in {parent_path}: {entry!r}')
            identity = (parent_path.casefold(), name.casefold())
            if identity in seen:
                raise ValueError(f'Duplicate directory shortcut name in {parent_path}: {name}')
            seen.add(identity)
            cursor.execute("SELECT id FROM Paths WHERE path = %s", (target,))
            original = cursor.fetchone()
            if original is None:
                raise ValueError(f'Directory shortcut target is not indexed: {target}')
            records.append((original[0], parent[0], name))
    # Validate and resolve all paths before replacing the directory shortcut index.
    cursor.execute("DELETE FROM DirectoryShortcuts")
    for record in records:
        cursor.execute("""
            INSERT INTO DirectoryShortcuts (target_path_id, parent_path_id, name)
            VALUES (%s, %s, %s)
        """, record)
    logging.info("Loaded %s directory shortcuts (pending commit).", len(records))


def process_shortcuts(cursor, directories_only=False):
    logging.info(
        "Loading %s from gallery/%s using %s storage.",
        "directory shortcuts" if directories_only else "file and directory shortcuts",
        SHORTCUTS_FILENAME,
        os.environ.get('FILE_STORAGE_PROVIDER', 'Local'),
    )
    ensure_directory_shortcuts_table(cursor)
    if not STORE.exists('gallery', SHORTCUTS_FILENAME) and STORE.exists('gallery', LEGACY_SHORTCUTS_FILENAME):
        STORE.move_file('gallery', LEGACY_SHORTCUTS_FILENAME, SHORTCUTS_FILENAME)
    if not STORE.exists('gallery', SHORTCUTS_FILENAME):
        logging.info("No %s found, skipping shortcut processing.", SHORTCUTS_FILENAME)
        return

    data = json.loads(STORE.read_text('gallery', SHORTCUTS_FILENAME))
    if not isinstance(data, dict):
        raise ValueError('Shortcut configuration must be a JSON object.')
    process_directory_shortcuts(cursor, data)
    if directories_only:
        return

    shortcuts = data.get("shortcuts", {})
    inserted = 0

    for shortcut_path, source_files in shortcuts.items():
        if not isinstance(source_files, list):
            logging.error("Skipping %s: value must be a list of file paths", shortcut_path)
            continue

        for full_rel_path in source_files:
            parent_path, _, original_name = full_rel_path.rpartition('/')
            query = """
                SELECT id, original_name, parent_path
                FROM Items
                WHERE original_name = %s AND parent_path = %s
                LIMIT 1
            """
            cursor.execute(query, (original_name, parent_path))
            result = cursor.fetchone()
            if not result:
                logging.error("Item not found for shortcut: %s", full_rel_path)
                continue

            original_id, original_name_db, parent_path_db = result
            cursor.execute("""
                INSERT INTO Shortcuts (original_id, original_name, parent_path, shortcut_path)
                VALUES (%s, %s, %s, %s)
            """, (original_id, original_name_db, parent_path_db, shortcut_path))
            logging.debug("Inserted shortcut: %s → %s", shortcut_path, full_rel_path)
            inserted += 1

    logging.info("Loaded %s file shortcuts (pending commit).", inserted)

def main():
    parser = argparse.ArgumentParser(description='Import indexed gallery shortcuts.')
    parser.add_argument('--directories-only', action='store_true')
    args = parser.parse_args()
    conn = None
    cursor = None
    try:
        conn = mysql.connector.connect(
            host=DB_HOST,
            user=DB_USER,
            password=DB_PASSWORD,
            database=DB_NAME
        )
        cursor = conn.cursor()
        process_shortcuts(cursor, directories_only=args.directories_only)
        conn.commit()
        logging.info("Shortcut loading committed successfully.")
    except Exception:
        if conn is not None:
            conn.rollback()
        logging.exception("Fatal error occurred during shortcut processing")
        raise
    finally:
        if cursor is not None:
            cursor.close()
        if conn is not None:
            conn.close()

if __name__ == "__main__":
    main()
