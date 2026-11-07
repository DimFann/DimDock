#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ ! -f "$SCRIPT_DIR/.env" ]]; then
  echo "Missing $SCRIPT_DIR/.env. Copy .env.example to .env and set DATA_DIR." >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
. "$SCRIPT_DIR/.env"
set +a

if [[ -z "${DATA_DIR:-}" ]]; then
  echo "DATA_DIR is not set in $SCRIPT_DIR/.env." >&2
  exit 1
fi

if command -v cygpath >/dev/null 2>&1; then
  DATA_DIR="$(cygpath -u "$DATA_DIR")"
elif command -v wslpath >/dev/null 2>&1; then
  DATA_DIR="$(wslpath -u "$DATA_DIR")"
fi

if [[ "$DATA_DIR" != /* ]]; then
  echo "DATA_DIR must be an absolute path: $DATA_DIR" >&2
  exit 1
fi

if [[ ! -d "$DATA_DIR" ]]; then
  echo "DATA_DIR does not exist: $DATA_DIR" >&2
  exit 1
fi

clean_contents() {
  local directory="$1"

  if [[ -d "$directory" ]]; then
    find "$directory" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
    echo "Cleaned $directory"
  fi
}

echo "Clearing app_state, img_db_thumbs, and db_data..."
clean_contents "$DATA_DIR/app_state"
clean_contents "$DATA_DIR/img_db_thumbs"
clean_contents "$DATA_DIR/db_data"
echo "Done."
