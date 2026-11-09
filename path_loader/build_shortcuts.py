import os
import json
import mysql.connector
import logging

from file_store import create_file_store

# 🔧 Configuration
logging.basicConfig(level=logging.ERROR, format='%(levelname)s: %(message)s')
DB_HOST = os.environ['DB_HOST']
DB_USER = os.environ['DB_USER']
DB_PASSWORD = os.environ['DB_PASSWORD']
DB_NAME = os.environ['DB_NAME']
DATA_ROOT = os.environ.get('DIMDOCK_DATA_ROOT', '/data')
STORE = create_file_store()
SHORTCUTS_FILENAME = '.shortcuts.json'
LEGACY_SHORTCUTS_FILENAME = 'shortcuts.json'

def process_shortcuts(cursor):
    if not STORE.exists('gallery', SHORTCUTS_FILENAME) and STORE.exists('gallery', LEGACY_SHORTCUTS_FILENAME):
        STORE.move_file('gallery', LEGACY_SHORTCUTS_FILENAME, SHORTCUTS_FILENAME)
    if not STORE.exists('gallery', SHORTCUTS_FILENAME):
        logging.info("No %s found, skipping shortcut processing.", SHORTCUTS_FILENAME)
        return

    try:
        data = json.loads(STORE.read_text('gallery', SHORTCUTS_FILENAME))
    except Exception as e:
        logging.error("Error reading %s: %s", SHORTCUTS_FILENAME, e)
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
            logging.info("Inserted shortcut: %s → %s", shortcut_path, full_rel_path)
            inserted += 1

    logging.info("Finished inserting %s shortcuts.", inserted)

def main():
    try:
        conn = mysql.connector.connect(
            host=DB_HOST,
            user=DB_USER,
            password=DB_PASSWORD,
            database=DB_NAME
        )
        cursor = conn.cursor()
        process_shortcuts(cursor)
        conn.commit()
        cursor.close()
        conn.close()
    except Exception:
        logging.exception("Fatal error occurred during shortcut processing")

if __name__ == "__main__":
    main()
