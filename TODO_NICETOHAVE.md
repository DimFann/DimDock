# Nice to have

Optional improvements that are not required for correct operation.

## Direct gallery uploads

- [x] Recover orphaned direct uploads. If the browser closes after the presigned
  PUT reaches the bucket but before `CompleteUpload` registers it, the next
  attempt offers to register the existing object in the gallery.
