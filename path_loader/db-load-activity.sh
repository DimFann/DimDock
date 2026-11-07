#!/bin/bash
set -e

# Wait for the database to be ready
until nc -z "$DB_HOST" 3306; do
  echo "Waiting for database at $DB_HOST..."
  sleep 1
done

# Scan the data directories on each run; the loader only inserts new records.
if [ ! -f "/app_state/.initialized" ]; then
  echo "Running path-loader for the first time..."
else
  echo "Path-loader already initialized. Scanning for new files..."
fi
python load_paths.py

# The blog importer rebuilds Blogs when this marker is absent. Only mark it
# initialized after the loader has completed successfully.
if [ ! -f "/app_state/.blogs_initialized" ]; then
  touch /app_state/.blogs_initialized
  echo "Blog table rebuilt and initialized."
else
  echo "Blogs already initialized."
fi

touch /app_state/.initialized

# Check for shortcut initialization
if [ ! -f "/app_state/.shortcuts_initialized" ]; then
  echo "Running shortcut builder for the first time..."
  python build_shortcuts.py
  touch /app_state/.shortcuts_initialized
else
  echo "Shortcuts already initialized. Skipping."
fi
