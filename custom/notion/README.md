# Windows Notion vocabulary extension

Work in progress. This branch is based on the retained Windows source revision
`a6fee12757b15edbeef7c60f8895e0be694801e1`, not the newest upstream revision.

The installed personalized executable contains additional changes not represented
by that source tree. Delivery therefore uses a reproducible, narrowly scoped
assembly patch against the existing executable. Its original resources and
unrelated method bodies must remain intact. Do not deploy a normal upstream build
over the personalized installation.

The extension will provide paired read-only caption selection, an independent
durable FIFO queue, local Windows credential encryption, and official Notion API
archiving. A remote write with an uncertain outcome is reconciled by a stable note
identifier; it is never blindly re-posted. No credentials, personal configuration,
caption history, or installed binaries belong in this repository.

Initial implementation is incomplete and not yet built or tested. Build and
acceptance instructions will be added with the completed implementation.
