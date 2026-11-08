import os
import datetime
import hashlib
import mysql.connector
from concurrent.futures import ThreadPoolExecutor, as_completed
from PIL import Image
import re
import json
import unicodedata
import logging

# 🔧 Configuration
logging.basicConfig(level=logging.ERROR, format='%(levelname)s: %(message)s')
MAX_THUMBNAIL_SIZE = (256, 256)
MAX_WORKERS = 8

# 📁 Paths and extensions
DB_HOST = os.environ['DB_HOST']
DB_USER = os.environ['DB_USER']
DB_PASSWORD = os.environ['DB_PASSWORD']
DB_NAME = os.environ['DB_NAME']
ROOT_DIR = '/img_src'
THUMB_DIR = '/img_db_thumbs'
BLOG_DIR = '/blogs'
ABOUT_FILE = '/about/about.md'
BLOGS_INITIALIZED_FILE = '/app_state/.blogs_initialized'
TAG_MANIFEST_FILENAME = '.dimdock.json'
TAG_NAME_MAX_LENGTH = 100
VALID_EXTENSIONS = {'.jpg', '.jpeg', '.png', '.gif'}
BLOG_FILENAME_PATTERN = re.compile(r'^([A-Z]{2,5})_(\d{8})_(\d{6})\.md$')
BLOG_TIMEZONE_OFFSETS = {
    'PST': -8,
    'PDT': -7,
    'MST': -7,
    'MDT': -6,
    'CST': -6,
    'CDT': -5,
    'EST': -5,
    'EDT': -4,
    'UTC': 0,
    'GMT': 0,
}

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
    """, (path, parent, description, None, '{}'))
    return cursor.lastrowid

def normalize_tags(raw_tags, source):
    if not isinstance(raw_tags, list):
        raise ValueError(f"{source} must be a list of tag strings")

    tags = set()
    for raw_tag in raw_tags:
        if not isinstance(raw_tag, str):
            raise ValueError(f"{source} contains a non-string tag")
        tag = raw_tag.strip().lower()
        if not tag:
            continue
        if len(tag) > TAG_NAME_MAX_LENGTH or ',' in tag or any(ord(char) < 32 for char in tag):
            raise ValueError(f"Invalid tag in {source}: {raw_tag!r}")
        tags.add(tag)
    return sorted(tags)

def date_path_tags(path):
    tags = set()
    for segment in path.split('/'):
        if re.fullmatch(r'\d{4}', segment):
            tags.add(segment)
        elif re.fullmatch(r'\d{4}-\d{2}', segment):
            year, month = segment.split('-')
            if 1 <= int(month) <= 12:
                tags.add(year)
                tags.add(segment)
    return sorted(tags)

def read_tag_manifest(directory):
    manifest_path = os.path.join(directory, TAG_MANIFEST_FILENAME)
    if not os.path.isfile(manifest_path):
        return {'type': None, 'tags': [], 'items': {}}

    try:
        with open(manifest_path, 'r', encoding='utf-8') as manifest_file:
            manifest = json.load(manifest_file)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Unable to read tag manifest {manifest_path}: {error}") from error

    if not isinstance(manifest, dict) or type(manifest.get('version')) is not int or manifest['version'] != 1:
        raise ValueError(f"Unsupported or missing version in tag manifest {manifest_path}")

    directory_tags = normalize_tags(manifest.get('tags', []), f"{manifest_path}: tags")
    raw_type = manifest.get('type')
    if raw_type is not None and not isinstance(raw_type, str):
        raise ValueError(f"{manifest_path}: type must be a string or null")
    path_type = raw_type.strip().lower() if raw_type else None
    if path_type not in (None, 'comic'):
        raise ValueError(f"Unsupported directory type in {manifest_path}: {raw_type!r}")

    raw_items = manifest.get('items', {})
    if not isinstance(raw_items, dict):
        raise ValueError(f"{manifest_path}: items must be an object")

    items = {}
    for filename, item_metadata in raw_items.items():
        if (not isinstance(filename, str) or not filename or filename in ('.', '..') or
                '/' in filename or '\\' in filename):
            raise ValueError(f"Invalid item filename in tag manifest {manifest_path}: {filename!r}")
        if not isinstance(item_metadata, dict):
            raise ValueError(f"{manifest_path}: metadata for {filename!r} must be an object")
        items[filename] = normalize_tags(item_metadata.get('tags', []), f"{manifest_path}: {filename!r} tags")
    return {'type': path_type, 'tags': directory_tags, 'items': items}

def replace_entity_tags(cursor, table, entity_column, entity_id, tag_names):
    cursor.execute(f"DELETE FROM {table} WHERE {entity_column} = %s", (entity_id,))
    for tag_name in tag_names:
        cursor.execute("INSERT IGNORE INTO Tags (name) VALUES (%s)", (tag_name,))
        cursor.execute("SELECT id FROM Tags WHERE name = %s", (tag_name,))
        tag_id = cursor.fetchone()[0]
        cursor.execute(
            f"INSERT INTO {table} ({entity_column}, tag_id) VALUES (%s, %s)",
            (entity_id, tag_id)
        )

def get_entity_tags(cursor, table, entity_column, entity_id):
    cursor.execute(f"""
        SELECT Tags.name
        FROM {table}
        JOIN Tags ON Tags.id = {table}.tag_id
        WHERE {table}.{entity_column} = %s
    """, (entity_id,))
    return {row[0] for row in cursor.fetchall()}

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

def create_thumbnail_key(parent_path, original_name):
    normalized_path = original_name if parent_path == "root" else f"{parent_path}/{original_name}"
    normalized_path = normalized_path.replace(os.sep, '/')
    return hashlib.sha256(normalized_path.encode('utf-8')).hexdigest()

def insert_item(cursor, original_name, parent_path, thumbnail_key):
    now = datetime.datetime.now()
    created_date = extract_date_from_filename_or_path(original_name, parent_path)

    cursor.execute("""
        INSERT INTO Items (original_name, parent_path, thumbnail_key, uploaded, created, display_index, type, metadata)
        VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
    """, (original_name, parent_path, thumbnail_key, now, created_date, None, 'image', '{}'))
    return cursor.lastrowid

def extract_blog_title(text):
    first_line = text.splitlines()[0] if text else ''
    match = re.match(r'^ {0,3}#(?!#)[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$', first_line)
    if match:
        title = match.group(1).strip()
        return title or None
    return None

def create_blog_alias(title, timestamp, used_aliases):
    if title:
        normalized_title = unicodedata.normalize('NFKD', title)
        ascii_title = normalized_title.encode('ascii', 'ignore').decode('ascii').lower()
        alias = re.sub(r'[^a-z0-9]+', '-', ascii_title).strip('-')
    else:
        alias = ''

    if not alias:
        alias = (
            f"{timestamp.strftime('%b').lower()}-{timestamp.day}-"
            f"{timestamp.year}-{timestamp:%H%M%S}-utc"
        )

    base_alias = alias
    suffix = 2
    while alias in used_aliases:
        alias = f"{base_alias}-{suffix}"
        suffix += 1
    used_aliases.add(alias)
    return alias

def process_blogs(cursor):
    if os.path.exists(BLOGS_INITIALIZED_FILE):
        logging.info("Blogs already initialized. Skipping blog import.")
        return 0

    if not os.path.isdir(BLOG_DIR):
        raise FileNotFoundError(f"Blogs directory not found: {BLOG_DIR}")

    cursor.execute("DROP TABLE IF EXISTS Blogs")
    cursor.execute("""
        CREATE TABLE Blogs (
            id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
            source_filename VARCHAR(255) NOT NULL,
            alias VARCHAR(255) NOT NULL,
            title VARCHAR(1024),
            timestamp DATETIME,
            `text` TEXT,
            UNIQUE KEY uq_blogs_source_filename (source_filename),
            UNIQUE KEY uq_blogs_alias (alias)
        )
    """)

    inserted_blogs = 0
    used_aliases = set()
    for filename in sorted(os.listdir(BLOG_DIR)):
        match = BLOG_FILENAME_PATTERN.fullmatch(filename)
        if not match:
            continue

        timezone_abbrev, date_part, time_part = match.groups()
        offset_hours = BLOG_TIMEZONE_OFFSETS.get(timezone_abbrev)
        if offset_hours is None:
            logging.error("Unsupported timezone abbreviation in blog filename %s; skipping.", filename)
            continue

        try:
            local_timestamp = datetime.datetime.strptime(
                f"{date_part}_{time_part}", "%Y%m%d_%H%M%S"
            ).replace(tzinfo=datetime.timezone(datetime.timedelta(hours=offset_hours)))
            # MySQL DATETIME has no timezone, so store the UTC value as a naive datetime.
            utc_timestamp = local_timestamp.astimezone(datetime.timezone.utc).replace(tzinfo=None)
        except (OverflowError, ValueError) as e:
            logging.error("Invalid date in blog filename %s: %s; skipping.", filename, e)
            continue

        file_path = os.path.join(BLOG_DIR, filename)
        if not os.path.isfile(file_path):
            continue

        try:
            with open(file_path, "r", encoding="utf-8") as blog_file:
                blog_text = blog_file.read()
        except (OSError, UnicodeError) as e:
            logging.error("Error reading blog file %s: %s; skipping.", file_path, e)
            continue

        title = extract_blog_title(blog_text)
        alias = create_blog_alias(title, utc_timestamp, used_aliases)
        cursor.execute("""
            INSERT INTO Blogs (source_filename, alias, title, timestamp, `text`)
            VALUES (%s, %s, %s, %s, %s)
        """, (filename, alias, title, utc_timestamp, blog_text))
        inserted_blogs += 1
        logging.info("Imported blog: %s", filename)

    return inserted_blogs

def process_about(cursor):
    try:
        with open(ABOUT_FILE, 'r', encoding='utf-8') as about_file:
            description = about_file.read()
    except (OSError, UnicodeError) as error:
        raise RuntimeError(f"Unable to load About content from {ABOUT_FILE}: {error}") from error

    cursor.execute("""
        INSERT INTO About (id, description)
        VALUES (1, %s)
        ON DUPLICATE KEY UPDATE description = VALUES(description)
    """, (description,))
    logging.info("Loaded About content from %s", ABOUT_FILE)

def process_image(file_path, thumbnail_key):
    logging.info("Generating thumbnail: %s", file_path)
    try:
        with Image.open(file_path) as img:
            img = img.convert("RGB")
            thumb = img.copy()
            thumb.thumbnail(MAX_THUMBNAIL_SIZE)
            thumb_with_bg = Image.new("RGB", MAX_THUMBNAIL_SIZE, (255, 255, 255))
            thumb_with_bg.paste(
                thumb, ((MAX_THUMBNAIL_SIZE[0] - thumb.width) // 2, (MAX_THUMBNAIL_SIZE[1] - thumb.height) // 2)
            )
            thumb_path = os.path.join(THUMB_DIR, f"{thumbnail_key}.png")
            thumb_with_bg.save(thumb_path, format="PNG", optimize=True, quality=85)
    except Exception as e:
        logging.error("Error processing image %s", file_path, exc_info=e)

def traverse_and_insert(cursor):
    total_images = 0
    cursor.execute("SELECT path, id, type FROM Paths")
    path_rows = cursor.fetchall()
    existing_paths = {row[0]: row[1] for row in path_rows}
    existing_path_types = {row[0]: row[2] for row in path_rows}
    cursor.execute("SELECT id, parent_path, original_name, thumbnail_key FROM Items")
    existing_items = {
        (row[1], row[2]): {'id': row[0], 'thumbnail_key': row[3]}
        for row in cursor.fetchall()
    }
    existing_items_by_path = {}
    for (item_path, filename), item in existing_items.items():
        existing_items_by_path.setdefault(item_path, {})[filename] = item['id']

    for current_path, dirs, files in os.walk(ROOT_DIR):
        logging.info("Processing folder: %s", current_path)
        rel_path = os.path.relpath(current_path, ROOT_DIR)
        path = "root" if rel_path == "." else rel_path
        parent_dir = os.path.dirname(rel_path)
        parent = "root" if parent_dir in ("", ".") else parent_dir
        tag_manifest = read_tag_manifest(current_path)
        directory_name = os.path.basename(path)
        path_type = 'comic' if directory_name.casefold().startswith('comic_') else tag_manifest['type']
        path_tags = set(tag_manifest['tags'])
        if directory_name.casefold().startswith('wip_'):
            path_tags.add('hidden')

        # 🔍 Check for description.md
        description_path = os.path.join(current_path, "description.md")
        description = None
        if os.path.isfile(description_path):
            try:
                with open(description_path, "r", encoding="utf-8") as f:
                    description = f.read()
            except Exception as e:
                logging.error("Error reading description.md in %s: %s", current_path, e)

        if path not in existing_paths:
            existing_paths[path] = insert_path(cursor, path, parent, description)
            existing_path_types[path] = None
        path_id = existing_paths[path]
        if existing_path_types[path] != path_type:
            cursor.execute(
                "UPDATE Paths SET type = %s WHERE id = %s",
                (path_type, path_id)
            )
            existing_path_types[path] = path_type
        if get_entity_tags(cursor, 'PathTags', 'path_id', path_id) != path_tags:
            replace_entity_tags(cursor, 'PathTags', 'path_id', path_id, sorted(path_tags))

        image_tasks = []
        for file in files:
            ext = os.path.splitext(file)[1].lower()
            item_key = (path, file)
            if ext not in VALID_EXTENSIONS:
                continue

            thumbnail_key = create_thumbnail_key(path, file)
            if item_key not in existing_items:
                full_path = os.path.join(current_path, file)
                item_id = insert_item(cursor, file, path, thumbnail_key)
                existing_items[item_key] = {'id': item_id, 'thumbnail_key': thumbnail_key}
                existing_items_by_path.setdefault(path, {})[file] = item_id
                total_images += 1
            else:
                item = existing_items[item_key]
                item_id = item['id']
                if item['thumbnail_key'] != thumbnail_key:
                    cursor.execute(
                        "UPDATE Items SET thumbnail_key = %s WHERE id = %s",
                        (thumbnail_key, item_id)
                    )
                    item['thumbnail_key'] = thumbnail_key

            thumb_path = os.path.join(THUMB_DIR, f"{thumbnail_key}.png")
            if not os.path.isfile(thumb_path):
                image_tasks.append((os.path.join(current_path, file), thumbnail_key))

        directory_items = existing_items_by_path.get(path, {})
        cursor.execute("""
            SELECT ItemTags.item_id, Tags.name
            FROM ItemTags
            JOIN Tags ON Tags.id = ItemTags.tag_id
            JOIN Items ON Items.id = ItemTags.item_id
            WHERE Items.parent_path = %s
        """, (path,))
        item_tags_by_id = {}
        for item_id, tag_name in cursor.fetchall():
            item_tags_by_id.setdefault(item_id, set()).add(tag_name)

        for filename, item_id in directory_items.items():
            desired_tags = sorted(
                set(tag_manifest['items'].get(filename, [])) | set(date_path_tags(path))
            )
            if item_tags_by_id.get(item_id, set()) != set(desired_tags):
                replace_entity_tags(cursor, 'ItemTags', 'item_id', item_id, desired_tags)

        for filename in tag_manifest['items']:
            if filename not in directory_items:
                logging.error(
                    "Tag manifest entry has no indexed item in %s: %s",
                    current_path,
                    filename
                )

        for filename, item_id in directory_items.items():
            explicit_tags = tag_manifest['items'].get(filename, [])
            auto_tags = date_path_tags(path)
            for tag_name in explicit_tags:
                cursor.execute("INSERT IGNORE INTO Tags (name) VALUES (%s)", (tag_name,))
            for tag_name in auto_tags:
                cursor.execute("INSERT IGNORE INTO Tags (name) VALUES (%s)", (tag_name,))

        with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
            futures = [
                executor.submit(process_image, file_path, thumbnail_key)
                for file_path, thumbnail_key in image_tasks
            ]
            for future in as_completed(futures):
                try:
                    future.result()
                except Exception as e:
                    logging.error("Unhandled thumbnail worker error: %s", e, exc_info=e)

        logging.info("Finished processing folder: %s", current_path)

    return total_images

def main():
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
        total_images = traverse_and_insert(cursor)
        inserted_blogs = process_blogs(cursor)
        process_about(cursor)
        # process_shortcuts(cursor)
        conn.commit()
        logging.info(
            "Finished scanning: %s new images, %s new blogs.",
            total_images,
            inserted_blogs
        )
    except Exception:
        if conn is not None:
            conn.rollback()
        logging.exception("Fatal error occurred")
        raise
    finally:
        if cursor is not None:
            cursor.close()
        if conn is not None:
            conn.close()

if __name__ == "__main__":
    main()
