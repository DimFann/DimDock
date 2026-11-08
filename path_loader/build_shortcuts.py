import os
import json
import mysql.connector
import logging

# 🔧 Configuration
logging.basicConfig(level=logging.ERROR, format='%(levelname)s: %(message)s')
DB_HOST = os.environ['DB_HOST']
DB_USER = os.environ['DB_USER']
DB_PASSWORD = os.environ['DB_PASSWORD']
DB_NAME = os.environ['DB_NAME']
ROOT_DIR = '/img_src'

def process_shortcuts(cursor):
    shortcuts_path = os.path.join(ROOT_DIR, "shortcuts.json")
    if not os.path.isfile(shortcuts_path):
        logging.info("No shortcuts.json found, skipping shortcut processing.")
        return

    try:
        with open(shortcuts_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except Exception as e:
        logging.error("Error reading shortcuts.json: %s", e)
        return

    shortcuts = data.get("shortcuts", {})
    inserted = 0

    for shortcut_path, source_files in shortcuts.items():
        if not isinstance(source_files, list):
            logging.error("Skipping %s: value must be a list of file paths", shortcut_path)
            continue

        for full_rel_path in source_files:
            parent_path, original_name = os.path.split(full_rel_path)
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
