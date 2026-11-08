# DimDock

**In development. Do not expose this app to untrusted users:** admin controls
are currently unauthenticated.

## Build and run

1. Copy `.env.example` to `.env` and set `DATA_DIR` to an absolute writable
   directory. Configure `WSL_DATA_DIR` and `WSL_PROJECT_DIR` if Docker runs in
   WSL and cannot access Windows paths directly. Ensure
   `DATA_DIR/admin/admin-only-tags.json` and `DATA_DIR/about/about.md` exist.
   About Markdown is imported into the database on path-loader startup, edited
   from the Home page in admin mode, and synced back to disk with the Admin
   page's “Sync DB to disk” control.
2. Build and start the services:

   ```sh
   docker compose up --build
   ```

Open the website at `http://localhost:<WEBSITE_PORT>` (set in `.env`).

In admin mode, the Gallery page can upload up to 50 JPEG, PNG, or GIF images
(50 MB each) to the current directory. The private thumbnail service uses
Pillow to create the same 256-pixel thumbnails used by the path loader. Its
internal port can be changed with `THUMBNAIL_SERVICE_PORT` in `.env` (default
`8080`); the service is not published directly to the host.
