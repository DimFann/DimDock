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
Timestamped path-loader logs show storage initialization, database connection,
folder start/finish messages, commits, and total elapsed time.
View them with `docker compose logs -f path-loader`.
The final gallery timing breakdown separates storage listing, metadata reads,
directory database work, file indexing/thumbnail existence checks, item tag
database work, and thumbnail tasks. The final gallery timing summary aggregates
these wall-clock durations and the initial index read. Per-folder timing details
are not printed; thumbnail queue/progress messages are DEBUG-only. Database phases include
Python tag processing; file checks include both filesystem and any index updates.
Listing time includes advancing the storage walk between folders.
Thumbnail names are listed once per scan and checked in memory rather than
performing one filesystem/S3 existence check per image. The summary reports this
up-front `thumbnail_listing` time separately. This snapshot uses memory proportional
to thumbnail count; pause external thumbnail deletions during indexing, since
changes after the listing are observed on the next run.
The scan ends with mean/median milliseconds per folder for each operation,
pooled time per unit, mean/median per-folder normalized time, and units/second.
File work is normalized by indexed candidate files, tag work by items, and
thumbnail work by queued tasks (not confirmed successful generations). Empty
workloads report N/A rates. `sha256_keys` measures key generation inside file
work, so it is a subset, not an additional duration to add to the total.

The shared Razor layout loads the stylesheets in `website/wwwroot/css` in a
fixed order. `site.css` contains site-wide styles; the other files group blog,
editor, admin, gallery, image, search, shared component, and comic styles.
They are loaded on every page because several components are shared across
pages and partials.

Use the **Admin Login** link to sign in. The website issues an HTTP-only,
SameSite-protected cookie with an eight-hour sliding lifetime. Logging out
returns to the current page, preserving its path and query string; admin-only
pages still require signing in again. Login attempts are rate-limited, and all
write requests require an authenticated admin and a
valid antiforgery token. Environment variables keep credentials out of the
repository, but they are visible to users with access to the Docker host;
for remote deployments, use HTTPS and a deployment secret manager or Docker
secrets. MariaDB's root and application-user credentials are also read from
`.env`; keep these secrets private as well.

Run logout checks with `dotnet run --project tests\LoginChecks\LoginChecks.csproj`.

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

## Markdown rendering

Markdown views and previews support plain `<br>`, `<br/>`, and `<br />` line
breaks. Other raw HTML (including attributes on break tags) remains disabled;
tags inside code spans/blocks or escaped with a backslash remain literal.
Run renderer checks with `dotnet run --project tests\MarkdownChecks\MarkdownChecks.csproj`.

## Legacy URL redirects

Links from the old website are redirected (301) using the `LegacyRedirects`
list in the optional `website/legacy_redirects.json` (loaded alongside
`appsettings.json` if present; without it, nothing is redirected). Keep the list
in that one file only: .NET merges JSON arrays by index, so rules split across
config files would mix. Each rule has a `Path`, an optional `Query`
of exact parameter values, and a `Target`; set `"Permanent": false` for a 302.
Paths ignore case and trailing slashes. When several rules match, the one with
the most matching query parameters wins, so a rule without `Query` acts as the
fallback for that path. A running site reloads edits to that file; a Docker
image needs a rebuild because the file is copied into the image.

```json
{
  "LegacyRedirects": [
    { "Path": "/sketch", "Query": { "folderID": "1uiJ..." }, "Target": "/Gallery/Misc/Reference/PonyAnatomy" },
    { "Path": "/sketch", "Target": "/Gallery" }
  ]
}
```

## Export Google Drive shortcuts

`gdrive_scripts/export_drive_shortcuts.py` (Python 3.10+, standard library only) walks ordinary
folders beneath a Drive root and exports file and directory shortcuts in the gallery's
`.shortcuts.json` format. Enable the Google Drive API for your Google Cloud
project and use an API key allowed to call it. Set the key in your environment,
not in source control or the command's arguments:

```powershell
$env:GOOGLE_DRIVE_API_KEY = '<your-api-key>'
python -m gdrive_scripts.export_drive_shortcuts --root-folder-id '<folder-id>'
Remove-Item Env:\GOOGLE_DRIVE_API_KEY
```

You can also run `python export_drive_shortcuts.py --root-folder-id '<folder-id>'`
from inside `gdrive_scripts`. Default output is
`gdrive_scripts/output/shortcuts/.shortcuts.json`, regardless of working directory;
use `--output` to choose another location.

On Linux/macOS, set `GOOGLE_DRIVE_API_KEY` in the environment and run the same
Python command from the repository root. Supply the ID from the folder's sharing URL, not the entire URL.
All `gdrive_scripts` exporters read the repository-root `.env` without executing it. Environment
variables override file values. You can put `GOOGLE_DRIVE_API_KEY`, optional
`GOOGLE_DRIVE_ROOT_RESOURCE_KEY`, and `GOOGLE_DRIVE_ROOT_FOLDER_ID` there instead of
setting them in the shell. With `GOOGLE_DRIVE_ROOT_FOLDER_ID` set, `--root-folder-id`
may be omitted; when both are given, the argument wins.
Keep `.env` untracked. An API key does not authenticate a Drive user:
the root and all descendants must be accessible without user authentication.
Even a link-shared folder can require OAuth for API listing; HTTP access errors
are reported, not treated as an empty folder. Private Drives require a separate
OAuth-based workflow, which this script does not implement.

If the sharing URL includes `resourcekey=...`, set its value (not the full URL):

```powershell
$env:GOOGLE_DRIVE_ROOT_RESOURCE_KEY = '<resourcekey-value>'
```

The exporter sends this via Google's resource-key header for root metadata and
listing requests, and forwards resource keys returned for descendant folders.
Keep it out of source control. A missing resource key can cause a 404 even when
the folder is shared with anyone who has the link; supplying it does not replace
OAuth when user authentication is required.

The root folder's name is omitted. Shortcut destinations are their containing
folder paths (`root` at the top level); sources use the actual target filename,
not the shortcut label. Root-level sources use `root/<filename>` to match the
current importer. The original files must already exist in your gallery with
the same relative paths and names; this script does not download images.
Directory shortcuts appear in the navigation list alongside ordinary directories
and link to the real target gallery, using the Drive shortcut's label.
Folder shortcuts are not recursively followed (including self-links and cycles).
Missing metadata and targets outside the root are skipped with warnings and a
summary count. File destination/target pairs are deduplicated; file shortcut
labels are not preserved. Directory shortcut labels must be valid gallery names
and unique within each destination.

The existing file format is unchanged; directory links use an additional property:

```json
{
  "shortcuts": {"root": ["Album/photo.png"]},
  "directory_shortcuts": {
    "root": [{"name": "Favourites", "target_path": "Album"}],
    "Album": [{"name": "Home", "target_path": "root"}]
  }
}
```

Destination and target paths must already be indexed in `Paths`. Directory links
inherit visibility from the target and its ancestors: hidden targets are omitted
for visitors and marked dark red for admins. Links navigate to the canonical
directory rather than creating virtual copies or duplicating files.

Ambiguous duplicate paths (including case-only differences) and invalid entries
are skipped with warnings. All files/folders sharing an ambiguous path are excluded,
including duplicate folders' contents and shortcuts targeting these entries.
Duplicate directory alias labels are also excluded rather than picking one.
Valid shortcuts still export. Incomplete listings and API errors abort without
replacing the output.
Existing output is protected unless `--overwrite` is supplied; replacement
does not merge existing shortcuts. Review the JSON and warnings before copying
it to Local `img_src/.shortcuts.json` or uploading it to your configured Linode
`<prefix>/img_src/.shortcuts.json`. The legacy `shortcuts.json` filename is also
accepted by the importer. Rebuild and restart the path loader after updating
these scripts. Startup automatically creates the additive `DirectoryShortcuts`
table on existing databases and reconciles directory links from the JSON every
run, even when file shortcuts were previously initialized. No database reset is
needed. File shortcuts retain their existing one-time import behavior.
Export alone does not update the database.
Loader logs show the selected storage provider, shortcut manifest, imported
directory/file counts, missing-manifest skips, and successful database commit.

Directory links reference source/destination directory IDs, so path renames
remain valid and deleting either directory removes the link. Rename/delete and
Admin “Sync DB to disk” persist directory links back to the JSON, preserving
the existing file-shortcut property. Invalid configuration or unresolved directory
paths abort the importer without replacing its directory link index.

Run the offline exporter checks with:

```sh
python -m unittest discover -s gdrive_scripts -t .
```

### Export folder descriptions

Run from the repository root:

```powershell
python -m gdrive_scripts.export_folder_descriptions --root-folder-id '<folder-id>'
```

Or run `python export_folder_descriptions.py --root-folder-id '<folder-id>'`
inside `gdrive_scripts`. It uses the same API key/resource-key configuration.
Output is `gdrive_scripts/output/folder_descriptions/`, with the selected root's
name omitted. Folders with descriptions and their required ancestors are recreated
at their gallery-relative paths; empty branches are omitted.
nonempty Drive `description` metadata is written verbatim as UTF-8 `.description.md`,
including the root description. Empty descriptions produce no file, so copying
the hierarchy does not blank existing descriptions. This does not download
description files or images, convert HTML, or follow folder shortcuts.

Copy the hierarchy's contents into your data's `img_src` directory (or merge
the objects into your configured Linode gallery prefix). The loader supports
`.description.md`, the canonical filename used by the website. The older
`description.md` filename is also accepted for legacy imports. Review existing
`.description.md` files before copying if you intend to overwrite descriptions.

Duplicate/invalid folder paths are logged and skipped, including their contents.
API failures abort before creating output. Existing output directories are refused
to prevent stale descriptions from mixing with a new export; move the previous
export aside or choose a fresh `--output` directory. Both default output trees
are beneath `gdrive_scripts/output/`, which is ignored by Git.

Use `python -m gdrive_scripts.export_folder_descriptions --prune-only` to prune
empty subfolders from an existing export without contacting Drive. This preserves
the output root, description files, other files, and their ancestors.

### Export folder redirects

Old website links such as `/sketch/?folderID=<drive-folder-id>` can be turned
into [legacy URL redirects](#legacy-url-redirects) for every folder under the root:

```powershell
python -m gdrive_scripts.export_folder_redirects
```

This uses the same API key, resource key, and root folder settings, and writes
`gdrive_scripts/output/folder_redirects.json` (overwriting it). The file contains
a complete `LegacyRedirects` section: one rule per folder (the root maps to
`/Gallery`), plus a final `/sketch` → `/Gallery` fallback. Copy it to
`website/legacy_redirects.json` as-is. Use
`--legacy-path` and `--query-key` if old links used a different path or
parameter. Every folder gets a rule: a folder whose name is invalid or
duplicated (so it has no gallery page of its own), and everything inside it,
redirects to its nearest ancestor that does have one, with a warning. The
export fails rather than writing a partial list if any folder ID is unusable.

Importer checks are included in the storage test image below. Database-backed
navigation/Razor and rename/delete persistence checks use a disposable MariaDB
service on loopback (no database selected in the connection string):

```powershell
$env:DIRECTORY_SHORTCUT_TEST_CONNECTION = 'Server=127.0.0.1;Port=<test-port>;User ID=root;Password=<test-password>'
dotnet run --project tests\DirectoryShortcutChecks\DirectoryShortcutChecks.csproj
Remove-Item Env:\DIRECTORY_SHORTCUT_TEST_CONNECTION
```

The checker creates and drops only its own uniquely named database and local
fixture directory. Run it from the repository root; never use a production server.

## Linode Object Storage

### Resetting deployment data

Stop the deployment with `docker compose down` before cleaning. `clean.bat` and
`clean.sh` delegate to `clean.py` (Python 3 required) and read the repository's
`.env` without executing it. `python clean.py --dry-run` previews cleanup.
Cleaning resets only local `app_state` and `db_data`, including Data Protection
keys and initialization markers, for either backend. This deletes the database
and invalidates existing login cookies; back up first.

Use `python clean.py --antiforgery-only` (also accepted by either launcher) to
clear only `app_state/data-protection-keys`, preserving the database, thumbnails,
and initialization markers. Normal cleanup already clears these keys as part
of `app_state`. Both invalidate antiforgery tokens **and login cookies** because
they share Data Protection keys; restart the website, sign in again, and reload
open forms afterward. Add `--dry-run` to preview. Do not combine
`--antiforgery-only` with `--thumbs`; stop the website before resetting keys.

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

Keep the bucket private, denying anonymous reads and listing. Media is streamed
through the website using authenticated S3 reads; browsers receive stable,
hotlinkable application URLs such as `/img_src/...`. Hidden items are unlisted,
not confidential: anyone who knows their application URL can fetch them.

All public media traffic passes through the VPS. App/proxy request limits,
global download-concurrency limits, and aggregate bandwidth/budget controls are
planned in `TODO_RATE_LIMITING.md`; they are not implemented yet. Request counts
and transfer allowances are not hard spending caps. Monitor VPS resources and
transfer usage, and upgrade the VPS if measured capacity requires it.

### Failure and streaming behavior

Objects stream over the network. Non-seekable website uploads and Pillow input
use temporary scratch disk as needed; ensure free scratch space for concurrent
uploads. No persistent local copy of S3 originals is required.
Authenticated website streaming currently does not offer S3 byte ranges or
conditional caching.
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
