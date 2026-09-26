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

## Why there is no foreign key

A media row points at your entity by **name and key** — `("Product", "42")` — and there is
deliberately no foreign key back.

That is what lets one table serve every entity in the application. Adding media to a new entity
costs no table, no column and no migration; it costs an attribute at most.

The price is explicit, and the library does not pretend otherwise: **deleting an entity does not
cascade to its files.** Call `media.For(entity).DeleteAllAsync()` when it goes.

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

## Relationship to the other DotNetBoost libraries

The shape is deliberately the same as DotNetBoost.Settings and DotNetBoost.MultiLingual: one
service to inject, one builder chain, the same storage providers, the same `[Key]` / `Id` /
`{Type}Id` key convention, the same problem-details terms. They are independent packages; an
application can use one or all three.
