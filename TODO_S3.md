# Storage: portability and recovery

Local and Linode Object Storage adapters are done and the live site runs on
Linode. The bucket stays private; media is streamed through the app. Remaining
work is about rebuilding from storage and recovering from partial failures.

## LinodeMeta mode

- [ ] Add `LinodeMeta` alongside `Local` and `Linode`; preserve both existing
  modes and their directory-based storage behavior.
- [ ] Separate logical gallery paths from immutable object keys in LinodeMeta.
  Use stable item/folder IDs and parent relationships so moves and renames
  update metadata without copying source objects or regenerating thumbnails.
- [ ] Store a portable catalog in the bucket covering names, paths, tags,
  descriptions, timestamps, ordering, shortcuts, deletions and settings.
  Support rebuilding a fresh database with IDs and relationships preserved.
- [ ] Make catalog updates retryable through a transactional database outbox;
  define crash recovery, reconciliation and versioned snapshots. Avoid
  rewriting the entire catalog for each edit.
- [ ] Wire website reads/uploads/edits, thumbnails, loader/rebuild and admin
  operations through the selected mode; test equivalent gallery behavior.
- [ ] Add migration scripts that build a **new destination bucket** from an
  existing Linode bucket, copying originals/assets and translating manifests
  and shortcuts into stable-key objects and catalog records. Keep the source
  untouched; reject identical source/destination locations and unsafe
  destination overwrites.
- [ ] Support migration dry-run, inventory/reporting, resumable copies,
  integrity checks and explicit failure reporting. Freeze source writes or
  reconcile changes before cutover; verify counts, content and relationships
  with a fresh-database rebuild before switching modes.
- [ ] Document credentials, migration/cutover, backup/restore and rollback to
  the old bucket; provide an export to a conventional directory tree.

## Rebuild from storage

- [ ] Persist stable item IDs. IDs are auto-incremented today, so a fresh index
  can change shortcut targets and blog asset references.
- [ ] Store everything a rebuild needs (upload times, ordering, relationships,
  deletions, admin settings) in portable metadata.
- [ ] Add a full rebuild/reconcile tool. The loader doesn't remove rows for
  deleted files, and blog imports only run once.
- [ ] Define Unicode normalization and case-collision rules across Windows/S3.

## Recovery

- [ ] Tooling to find and repair partial directory moves/deletes and
  database/storage mismatches (some handlers save MariaDB before the file).
- [ ] Pause other writers during moves; S3 destination checks aren't atomic.
- [ ] Test multipart/encrypted ETags and large-object copy limits on Linode.
- [ ] Range requests for S3 media (S3 streams aren't seekable, so ranges are
  ignored today).

## Operations

- [ ] Use least-privilege bucket credentials; confirm anonymous reads/listing
  are denied.
- [ ] Document and test backup/restore for MariaDB and the bucket.
