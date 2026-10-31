import os
import datetime
import mysql.connector
from concurrent.futures import ThreadPoolExecutor, as_completed
from PIL import Image
import traceback
import re
import json

# 🔧 Configuration
MAX_THUMBNAIL_SIZE = (256, 256)
MAX_WORKERS = 8

# 📁 Paths and extensions
DB_HOST = os.environ['DB_HOST']
DB_USER = os.environ['DB_USER']
DB_PASSWORD = os.environ['DB_PASSWORD']
DB_NAME = os.environ['DB_NAME']
ROOT_DIR = '/img_src'
THUMB_DIR = '/img_db_thumbs'
VALID_EXTENSIONS = {'.jpg', '.jpeg', '.png', '.gif'}

# def process_shortcuts(cursor):
#     shortcuts_path = os.path.join(ROOT_DIR, "shortcuts.json")
#     if not os.path.isfile(shortcuts_path):
#         print("No shortcuts.json found, skipping shortcut processing.", flush=True)
#         return

#     try:
#         with open(shortcuts_path, "r", encoding="utf-8") as f:
#             data = json.load(f)
#     except Exception as e:
#         print(f"Error reading shortcuts.json: {e}", flush=True)
#         return

#     shortcuts = data.get("shortcuts", [])
#     for entry in shortcuts:
#         for full_rel_path, shortcut_target in entry.items():
#             parent_path, original_name = os.path.split(full_rel_path)
#             query = """
#                 SELECT id, original_name, parent_path
#                 FROM Items
#                 WHERE original_name = %s AND parent_path = %s
#                 LIMIT 1
#             """
#             cursor.execute(query, (original_name, parent_path))
#             result = cursor.fetchone()
#             if result:
#                 original_id, original_name_db, parent_path_db = result
#                 cursor.execute("""
#                     INSERT INTO Shortcuts (original_id, original_name, parent_path, shortcut_path)
#                     VALUES (%s, %s, %s, %s)
#                 """, (original_id, original_name_db, parent_path_db, shortcut_target))
#                 print(f"Inserted shortcut for {full_rel_path} → {shortcut_target}", flush=True)
#             else:
#                 print(f"Shortcut target not found in Items: {full_rel_path}", flush=True)

def insert_path(cursor, path, parent, description):
    cursor.execute("""
        INSERT INTO Paths (path, parent_path, description, type, metadata)
        VALUES (%s, %s, %s, %s, %s)
    """, (path, parent, description, 'directory', '{}'))

def extract_date_from_filename_or_path(filename, folder_path):
    now = datetime.datetime.now()
    current_year = now.year

    match = re.match(r'^(\d{4})[-]?(\d{2})(\d{2})', filename)
    if match:
        year, month, day = map(int, match.groups())
        if 2000 <= year <= current_year and 1 <= month <= 12 and 1 <= day <= 31:
            return datetime.datetime(year, month, day)

    match = re.search(r'(\d{4})-(\d{2})', folder_path)
    if match:
        year, month = map(int, match.groups())
        if 2000 <= year <= current_year and 1 <= month <= 12:
            return datetime.datetime(year, month, 1)

    match = re.search(r'(\d{4})', folder_path)
    if match:
        year = int(match.group(1))
        if 2000 <= year <= current_year:
            return datetime.datetime(year, 1, 1)

    return datetime.datetime(2000, 1, 1)

def insert_item(cursor, original_name, parent_path):
    now = datetime.datetime.now()
    created_date = extract_date_from_filename_or_path(original_name, parent_path)

    cursor.execute("""
        INSERT INTO Items (original_name, parent_path, uploaded, created, display_index, type, metadata)
        VALUES (%s, %s, %s, %s, %s, %s, %s)
    """, (original_name, parent_path, now, created_date, None, 'image', '{}'))
    return cursor.lastrowid

def process_image(file_path, item_id):
    print(f"Processing image: {file_path} → ID {item_id}", flush=True)
    try:
        with Image.open(file_path) as img:
            img = img.convert("RGB")
            thumb = img.copy()
            thumb.thumbnail(MAX_THUMBNAIL_SIZE)
            thumb_with_bg = Image.new("RGB", MAX_THUMBNAIL_SIZE, (255, 255, 255))
            thumb_with_bg.paste(
                thumb, ((MAX_THUMBNAIL_SIZE[0] - thumb.width) // 2, (MAX_THUMBNAIL_SIZE[1] - thumb.height) // 2)
            )
            thumb_path = os.path.join(THUMB_DIR, f"{item_id}.png")
            thumb_with_bg.save(thumb_path, format="PNG", optimize=True, quality=85)
    except Exception as e:
        print(f"Error processing image {file_path}: {e}", flush=True)
        traceback.print_exc()

def traverse_and_insert(cursor):
    total_images = 0
    for current_path, dirs, files in os.walk(ROOT_DIR):
        print(f"Processing folder: {current_path}", flush=True)
        rel_path = os.path.relpath(current_path, ROOT_DIR)
        path = "root" if rel_path == "." else rel_path
        parent_dir = os.path.dirname(rel_path)
        parent = "root" if parent_dir in ("", ".") else parent_dir

        # 🔍 Check for description.md
        description_path = os.path.join(current_path, "description.md")
        description = None
        if os.path.isfile(description_path):
            try:
                with open(description_path, "r", encoding="utf-8") as f:
                    description = f.read()
            except Exception as e:
                print(f"Error reading description.md in {current_path}: {e}", flush=True)

        insert_path(cursor, path, parent, description)

        image_tasks = []
        for file in files:
            ext = os.path.splitext(file)[1].lower()
            if ext in VALID_EXTENSIONS:
                full_path = os.path.join(current_path, file)
                item_id = insert_item(cursor, file, path)
                image_tasks.append((full_path, item_id))

        total_images += len(image_tasks)

        with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
            futures = [executor.submit(process_image, file_path, item_id)
                       for file_path, item_id in image_tasks]
            for future in as_completed(futures):
                try:
                    future.result()
                except Exception as e:
                    print(f"Unhandled thread error: {e}", flush=True)
                    traceback.print_exc()

        print(f"Finished processing folder: {current_path}", flush=True)

    return total_images

def main():
    try:
        conn = mysql.connector.connect(
            host=DB_HOST,
            user=DB_USER,
            password=DB_PASSWORD,
            database=DB_NAME
        )
        cursor = conn.cursor()
        total_images = traverse_and_insert(cursor)
        # process_shortcuts(cursor)
        conn.commit()
        cursor.close()
        conn.close()
        print(f"Finished processing {total_images} images.", flush=True)
    except Exception as e:
        print("Fatal error occurred:", flush=True)
        traceback.print_exc()

if __name__ == "__main__":
    main()
