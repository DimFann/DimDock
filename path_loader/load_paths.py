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
import io
import time
import statistics

from file_store import create_file_store

# 🔧 Configuration
logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s: %(message)s')
MAX_THUMBNAIL_SIZE = (256, 256)
MAX_WORKERS = 8

# 📁 Paths and extensions
DB_HOST = os.environ['DB_HOST']
DB_USER = os.environ['DB_USER']
DB_PASSWORD = os.environ['DB_PASSWORD']
DB_NAME = os.environ['DB_NAME']
DATA_ROOT = os.environ.get('DIMDOCK_DATA_ROOT', '/data')
STORE = create_file_store()
TAG_MANIFEST_FILENAME = '.dimdock.json'
DESCRIPTION_FILENAME = '.description.md'
LEGACY_DESCRIPTION_FILENAME = 'description.md'
SHORTCUTS_FILENAME = '.shortcuts.json'
LEGACY_SHORTCUTS_FILENAME = 'shortcuts.json'
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
#     shortcuts_path = os.path.join(ROOT_DIR, SHORTCUTS_FILENAME)
#     if not os.path.isfile(shortcuts_path):
#         print(f"No {SHORTCUTS_FILENAME} found, skipping shortcut processing.", flush=True)
#         return

#     try:
#         with open(shortcuts_path, "r", encoding="utf-8") as f:
#             data = json.load(f)
#     except Exception as e:
#         print(f"Error reading {SHORTCUTS_FILENAME}: {e}", flush=True)
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

def join_key(directory, name):
    return f"{directory}/{name}" if directory else name

def read_tag_manifest(directory):
    manifest_path = join_key(directory, TAG_MANIFEST_FILENAME)
    if not STORE.exists('gallery', manifest_path):
        return {'type': None, 'tags': [], 'items': {}}

    try:
        manifest = json.loads(STORE.read_text('gallery', manifest_path))
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
        normalized_item = {
            'tags': normalize_tags(item_metadata.get('tags', []), f"{manifest_path}: {filename!r} tags")
        }
        if 'description' in item_metadata:
            description = item_metadata['description']
            if description is not None and not isinstance(description, str):
                raise ValueError(
                    f"{manifest_path}: description for {filename!r} must be a string or null"
                )
            normalized_item['description'] = description
        items[filename] = normalized_item
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

def insert_item(cursor, original_name, parent_path, thumbnail_key, file_type):
    now = datetime.datetime.now()
    created_date = extract_date_from_filename_or_path(original_name, parent_path)

    cursor.execute("""
        INSERT INTO Items (original_name, parent_path, thumbnail_key, uploaded, created, display_index, type, metadata)
        VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
    """, (original_name, parent_path, thumbnail_key, now, created_date, None, file_type, '{}'))
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
    if STORE.exists('state', '.blogs_initialized'):
        logging.info("Blogs already initialized. Skipping blog import.")
        return 0

    if not STORE.is_directory('blogs'):
        raise FileNotFoundError("Blogs directory not found in the selected file store.")

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
    for filename in STORE.list_files('blogs'):
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

        if not STORE.exists('blogs', filename):
            continue

        try:
            blog_text = STORE.read_text('blogs', filename)
        except (OSError, UnicodeError) as e:
            logging.error("Error reading blog file %s: %s; skipping.", filename, e)
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

def read_or_create_text(area, key, default_content):
    try:
        try:
            return STORE.read_text(area, key)
        except FileNotFoundError:
            try:
                STORE.write(area, key, default_content.encode('utf-8'), overwrite=False)
                logging.warning("Created missing %s/%s with default content.", area, key)
            except FileExistsError:
                # Another writer created the file after our read; import their content.
                pass
            return STORE.read_text(area, key)
    except (OSError, UnicodeError) as error:
        raise RuntimeError(f"Unable to load content from {area}/{key}: {error}") from error


def initialize_admin_only_tags():
    read_or_create_text(
        'admin', 'admin-only-tags.json',
        json.dumps({'admin-only-tags': ['hidden']}, indent=2) + '\n')


def process_about(cursor):
    description = read_or_create_text('about', 'about.md', '')

    cursor.execute("""
        INSERT INTO About (id, description)
        VALUES (1, %s)
        ON DUPLICATE KEY UPDATE description = VALUES(description)
    """, (description,))
    logging.info("Loaded About content from about/about.md")

def migrate_legacy_metadata(directory):
    for legacy_name, hidden_name in (
        (LEGACY_DESCRIPTION_FILENAME, DESCRIPTION_FILENAME),
        (LEGACY_SHORTCUTS_FILENAME, SHORTCUTS_FILENAME),
    ):
        legacy_path = join_key(directory, legacy_name)
        hidden_path = join_key(directory, hidden_name)
        if STORE.exists('gallery', legacy_path):
            if STORE.exists('gallery', hidden_path):
                logging.error(
                    "Both legacy and hidden metadata files exist in %s; keeping %s.",
                    directory,
                    hidden_name
                )
                continue
            STORE.move_file('gallery', legacy_path, hidden_path)

def process_image(file_key, thumbnail_key):
    logging.info("Generating thumbnail: %s", file_key)
    try:
        with STORE.open_read('gallery', file_key) as source:
            with Image.open(source) as img:
                img = img.convert("RGB")
                thumb = img.copy()
                thumb.thumbnail(MAX_THUMBNAIL_SIZE)
                thumb_with_bg = Image.new("RGB", MAX_THUMBNAIL_SIZE, (255, 255, 255))
                thumb_with_bg.paste(
                    thumb,
                    (
                        (MAX_THUMBNAIL_SIZE[0] - thumb.width) // 2,
                        (MAX_THUMBNAIL_SIZE[1] - thumb.height) // 2,
                    ),
                )
                thumbnail_content = io.BytesIO()
                thumb_with_bg.save(thumbnail_content, format="PNG", optimize=True, quality=85)
        STORE.write(
            'thumbnails',
            f"{thumbnail_key}.png",
            thumbnail_content.getvalue(),
        )
    except Exception as e:
        logging.error("Error processing image %s", file_key, exc_info=e)

def log_scan_statistics(samples):
    logging.info("Gallery statistics (wall-clock; rates use total units / total time):")
    for operation, (unit, measurements) in samples.items():
        if not measurements:
            logging.info("  %s: no folders measured.", operation)
            continue
        seconds = [elapsed for elapsed, _ in measurements]
        total_seconds = sum(seconds)
        units = sum(count for _, count in measurements)
        normalized = [
            elapsed * 1000 / count for elapsed, count in measurements if count
        ]
        logging.info(
            "  %s: folders=%s mean=%.3fms/folder median=%.3fms/folder total=%.3fs",
            operation, len(seconds), statistics.mean(seconds) * 1000,
            statistics.median(seconds) * 1000, total_seconds,
        )
        if normalized and total_seconds > 0:
            logging.info(
                "    %s=%s pooled=%.3fms/%s normalized_mean=%.3fms/%s "
                "normalized_median=%.3fms/%s rate=%.1f %s/s",
                unit, units, total_seconds * 1000 / units, unit,
                statistics.mean(normalized), unit, statistics.median(normalized), unit,
                units / total_seconds, unit,
            )
        else:
            logging.info("    %s=%s normalized time/rate=N/A (no work or zero elapsed time).", unit, units)


def traverse_and_insert(cursor):
    scan_started = time.perf_counter()
    totals = dict.fromkeys(('listing', 'metadata', 'path_db', 'files', 'tags_db', 'thumbnails'), 0.0)
    folder_count = 0
    samples = {
        operation: (unit, []) for operation, unit in (
            ('folder_total', 'folder'), ('listing', 'folder'), ('metadata', 'folder'),
            ('path_db', 'folder'), ('files', 'file'), ('tags_db', 'item'),
            ('thumbnails', 'task'), ('sha256_keys', 'key'),
        )
    }
    logging.info("Listing existing thumbnails once for this scan.")
    thumbnail_listing_started = time.perf_counter()
    existing_thumbnails = set(STORE.list_files('thumbnails'))
    thumbnail_listing_seconds = time.perf_counter() - thumbnail_listing_started
    logging.info("Listed %s existing thumbnails in %.3fs.",
                 len(existing_thumbnails), thumbnail_listing_seconds)
    logging.info("Reading existing gallery index from database.")
    total_files = 0
    cursor.execute("SELECT path, id, type FROM Paths")
    path_rows = cursor.fetchall()
    existing_paths = {row[0]: row[1] for row in path_rows}
    existing_path_types = {row[0]: row[2] for row in path_rows}
    cursor.execute("SELECT id, parent_path, original_name, thumbnail_key, type FROM Items")
    existing_items = {
        (row[1], row[2]): {'id': row[0], 'thumbnail_key': row[3], 'type': row[4]}
        for row in cursor.fetchall()
    }
    existing_items_by_path = {}
    for (item_path, filename), item in existing_items.items():
        existing_items_by_path.setdefault(item_path, {})[filename] = item['id']

    logging.info("Walking gallery storage: %s indexed directories, %s indexed items.",
                 len(existing_paths), len(existing_items))
    index_seconds = time.perf_counter() - scan_started - thumbnail_listing_seconds
    walk = iter(STORE.walk('gallery'))
    while True:
        listing_started = time.perf_counter()
        try:
            current_key, dirs, files = next(walk)
        except StopIteration:
            totals['listing'] += time.perf_counter() - listing_started
            break
        timings = {'listing': time.perf_counter() - listing_started}
        folder_started = time.perf_counter()
        stage_started = folder_started
        folder_count += 1
        logging.info("Processing folder: %s", current_key or "root")
        dirs[:] = sorted(directory for directory in dirs if not directory.startswith('.'))
        migrate_legacy_metadata(current_key)
        path = current_key or "root"
        parent_dir = current_key.rpartition('/')[0]
        parent = parent_dir or "root"
        tag_manifest = read_tag_manifest(current_key)
        directory_name = current_key.rsplit('/', 1)[-1] if current_key else ''
        path_type = 'comic' if directory_name.casefold().startswith('comic_') else tag_manifest['type']
        path_tags = set(tag_manifest['tags'])
        if directory_name.casefold().startswith('wip_'):
            path_tags.add('hidden')

        description_path = join_key(current_key, DESCRIPTION_FILENAME)
        description = None
        if STORE.exists('gallery', description_path):
            try:
                description = STORE.read_text('gallery', description_path)
            except Exception as e:
                logging.error("Error reading %s in %s: %s", DESCRIPTION_FILENAME, path, e)

        timings['metadata'] = time.perf_counter() - stage_started
        stage_started = time.perf_counter()
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

        timings['path_db'] = time.perf_counter() - stage_started
        stage_started = time.perf_counter()
        image_tasks = []
        processed_files = 0
        hash_seconds = 0.0
        for file in files:
            if file.startswith('.'):
                continue
            if file in (LEGACY_DESCRIPTION_FILENAME, LEGACY_SHORTCUTS_FILENAME):
                continue
            ext = os.path.splitext(file)[1].lower()
            item_key = (path, file)
            hash_started = time.perf_counter()
            thumbnail_key = create_thumbnail_key(path, file)
            hash_seconds += time.perf_counter() - hash_started
            processed_files += 1
            file_type = 'image' if ext in VALID_EXTENSIONS else 'file'
            if item_key not in existing_items:
                item_id = insert_item(cursor, file, path, thumbnail_key, file_type)
                existing_items[item_key] = {
                    'id': item_id,
                    'thumbnail_key': thumbnail_key,
                    'type': file_type
                }
                existing_items_by_path.setdefault(path, {})[file] = item_id
                total_files += 1
            else:
                item = existing_items[item_key]
                item_id = item['id']
                if item.get('type') != file_type:
                    cursor.execute(
                        "UPDATE Items SET type = %s WHERE id = %s",
                        (file_type, item_id)
                    )
                    item['type'] = file_type
                if item['thumbnail_key'] != thumbnail_key:
                    cursor.execute(
                        "UPDATE Items SET thumbnail_key = %s WHERE id = %s",
                        (thumbnail_key, item_id)
                    )
                    item['thumbnail_key'] = thumbnail_key

            thumb_key = f"{thumbnail_key}.png"
            if file_type == 'image' and thumb_key not in existing_thumbnails:
                image_tasks.append((join_key(current_key, file), thumbnail_key))

        timings['files'] = time.perf_counter() - stage_started
        stage_started = time.perf_counter()
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
            item_metadata = tag_manifest['items'].get(filename, {'tags': []})
            desired_tags = sorted(
                set(item_metadata['tags']) | set(date_path_tags(path))
            )
            if item_tags_by_id.get(item_id, set()) != set(desired_tags):
                replace_entity_tags(cursor, 'ItemTags', 'item_id', item_id, desired_tags)
            if 'description' in item_metadata:
                cursor.execute(
                    "UPDATE Items SET description = %s WHERE id = %s",
                    (item_metadata['description'], item_id)
                )

        for filename in tag_manifest['items']:
            if filename not in directory_items:
                logging.error(
                    "Tag manifest entry has no indexed item in %s: %s",
                    path,
                    filename
                )

        timings['tags_db'] = time.perf_counter() - stage_started
        stage_started = time.perf_counter()
        logging.debug("Folder %s: %s files, %s thumbnails queued (%s workers).",
                     path, len(files), len(image_tasks), MAX_WORKERS)
        with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
            futures = [
                executor.submit(process_image, file_path, thumbnail_key)
                for file_path, thumbnail_key in image_tasks
            ]
            for completed, future in enumerate(as_completed(futures), 1):
                try:
                    future.result()
                except Exception as e:
                    logging.error("Unhandled thumbnail worker error: %s", e, exc_info=e)
                if completed % 10 == 0 or completed == len(futures):
                    logging.debug("Folder %s: %s/%s thumbnail tasks completed.",
                                 path, completed, len(futures))

        timings['thumbnails'] = time.perf_counter() - stage_started
        for stage, seconds in timings.items():
            totals[stage] += seconds
            count = {
                'files': processed_files, 'tags_db': len(directory_items),
                'thumbnails': len(image_tasks),
            }.get(stage, 1)
            samples[stage][1].append((seconds, count))
        folder_seconds = time.perf_counter() - folder_started + timings['listing']
        samples['folder_total'][1].append((folder_seconds, 1))
        samples['sha256_keys'][1].append((hash_seconds, processed_files))
        logging.info("Finished processing folder: %s", path)

    log_scan_statistics(samples)
    logging.info(
        "Gallery timing: %s folders | thumbnail_listing=%.3fs initial_index=%.3fs listing=%.3fs metadata=%.3fs "
        "path_db=%.3fs files/thumbnail_checks=%.3fs tags_db=%.3fs "
        "thumbnail_tasks=%.3fs total=%.3fs",
        folder_count, thumbnail_listing_seconds, index_seconds, totals['listing'], totals['metadata'],
        totals['path_db'], totals['files'], totals['tags_db'], totals['thumbnails'],
        time.perf_counter() - scan_started,
    )
    return total_files

def main():
    conn = None
    cursor = None
    started = time.monotonic()
    try:
        logging.info("Starting path-loader with %s storage.", os.environ.get('FILE_STORAGE_PROVIDER', 'Local'))
        for area in ('gallery', 'thumbnails', 'blogs', 'about', 'admin', 'state'):
            logging.info("Ensuring storage area: %s", area)
            STORE.create_directory(area)

        logging.info("Loading admin-only tag configuration.")
        initialize_admin_only_tags()

        logging.info("Connecting to gallery database.")
        conn = mysql.connector.connect(
            host=DB_HOST,
            user=DB_USER,
            password=DB_PASSWORD,
            database=DB_NAME
        )
        cursor = conn.cursor()
        logging.info("Connected to gallery database.")
        for attempt in range(1, 4):
            try:
                logging.info("Starting gallery scan (attempt %s/3).", attempt)
                total_files = traverse_and_insert(cursor)
                logging.info("Committing gallery scan: %s new files.", total_files)
                conn.commit()
                logging.info("Gallery scan committed.")
                break
            except mysql.connector.Error as error:
                if error.errno not in (1020, 1205, 1213) or attempt == 3:
                    raise
                conn.rollback()
                logging.warning(
                    "Gallery scan transaction conflicted (MySQL %s); retrying attempt %s/3.",
                    error.errno,
                    attempt + 1
                )
                time.sleep(attempt)
        logging.info("Checking blog imports.")
        inserted_blogs = process_blogs(cursor)
        logging.info("Importing About content.")
        process_about(cursor)
        # process_shortcuts(cursor)
        logging.info("Committing blog/About imports.")
        conn.commit()
        logging.info(
            "Finished scanning: %s new files, %s new blogs in %.1f seconds.",
            total_files,
            inserted_blogs,
            time.monotonic() - started,
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
