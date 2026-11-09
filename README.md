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
   Set `FILE_STORAGE_PROVIDER=Local` and `FILE_STORAGE_ROOT=/data` in `.env`.
   `FILE_STORAGE_ROOT` is the shared data volume's path inside the containers;
   the host directory remains `DATA_DIR`. Local is currently the only
   supported file-storage provider. Gallery files, thumbnails, blog/About
   content, and admin tag settings are accessed through the local file-store
   abstraction; MariaDB remains a separate service.
   About Markdown is imported into the database on path-loader startup and
   written to disk automatically when edited. Gallery descriptions, tags,
   display types, blog posts, admin-only tags, and deletions are also persisted
   to their data files as changes are made. The Admin page's “Sync DB to disk”
   control remains available to reconcile the database and files. ASP.NET Data
   Protection keys are stored in `DATA_DIR/app_state/data-protection-keys` so
   antiforgery tokens remain valid across website container restarts; preserve
   this directory with the rest of the application data.
2. Build and start the services:

   ```sh
   docker compose up --build
   ```

Open the website at `http://localhost:<WEBSITE_PORT>` (set in `.env`).

The website waits for the path loader to finish successfully before starting.
Gallery indexing retries its full database transaction up to three attempts
on record-change conflicts, deadlocks, or lock timeouts; other errors, or
exhausted retries, fail startup rather than silently skipping indexing.

The shared Razor layout loads the stylesheets in `website/wwwroot/css` in a
fixed order. `site.css` contains site-wide styles; the other files group blog,
editor, admin, gallery, image, search, shared component, and comic styles.
They are loaded on every page because several components are shared across
pages and partials.

Use the **Admin Login** link to sign in. The website issues an HTTP-only,
SameSite-protected cookie with an eight-hour sliding lifetime. Login attempts
are rate-limited, and all write requests require an authenticated admin and a
valid antiforgery token. Environment variables keep credentials out of the
repository, but they are visible to users with access to the Docker host;
for remote deployments, use HTTPS and a deployment secret manager or Docker
secrets. MariaDB's root and application-user credentials are also read from
`.env`; keep these secrets private as well.

The local `http` and `https` launch profiles enable the development-only
`Development__AllowAnyAdminPassword` switch, which accepts any password for
the configured admin username. This bypass is honored only when
`ASPNETCORE_ENVIRONMENT` is `Development`. Docker Compose defaults to
`Production` and leaves `DEV_ALLOW_ANY_ADMIN_PASSWORD` disabled; do not enable
the bypass for a deployment.

MariaDB only uses `DB_ROOT_PASSWORD`, `DB_NAME`, `DB_USER`, and `DB_PASSWORD`
when initializing an empty database directory. Changing these values later
does not update users inside an existing database; update the MariaDB users
explicitly to rotate credentials without losing data.

In admin mode, the Gallery page can upload up to 50 JPEG, PNG, or GIF images
(50 MB each) to the current directory. The private thumbnail service uses
Pillow to return the same 256-pixel thumbnails used by the path loader; the
website writes originals and generated thumbnails through the configured file
store. Its internal port can be changed with `THUMBNAIL_SERVICE_PORT` in `.env`
(default `8080`); the service is not published directly to the host.

Admins can create blog posts from the Blog page. Posts created in the website
are stored in the database; posts imported from Markdown files in `/blogs`
retain their existing file-based source. Images uploaded in a post's Markdown
editor are stored under `DATA_DIR/blogs/assets/<post-id>` and are removed when
that post is deleted. Saving a post with no image embeds also removes all
lingering assets for that post, including images uploaded and then removed
before saving. If any image embeds remain, the assets are left unchanged.
PNG, JPEG, GIF, and WebP images up to 20 MB can be
selected or dragged into the editor; the image scale selector inserts the
chosen size using Markdown image syntax.

Admins can also edit the Home/About Markdown and upload or drop images using
the same editor controls. Images are stored under `DATA_DIR/about/assets`.
Saving removes managed About images that were removed from the Markdown,
including images uploaded and then removed during the same editing session.
If no image embeds remain, saving also removes all lingering About assets.
