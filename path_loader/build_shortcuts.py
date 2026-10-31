import os
import json
import mysql.connector
import traceback

# 🔧 Configuration
DB_HOST = os.environ['DB_HOST']
DB_USER = os.environ['DB_USER']
DB_PASSWORD = os.environ['DB_PASSWORD']
DB_NAME = os.environ['DB_NAME']
ROOT_DIR = '/img_src'

def process_shortcuts(cursor):
    shortcuts_path = os.path.join(ROOT_DIR, "shortcuts.json")
    if not os.path.isfile(shortcuts_path):
        print("No shortcuts.json found, skipping shortcut processing.", flush=True)
        return

    try:
        with open(shortcuts_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except Exception as e:
        print(f"Error reading shortcuts.json: {e}", flush=True)
        return

    shortcuts = data.get("shortcuts", {})
    inserted = 0

    for shortcut_path, source_files in shortcuts.items():
        if not isinstance(source_files, list):
            print(f"Skipping {shortcut_path}: value must be a list of file paths", flush=True)
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
                print(f"Item not found for shortcut: {full_rel_path}", flush=True)
                continue

            original_id, original_name_db, parent_path_db = result
            cursor.execute("""
                INSERT INTO Shortcuts (original_id, original_name, parent_path, shortcut_path)
                VALUES (%s, %s, %s, %s)
            """, (original_id, original_name_db, parent_path_db, shortcut_path))
            print(f"Inserted shortcut: {shortcut_path} → {full_rel_path}", flush=True)
            inserted += 1

    print(f"Finished inserting {inserted} shortcuts.", flush=True)

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
    except Exception as e:
        print("Fatal error occurred during shortcut processing:", flush=True)
        traceback.print_exc()

if __name__ == "__main__":
    main()
