# Storage: portability and recovery

Local and Linode Object Storage adapters are done and the live site runs on
Linode. The bucket stays private; media is streamed through the app. Remaining
work is about rebuilding from storage and recovering from partial failures.

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
