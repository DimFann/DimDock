# DimDock

**In development.** Configure strong admin credentials and use HTTPS before
exposing this app to untrusted networks. The admin session cookie is Secure;
use `localhost` for local HTTP development.

## Build and run

1. Copy `.env.example` to `.env` and set `DATA_DIR` to an absolute writable
   directory. Set unique `ADMIN_USERNAME` and `ADMIN_PASSWORD` values in `.env`
   (use a password of at least 12 characters), and set strong, distinct
   `DB_ROOT_PASSWORD` and `DB_PASSWORD` values. Do not commit `.env` or share
   these credentials with the source code. Configure `WSL_DATA_DIR` and
   `WSL_PROJECT_DIR` if Docker runs in WSL and cannot access Windows paths
   directly. Ensure
   `DATA_DIR/admin/admin-only-tags.json` and `DATA_DIR/about/about.md` exist.
   About Markdown is imported into the database on path-loader startup, edited
   from the Home page in admin mode, and synced back to disk with the Admin
   page's “Sync DB to disk” control. ASP.NET Data Protection keys are stored in
   `DATA_DIR/app_state/data-protection-keys` so antiforgery tokens remain valid
   across website container restarts; preserve this directory with the rest of
   the application data.
2. Build and start the services:

   ```sh
   docker compose up --build
   ```

Open the website at `http://localhost:<WEBSITE_PORT>` (set in `.env`).

Use the **Admin Login** link to sign in. The website issues an HTTP-only,
SameSite-protected cookie with an eight-hour sliding lifetime. Login attempts
are rate-limited, and all write requests require an authenticated admin and a
valid antiforgery token. Environment variables keep credentials out of the
repository, but they are visible to users with access to the Docker host;
for remote deployments, use HTTPS and a deployment secret manager or Docker
secrets. MariaDB's root and application-user credentials are also read from
`.env`; keep these secrets private as well.

MariaDB only uses `DB_ROOT_PASSWORD`, `DB_NAME`, `DB_USER`, and `DB_PASSWORD`
when initializing an empty database directory. Changing these values later
does not update users inside an existing database; update the MariaDB users
explicitly to rotate credentials without losing data.

In admin mode, the Gallery page can upload up to 50 JPEG, PNG, or GIF images
(50 MB each) to the current directory. The private thumbnail service uses
Pillow to create the same 256-pixel thumbnails used by the path loader. Its
internal port can be changed with `THUMBNAIL_SERVICE_PORT` in `.env` (default
`8080`); the service is not published directly to the host.

Admins can create blog posts from the Blog page. Posts created in the website
are stored in the database; posts imported from Markdown files in `/blogs`
retain their existing file-based source.
