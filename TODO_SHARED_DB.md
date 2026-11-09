# Shared database: local authoring and Nanode delivery

## Goal

Run the authoring instance on a higher-spec local machine and the public
instance on the 1 GB Nanode. Both use the same private Linode Object Storage
bucket and the Nanode's MariaDB. Uploads, thumbnail generation, and edits run
locally; the Nanode serves readers. This is future work, not the current
deployment configuration.

## Connection and deployment configuration

- [ ] Allow configuration of an external database host/port independently of
  Compose's bundled MariaDB service. Provide separate authoring and public
  deployment configurations without breaking the existing default.
- [ ] Connect the local instance through a VPN or SSH tunnel. Do not publish
  MariaDB to the unrestricted internet. Document tunnel startup, reconnection,
  and failure behavior; require encrypted transport where appropriate.
- [ ] Use distinct database credentials for local authoring and public reads.
  Audit startup and request-time writes before granting the public instance
  only the minimum required privileges.
- [ ] Configure both instances with the same bucket, deployment prefix, and
  logical key layout. Keep runtime state and Data Protection keys private
  and persistent on each host; do not share login encryption keys by default.
- [ ] Keep secrets out of source control and logs. Document backup and restore
  for the shared database and authoritative object-store content.

## Public read-only mode

- [ ] Add a server-enforced read-only mode that disables uploads, edits,
  deletes, moves, admin synchronization, and other mutation handlers.
  Hiding controls alone is insufficient.
- [ ] Audit all routes and background work for database/storage mutations.
  Give public storage credentials read-only access where feasible.
- [ ] Allow the public instance to start without running the path loader or
  thumbnail service. Never generate missing thumbnails on the Nanode.
- [ ] Keep private-bucket media streaming and existing hotlinkable application
  URLs. Verify originals, thumbnails, blog/About assets, downloads, and HEAD
  requests work with read-only credentials.

## Authoring and consistency

- [ ] Run uploads, thumbnail generation, metadata persistence, and indexing
  only on the local authoring instance. Prevent concurrent indexers and
  reconciliation jobs from racing edits.
- [ ] Verify changes become visible on subsequent public requests without a
  second database scan. Define cache invalidation/revalidation if caching is
  introduced.
- [ ] Test failure recovery across object-store writes and database updates:
  interrupted uploads, unavailable database/tunnel, moves, deletes, and
  stale index entries. Do not claim a distributed transaction.
- [ ] Ensure public startup cannot reimport stale blog/About content or reset
  initialization state. Keep database schema changes coordinated and backed up.
- [ ] Document how to rebuild the shared index from authoritative storage,
  including current stable-ID and metadata-rebuild limitations in TODO_S3.md.

## 1 GB Nanode capacity and overload protection

Detailed delivery protection work is tracked in [TODO_RATE_LIMITING.md](TODO_RATE_LIMITING.md).

- [ ] Tune MariaDB memory and measure the website/database working set.
- [ ] Add per-client request limits and global media-download concurrency
  limits, with explicit overload responses and cancellation handling.
- [ ] Evaluate aggregate bandwidth/transfer-budget enforcement for private
  streaming. Request counts alone do not bound bytes or hosting cost.
- [ ] Configure trusted proxy handling before relying on client-IP limits.
- [ ] Load-test representative thumbnail browsing and original downloads at
  10–100 browsing users. Measure RAM, swap, CPU, bandwidth, latency, failures,
  and service restarts; establish safe limits rather than promising capacity.
- [ ] Monitor memory pressure and outages; document when to upgrade the Nanode.

## Acceptance checks

- [ ] A local upload creates its thumbnail locally and appears on the public
  site through the shared database and bucket.
- [ ] Local blog/About edits, tags, moves, shortcuts, and deletions are reflected
  publicly without running the public loader.
- [ ] Public mutation requests fail server-side and read-only credentials cannot
  write to MariaDB or Object Storage.
- [ ] The Nanode never decodes uploads or generates thumbnails.
- [ ] Tunnel outages fail visibly without unsafe fallbacks; authoring recovery
  does not corrupt existing content.
- [ ] Existing single-host Local and Linode deployments remain supported.
