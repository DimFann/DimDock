#!/bin/bash
set -e

# Wait for the database to be ready
until nc -z "$DB_HOST" 3306; do
  echo "Waiting for database at $DB_HOST..."
  sleep 1
done

# Check for path-loader initialization
if [ ! -f "/app_state/.initialized" ]; then
  echo "Running path-loader for the first time..."
  python load_paths.py
  touch /app_state/.initialized
else
  echo "Path-loader already initialized. Skipping."
fi

# Check for shortcut initialization
if [ ! -f "/app_shared/.shortcuts_initialized" ]; then
  echo "Running shortcut builder for the first time..."
  python build_shortcuts.py
  touch /app_shared/.shortcuts_initialized
else
  echo "Shortcuts already initialized. Skipping."
fi
