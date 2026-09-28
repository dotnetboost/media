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
- `IMediaStore` gains eight attachment operations, one of them `UpdateAttachmentsAsync` — several
  changed links saved in one call, which is what reordering a gallery actually is. Custom stores
  must implement them. A store with nothing better available can loop over
  `UpdateAttachmentAsync`, but the point of the method is that it need not: the shipped stores do
  it in one command and, more importantly, one transaction.
- REST: `PATCH /api/media/{id}` loses `ownerEntityName`, `ownerEntityId` and `clearOwner`;
  `MediaFileResponse` loses the two owner fields and gains `attachments` on the single-file read
  (`null` in listings). `POST /api/media/{id}/attachments`,
  `DELETE /api/media/{id}/attachments/{attachmentId}` and `PUT /api/media/attachments/order` replace
  them. A link is addressed by its own id, which the single-file read hands back; the file id in the
  route is checked, so an attachment belonging to another file is a `404`.
- `MediaFile.Category` is trimmed and lower-cased on write, as tags and roles are, and a category
  filter is lower-cased to match. It matters more for a category than for either: the category leads
  the generated storage path, so `Product-Images` and `product-images` were two buckets *and* two
  directories in the backend holding the same kind of file. Rows written before this keep whatever
  case they were stored under and stop matching a normalized filter; there are no released
  consumers, so no backfill ships. An empty string now clears a category rather than storing one.
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
  "remove from product" as distinct from deleting the file. Its picker reads the roles from
  `GET api/media/roles/{entityName}` instead of hard-coding one entity's vocabulary, takes
  replace-or-append from the declared cardinality, and falls back to a free-text box for an entity
  that declares nothing.

### Added

- `[MediaRole]`, and the checking it turns on. An entity declares which roles its files may be
  attached under and whether each holds one file or many; `DeclareRolesFrom(assembly)`,
  `DeclareRoles<TEntity>()` or `DeclareRoles(name, r => r.One("…").Many("…"))` reads them at
  `Build()`. Entirely opt-in — an entity that declares nothing accepts any role, exactly as before.

  It catches what the verbs alone cannot. Nothing told `AddAsync` that `primary` was meant to hold
  one file, and `SetAsync` on a nine-image gallery detached eight of them and looked like it had
  worked. Both are now refused with a message naming the operation to use instead, as is a
  misspelled role — which otherwise became an attachment nothing ever read.

  Declarations are filed under the entity's **stored name**, not its type, and read once at startup.
  Half the ways of attaching have no class to read an attribute from: `media.For("Product", "42")`
  is two strings, and the library's own REST endpoints never see the application's types at all.
  Keying by name is what stops the same mistake being refused one way and accepted the other.

  Reading them happens at `Build()` rather than when the builder method is called, so the order of
  those lines cannot matter — scanning eagerly would file roles under names a later line could still
  change, and a registry keyed on the wrong name silently checks nothing.
- `IMediaManager.RolesFor(entityName)` and `GET /api/media/roles/{entityName}`, so a UI can offer the
  right roles for whatever it was opened for instead of hard-coding one entity's vocabulary. An
  undeclared entity returns an empty list rather than a 404: it accepts any role.
- `MediaCategories.Normalize` and `MediaCategories.MaxLength`.
- `MediaRoles` holds only the rules for a role — `Default`, `MaxRoleLength`, `Normalize` — and ships
  no role names. It briefly had `Primary` and `Gallery` constants; they were two example strings
  dressed as library concepts, they had no counterpart for `seo`, and neither `MediaTags` nor
  `MediaCategories` ships example values either. Role names belong in the application's own class,
  next to the entity, which is also where a typo becomes a compile error. The length was a literal `64`
  repeated in the EF Core mapping and the Dapper DDL while every other column's came from a Core
  constant; both now read it from one place.
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
  entities" before offering a delete that removes it from all of them — and
  `IMediaManager.RemoveAttachmentAsync(mediaFileId, attachmentId)`, for a caller holding a link
  rather than an entity to scope by. Application code that knows the entity should still use
  `IMediaAccessor.DetachAsync`, which cannot reach another entity's links at all.
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

- `samples/SampleApp` answers a malformed JSON body with `400` in Development as well as in
  Production. `UseExceptionHandler()` rethrows what no handler claims, and the developer exception
  page then rendered `BadHttpRequestException` — which already carries `400` — as a `500`, so the
  same request answered differently on a developer's machine than on a server. A ten-line
  `BadRequestExceptionHandler` honours the status the exception carries and returns problem details
  naming the offending field. `MediaExceptionHandler`'s own documentation now warns about it, since
  it is the line telling applications to enable `UseExceptionHandler()` in the first place.
- Deduplication no longer discards the entity an upload was for. With `DeduplicateByChecksum` on,
  `media.For(productB).UploadAsync(sameBytes)` returned product A's existing file and dropped
  `upload.Owner` on the floor, so the call reported success and attached nothing. It was unfixable
  while a row held one owner; the accessor now stores or deduplicates the bytes and then attaches,
  so identical bytes mean one file and two links.
- Reordering a gallery is one write rather than one per image. `ReorderAsync` renumbered the role
  by calling the store once per moved file, and a store commits per call, so saving a fifty-image
  drag cost fifty round trips and fifty transactions — and a failure partway through left the
  gallery in an order the user never chose and could not describe. It now hands the moved rows to
  `IMediaStore.UpdateAttachmentsAsync` together: the EF Core store saves them in one batched
  command inside one transaction, the MongoDB store in one unordered `BulkWrite`, and the Dapper
  store in one prepared statement executed per row on the connection the application already has
  — inside its transaction where it has one. Rows already in the right place are still left out,
  and a reorder into the order a role already has writes nothing at all.
- `SetAsync` displaces the holders of a single-valued role in one statement instead of one call
  each, closing the window in which the role held some of its old files and none of its new one.
- `MeasuringStream` is disposed, so the `IncrementalHash` behind every upload's SHA-256 is
  released when the upload finishes rather than whenever the finalizer queue gets to it. The class
  had a `Dispose` override that nothing ever called.
- An entity's own listings no longer hand back one page as though it were everything.
  `media.For(entity).ListAsync()`, `ListByRoleAsync`, `ListByCategoryAsync` and
  `ListWithRolesAsync` return a list rather than a page, so they promise every file — but they
  asked for `MediaQuery.MaxTake` rows and returned whatever came back, so an entity holding more
  than 500 files silently lost the rest, with nothing in the result to say so. `ListWithRolesAsync`
  was worse: it reads the links whole and the files a page at a time, and dropped the overflowing
  links on the floor under a comment explaining that a missing file means a soft-deleted one.

  They now refuse. An entity or role over the cap throws `InvalidMediaException` naming
  `IMediaManager.ListAsync`, which takes `Skip` and `Take` and reports a total. The check costs
  nothing until it matters: it only probes for a further page when the one it got came back exactly
  full, so an entity with five images never pays for it. Raising the cap instead was the other
  option and is the wrong one — it would turn one gallery render into an unbounded read.
- An entity's own listings no longer run a `COUNT` nobody reads. They went through
  `IMediaManager.ListAsync`, which owes its caller a total and issues a second query — an
  aggregate, and over the `EXISTS` subquery too once an owner is involved — and then discarded it.
  Drawing a product's gallery cost two queries to produce one list. They now take a count-free path
  and the public `ListAsync` is unchanged, total and all.
- `GET /api/media/{id}/content` honours `Range` on Amazon S3. The endpoint has always asked
  ASP.NET Core for range processing, but ASP.NET Core serves a partial response only when the
  stream it is given can report a length and seek — and S3's response stream only reads forward,
  so every range request on that backend quietly became a `200` carrying the whole object. A
  client seeking through a video pulled the entire file through the application each time, and
  the documented `Range` support was true of local disk and Azure and a fiction on S3.
  `AmazonS3StorageProvider.OpenReadAsync` now returns a seekable stream: the length comes from
  the response that opened it, seeking moves a counter, and a read that lands where the open
  response cannot reach issues a fresh ranged `GET`. A straight download and the `bytes=0-` a
  media element asks for first still cost exactly one request, and a short forward seek is served
  by discarding bytes rather than paying for another. `StorageContractTests` asserts seekability
  and window reads against LocalStack and Azurite, so this cannot regress unnoticed — the previous
  range test used an in-memory double, which seeks happily and proved nothing about S3.
  `IMediaStorageProvider.OpenReadAsync` now documents seekability as part of the contract, and
  [adding-a-backend.md](docs/adding-a-backend.md) explains how to satisfy it over a forward-only
  SDK.
- `GET /api/media/{id}/content` no longer serves script-bearing uploads inline. Nothing blocks an
  HTML or SVG upload — both are ordinary things to store — but the endpoint served them back with
  their stored type, inline, from the application's own origin, which is a stored-XSS vector:
  open the link and the file's script runs in your origin with your session cookie. The
  `.js` extension was blocked while `.html` and `.svg` were not, and the security guide told the
  reader to add the missing headers themselves, which sits badly beside a library whose defaults
  are meant to be the safe ones.

  Every download now carries `X-Content-Type-Options: nosniff`, and the types a browser runs
  script from as a top-level document — `text/html`, `application/xhtml+xml`, `image/svg+xml`,
  `text/xml`, `application/xml`, `text/xsl`, `text/javascript`, `application/javascript` — are
  served as `Content-Disposition: attachment` with `Content-Security-Policy: sandbox`, whatever
  `?download` says. Embedding is unaffected, since browsers honour `Content-Disposition` for
  navigations and not for `<img>` or `<video>`: a stored SVG logo still draws where a page asks
  for it. There is no opt-out, for the same reason the Azure backend's containers are private
  with no switch; an application that means to serve user HTML inline should do it from a separate
  origin over its own route, and `MediaContentTypes.IsActiveContent` is public so that route can
  ask the same question.
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
