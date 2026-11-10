# Shared database: local authoring, read-only public site

Future idea: run uploads, thumbnails and edits on a local machine, and serve
readers from the VPS. Both share the bucket and the VPS's MariaDB.

- [ ] Configure an external database host (reach it over VPN/SSH tunnel, never
  the open internet) without breaking the bundled-MariaDB default.
- [ ] Server-enforced read-only mode for the public instance, with read-only
  database and bucket credentials.
- [ ] Let the public instance start without the path loader or thumbnail
  service, and never import stale blog/About content.
- [ ] Ensure only the authoring instance indexes, and that its changes show
  publicly without a rescan.
- [ ] Fail visibly when the tunnel is down; existing single-host deployments
  must keep working.
