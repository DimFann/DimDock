# Media rate limiting and capacity

All media is streamed from the private bucket through the app, so heavy traffic
costs VPS memory, bandwidth and Object Storage transfer. Only the login page is
rate limited today.

- [ ] Enable forwarded headers for the local nginx proxy (and Cloudflare's
  client-IP header). Without this every request appears to come from
  `127.0.0.1`, so the login limit is shared by all visitors.
- [ ] Per-client limits for media and expensive pages (search, gallery), with a
  global cap on concurrent S3 downloads. Reject with 429/503 before opening S3.
- [ ] Let Cloudflare cache public media (originals/thumbnails), never admin or
  hidden-item responses. Purge on edit/move/delete.
- [ ] Monthly transfer budget with alerts and a kill switch for new media
  fetches.
- [ ] Load-test realistic browsing on the VPS; measure RAM, CPU, bandwidth and
  latency, and document when to upgrade.
