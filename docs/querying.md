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
    Search            = "hero",
    CreatedAfter      = DateTime.UtcNow.AddDays(-30),
    CreatedBefore     = DateTime.UtcNow,
    SortBy            = MediaSortBy.CreatedAt,
    Descending        = true,
    Skip              = 0,
    Take              = 50
}, ct);
```

Every property is optional and the set ones are ANDed, so `new MediaQuery()` means "every live
file, newest first". The query is deliberately small enough that each of the three stores can
translate it into one statement.

## Searching by name

`Search` matches anywhere in the file name, case-insensitively:

```csharp
new MediaQuery { Search = "invoice" }   // matches "2026-invoice-final.pdf"
```

Both sides are lowered before the comparison, deliberately. `LIKE` is case-**sensitive** on
PostgreSQL and case-insensitive on SQL Server by default, so matching the column's collation would
make "case-insensitive" true on one engine and false on another — worse than either.

A value containing `%` or `_` is escaped, so a search for `report_final` matches that and not
`reportxfinal`.

It cannot use an index, being an unanchored `LIKE`. That is fine for an administrative search over
a library of ordinary size and not for a hot path on a large one; there, put a full-text index on
the column and query it yourself.

## Ordering

`SortBy` takes `MediaSortBy.CreatedAt` (the default), `FileName` or `Size`, and `Descending`
(default `true`) picks the direction — so the default is newest first, which is the order a media
library is nearly always wanted in.

```csharp
new MediaQuery { SortBy = MediaSortBy.Size, Descending = true }       // largest first
new MediaQuery { SortBy = MediaSortBy.FileName, Descending = false }  // A–Z
```

Every ordering ends on the id. Without that tiebreaker, rows that tie on the sort column — three
files of the same size, two uploaded in the same tick — page unstably, repeating one row on the
next page and skipping another.

There is deliberately no "order by tag" or "order by owner": one would sort by a delimited string
and the other by a key that is not meant to be read.

## Paging

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
