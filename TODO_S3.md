# Filesystem Storage Abstraction and S3 Deployment Plan

## Goals and invariants

- Keep MariaDB on the VPS as a queryable, rebuildable index. Gallery files and
  their metadata are authoritative in the selected file store.
- Use the same logical keys and metadata formats in local and S3-compatible
  modes, so local content can be migrated to a bucket without changing item
  identities or database schema.
- Preserve the gallery's existing relative path identity across backends. A
  MariaDB path such as `2023/2023-01/` remains that same application path in
  local and S3 modes; the selected adapter maps it to a local directory or an
  S3 key prefix. Backend selection must not rewrite paths stored in MariaDB,
  URLs, manifests, or user-facing navigation.
- Select the backend through configuration. Local development should continue
  to work without S3 credentials or network access.
- Stream file content; do not load full-size originals into application memory.
- All gallery uploads are intended to become public eventually. "Hidden" means
  omitted from normal gallery browsing/search until ready; it is a publishing
  state, not confidentiality or access control. Anyone who obtains a public
  object's URL may still view and hotlink it before it is listed in the app.
- Public hotlinking is a requirement for "view source". Do not claim that
  admin-only tags or hidden state protect an object once its bucket URL is
  public. Keep deployment credentials and unrelated private runtime data out
  of the public object store.
- Do not treat S3 as a mounted POSIX filesystem. The abstraction works with
  keys and prefixes, and does not expose physical paths or assume directories,
  atomic rename, or multi-object transactions.
- Treat direct public image URLs as an unauthenticated, potentially
  high-bandwidth service. A public object URL can be shared or requested
  repeatedly outside the application, so application-level rate limits do not
  protect direct bucket requests. Hiding an item in the app does not reduce
  this risk if its URL is known.
- Add a Linode-specific cost circuit breaker: monitor Object Storage usage and
  disable public reads when configured storage, transfer, or estimated-cost
  thresholds are exceeded. This is a containment mechanism, not a guaranteed
  hard spending cap; metric/reporting delays and already cached copies mean
  some usage can continue after the threshold is crossed.

## Phase 1 — Define and adopt the storage abstraction

### 1. Inventory and define ownership

- Inventory every persistent, application-managed file operation in the web
  app, path loader, and thumbnail service. Classify each path as content,
  metadata, deployment configuration, or runtime state.
- Define a backend-neutral portability contract before implementing adapters.
  The contract should specify the exact logical data and observable behaviors
  that must be preserved when the same store is moved between implementations;
  "same database" means equivalent logical records and gallery behavior, not
  provider-specific metadata or incidental database internals.
- Define the authoritative object set and key layout for originals,
  thumbnails, directory descriptions, `.dimdock.json` manifests, blog
  Markdown/assets, About Markdown/assets, and admin-only tag metadata.
- Decide the authority for each existing piece of metadata that is currently
  stored both in MariaDB and on disk. Make the file store authoritative and
  MariaDB a projection; define how edits, deletes, moves, and rebuilds update
  that projection.
- Keep MariaDB data files, deployment secrets, logs, and ASP.NET Data
  Protection keys outside the gallery content store. In particular, keep
  Data Protection keys in private persistent VPS storage (or a separately
  secured key service), not in a publicly served bucket.
- Specify portability invariants for the stored data:
  - Canonical application paths use one separator and trailing-slash rule.
    Preserve values such as `2023/2023-01/` in every adapter and normalize
    legacy values in a deliberate migration, not opportunistically at runtime.
  - Filenames are preserved exactly as logical names. Define Unicode
    normalization and case-collision rules that work across Windows, Linux,
    and case-sensitive S3 keys; reject or explicitly resolve names that would
    collide on any supported backend.
  - Store content bytes and authoritative metadata independently of filesystem
    timestamps, inode data, ACLs, ETags, or provider-specific headers. Persist
    creation/upload times, type, descriptions, tags, display ordering, hidden
    state, and other rebuild-required fields in a versioned portable format.
  - Define stable item identity independent of adapter and physical key.
    Persist identity in authoritative metadata so moving an item or rebuilding
    MariaDB does not unexpectedly change item IDs, URLs, shortcut targets, or
    references. Audit current auto-increment IDs and path/name-derived values
    (including thumbnail keys) and define their migration/compatibility rules.
  - Define canonical serialization, encoding, null/default handling, tag
    normalization, date/time zone semantics, type inference, and sort/order
    rules so scans of equivalent stores produce equivalent logical records.
  - Keep all relationships and non-file state required to rebuild the gallery
    recoverable from the portable store, including shortcuts and blog/About
    content where these are in scope. Clearly identify any database data that
    is intentionally not rebuildable from the file store.
- Use a versioned metadata schema with backward-compatible readers and an
  explicit migration path. Unknown schema versions or invalid metadata must
  fail visibly and be reported; do not silently drop fields during indexing.

### 2. Add a backend-neutral contract and local implementation

- Introduce an asynchronous storage interface based on validated relative
  gallery paths / object keys and streams. Include the operations the
  application actually needs: open/read, create or replace, existence/stat,
  delete, list by prefix, and move/copy where needed. Keep the application
  path (for example `2023/2023-01/`) distinct from a backend's physical
  directory or S3 object key.
- Keep path/key normalization and traversal protection in shared code.
  Reject rooted paths, `..` segments, invalid names, and key collisions
  consistently across platforms. Define one canonical separator and
  trailing-slash convention for application paths, and convert only inside
  each adapter.
- Implement the local adapter with the current directory layout and safe
  filesystem operations. Preserve local development and existing deployment
  behavior by default.
- For S3, derive object keys from the same relative application paths plus a
  configured bucket prefix; do not persist provider-specific endpoints or
  bucket names in MariaDB path values. Ensure list results are converted back
  into the same canonical application paths used by local mode.
- Use dependency injection and configuration to select the local adapter.
  Startup should fail clearly for invalid storage configuration rather than
  silently falling back to another backend.
- Define cancellation, overwrite, missing-object, and exception behavior in
  the contract. Avoid returning local paths from the abstraction.

### 3. Migrate all persistent file consumers and producers

- Replace direct gallery-file access in `FilesystemPersistenceService`,
  Gallery and Image page handlers, and Admin synchronization/deletion flows
  with the abstraction.
- Adapt `path_loader` to enumerate and read the selected store when building
  or reconciling MariaDB. Ensure it uses the same key layout, metadata
  parsing, and backend configuration as the web app. If maintaining matching
  C# and Python adapters would duplicate substantial logic, consolidate
  indexing into a .NET command-line/service rather than letting the two
  implementations drift.
- Adapt thumbnail generation and storage so both source images and generated
  thumbnails use the selected store. The thumbnail worker may process a
  streamed/local temporary input, but temporary files must be bounded,
  cleaned up, and must not become the persistent source of truth.
- Route original and thumbnail reads through stable, hotlinkable public URLs
  for "view source". Preserve content types, download names, and
  caching/ETag behavior where available.
- Define hidden/published behavior explicitly: hidden items are excluded from
  normal gallery listings, search, and navigation, while published items are
  listed normally. Both states remain publicly fetchable by direct URL in S3
  mode. Do not present hidden state or admin-only tags as security controls.
- Consider opaque or unguessable object keys to reduce accidental discovery,
  but do not treat them as access control; a URL can be copied or shared.
- Replace filesystem directory checks with key/prefix listing or explicit
  metadata. S3 has no real directories; empty-directory semantics must be
  represented deliberately if the gallery needs them.
- Ensure all persistent content paths, including blog/About assets and
  metadata files, are accounted for. Static application assets under
  `wwwroot` remain part of the application deployment, not user content.

### 4. Make file-store changes recoverable and verify local parity

- Treat a successful write to the file store as the authoritative commit.
  Update MariaDB afterward; if index synchronization fails, report the error
  and provide a retry/rebuild path instead of deleting the committed object
  or reporting success.
- Make database indexing idempotent and rebuildable from the selected store.
  Define how interrupted uploads, partially completed moves, orphaned
  thumbnails, and stale index rows are detected and reconciled.
- For local moves, preserve safe same-volume rename behavior where possible.
  Structure operations so the S3 adapter can implement move as copy, verify,
  then delete. Do not claim atomicity across the file store and MariaDB.
- Add tests for local storage contract behavior, key validation, streaming,
  CRUD/list/move, overwrite and missing-object semantics, cancellation, and
  failure recovery. Add integration tests for upload, display/download,
  delete, rename/move, metadata edits, and MariaDB rebuild. Include a canonical
  fixture with mixed-case and Unicode names, nested paths, hidden and
  published items, shortcuts, tags, descriptions, timestamps, and ordering.
- Define a normalized logical-database snapshot for parity tests. Index the
  same fixture through each adapter and compare paths, stable item identities,
  metadata, relationships, and user-visible ordering while ignoring only
  explicitly nondeterministic/provider-specific fields.
- Run existing tests and regression checks against the local adapter before
  proceeding. Preserve a documented backup/restore procedure for local data.

## Phase 2 — Implement an S3-compatible adapter

- Implement the same storage contract using an S3-compatible SDK, configured
  with endpoint/region, bucket, key prefix, and credentials from environment
  variables or a deployment secret manager. Do not check credentials into
  source control or expose them to the browser.
- Map logical relative keys to object keys without changing their meaning.
  Store content types and useful cache metadata with objects. Configure
  public read access for the gallery media objects required by hotlinking,
  while disabling bucket listing where supported. Do not store credentials,
  Data Protection keys, or unrelated private deployment data in this public
  namespace.
- Preserve MariaDB and application path values exactly when translating to
  S3 keys. For example, `2023/2023-01/` must refer to the same logical gallery
  location in local mode and in the S3 adapter; bucket names, endpoints, and
  configured deployment prefixes are adapter configuration, not path data.
- Implement safe replacement and delete semantics, prefix listing with
  pagination, cancellation, and streaming. Handle provider errors explicitly.
- Implement moves as copy-to-destination, verify destination availability,
  then delete source. Define retry/idempotency and recovery behavior if a
  copy or delete fails midway; do not update the MariaDB index to the new
  location until the authoritative move is complete.
- Choose and document consistency for related objects (for example an image,
  thumbnail, and sidecar manifest). Use a versioned manifest or a reconciliation
  process where a multi-object change can be interrupted.
- Implement public-read policy for gallery media, including not-yet-published
  uploads, and ensure bucket listing is disabled where supported. Prefer a
  dedicated media bucket/prefix so private operational data cannot be exposed
  by its access policy.
- Add adapter integration tests against a local S3-compatible test service,
  including pagination, large streamed objects, permissions, failed copies,
  interrupted operations, and cross-backend parity against the canonical
  fixture. Add a small, explicit Linode Object Storage smoke test before
  deployment.
- Verify that the indexing/rebuild tool can scan the S3 adapter and recreate
  MariaDB without relying on a mounted bucket or local image copy.

## Phase 3 — Prepare, migrate, and deploy to Linode

### 1. Prepare the deployment

- Confirm the target bucket/endpoint region, public-read policy for media,
  disabled listing where supported, credentials with least privilege, and a
  distinct key prefix for the deployment.
- Integrate with Linode/Akamai monitoring APIs for the bucket's stored size and
  downloaded-byte metrics, and with account transfer/billing data where
  available. The bucket API's current size and object count are useful
  inventory signals; use time-series download/egress metrics for abuse
  detection. Do not equate bucket egress alone with the account's bill: transfer
  pools and overages can be account/region-wide and shared with other services.
  Use the [Object Storage bucket API](https://techdocs.akamai.com/linode-api/reference/get-object-storage-bucket)
  for current bucket size/object count and [Cloud Pulse Object Storage metrics](https://techdocs.akamai.com/cloud-computing/docs/object-storage-cloud-pulse-metrics)
  for bytes downloaded and egress. Confirm endpoint compatibility first:
  Cloud Pulse monitoring is supported for standard E2/E3 endpoints, and the
  documented byte metrics are time-series usage signals, not necessarily a
  final per-bucket invoice total.
- Make the storage ceiling, monthly egress ceiling, and estimated-cost ceiling
  explicit deployment settings, chosen before launch. Define polling cadence,
  metric freshness, alert thresholds, and whether thresholds are evaluated
  per-bucket or account-wide. Prefer provider-reported usage; document any
  locally computed cost as an estimate and account for transfer pools and
  regional overage pricing.
- Implement the emergency public-access shutdown using a separately secured
  credential and the supported Object Storage bucket policy or ACL mechanism.
  Monitoring uses the Linode API; changing anonymous access may instead require
  an S3-compatible bucket-policy/ACL operation, depending on endpoint type.
  Prefer bucket policies where supported; verify endpoint-specific support in
  [Akamai's bucket-policy docs](https://techdocs.akamai.com/cloud-computing/docs/define-access-and-permissions-using-bucket-policies).
  The normal application S3 credentials must not be able to change
  public-access policy. Test that the policy change denies anonymous reads
  while the app can still use authenticated S3 access for uploads and
  maintenance.
- On threshold breach, alert and apply the private policy automatically;
  record the trigger, observed metric values, and timestamp. Treat stale or
  unavailable monitoring data as an explicit operational condition with a
  documented policy (for example, disable public access if data is stale beyond
  a configured grace period). Require operator review before restoring public
  access, and document that making the bucket private breaks hotlinks and may
  not revoke copies already cached or downloaded.
- Configure the app and index/rebuild process to select S3. Keep MariaDB on
  the VPS with persistent backups; keep Data Protection keys and other
  deployment-only state in private persistent VPS storage.
- Ensure upload limits, reverse-proxy timeouts, streaming, connection limits,
  logging, and health checks are appropriate for the 1 GB Nanode. Avoid
  unbounded buffering or parallel thumbnail work that can exhaust memory.
- Provide stable hotlinkable URLs for image originals and thumbnails, using a
  custom media/CDN hostname if available. Document clearly that hidden and
  published objects are both directly fetchable without logging in, and that
  app listing/search controls only affect discoverability in the gallery UI.
- Put a CDN in front of public objects and configure long-lived caching for
  immutable/versioned object keys, cache revalidation for mutable keys,
  origin shielding, and query-string cache-key normalization where safe.
  Determine whether direct requests to the raw Object Storage URL can bypass
  the CDN. If public bucket URLs remain accessible, CDN rate limiting and
  caching do not protect the origin from direct requests; explicitly accept
  that residual abuse/transfer risk or choose a delivery architecture that
  can enforce all reads through the protected edge.
- Add edge rate limits and bot/DDoS controls for public media, with sensible
  per-IP and aggregate thresholds. Treat Referer-based hotlink prevention as
  an optional abuse signal only: clients can omit or forge Referer, and it is
  not access control. Since originals are intended to be hotlinkable, assess
  whether public direct-origin access creates an unacceptable cost risk before
  launch; consider edge-enforced delivery, CDN-only links, or an explicit
  operational kill switch rather than assuming hidden state protects originals.
- Set image and response size limits, cache headers, and request timeouts;
  avoid cache-busting query parameters bypassing CDN caching. Configure
  Linode Object Storage metrics/API polling, usage alerts, and billing
  notifications. Define and test the automated private-bucket circuit breaker
  and operator recovery procedure. Verify current transfer-pool and overage
  behavior; a transfer allowance is not a hard spending cap.
- Measure expected storage and outbound transfer, including Object Storage's
  transfer allowance and account-level pool/overage behavior. Confirm current
  regional pricing and monitor usage after launch.

### 2. Migrate and validate

- Take a MariaDB backup and a consistent backup of local content before
  migration. Pause writes or otherwise establish a consistent cutover point.
- Upload originals, thumbnails, and authoritative metadata while preserving
  the current relative gallery paths as S3 key prefixes. Do not change
  MariaDB path values (for example `2023/2023-01/`) or delete local files as
  part of the upload step.
- Verify object counts, total bytes, representative checksums, content types,
  metadata, and application reads. Run a dry-run index rebuild and compare its
  result with the expected gallery before replacing the live index.
- Test the circuit breaker in a non-production bucket: simulate threshold
  breach and stale metrics, verify alerts are delivered, confirm anonymous
  reads are denied after the policy change, and verify recovery requires the
  documented operator action. Confirm that the monitoring credential cannot
  modify bucket policy and that the policy credential is not available to the
  normal application process.
- Switch configuration to S3, rebuild/reconcile MariaDB from the bucket, then
  test login, hidden/published listing behavior, gallery browsing, direct
  public image delivery, uploads, edits, moves, deletes, metadata persistence,
  and restart/recovery behavior.
- Verify public "view source" URLs from an unauthenticated browser, confirm
  they are cacheable and hotlinkable, and verify that hidden media remains
  fetchable by direct URL as documented. Test CDN rate limiting, whether raw
  origin access bypasses it, the cost-control/kill-switch procedure, and
  billing alerts.
- Keep the local copy until the S3 deployment has passed validation and a
  restore test. Document rollback: switch back to local only after reconciling
  any writes made since cutover, to prevent divergent authoritative copies.

### 3. Operate and maintain

- Document how to back up/restore both the bucket and MariaDB, rotate access
  keys, rebuild the index, and reconcile partial operations.
- Monitor storage, transfer pool usage/overages, S3 errors/latency, app memory,
  and failed indexing. Alert on failures that could leave MariaDB stale.
- Review circuit-breaker events, metric freshness, estimated versus invoiced
  transfer costs, and false-positive/false-negative behavior. Keep the
  emergency policy-change credential separate from the application and
  monitoring credentials; do not automatically restore public access.
- Periodically test recovery from the bucket into a clean MariaDB instance.

## Acceptance criteria

- Local mode works without S3 credentials and passes existing gallery behavior
  and storage tests.
- All persistent user-managed files use the abstraction; no web, loader, or
  thumbnail code depends on `/img_src`, `/img_db_thumbs`, or other content
  paths as physical files.
- S3 mode can upload, read, list, edit, move, delete, and rebuild the MariaDB
  index from objects; hidden/published state controls app discoverability,
  not direct access to public object URLs.
- Existing relative gallery paths remain identical across local and S3 modes;
  switching adapters does not require rewriting MariaDB path records.
- Given identical portable content and metadata, local and S3 indexing
  produces equivalent logical MariaDB records, stable item identities, and
  user-visible behavior; differences are limited to documented
  provider-specific/runtime fields.
- Interrupted cross-object operations are recoverable and surfaced clearly.
- A documented backup, restore, verification, and rollback procedure has been
  tested before removing the local source copy.
