# Querying

```csharp
var page = await media.ListAsync(new MediaQuery
{
    OwnerEntityName   = "Product",
    OwnerEntityId     = "42",
    Role              = "gallery",
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

`SortBy` takes `MediaSortBy.CreatedAt` (the default), `FileName`, `Size` or `SortOrder`, and `Descending`
(default `true`) picks the direction — so the default is newest first, which is the order a media
library is nearly always wanted in.

```csharp
new MediaQuery { SortBy = MediaSortBy.Size, Descending = true }       // largest first
new MediaQuery { SortBy = MediaSortBy.FileName, Descending = false }  // A–Z
```

`SortOrder` is the order an entity put its own files in, so it sorts on the attachment rather than
on the file and needs an owner filter; the engine refuses it without one rather than quietly
ignoring it. A file one entity attached under several roles sorts by its lowest role and its lowest
position, so a row that appears once has one place in the results. Narrowed with `Role` — the
ordering anyone actually asks for, a product's gallery — the role is fixed and this is simply that
role's position.

Every ordering ends on the id. Without that tiebreaker, rows that tie on the sort column — three
files of the same size, two uploaded in the same tick — page unstably, repeating one row on the
next page and skipping another.

There is deliberately no "order by tag" or "order by owner": one would sort by a delimited string
and the other by a key that is not meant to be read.

## Paging

`Take` defaults to 50 and is **capped at 500**. A page size is a client's suggestion, and an
unbounded one is how a listing endpoint becomes a way to exhaust the server's memory.

`MediaPage.Total` is how many rows match the filter, ignoring `Skip` and `Take`. It costs a second
query, which is why it is on the page rather than on every read — and why `media.For(entity)`'s own
listings, which have no total to report, do not run it.

Those listings return a list rather than a page, so they promise *every* file. One query cannot
return more than 500, so an entity holding more than that is **refused** rather than answered with
the first page: a truncated list and a complete one are indistinguishable to the caller, and a
gallery quietly missing its tail is worse than an error that says so. Page it here instead, with
`OwnerEntityName`, `OwnerEntityId` and `Role` set.

## Categories and tags

A **category** is one coarse bucket, and it leads the storage path — so it is also how the files
are laid out in the backend. Use it for kinds of file: `avatars`, `invoices`, `product-images`.

Trimmed and lower-cased on write, like a tag, and at most 64 characters. Leading the storage path is
why that matters here: two spellings would be two buckets *and* two directories in the backend
holding the same kind of file. The filter is lower-cased too, so it matches however it is asked for.
A category is what a file *is*; a [role](attaching-to-entities.md#category-is-not-role) is what one
entity uses it *for*.

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

## Owner, role and unattached

An owner filter resolves through the attachment table. A file that entity attached under several
roles — as its `primary` and its `seo` image — is still returned once.

`OwnerEntityId` needs `OwnerEntityName` as well, and the engine refuses the query otherwise: on its
own, a bare key matches rows belonging to unrelated entity types that happen to share it.

`Role` needs an owner for the same reason: a role belongs to a link between a file and one entity,
so on its own it would match every entity's gallery at once.

```csharp
new MediaQuery { OwnerEntityName = "Product", OwnerEntityId = "42", Role = "gallery" }
```

**Without an owner the attachment table is not touched at all.** That is what keeps a file linked to
nine entities from appearing nine times in the library listing, and what keeps a file linked to none
appearing in it — an unattached file is an ordinary file waiting to be claimed, not one in limbo.

`Unattached` asks for exactly those, for a cleanup sweep. It is never a default, and cannot be
combined with an owner, which asks the opposite question:

```csharp
new MediaQuery { Unattached = true, CreatedBefore = DateTime.UtcNow.AddDays(-1) }
```

For one entity, prefer the accessor — it fills the owner in and cannot get it wrong:

```csharp
await media.For(product).ListAsync(ct);
await media.For(product).ListByRoleAsync("gallery", ct);
await media.For(product).ListByCategoryAsync("product-images", ct);
```

## Indexes

Each store creates five, matching the queries the library actually issues.

On the files:

| Index | Serves |
|---|---|
| `(Checksum)` | deduplication and integrity checks |
| `(Category, CreatedAt)` | the default listing and every category browse |

On the attachments:

| Index | Serves |
|---|---|
| `(MediaFileId, OwnerEntityName, OwnerEntityId, Role)` — unique | one file may hold several roles on one entity, but not the same role twice. It is also what makes attaching idempotent |
| `(OwnerEntityName, OwnerEntityId, Role, SortOrder)` | "this entity's files, in the order it put them in" — filter, role narrowing and sort from one index |
| `(MediaFileId)` | the cascade when a file is deleted, and the sole-holder test behind `DeleteAllAsync` |

A tag search is a `LIKE '%…%'`, which no index can serve. That is the cost of one column instead of
a join table, and it is the right trade until tag search is a hot path — at which point add a
full-text or trigram index, or your own tag table.
