# Architecture

DotNetBoost.Media is two halves and one rule about the order they are written in.

```
                         ┌─────────────────────────┐
  your code  ──────────► │     IMediaManager       │
                         │  (the engine)           │
                         └───────────┬─────────────┘
                                     │
                ┌────────────────────┴────────────────────┐
                ▼                                         ▼
      ┌───────────────────┐                     ┌────────────────────────┐
      │   IMediaStore     │                     │ IMediaStorageProvider  │
      │   the ROW         │                     │ the BYTES              │
      ├───────────────────┤                     ├────────────────────────┤
      │ EntityFrameworkCore│                    │ FileSystem             │
      │ Dapper             │                    │ AzureBlobStorage       │
      │ MongoDb            │                    │ AmazonS3               │
      └───────────────────┘                     └────────────────────────┘
        exactly one                               one or more
```

## The row and the bytes

A **metadata store** holds one row per uploaded file: what it is, how big, who uploaded it, what
it is attached to — and, crucially, **where its bytes are**: the backend's name, its container,
and the path inside it.

A **storage backend** holds bytes at a path. It knows nothing about media rows, never validates
an upload, and is never asked to be transactional.

Everything that makes the library correct rather than merely functional lives in the engine
between them.

## Why the row is the source of truth

The backend only has bytes at a path. It cannot tell you what a file was called when it was
uploaded, who uploaded it, or what it belongs to — and listing a container to find out is a
catastrophic query at any real scale.

Putting all of that in one row buys three things:

- **Queryability.** "Every PDF this customer uploaded last month" is a `WHERE` clause, not a
  container scan.
- **Portability.** Because each row records the backend that wrote it, an application can change
  which backend receives new uploads and keep serving everything that came before. Move a single
  file between backends by copying the bytes and updating three columns.
- **Auditability.** A soft-deleted row still says the file existed, what it was, and who put it
  there, long after the bytes are gone.

## Why the link is a row of its own

A file and the use something makes of it are two different things. The file is the bytes and the
facts about them, which is inherently singular. What an entity uses those bytes *for* is inherently
plural: the same image can be one product's main photo and another's third gallery shot, so neither
the role nor the position belongs on the file.

So `MediaAttachments` holds `(MediaFileId, OwnerEntityName, OwnerEntityId, Role, SortOrder)`, unique
on all but the position. One extra table in total — not one per entity type — so adding media to
something new still costs no table, no column and no migration.

Keeping them apart is also what makes deduplication coherent. Identical bytes resolve to one file
and a second attachment; with a single owner slot on the file, a deduplicated upload had nowhere to
put the second entity.

## Why there is no foreign key

An attachment row points at your entity by **name and key** — `("Product", "42")` — and there is
deliberately no foreign key back.

That is what lets two tables serve every entity in the application. Adding media to a new entity
costs no table, no column and no migration; it costs an attribute at most.

The price is explicit, and the library does not pretend otherwise: **deleting an entity does not
cascade to its files.** Call `media.For(entity).DeleteAllAsync()` when it goes — it deletes the
files that entity alone was using, merely detaches the ones others still reference, and returns the
ids it deleted so your own cleanup can act on exactly those.

Nothing cascades **out** either: the library cannot know what records an application keeps about a
file. `IMediaFilesDeletedHandler` is how that cleanup is registered once instead of remembered at
every call site — including the REST endpoint's own `DELETE`, which runs no application code at
all. Handlers are told after the fact, in registration order, and their failures are logged and
swallowed: the bytes are already gone, so a throw could only misreport a completed delete.

## Why the bytes are written first

An upload is two writes that cannot be one transaction — a blob store and a database do not share
one. So the engine picks the order, and cleans up after the half that can fail:

```
1. validate         name, extension, size limit, sniff the leading bytes
2. write the bytes  to the backend, measuring and hashing as they stream past
3. write the row    to the store
   └─ if this fails: remove the bytes again, then rethrow the original failure
```

The other order — row first — leaves a row pointing at bytes that were never written, which every
subsequent read reports as a missing file. This order can only leave an object nothing references,
and it removes that itself.

Deleting reverses it, for the same reason:

```
1. remove the bytes   a backend that says they are already gone counts as success
2. remove the row     or set DeletedAt, under soft delete
```

A delete that fails halfway leaves a row whose bytes are gone — which `OpenReadAsync` reports
honestly as `MediaContentMissingException`, and which retrying the delete completes.

## Why an upload is read exactly once

Three things have to be known about an upload: its real format, its size, and its hash. The
obvious implementation reads the file three times, or buffers it.

Instead:

- The first 32 bytes are read into a small array and matched against known signatures. A
  `PrefixStream` then replays those bytes and continues straight into the untouched remainder, so
  the backend sees one ordinary stream.
- That stream is wrapped in a `MeasuringStream`, which counts, hashes and enforces the size limit
  **as the backend reads**. The read the backend performs is the only read.

A 2 GB upload therefore costs 2 GB of network and a few kilobytes of memory, and the size limit
stops it at the limit rather than after it.

## Several backends at once

The registry holds every configured backend by name, and one of them is the default that new
uploads go to. Each row records the backend that wrote it, and every read goes through the
registry to that backend.

```csharp
services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseFileSystem("./uploads")                              // still serves everything already here
    .UseAzureBlobStorage(connectionString, isDefault: true)  // new uploads go here
    .Build();
```

Removing a backend that rows still reference is the one thing that strands files, and the registry
says so rather than failing obscurely.

## Why one owner at a time is not enough

`For(entity)` scopes every operation to one entity, which is what makes it impossible to reach
another's files by accident. It also makes a listing page expensive: read through it per row and a
page of fifty products costs fifty queries — a hundred, since pairing links with files is two reads.

So the engine grew a second shape rather than relaxing the first. `ForMany(entities)` asks the same
questions about many owners, and the store answers in **one statement with the file rows joined
in** — the join moves into the store precisely so there is no second read to multiply. The writes
stayed on `For(entity)`: a role and a position belong to one link, so there is nothing to batch.

One question still cannot be asked that way. "The first 20 products that have an image" is not a
subset of the first 20 products, so it has to be answered before paging, where the product rows and
the attachment rows meet — the database. `DbContext.OwnersWithMedia<T>()` in the EF Core package is
that one as an uncomposed `IQueryable<string>`, foldable into the application's own query.

It rests on a guarantee the library makes elsewhere: deleting a file removes every attachment of it
outright, **even under soft delete**. That is what lets the filter be a single statement over the
attachment table instead of a join to the files, and it is why the guarantee is documented on
`IMediaManager.DeleteAsync` rather than left as an implementation detail.

## Relationship to the other DotNetBoost libraries

The shape is deliberately the same as DotNetBoost.Settings and DotNetBoost.MultiLingual: one
service to inject, one builder chain, the same storage providers, the same `[Key]` / `Id` /
`{Type}Id` key convention, the same problem-details terms. They are independent packages; an
application can use one or all three.

Entity addressing goes further than a shared convention. This library and DotNetBoost.MultiLingual
resolve entity names and format keys **identically**, which is what lets an application join a
media row to a translation row on `(EntityName, EntityId)`. Nothing enforced that agreement for a
while — it rested on comments in two repositories — so both now publish
`EntityKey.AddressingVersion`, and `DotNetBoost.Media.TestSupport.EntityAddressingConformance` is
the suite that holds them to it. The rules are written out in the
[README](https://github.com/dotnetboost/media/blob/main/README.md#dotnetboost-entity-addressing-v1)
as DotNetBoost Entity Addressing v1.
