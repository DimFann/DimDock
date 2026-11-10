# DimDock

A self-hosted art gallery and blog: an ASP.NET Core website, a Python path
loader that indexes the gallery into MariaDB, and a thumbnail service, run
together with Docker Compose. Files live on local disk or in Linode Object
Storage (S3-compatible).

> **Status: in development.** Expect breaking changes. Use strong admin
> credentials and put the site behind HTTPS before exposing it publicly.

## Quick start

1. Copy `.env.example` to `.env` and fill it in (see [Configuration](#configuration)).
   At minimum set `DATA_DIR`, `DB_ROOT_PASSWORD`, `DB_PASSWORD`,
   `ADMIN_USERNAME` and `ADMIN_PASSWORD`. Never commit `.env`.
2. Put your gallery files in `DATA_DIR/img_src` (folders become gallery pages).
3. Start everything:

   ```sh
   docker compose up --build
   ```

4. Open `http://localhost:<WEBSITE_PORT>` and use **Admin Login** to sign in.

On startup the path loader indexes `img_src`, generates missing thumbnails and
imports blog/About content; the website starts once it has finished
(`docker compose logs -f path-loader` to watch). Edits made in the website are
written back to the data files automatically.

## Configuration

All settings are read from `.env` by `compose.yaml`.

| Variable | Default | Description |
| --- | --- | --- |
| `DATA_DIR` | — | **Required.** Absolute host path of the data directory (gallery, thumbnails, blogs, database, keys). |
| `WSL_DATA_DIR` / `WSL_PROJECT_DIR` | — | Only if Docker runs in WSL2 and can't see Windows paths: the data and repository paths as WSL sees them (e.g. `/mnt/c/...`). |
| `WEBSITE_PORT` | `18080` | Host port the website is published on. |
| `THUMBNAIL_SERVICE_PORT` | `8080` | Internal port of the thumbnail service (not published). |
| `FILE_STORAGE_PROVIDER` | `Local` | `Local` (files under `DATA_DIR`) or `Linode` (S3 bucket, see below). |
| `FILE_STORAGE_ROOT` | `/data` | Path of the data volume inside the containers. Rarely needs changing. |
| `LINODE_STORAGE_ENDPOINT` | — | Linode: S3 endpoint URL, without bucket or path. |
| `LINODE_STORAGE_REGION` | — | Linode: signing region. |
| `LINODE_STORAGE_BUCKET` | — | Linode: bucket name. Keep the bucket private. |
| `LINODE_STORAGE_PREFIX` | — | Linode: optional folder inside the bucket (no leading/trailing `/`). |
| `LINODE_STORAGE_ACCESS_KEY` / `LINODE_STORAGE_SECRET_KEY` | — | Linode: bucket-scoped read/write access key. |
| `UPLOAD_MODE` | `Chunked` | Gallery upload mode: `Chunked` or `Bypass` (see [Uploads](#uploads)). |
| `UPLOAD_CHUNK_SIZE_MB` | `50` | Chunked mode request size, 5–95 MB. Keep it under your proxy/CDN limit. |
| `UPLOAD_BYPASS_HOST` | — | Required for `Bypass`: host name (optional `:port`) of a DNS-only upload host. |
| `DB_ROOT_PASSWORD` | — | **Required.** MariaDB root password. |
| `DB_NAME` | `mydb` | Database name. |
| `DB_USER` | `dimdock` | Application database user. |
| `DB_PASSWORD` | — | **Required.** Application database password. |
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | — | **Required.** Website admin login (password at least 12 characters). |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Keep `Production` for deployments. |
| `DEV_ALLOW_ANY_ADMIN_PASSWORD` | `false` | Development only: accept any password for the admin user. Ignored unless the environment is `Development`. |

The `DB_*` values only apply when the database is first created; changing them
later does not update existing MariaDB users. `GOOGLE_DRIVE_*` values are only
used by the migration tools in [`gdrive_scripts`](gdrive_scripts/README.md).

## Data directory

```
DATA_DIR/
  img_src/         gallery originals (folders = gallery pages)
  img_db_thumbs/   generated thumbnails
  blogs/           blog posts (Markdown) and uploaded post images
  about/           Home/About Markdown and images
  admin/           admin settings (e.g. admin-only tags)
  db_data/         MariaDB data
  app_state/       login/antiforgery keys and loader markers
```

Back up the whole directory. With Linode storage, the first five live in the
bucket instead; `db_data` and `app_state` always stay on the server.

## Server setup

The live site, [dimdock.com](https://dimdock.com), is an example deployment:
Docker Compose on a Linode VPS with Linode Object Storage, nginx on the host
terminating HTTPS and proxying to `http://127.0.0.1:<WEBSITE_PORT>`, and
Cloudflare in front of the domain. The proxy should forward `Host`,
`X-Forwarded-For` and `X-Forwarded-Proto`, and allow large enough request
bodies for your upload mode (below).

## Uploads

Requests through Cloudflare (and many proxies) are limited to 100 MB, so large
gallery uploads use one of two modes:

- **Chunked** (default): files are sent in `UPLOAD_CHUNK_SIZE_MB` pieces through
  the normal site. Your reverse proxy must accept bodies of at least that size
  (nginx: `client_max_body_size 100m;`).
- **Bypass**: files are sent whole to `https://UPLOAD_BYPASS_HOST`, a separate
  DNS-only (not proxied) host name with its own valid TLS certificate, which
  must reach the same server. The proxy must pass the original `Host` header
  and allow unlimited bodies for it (nginx: `client_max_body_size 0;`). Admins
  sign in again on that host. This exposes the server's real IP, so consider
  restricting it to your own IPs.

## Linode Object Storage

1. Create a private bucket and a read/write access key limited to it, and fill
   in the `LINODE_STORAGE_*` settings (leave `FILE_STORAGE_PROVIDER=Local` for now).
2. Test the connection without touching any data:

   ```sh
   docker compose build path-loader
   docker compose run --rm --no-deps -e FILE_STORAGE_PROVIDER=Linode --entrypoint python path-loader /app/storage_smoke.py
   ```

3. Back up, then copy `img_src`, `img_db_thumbs`, `blogs`, `about` and `admin`
   unchanged (including hidden files) to `<prefix>/<name>/` in the bucket.
4. Set `FILE_STORAGE_PROVIDER=Linode` and run `docker compose up --build`.

Media is served through the website, so the bucket never needs public access.
The site also clears out abandoned multipart uploads; a bucket lifecycle rule
that aborts incomplete uploads after a day is a useful backstop.

## Old site redirects

Old links can be redirected (301) by creating `website/legacy_redirects.json`.
Rules match the path (case-insensitive, trailing slash ignored) and optional
exact query values; the rule matching the most query values wins. Add
`"Permanent": false` for a 302. Keep all rules in this one file, and rebuild
the image after editing it.

```json
{
  "LegacyRedirects": [
    { "Path": "/sketch", "Query": { "folderID": "1uiJ..." }, "Target": "/Gallery/Misc/Reference/PonyAnatomy" },
    { "Path": "/sketch", "Target": "/Gallery" }
  ]
}
```

`gdrive_scripts/export_folder_redirects` can generate this file from a Google
Drive folder tree.

## Maintenance

Stop the services (`docker compose down`) first. `clean.bat` / `clean.sh` run
`clean.py`, which reads `.env`:

| Command | Effect |
| --- | --- |
| `python clean.py` | Delete the database (`db_data`) and `app_state` so everything is rebuilt from the data files on next start. Logs everyone out. |
| `python clean.py --thumbs` | Also delete generated thumbnails (local or in the bucket). |
| `python clean.py --antiforgery-only` | Only reset login/antiforgery keys. |

Add `--dry-run` to preview any of them. Originals are never deleted.

## Development and tests

Run the website outside Docker with `dotnet run --project website` (the
launch profiles use the `Development` environment). Front-end libraries are
restored by `npm run build` in `website/`.

| Check | Command |
| --- | --- |
| Unit tests (no external services) | `dotnet test DimDock.sln` |
| Markdown rendering | `dotnet run --project tests\MarkdownChecks` |
| Login/logout | `dotnet run --project tests\LoginChecks` |
| File stores (needs a local [Moto](https://github.com/getmoto/moto) S3 server) | set `STORAGE_TEST_ENDPOINT=http://127.0.0.1:<port>`, then `dotnet run --project tests\FileStoreChecks` |
| Database-backed checks (needs a throwaway MariaDB on loopback) | set `DIRECTORY_SHORTCUT_TEST_CONNECTION=Server=127.0.0.1;Port=<port>;User=root;Password=<pw>`, then `dotnet run --project tests\DirectoryShortcutChecks` from the repository root |
| Python loader/storage tests | `docker build -f path_loader/Dockerfile.tests -t dimdock-storage-tests .` then `docker run --rm dimdock-storage-tests` |

Test checks create and remove their own databases, buckets and folders, and
refuse non-loopback endpoints. Never point them at production.