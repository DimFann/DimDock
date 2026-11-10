# Google Drive export scripts

One-off tools for migrating content from a public Google Drive gallery. They
use Python 3.10+ (standard library only), never download images, and write
under `gdrive_scripts/output/` (ignored by Git). Run them from the repository
root with `python -m gdrive_scripts.<script>`, or as `python <script>.py` from
inside this folder.

## Configuration

Enable the Google Drive API for a Google Cloud project and create an API key
for it. The scripts read these from the repository-root `.env` (without
executing it); environment variables override file values:

| Variable | Meaning |
| --- | --- |
| `GOOGLE_DRIVE_API_KEY` | Drive API key. Keep it out of source control and command arguments. |
| `GOOGLE_DRIVE_ROOT_FOLDER_ID` | Gallery root folder ID (from the sharing URL, not the whole URL). `--root-folder-id` overrides it. |
| `GOOGLE_DRIVE_ROOT_RESOURCE_KEY` | Optional `resourcekey=` value from the sharing URL. A missing key can cause 404s on link-shared folders. |

An API key does not authenticate a user: the root and everything under it must
be readable without signing in. Private Drives (OAuth) are not supported.
HTTP/API errors abort the export rather than producing partial output.

Offline checks: `python -m unittest discover -s gdrive_scripts -t .`

## Shortcuts — `export_drive_shortcuts`

```powershell
python -m gdrive_scripts.export_drive_shortcuts [--root-folder-id <id>] [--output <file>] [--overwrite]
```

Exports Drive file and folder shortcuts to `output/shortcuts/.shortcuts.json`
in the gallery's format. Existing output is kept unless `--overwrite` is given.

```json
{
  "shortcuts": {"root": ["Album/photo.png"]},
  "directory_shortcuts": {
    "root": [{"name": "Favourites", "target_path": "Album"}]
  }
}
```

- Destinations are containing-folder paths (`root` at the top level); file
  sources use the real target filename, which must already exist in the gallery
  at the same relative path.
- Folder shortcuts become navigation links to the real folder using the
  shortcut's label (not followed recursively). They inherit the target's
  hidden/visible state.
- Ambiguous (including case-only) duplicate paths, invalid names, targets
  outside the root and duplicate labels are skipped with warnings.

Review the output, then copy it to `img_src/.shortcuts.json` (Local) or
`<prefix>/img_src/.shortcuts.json` (Linode) and restart the path loader.
Directory links are reconciled from the file on every startup; file shortcuts
are imported once. Path renames/deletes and Admin "Sync DB to disk" write
directory links back to the file.

## Folder descriptions — `export_folder_descriptions`

```powershell
python -m gdrive_scripts.export_folder_descriptions [--root-folder-id <id>] [--output <dir>]
python -m gdrive_scripts.export_folder_descriptions --prune-only
```

Writes each non-empty Drive folder description verbatim to
`output/folder_descriptions/<gallery path>/.description.md` (root name
omitted; folders without descriptions are skipped). An existing output
directory is refused; move it aside first. `--prune-only` removes empty
subfolders from an existing export without contacting Drive.

Copy the tree's contents into `img_src` (or the Linode gallery prefix). Check
for existing `.description.md` files you don't want overwritten.

## Folder redirects — `export_folder_redirects`

```powershell
python -m gdrive_scripts.export_folder_redirects [--legacy-path /sketch] [--query-key folderID]
```

Writes `output/folder_redirects.json` (overwriting it): a complete
`LegacyRedirects` section with one rule per folder, so old links like
`/sketch/?folderID=<id>` go to the matching gallery page, plus a `/sketch` →
`/Gallery` fallback. Folders without a valid gallery page of their own redirect
to their nearest valid ancestor, with a warning. Copy the file to
`website/legacy_redirects.json` (see the main README).
