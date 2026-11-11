#!/bin/bash
set -e

case "${PATH_LOADER_FAST:-false}" in
  true|false) ;;
  *) echo "PATH_LOADER_FAST must be true or false." >&2; exit 1 ;;
esac

# Wait for the database to be ready
until nc -z "$DB_HOST" 3306; do
  echo "Waiting for database at $DB_HOST..."
  sleep 1
done

if [ "${PATH_LOADER_FAST:-false}" = "true" ]; then
  python /app/fast_start.py
  exit 0
fi

marker_exists() {
  python -c '
import os
import sys
from file_store import LocalFileStore
store = LocalFileStore(os.environ.get("DIMDOCK_DATA_ROOT", "/data"))
sys.exit(0 if store.exists("state", sys.argv[1]) else 1)
' "$1"
}

write_marker() {
  python -c '
import os
import sys
from file_store import LocalFileStore
store = LocalFileStore(os.environ.get("DIMDOCK_DATA_ROOT", "/data"))
store.write("state", sys.argv[1], b"")
' "$1"
}

# Scan the data directories on each run; the loader only inserts new records.
if ! marker_exists ".initialized"; then
  echo "Running path-loader for the first time..."
else
  echo "Path-loader already initialized. Scanning for new files..."
fi
python load_paths.py

# The blog importer rebuilds Blogs when this marker is absent. Only mark it
# initialized after the loader has completed successfully.
if ! marker_exists ".blogs_initialized"; then
  write_marker ".blogs_initialized"
  echo "Blog table rebuilt and initialized."
else
  echo "Blogs already initialized."
fi

write_marker ".initialized"

# Check for shortcut initialization
# Directory links are reconciled on every startup, including existing deployments.
# This also creates the additive schema without resetting the database.
python build_shortcuts.py --directories-only
if ! marker_exists ".shortcuts_initialized"; then
  echo "Running shortcut builder for the first time..."
  python build_shortcuts.py
  write_marker ".shortcuts_initialized"
else
  echo "Shortcuts already initialized. Skipping."
fi
