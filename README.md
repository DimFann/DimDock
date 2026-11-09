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
   directly. The path loader creates missing `admin/admin-only-tags.json`
   (with `hidden` as the default admin-only tag) and an empty `about/about.md`
   in the selected store. Existing files are never overwritten by initialization.
   Set `FILE_STORAGE_PROVIDER=Local` and `FILE_STORAGE_ROOT=/data` in `.env`.
   `FILE_STORAGE_ROOT` is the shared data volume's path inside the containers;
   the host directory remains `DATA_DIR`. Local is the default; Linode Object
   Storage is available as described below. Gallery files, thumbnails, blog/About
   content, and admin tag settings are accessed through the selected file-store
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

## Linode Object Storage

### Resetting deployment data

Stop the deployment with `docker compose down` before cleaning. `clean.bat` and
`clean.sh` delegate to `clean.py` (Python 3 required) and read the repository's
`.env` without executing it. `python clean.py --dry-run` previews cleanup.
Cleaning resets only local `app_state` and `db_data`, including Data Protection
keys and initialization markers, for either backend. This deletes the database
and invalidates existing login cookies; back up first.

Use `--thumbs` (or `/thumbs`) to also remove generated thumbnails: local
`img_db_thumbs` in Local mode, or only the configured bucket's
`<prefix>/img_db_thumbs/` in Linode mode. Originals and metadata are preserved.
Linode thumbnail cleanup uses the loader image; rebuild it with
`docker compose build path-loader` after updating these scripts.
`python clean.py --thumbs --dry-run` lists the remote thumbnail count without
deleting objects. The scripts never delete the bucket or change its access policy.

Keep `FILE_STORAGE_PROVIDER=Local` until migration and live verification are
complete. Create a dedicated, initially **private** bucket and a bucket-scoped
read/write S3 access key. Do not give the app Linode account-management or
bucket-policy credentials. Put these settings in the untracked `.env`, never
in chat or source control:

```dotenv
LINODE_STORAGE_ENDPOINT=https://<region-endpoint>
LINODE_STORAGE_REGION=<signing-region>
LINODE_STORAGE_BUCKET=<bucket-name>
LINODE_STORAGE_PREFIX=dimdock
LINODE_STORAGE_ACCESS_KEY=<access-key>
LINODE_STORAGE_SECRET_KEY=<secret-key>
LINODE_STORAGE_PUBLIC_BASE_URL=
```

Use the S3 endpoint and signing region shown for your Linode bucket; the endpoint
must not include the bucket or a path. Both services use Signature V4 with
path-style requests. The key must permit listing and reading, writing, copying,
and deleting objects within the deployment prefix.

### Connection test and cutover

After filling in the settings, build the loader and run this explicit smoke
test without starting MariaDB or indexing anything:

```sh
docker compose build path-loader
docker compose run --rm --no-deps -e FILE_STORAGE_PROVIDER=Linode --entrypoint python path-loader /app/storage_smoke.py
```

The test creates uniquely named hidden scratch directories in each content area,
checks create/read/list/copy/verify/delete, then removes only its own objects.
It never changes public access or touches existing gallery files. It exercises
conditional writes as well; unsupported endpoint behavior fails visibly.

Back up the data and MariaDB, pause edits, and copy these directories unchanged
under `<prefix>/` in the bucket:

| Local directory | Object prefix |
| --- | --- |
| `img_src` | `<prefix>/img_src/` |
| `img_db_thumbs` | `<prefix>/img_db_thumbs/` |
| `blogs` | `<prefix>/blogs/` |
| `about` | `<prefix>/about/` |
| `admin` | `<prefix>/admin/` |

Include hidden metadata files. Preserve case, Unicode filenames, and all path
segments; do not copy `db_data`, `app_state`, credentials, or deployment files.
The adapters create zero-byte trailing-slash markers to preserve empty directories;
include equivalent markers for empty directories when migrating existing data.
Switching providers does not rewrite MariaDB path values or embedded URLs.
Keep the original local files for rollback. There is no automatic migration or
destructive database rebuild on provider selection.

After counts and representative bytes match, set `FILE_STORAGE_PROVIDER=Linode`
and rebuild/start the services. The loader reads content from S3; runtime markers,
MariaDB, and Data Protection keys still live on the VPS under `DATA_DIR`.
Existing import markers retain their previous meaning: switching providers does
not force reimport of blogs or regenerate database IDs. A full database rebuild
and stable-ID migration remain separate rollout tasks in `TODO_S3.md`.

### Hotlinks and access policy

With `LINODE_STORAGE_PUBLIC_BASE_URL` empty, media streams through the website
using authenticated S3 reads. For direct bucket/CDN delivery, configure anonymous
`GetObject` access **only** for the media prefixes `img_src/`, `img_db_thumbs/`,
`blogs/assets/`, and `about/assets/` under the deployment prefix. Do not grant
anonymous bucket listing, or public access to `admin/` or runtime state.
Gallery sidecar metadata shares `img_src/`; account for that when choosing a
public policy. Set appropriate image content types and `nosniff` at a CDN when
using direct delivery; upload operations set content types automatically.

Then set `LINODE_STORAGE_PUBLIC_BASE_URL` to the HTTPS bucket/CDN base URL,
without the deployment prefix. Existing media URLs redirect to
`<base>/<prefix>/<area>/<key>` and can be hotlinked; hidden items are unlisted,
not private. A private bucket without this setting can be used for initial testing.

**Public deployment is not cost-protected yet.** The Linode monitoring and
private-bucket circuit breaker in Phase 3 are not implemented. Do not treat
private app listings, app rate limits, a CDN, or transfer allowances as a hard
spending cap. Keep public reads disabled until that rollout work is complete.

### Failure and streaming behavior

Objects stream over the network. Non-seekable website uploads and Pillow input
use temporary scratch disk as needed; ensure free scratch space for concurrent
uploads. No persistent local copy of S3 originals is required.
The authenticated website streaming fallback does not offer S3 byte ranges or
conditional caching; direct bucket/CDN delivery uses the origin's native support.
Listings paginate and infer directories from prefixes as well as markers.
Permission and network errors propagate; only missing keys become not-found.
Create-only writes use conditional S3 puts and do not silently overwrite.

Moves copy with a source ETag precondition, verify destination length and ETag,
then delete the source. Directory moves verify **all** copies before deleting
any source. This is not a transaction: interrupted copies/deletes can leave
duplicate or split trees. Errors remain visible and MariaDB is not advanced
when the adapter reports failure. Pause writes, inspect source and destination,
restore a complete source or finish the move, and reconcile before retrying.
Destination existence checks cannot prevent a competing writer racing an S3
copy; run one app instance and pause external writers during moves/migration.
Objects whose ETags change during copying (for example provider-specific
multipart/encryption behavior) fail verification rather than deleting the source.

### Adapter checks (no live credentials)

The Python checks use Moto and include parity, pagination, streamed reads,
permissions, and injected move failures. Build and run:

```sh
docker build -f path_loader/Dockerfile.tests -t dimdock-storage-tests .
docker run --rm dimdock-storage-tests
```

For the C# contract checks, start a disposable local Moto service, then run
the console checker with PowerShell:

```powershell
$moto = docker run -d --rm -p 127.0.0.1:19543:5000 --entrypoint moto_server dimdock-storage-tests -H 0.0.0.0 -p 5000
try {
    $env:STORAGE_TEST_ENDPOINT = 'http://127.0.0.1:19543'
    dotnet run --project tests\FileStoreChecks\FileStoreChecks.csproj
} finally {
    docker stop $moto
    Remove-Item Env:\STORAGE_TEST_ENDPOINT
}
```

The checker refuses non-loopback endpoints, creates its own uniquely named
bucket, and removes it afterward. These checks do not certify Linode endpoint
compatibility or full database rebuild parity; run the explicit live smoke test
before switching and validate a staging gallery before deployment.

An additional mixed-fixture indexing test runs when the Python test container
receives `STORAGE_TEST_DB_HOST` and `STORAGE_TEST_DB_PASSWORD` for an isolated
MariaDB test service on the same Docker network. It creates/drops only its own
randomly named test databases and compares logical records, tags, shortcuts,
blog/About content, and generated thumbnails through Local and S3, excluding
auto-increment identities and scan timestamps. Do not point it at production.
