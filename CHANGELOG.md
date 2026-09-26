# Changelog

All notable changes to DotNetBoost.Media are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
