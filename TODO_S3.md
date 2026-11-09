# Filesystem abstraction and Linode Object Storage rollout

## Current status

The Local and Linode adapters are implemented for the website and Python path
loader. A live Linode smoke test passed for all five content areas, checking
write/read/list/copy/verification/deletion and removing its scratch objects.
Local remains the repository's default configuration.

Media is delivered exclusively through application streaming from a private
bucket. Direct bucket redirects and their configuration have been removed.
Hotlinkable application URLs remain supported. No automated bucket-policy
changes are planned.

Core adapter work is complete; full rebuild portability, recovery tooling,
production capacity, and delivery abuse protection still need work.

Related plans:

- [TODO_SHARED_DB.md](TODO_SHARED_DB.md): local authoring against the Nanode's
  shared database, with a read-only public instance.
- [TODO_RATE_LIMITING.md](TODO_RATE_LIMITING.md): streamed-media rate limits,
  concurrency, transfer budgets, caching, monitoring, and load testing.

## Goals and invariants

- Keep the file store authoritative for content and portable metadata; MariaDB
  is the queryable index. Complete rebuildability is a goal, not yet a guarantee.
- Preserve database paths such as `2023/2023-01/` across providers. Bucket,
  endpoint, and deployment prefix are configuration, never database path data.
- Map the gallery `root` sentinel to the empty storage prefix.
- Preserve file bytes, names, metadata formats, relationships, and intended
  ordering independently of filesystem timestamps, ACLs, and provider ETags.
- Keep MariaDB, runtime markers, Data Protection keys, credentials, and logs
  outside Object Storage content. Runtime state remains on private local disk.
- Keep the bucket private. Public application URLs can be hotlinked; hidden
  means unlisted, not confidential to someone who knows an application URL.
- Use validated keys and streams, not a mounted bucket or physical-path API.
- Surface errors and interrupted operations; do not claim atomic transactions
  across MariaDB and the file store or across multiple S3 objects.

## Phase 1 — Abstraction and Local implementation

### Implemented

- [x] C# `IFileStore` and `LocalFileStore` cover reads/writes, existence,
  directories, listing, deletion, and file/directory moves.
- [x] Python local adapter covers loader/indexing, thumbnails, shortcuts,
  metadata migration, and startup markers.
- [x] Website gallery, image, admin, blog, About, and admin-tag persistence use
  the abstraction. Application assets in `wwwroot` remain deployment files.
- [x] Thumbnail service returns PNG bytes; the website stores generated
  thumbnails through the selected adapter.
- [x] Compose and `.env.example` select Local by default with a shared data root.
- [x] Key validation is shared between adapters within each language.
- [x] Missing About Markdown and admin-only tag configuration are initialized
  without overwriting existing files; the default admin-only tag is `hidden`.
- [x] Website startup waits for indexing to finish. Transient gallery database
  conflicts retry the whole scan transaction with bounded attempts.
- [x] Local builds/smoke checks passed; user testing confirmed gallery behavior.
  Blog/About asset routing regressions were fixed.

### Remaining portability and authority work

- [ ] Persist stable item identities and define their migration. Current IDs
  are auto-incremented; thumbnail keys derive from path/name. A fresh index
  cannot yet promise identical IDs, shortcut targets, or embedded asset references.
- [ ] Audit metadata needed for a complete rebuild: upload times, ordering,
  relationships, deletions, blog assets referenced by post IDs, and admin settings.
- [ ] Define Unicode normalization and cross-platform case/name-collision rules.
  Relative-key validation alone does not ensure identical Windows/S3 behavior.
- [ ] Complete canonical serialization, schema migration, and unknown-version
  handling guarantees without silently losing metadata.
- [ ] Finish authoritative-write ordering and recovery across every mutation.
  Some handlers save MariaDB before file persistence; move handlers also have
  rollback behavior. These are not fully reconciled distributed commits.
- [ ] Provide explicit full rebuild/reconciliation tooling. The current loader
  is not a complete edit/delete synchronizer: blog imports use initialization
  markers and existing rows can survive source removal.
- [ ] Automate normalized full-deployment database parity checks, excluding only
  deliberately documented nondeterministic fields.

## Phase 2 — Linode adapter and integration

### Implemented and verified

- [x] Website AWS S3 SDK adapter and Python boto3 adapter use endpoint, signing
  region, bucket, prefix, and credentials from configuration.
- [x] Both map unchanged logical paths under `img_src`, `img_db_thumbs`, `blogs`,
  `about`, and `admin`. An empty deployment prefix is supported.
- [x] Empty directories use trailing-slash markers; prefix listing also infers
  directories from existing objects and supports pagination.
- [x] Authenticated media streaming retains gallery, thumbnail, blog/About
  asset URLs and GET/HEAD routes. Originals are not persistently cached locally.
- [x] Non-seekable website uploads and Pillow reads use scratch disk as needed
  instead of unbounded original-image memory buffering.
- [x] Thumbnail requests explicitly send Content-Length for non-seekable S3
  reads. Actual gallery-to-thumbnail HTTP generation passed a regression check.
- [x] Viewer/comic source URLs correctly handle root and nested paths.
- [x] Create-only puts use conditional writes. Permission/network failures
  propagate rather than becoming missing-file defaults.
- [x] Moves copy with source ETag preconditions and verify destination length
  and ETag before deletion. Website directory moves verify all copies before
  deleting any source. Interrupted operations report errors.
- [x] Disposable S3 checks cover Local/Linode parity, pagination, streamed
  objects, key validation, permissions, and injected move failures.
- [x] A mixed fixture indexed equivalently against isolated MariaDB through
  Local/S3, including tags, shortcuts, blog/About content, and thumbnails.
  This does not certify stable IDs or complete real-deployment rebuild parity.
- [x] Explicit live smoke command passed against the configured Linode bucket
  for all five content areas without modifying existing content or access policy.
- [x] Cleanup launchers delegate to Python; optional thumbnail cleanup targets
  the selected backend and preserves originals/metadata.

### Remaining hardening

- [ ] Add reconciliation/recovery tooling for partial directory copies/deletes
  and database failures. Current errors can leave duplicate or split trees.
- [ ] Define concurrent-writer safety for moves. Destination checks are not
  atomic with S3 copying; pause competing writers during moves/migration.
- [ ] Test provider-specific multipart/encryption ETag behavior and large-object
  copy limits. Current verification fails safely if ETags differ.
- [ ] Bound scratch-space consumption and thumbnail/import parallelism for
  production; no persistent local original copy is required, but disk is needed.
- [ ] Add authenticated streaming range and conditional-cache support if needed.
  Current non-seekable S3 delivery does not provide these guarantees.
- [ ] Expand live/staging coverage for moves, edits, deletion recovery, network
  interruptions, and permission failures beyond the scoped smoke test.

## Phase 3 — Deployment, migration, and operation

- [ ] Keep anonymous bucket reads/listing denied; verify this independently
  of successful authenticated app access.
- [ ] Use least-privilege storage credentials and private persistent VPS state.
  Keep database/key backups and document credential rotation.
- [ ] Before migrating existing data, back up MariaDB and local content, pause
  writes, and copy the five content areas unchanged, including hidden metadata
  and markers for empty directories. Never upload `db_data` or `app_state`.
- [ ] Verify counts, bytes/checksums, metadata, and representative application
  reads before cutover. Retain local data for rollback and reconcile later
  writes before switching back.
- [ ] Validate the actual staging/deployed app: login, gallery navigation,
  hidden styling/listing, originals/downloads, thumbnails, blog/About embeds,
  edits, moves, deletes, and restart recovery.
- [ ] Validate rebuilding a clean database and document the remaining ID and
  metadata limitations before claiming complete portability.
- [ ] Implement streamed-media protection from TODO_RATE_LIMITING.md. Only
  login rate limiting exists today; media concurrency/bandwidth/budget controls
  are not implemented.
- [ ] Measure memory, CPU, swap, throughput, S3 latency, and errors on the actual
  VPS. Reduce indexer thumbnail concurrency or move authoring locally as planned
  in TODO_SHARED_DB.md. Upgrade VPS capacity if measured load requires it.
- [ ] Monitor storage and both Object Storage/VPS transfer usage. Evaluate
  provider metrics where available; confirm endpoint support and pricing.
  The proposed $40 estimated Object Storage threshold is not an implemented
  cutoff or a guaranteed invoice cap.
- [ ] Test backup/restore, partial-operation recovery, alerts, and operator
  transfer-budget recovery procedures.

## Completion criteria

- [x] Local works without S3 credentials; Linode core storage operations work.
- [x] Application media URLs and database paths remain provider-independent.
- [ ] Identical portable data rebuilds equivalent complete logical records,
  stable identities, relationships, and ordering with documented exclusions.
- [ ] Interrupted operations have tested reconciliation and recovery paths.
- [ ] Private streaming has measured capacity and verified abuse/budget controls.
- [ ] Deployment backup, restore, cutover, and rollback procedures are tested
  before discarding the local source copy.
