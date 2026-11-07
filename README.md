```sh
docker exec -it console mariadb -h db -u myuser -p mypassword
docker compose build --no-cache path-loader
docker-compose down -v
docker-compose up --build -d
```


### Data directory

Copy `.env.example` to `.env` and set `DATA_DIR` to the absolute path of the
directory containing `app_state`, `img_src`, `img_db_thumbs`, `db_data`, and
`blogs`.
Docker Compose uses this path for the bind mounts; `.env` is local and is not tracked.
Set `WEBSITE_PORT` to choose the host port for the website (the container
continues to listen on port 80).

If Docker runs in WSL2, also set `WSL_DATA_DIR` to that same directory using a
path visible inside WSL. This applies to `db_data`, `app_state`, `img_src`, and
`img_db_thumbs`, and `blogs`.
For example, Windows `H:\data\dimdock` is typically `/mnt/h/data/dimdock` in
WSL. Set `WSL_PROJECT_DIR` to the repository path visible inside WSL as well;
Compose uses it for the MariaDB init scripts. Cleanup scripts continue to use
the Windows `DATA_DIR`.

Run `bash clean.sh` from any directory to clear `app_state`, `img_db_thumbs`,
and `db_data` under `DATA_DIR`. This permanently deletes their contents.

The database was previously stored in the project's `db_data` directory. If
you have an existing database there, stop the Compose services and copy its
contents to `DATA_DIR/db_data` before starting the updated configuration.

Markdown files in `DATA_DIR/blogs` are imported when the path loader runs.
Name them `TIMEZONE_YYYYMMDD_HHMMSS.md` (for example,
`PST_20261107_000000.md`). Supported fixed-offset abbreviations are PST, PDT,
MST, MDT, CST, CDT, EST, EDT, UTC, and GMT. A leading single-`#` heading
becomes the blog title; the full file contents are stored as the blog text,
and the filename time is converted to UTC.

The loader incrementally scans the image directory whenever it runs, skipping
files already represented in the database. Run
`docker compose up --build --force-recreate path-loader` to scan for image
additions without restarting the website. Blog imports use a unique
source-filename key, defined in the initial schema. Blog files are imported
once when `app_state/.blogs_initialized` is absent; the loader drops and
recreates the `Blogs` table, then imports the current files. To rebuild blogs
after adding, editing, or removing files (or changing alias generation), delete
that marker and recreate the path-loader:

```powershell
$dataDir = (Get-Content .env | Where-Object { $_ -match '^DATA_DIR=' } | Select-Object -First 1) -replace '^DATA_DIR=', ''
Remove-Item -LiteralPath (Join-Path $dataDir 'app_state\.blogs_initialized')
docker compose up --build --force-recreate path-loader
```

This only rebuilds the `Blogs` table; image and path records are left intact.
Post URLs use a lowercase title slug, or a UTC timestamp slug such as
`nov-7-2026-080000-utc` for untitled posts. Duplicate slugs receive a numeric
suffix. Existing numeric `/blog/{id}` URLs remain supported.
In-place migrations for other schema changes are not supported; recreate the
development database for those changes.
