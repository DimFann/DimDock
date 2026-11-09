# Private streaming: rate limits and overload protection

## Goal

Keep Linode Object Storage private and stream all public media through the app.
Preserve hotlinkable application URLs while containing resource exhaustion and
transfer abuse. These controls are planned, not implemented. Upgrade the VPS
when measured capacity requires it.

## Request and concurrency limits

- [ ] Inventory gallery originals, thumbnails, blog/About assets, image source
  and download handlers, search, and other expensive routes. Cover equivalent
  routes so alternate URLs cannot bypass limits.
- [ ] Configure trusted proxies and forwarded headers before partitioning limits
  by IP. Reject untrusted forwarded identities; account for shared NAT and IPv6.
- [ ] Add per-client request limits with separate policies for thumbnail browsing,
  large originals/downloads, and expensive database requests.
- [ ] Add global concurrent-download limits with a bounded queue; acquire permits
  before opening S3 streams and release them on success, failure, and disconnect.
- [ ] Return explicit 429/503 responses and Retry-After where appropriate. Preserve
  existing login limits and avoid unbounded client-partition bookkeeping.
- [ ] Set upstream/request timeouts, cancellation propagation, response-size limits,
  and slow-client handling. Ensure rejected requests do not fetch S3 objects.

## Transfer and cost containment

- [ ] Define configurable per-client and aggregate bandwidth/byte budgets;
  request counts alone do not bound transfer.
- [ ] Account separately for S3 bytes fetched and VPS bytes delivered, including
  aborted transfers, retries, HEAD, and any future range requests.
- [ ] Persist aggregate budget state across restarts and coordinate it across
  replicas if deployed. Define window boundaries and operator reset/recovery.
- [ ] Implement an operator kill switch and budget cutoff for new media fetches.
  Specify behavior for in-flight streams and stale/unavailable accounting.
- [ ] Evaluate the previously proposed $40 monthly estimated Object Storage
  threshold with actual regional pricing and allowances; document that this is
  not a guaranteed invoice cap and does not include all VPS transfer charges.
- [ ] Add alerts for approaching limits and shutdown; never change bucket access
  policy as part of media-rate limiting.

## Caching and host controls

- [ ] Evaluate bounded disk/proxy caching to reduce repeated S3 downloads.
  Specify invalidation for edited/moved/deleted files and mutable metadata.
- [ ] Avoid cache-key bypass via arbitrary query strings; preserve intended URL
  semantics and never cache admin/session-sensitive responses publicly.
- [ ] Consider node/proxy bandwidth shaping and connection limits as additional
  controls, not substitutes for application-side S3-fetch limits.
- [ ] Tune MariaDB/website memory and monitor swap, OOM kills, CPU, bandwidth,
  connection counts, S3 errors, queue lengths, rejections, and response latency.

## Verification

- [ ] Test all media routes, GET/HEAD, disconnects, upstream failures, and denied
  requests; prove no leaked permits or unexpected S3 downloads.
- [ ] Test shared-NAT fairness, distributed-IP abuse, spoofed forwarded headers,
  large objects, slow clients, and exhausted global queues.
- [ ] Test budget enforcement, restarts, window rollover, alerts, and operator
  recovery without silent resets or success-shaped failures.
- [ ] Load-test 10–100 browsing users using representative image sizes and
  navigation patterns. Establish safe limits on the actual Nanode and document
  upgrade thresholds. Keep thumbnail generation off the public host as planned
  in TODO_SHARED_DB.md.
