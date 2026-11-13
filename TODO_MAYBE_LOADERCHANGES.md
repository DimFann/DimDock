# Maybe: path-loader re-architecture

Gallery indexing currently has two implementations: `path_loader/load_paths.py`
(startup) and `website/Services/GalleryPathReloader.cs` (admin **Reload Path**).
Both encode the same rules (manifests, `comic_`/`wip_`, date tags, pending
deletions, legacy-file migration), so every rule change must be made twice.

## Option considered: merge the loader into the thumbnail service

Not recommended:

- Saves no runtime resources. `path-loader` is a one-shot container that exits
  after startup; merging grows an always-on service instead.
- Weakens isolation. The thumbnail service is stateless, holds no credentials
  and is the only process decoding untrusted uploads (Pillow). Merging would put
  DB and S3 credentials into that process.
- Still two writers. The website already performs all other writes (uploads,
  rename, move, delete, manifest write-back), and the parallel C#/Python storage
  adapters would remain. A Python reload could run concurrently with a C# move
  because `GalleryPathOperations` is in-process; fixing that needs a DB lock or
  job table.
- More orchestration. The website gates on `service_completed_successfully`; a
  long-running worker needs a "first scan done" readiness signal plus job
  tracking/polling for browser-triggered reloads.

## Recommended: the website owns indexing

- Keep the thumbnail service tiny, credential-free and single-purpose (optionally
  rename to `image-service`).
- Make `GalleryPathReloader` the only gallery indexer. Run
  `ReloadAsync("root")` as a startup hosted service under the same operation lock
  as moves and renames; the site can serve immediately instead of waiting.
- Port the remaining one-shot work: blog/About import, first-run shortcut import
  and the startup markers.
- Delete `path-loader`, the Python storage adapters and duplicated tests; move
  worthwhile test cases into `tests/DirectoryShortcutChecks`.

Costs: a moderate port (`load_paths.py` is ~29 KB, mostly blog import); losing
the "run the loader by hand" workflow unless replaced by a small `dotnet` admin
command; the startup scan uses website memory (but drops a container).

## Alternative: stay in Python

One `backend` service owning all indexing (startup and on-demand), called by the
website over HTTP. Keep image decoding in a separate credential-free container.
This means rebuilding the job runner, cross-process lock and readiness signal the
website already has.

## Migration steps (C# route)

- [ ] Add the startup scan hosted service behind an environment flag; run it
  alongside the loader and compare results.
- [ ] Port blog/About import and shortcut import.
- [ ] Remove `path-loader` from `compose.yaml` and update the README.

## Unrelated cleanup

- [ ] `compose.yaml` sets `command: ["python", "/app/load_paths.py"]` for
  `path-loader`, but the Dockerfile `ENTRYPOINT` is `db-load-activity.sh`, so the
  command is only passed as ignored arguments. Harmless but misleading.
