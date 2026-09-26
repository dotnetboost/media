# Querying

```csharp
var page = await media.ListAsync(new MediaQuery
{
    OwnerEntityName   = "Product",
    OwnerEntityId     = "42",
    Category          = "product-images",
    Tag               = "hero",
    ContentTypePrefix = "image/",
    UploadedBy        = "alice@example.com",
    CreatedAfter      = DateTime.UtcNow.AddDays(-30),
    CreatedBefore     = DateTime.UtcNow,
    Skip              = 0,
    Take              = 50
}, ct);
```

Every property is optional and the set ones are ANDed, so `new MediaQuery()` means "every live
file, newest first". The query is deliberately small enough that each of the three stores can
translate it into one statement.

## Ordering and paging

Newest first by default; `OldestFirst = true` reverses it. Both orders break ties on the id, so
two files created in the same tick page stably rather than repeating one row and skipping another.

`Take` defaults to 50 and is **capped at 500**. A page size is a client's suggestion, and an
unbounded one is how a listing endpoint becomes a way to exhaust the server's memory.

`MediaPage.Total` is how many rows match the filter, ignoring `Skip` and `Take`. It costs a second
query, which is why it is on the page rather than on every read.

## Categories and tags

A **category** is one coarse bucket, and it leads the storage path — so it is also how the files
are laid out in the backend. Use it for kinds of file: `avatars`, `invoices`, `product-images`.

**Tags** are free-form labels, normalised on the way in (trimmed, lower-cased, de-duplicated,
ordered) and stored in one column, comma-delimited *and* comma-surrounded:

```
,2026,invoice,paid,
```

The surrounding commas are the whole trick: a whole-tag match is then `LIKE '%,invoice,%'` on
every engine, with no join table — and `invoice` cannot match `invoiced`.

```csharp
new MediaQuery { Tag = "invoice" }   // matches ",2026,invoice,paid," — and not ",invoiced,"
```

Limits: 32 tags per file, 64 characters each, no commas. A tag containing `%` or `_` is escaped
before it reaches the LIKE, so it matches itself rather than acting as a wildcard.

One tag per query. Two would need either an AND of two LIKEs (fine, but it starts to want an
index this schema does not have) or a join table (which is the thing the single column exists to
avoid). If you need multi-tag intersection at scale, query for the narrowest tag and filter the
page in memory — or keep your own index alongside.

## Content type

`ContentTypePrefix` matches from the start, so:

```csharp
new MediaQuery { ContentTypePrefix = "image/" }      // every image
new MediaQuery { ContentTypePrefix = "image/png" }   // only PNGs
```

## Soft-deleted files

Excluded unless asked for. A deleted file is gone as far as every ordinary read is concerned, and
it takes saying so to see one:

```csharp
new MediaQuery { IncludeDeleted = true }
```

Their bytes are gone either way — the row is the record that the file existed, not a way to get it
back.

## Owner

`OwnerEntityId` needs `OwnerEntityName` as well, and the engine refuses the query otherwise: on its
own, a bare key matches rows belonging to unrelated entity types that happen to share it.

For one entity, prefer the accessor — it fills both in and cannot get them wrong:

```csharp
await media.For(product).ListAsync(ct);
await media.For(product).ListAsync("product-images", ct);
```

## Indexes

Each store creates three, matching the queries the library actually issues:

| Index | Serves |
|---|---|
| `(OwnerEntityName, OwnerEntityId, CreatedAt)` | "this entity's files, newest first" — the most common query, filter and sort from one index |
| `(Checksum)` | deduplication and integrity checks |
| `(Category, CreatedAt)` | the default listing and every category browse |

A tag search is a `LIKE '%…%'`, which no index can serve. That is the cost of one column instead of
a join table, and it is the right trade until tag search is a hot path — at which point add a
full-text or trigram index, or your own tag table.
