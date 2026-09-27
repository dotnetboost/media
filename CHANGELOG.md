# Changelog

All notable changes to DotNetBoost.Media are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed — **breaking**: one file can now belong to several entities

A media row carried its own `OwnerEntityName` and `OwnerEntityId` — one slot, one owner — so
attaching a file to a second entity overwrote the first and the file silently left it. The link
moves to a row of its own.

`MediaFile` loses both owner columns. `MediaAttachment` gains
`(MediaFileId, OwnerEntityName, OwnerEntityId, Role, SortOrder)`, unique on all but the position.
One extra table in total: adding media to a new entity type still costs no schema change.

**Migrating.** `1.0.0-preview.1` is untagged and no automatic backfill ships. If you ran the
preview, create the new table (`DapperSchema.EnsureCreatedAsync`, `ApplyMedia`, or
`MongoMediaIndexes.EnsureCreatedAsync`) and insert one attachment per row that had an owner, with
`Role = 'default'` and `SortOrder = 0`, then drop the two owner columns. **EF Core consumers need a
new migration**: the model gained an entity type and lost two properties.

- `MediaFile.OwnerEntityName` / `OwnerEntityId`, `MediaFileInfo.OwnerEntityName` / `OwnerEntityId`,
  `MediaUpload.Owner`, and `MediaUpdate.Owner` / `ClearOwner` are removed.
- `IMediaAccessor.AttachAsync(id)` is replaced by `AddAsync(id, role)` and `SetAsync(role, id)`.
  `AddAsync` appends within a role and is idempotent; `SetAsync` takes a single-valued role over
  from whatever held it, in one operation rather than two with a window in between. No schema can
  know that `primary` holds one file and `gallery` holds many, so the verb carries it.
- `IMediaAccessor.ListAsync(string category)` is renamed `ListByCategoryAsync`. It had to be renamed
  rather than overloaded: a role and a category are both strings, so leaving the old signature to
  mean role would have changed what existing calls did with no compiler error.
- `IMediaAccessor.DeleteAllAsync` now deletes only the files that entity was the **sole holder** of
  and merely detaches the ones others still reference — deleting one product must not pull a shared
  image out of the rest. Its return value counts only the files actually destroyed.
- `IMediaManager.DeleteAsync` cascades every attachment, so deleting a file removes it from every
  entity using it. Attachment rows go outright even under `SoftDelete`: a tombstoned file that still
  read as attached to a live entity would be worse than no record at all.
- `IMediaStore` gains seven attachment operations. Custom stores must implement them.
- REST: `PATCH /api/media/{id}` loses `ownerEntityName`, `ownerEntityId` and `clearOwner`;
  `MediaFileResponse` loses the two owner fields and gains `attachments` on the single-file read
  (`null` in listings). `POST`/`DELETE /api/media/{id}/attachments` and
  `PUT /api/media/attachments/order` replace them.
- Ordering by an entity's own arrangement is defined as its lowest role then its lowest position,
  taken independently. The exact `(role, position)` pair needs a correlated subquery ordered and
  limited inside an `ORDER BY`, which translates unevenly across three SQL dialects and not at all
  on MongoDB. Narrowed to one role — the ordering anyone actually asks for — the two are identical.

Unrelated to the above:

- `MediaQuery.OldestFirst` is replaced by `SortBy` + `Descending`. `new MediaQuery { OldestFirst = true }`
  becomes `new MediaQuery { Descending = false }`.
- `samples/SampleApp` maps its own routes under `/api` (`/api/products`), so the dashboard reaches
  the library's endpoints and the sample's through one proxy — as the sibling samples do. Its
  product endpoints now carry roles: `?role=` on upload, `PUT .../images/primary`,
  `PUT .../images/gallery/order`, and `DELETE .../images/{mediaId}` to take an image off one product
  without deleting it.
- `clients/dashboard` shows every entity using a file rather than one owner, warns on delete how
  many lose it, groups a product's media by role with make-primary and reordering, and offers
  "remove from product" as distinct from deleting the file.

### Added

- `MediaAttachment` and `MediaRoles`. A role is what an entity uses a file *for* — `primary`,
  `gallery`, `seo`, or whatever an application means. It lives on the link and not on the file
  because the same image can be one product's main photo and another's third gallery shot. Free
  text, lower-cased on write, and never null: it defaults to `MediaRoles.Default`, which is what
  lets the unique index cover all four columns. A nullable column inside it would reject the second
  NULL on SQL Server and allow unlimited NULLs on PostgreSQL and SQLite — the same DDL enforcing the
  constraint on one dialect of three.
- `IMediaAccessor.ReorderAsync`, `DetachAsync`, `DetachAllAsync`, `ListByRoleAsync`,
  `GetByRoleAsync` and `ListWithRolesAsync`. `ListWithRolesAsync` pairs each of an entity's files
  with the role it holds, so a product page draws its hero, its ordered gallery and its SEO picture
  from one read instead of one per role; it is per link, so a file held under two roles is listed
  twice.
- `IMediaManager.GetAttachmentsAsync` — everything using a file, so a UI can say "used by 4
  entities" before offering a delete that removes it from all of them.
- `MediaQuery.Role` and `MediaQuery.Unattached`, and `MediaSortBy.SortOrder`. Each needs an owner
  filter and is refused without one. Without an owner the attachment table is not touched at all,
  which is what keeps a file linked to nine entities from appearing nine times in a listing, and a
  file linked to none appearing in it at all — `Unattached` is an opt-in sweep filter, never a
  default.
- `MongoMediaIndexes.EnsureCreatedAsync`, the counterpart of `DapperSchema.EnsureCreatedAsync` and
  EF Core's `ApplyMedia`. Index creation was reachable only through the startup hosted service,
  leaving no way for a deployment step — or a test — to create the unique index the store's contract
  rests on.
- `MediaModelOptions.MediaAttachmentsTable`, `DapperOptions.MediaAttachmentsTable` and
  `MongoDbOptions.MediaAttachmentsCollection`.
- `MediaQuery.Search` — matches anywhere in the file name, case-insensitively. Both sides are
  lowered before the comparison, because `LIKE` is case-sensitive on PostgreSQL and insensitive on
  SQL Server; matching the column's collation would make the documented behaviour true on one
  engine only. A value containing `%` or `_` is escaped.
- `MediaQuery.SortBy` (`MediaSortBy.CreatedAt`, `FileName` or `Size`) and `MediaQuery.Descending`.
  Every ordering ends on the id, so rows that tie on the sort column still page stably.
- `GET api/media` takes `search`, `sortBy` and `descending`. An unrecognised `sortBy` falls back to
  the default rather than failing the request.
- `clients/dashboard` — a Nuxt 4 client, on the same template as the DotNetBoost.Settings and
  DotNetBoost.MultiLingual dashboards. A **Media** page over the whole library (search, category,
  tag and kind filters, six sort orders, pagination, upload, rename, retag, delete) and a
  **Products** page whose attach button opens that same browser in a modal. The Aspire AppHost
  starts it alongside the API.

### Fixed

- Deduplication no longer discards the entity an upload was for. With `DeduplicateByChecksum` on,
  `media.For(productB).UploadAsync(sameBytes)` returned product A's existing file and dropped
  `upload.Owner` on the floor, so the call reported success and attached nothing. It was unfixable
  while a row held one owner; the accessor now stores or deduplicates the bytes and then attaches,
  so identical bytes mean one file and two links.
- `AmazonS3StorageProvider.GetUrlAsync` pre-signs on the scheme its client's `ServiceURL` uses.
  The AWS SDK pre-signs over HTTPS regardless of how the client is configured, so a provider
  pointed at an S3-compatible endpoint served over plain HTTP — an on-prem gateway, a local proxy,
  MinIO or LocalStack without TLS — handed out `https://` links that endpoint never answers on.
  A client with no `ServiceURL` is talking to AWS and still gets HTTPS.
- `EfCoreMediaStore.AddAsync` and `DapperMediaStore.AddAsync` report a failed insert as
  `MediaConflictException` only when the id really is taken. They previously mapped every
  `DbUpdateException` and `DbException` to a conflict, so a missing table or a dropped connection
  surfaced as a duplicate id — and as a 409 through the API, which no retry could resolve. On
  PostgreSQL the Dapper store reads SQLSTATE `23505`, because a failed statement leaves a
  transaction aborted and a follow-up query inside the application's own transaction would fail
  too; on SQL Server and SQLite, whose transactions survive a constraint violation, it looks the
  row up rather than reference those providers' packages for an error code.

## [1.0.0-preview.1]

First public preview. Everything below is new.

### Core — `DotNetBoost.Media.Core`

- `IMediaManager`: upload, find, read, link, update, delete and list, plus `For(entity)` for media
  attached to one of your own entities.
- `MediaFile`: one row per upload, recording what a file is, where its bytes are, what it is
  attached to, and who uploaded it when. No foreign key back to your entities, so one table serves
  all of them.
- Single-pass uploads. Size, SHA-256 and the size limit all come out of the one read the storage
  backend performs, so a multi-gigabyte upload costs no memory and no second pass.
- Content-type verification against the file's own leading bytes, for the formats a signature can
  identify — including the container formats (OOXML, OpenDocument, ISO base media) where the
  signature is deliberately less specific than the declared type.
- Size limits, extension allow- and block-lists (executables blocked by default), media-type
  allow-lists with `image/*` family matching.
- Storage paths derived from the file's id, never from the uploaded name, and validated again on
  the way back out of the store.
- Optional deduplication by checksum, off by default.
- Soft delete, on by default: the bytes always go, the row stays.
- `MediaBuilder`: `services.AddMedia().Use…().Build()`, with a startup probe of every configured
  storage backend.

### Metadata stores

- `DotNetBoost.Media.EntityFrameworkCore` — the table in your own `DbContext`, so a file and its
  entity commit in one transaction. `modelBuilder.ApplyMedia()`.
- `DotNetBoost.Media.Dapper` — the same table through Dapper, with optional startup DDL for
  SQL Server, PostgreSQL and SQLite, and transaction enlistment.
- `DotNetBoost.Media.MongoDb` — the rows as documents in one collection, keyed by the file's id.

### Storage backends

- `DotNetBoost.Media.FileSystem` — local disk, with containment proved against the resolved root,
  empty-directory pruning, optional public URLs, and a startup writability probe.
- `DotNetBoost.Media.AzureBlobStorage` — blobs, with time-limited SAS URLs, a private container
  created at startup, managed-identity support, and CDN rewriting.
- `DotNetBoost.Media.AmazonS3` — S3 objects, with pre-signed URLs, key prefixes for a shared
  bucket, storage class and server-side encryption. Works with MinIO, Cloudflare R2, DigitalOcean
  Spaces and Backblaze B2.

Several backends can be registered at once, and each row records the one that wrote it — so
changing which backend receives new uploads does not strand the files that came before.

### ASP.NET Core — `DotNetBoost.Media.API`

- `MapMediaEndpoints()`: upload (multipart), list with filters and paging, metadata, download with
  Range and ETag support, direct URL, patch and delete.
- `MediaExceptionHandler` and RFC 7807 problem details: 413 for an oversized upload, 415 for a
  rejected or mismatched type, 404 for a missing file or missing content, 409 for a conflict,
  502 when the storage backend is what failed.
- Responses deliberately omit the storage provider, container and path.

[Unreleased]: https://github.com/dotnetboost/media/compare/v1.0.0-preview.1...HEAD
[1.0.0-preview.1]: https://github.com/dotnetboost/media/releases/tag/v1.0.0-preview.1
