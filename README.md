# DimDock

**In development. Do not expose this app to untrusted users:** admin controls
are currently unauthenticated.

## Build and run

1. Copy `.env.example` to `.env` and set `DATA_DIR` to an absolute writable
   directory. Configure `WSL_DATA_DIR` and `WSL_PROJECT_DIR` if Docker runs in
   WSL and cannot access Windows paths directly. Ensure
   `DATA_DIR/admin/admin-only-tags.json` exists.
2. Build and start the services:

   ```sh
   docker compose up --build
   ```

Open the website at `http://localhost:<WEBSITE_PORT>` (set in `.env`).
