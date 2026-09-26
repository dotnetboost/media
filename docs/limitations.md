# Limitations

What this library deliberately does not do, and what to reach for instead.

## The two writes are not one transaction

A blob store and a database cannot enlist in the same transaction, so an upload is two writes. The
engine orders them and cleans up after the half that can fail — bytes first, row second, bytes
removed again if the row fails — which is the strongest guarantee available without a distributed
transaction.

What remains: if the process is killed **between** the two writes, an object is left that no row
references. Nothing reads it and nothing serves it; it costs storage.

If that matters, sweep for it: list the container, and delete objects with no matching
`StoragePath`. Age the sweep by at least the longest upload you accept, or it will delete a file
that is still being written.

With the EF Core store, an upload inside your own transaction is *more* consistent, not less: the
row rolls back with everything else. The bytes do not — same sweep.

## Deleting an entity does not delete its files

There is no foreign key from a media row to your table, which is exactly what lets one table serve
every entity in the application. Nothing cascades.

```csharp
await media.For(product).DeleteAllAsync(ct);
db.Products.Remove(product);
```

Put it in one place if you would rather not remember it — a `SaveChanges` interceptor, or a
repository base class.

## No built-in migration between backends

Several backends can be registered at once and each row records its own, so **switching which
backend receives new uploads needs no migration at all**. Moving files that already exist does, and
the library does not ship one.

It is a copy and a three-column update:

```csharp
await using var source = await from.OpenReadAsync(row.StorageContainer, row.StoragePath, ct);
var stored = await to.SaveAsync(new MediaWrite(source, row.StoragePath, row.ContentType, false), ct);

row.StorageProvider  = to.Name;
row.StorageContainer = stored.Container;
row.StoragePath      = stored.Path;
await store.UpdateAsync(row, ct);
```

A shipped version would have to decide about batching, retries, throttling, verification and what
to do halfway through — all of which depend on the volume and the deadline. Keep both backends
registered until the last row has moved.

## No image processing

No thumbnails, no resizing, no format conversion, no EXIF stripping. Those need an imaging library
(ImageSharp, SkiaSharp, libvips), each with its own licence, native dependencies and
vulnerability surface, and none of them belongs as a hard dependency of a storage library.

Do it in your own code and upload the results as further files — a `thumbnail` category attached to
the same entity works well. `Metadata` is there for dimensions and the like.

**EXIF in particular:** uploaded photographs routinely carry GPS coordinates. This library stores
what it is given, so if you serve user photographs publicly, strip it before uploading.

## No virus scanning

Content-type verification catches a file lying about what it is. It is not malware detection.

If you accept files from the public and serve them to other users, put a scanner in front —
ClamAV, or your cloud's own (Microsoft Defender for Storage, Amazon GuardDuty Malware Protection).
A reasonable shape is to upload to a quarantine category, scan out of band, and move the row's
category on a clean result.

## No resumable or chunked uploads

An upload is one request. A connection that drops at 90% starts again.

For very large files over unreliable connections, hand the client a direct upload URL (a SAS with
write permission, or an S3 pre-signed POST), let it resume against the storage service, and record
the row yourself afterwards. The library does not mint write URLs — one is a licence to put bytes
in your account, and the decision about who gets one is not one a library should make.

## One tag per query

Tags live in one column, comma-surrounded, so a whole-tag match is a `LIKE` on every engine and
there is no join table. Two tags at once would need either two LIKEs or that join table.

Query for the narrowest tag and filter the page in memory, or keep your own index alongside if
multi-tag intersection is a hot path.

## Tag search cannot use an index

`LIKE '%,tag,%'` is unindexable, by construction. That is the cost of one column instead of a join
table, and it is the right trade until tag search is hot — at which point add a full-text or
trigram index on the column, or your own tag table.

## No built-in per-file authorization

The library records `OwnerEntityName`, `OwnerEntityId` and `UploadedBy` so you can decide who may
see what. It does not decide.

`MapMediaEndpoints()` maps anonymous endpoints; `RequireAuthorization()` gets you "signed in".
Anything finer is your own route over `IMediaManager`. See
[validation-and-security.md](validation-and-security.md#authorization).

## Composite entity keys need a decision

A media row records one key. For a composite primary key, either compose the value yourself and
use `For(name, key)`, or register an `IEntityKeyAccessor` that does it consistently. The EF Core
key accessor detects a composite key and says so rather than silently using the first column.

## Local disk does not scale out

One node's disk is not another's. Use it for development and single-host deployments with a
durable volume; use Azure or S3 behind a load balancer.

## Soft delete is not a recycle bin

The bytes go either way. The row is the record that the file existed — its name, size, owner and
uploader — not a way to get it back. If you need undelete, do not delete: move the row to an
`archived` category and sweep it later.

## No quotas

Nothing limits how much one user or one entity may store. The per-file limit is a per-file limit.
Aggregate quotas need a count or a sum you maintain; `ListAsync` gives you the numbers.
