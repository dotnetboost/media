# Attaching media to entities

A file is linked to the entities that use it by **attachment rows**. Nothing on your entity changes
— no column, no navigation, no migration.

```csharp
var file  = await media.For(product).UploadAsync(upload, ct);
var files = await media.For(product).ListAsync(ct);
await media.For(product).DeleteAllAsync(ct);
```

Two tables in total, whatever your application attaches media to: `MediaFiles` holds the bytes and
the facts about them, `MediaAttachments` holds the links. Adding media to a new entity type still
costs no schema change.

## One file, many entities

A file has **no owner of its own**. It has attachments — none, one, or many — so the same upload can
serve several entities at once:

```csharp
await media.For(chair).AddAsync(file.Id, ct);
await media.For(desk).AddAsync(file.Id, ct);     // the chair keeps it
```

Nothing is copied. Both entities point at the same row and the same bytes.

This is worth stating plainly because the obvious alternative — a single owner column on the file —
cannot express it. Under that design the second attach would move the file, silently taking it off
the first entity.

A file that nothing is attached to is perfectly ordinary. It is what an upload looks like before a
form claims it, and what one looks like after its last holder lets it go; it stays in every listing,
ready to be attached again.

## Roles: what the entity uses the file *for*

An attachment carries a **role** — `primary`, `gallery`, `seo`, or whatever your application means —
and a position within it.

The role lives on the link rather than on the file, and it has to. The same image can be the chair's
main photo and the desk's third gallery shot; neither answer belongs to the file.

### Cardinality lives in the verb

A product has one main image, many gallery images, and one SEO image. No schema constraint can
express all three at once — a uniqueness rule that fits `primary` would forbid a second gallery
shot — so the caller says which it means:

```csharp
await media.For(product).SetAsync("primary", heroId, ct);   // replaces the incumbent
await media.For(product).AddAsync(shotId, "gallery", ct);   // appended at the end
await media.For(product).ReorderAsync("gallery", ids, ct);  // saves a dragged order
```

`SetAsync` detaches whatever held the role and attaches the new file in one operation, which is what
"change the main image" needs — as two calls it would leave a window with two primaries or none.
**The file it displaces is only detached, never deleted**: it may well be in use elsewhere.

`AddAsync` is idempotent. Attaching a file that is already attached in that role changes nothing, so
a double-clicked button and a retried request are both harmless.

`ReorderAsync` wants every file in the role, in the order you want them — a partial list has no
unambiguous meaning, since the files left out would have no defined position. It saves the whole
order in **one write**, so a dragged gallery either takes or does not; the files that did not
actually move are left alone, and reordering into the order a role already has writes nothing.

### Reading them back

```csharp
var hero    = await media.For(product).GetByRoleAsync("primary", ct);   // one, or null
var gallery = await media.For(product).ListByRoleAsync("gallery", ct);  // in order
```

To draw a whole product page — hero, ordered gallery, SEO picture — ask once:

```csharp
foreach (var attached in await media.For(product).ListWithRolesAsync(ct))
    Console.WriteLine($"{attached.Role} {attached.SortOrder}: {attached.File.FileName}");
```

`ListWithRolesAsync` is per **link**, so a file the product uses as both its `primary` and its `seo`
image appears twice, once under each role — that is the question it answers. `ListAsync` asks the
other one, "which files does this entity use", and returns each of them once.

Each of these reads is a single query — no count is run, because none of them reports a total.
Each also returns *all* of the files it describes, and refuses rather than truncating: an entity
or a role holding more than 500 files throws `InvalidMediaException` pointing at
`IMediaManager.ListAsync`, which takes `Skip` and `Take`. Returning the first 500 as though they
were the lot would be a lie a caller could not detect.

### Reading a whole page of entities at once

`For(entity)` is one entity at a time by construction, so drawing a listing through it costs a read
per row — two, once the files are wanted as well. A page of fifty products is then a hundred
queries to show fifty thumbnails, which is not a filter on a table so much as a scan with a filter
printed on it.

`ForMany` is the same questions asked about many owners, each answered in **one** store round trip
whatever the owner count:

```csharp
var products = await db.Products.OrderBy(p => p.Name).Skip(skip).Take(50).ToListAsync(ct);

var heroes = await media.ForMany(products).GetByRoleAsync("primary", ct);

foreach (var product in products)
    Draw(product, heroes[product.Id.ToString(CultureInfo.InvariantCulture)]);
```

| | |
|---|---|
| `ListWithRolesAsync()` | each owner's files with the role each holds — the bulk `For(e).ListWithRolesAsync()`, same order, same per-link meaning |
| `GetByRoleAsync(role)` | each owner's file in a single-valued role, or `null` |
| `WhichHaveAttachmentsAsync(role?)` | which of the asked-about owners have anything at all — a set of the ones that do, not an entry each |

Results are keyed by the owner's **formatted entity id** — the same invariant string an attachment
row stores, so `product.Id.ToString(CultureInfo.InvariantCulture)` finds a product's entry.

**Every id you asked about is in the result**, including the ones with nothing attached: they map to
an empty list or to `null`. You never need `GetValueOrDefault`, and the first product without a
photo does not throw.

Pass the entities, or the keys if that is what you hold:

```csharp
media.ForMany(products)                 // one entity type per call
media.ForMany("Product", [7, 8, 9])     // keys as they are — int, long, Guid, string
```

One entity type per call: the name comes from the first entity's own type, and an entity of another
type later in the sequence is refused rather than read under the wrong name. A repeated id is read
once. An empty collection answers empty without touching the store.

`ForMany` reads; it does not write. Attaching, detaching and reordering are per entity by nature —
a role and a position belong to one link — so they stay on `For(entity)`.

### Filtering by "has an image" before paging

`ForMany` answers "which of *these* owners have images", which is all a listing needs once it knows
its page. Filtering **before** paging is a different question: "the first 20 products that have an
image" is not a subset of the first 20 products, so it can only be asked where both sets live. In
the EF Core package:

```csharp
var withImage = db.OwnersWithMedia<Product>("primary");   // IQueryable<string>, not run yet

var page = await db.Products
    .Where(p => withImage.Contains(p.Id.ToString()))
    .OrderBy(p => p.Name)
    .Skip(skip).Take(take)
    .ToListAsync(ct);
```

Negate it with `!withImage.Contains(...)` for the products still missing a photo. Nothing is
materialised — it is a `SELECT DISTINCT OwnerEntityId` that EF folds into your query as a subquery —
so it composes into whatever you are already building. Leave `role` off for "anything attached".

Write the owner side as the provider's own string conversion of the key (`p.Id.ToString()`, which
EF translates to a cast in SQL), not as anything that formats client-side under the current
culture: the comparison is against the stored invariant key.

### Declaring an entity's roles

By default a role is free text and nothing checks it. An entity can opt in by declaring what it
accepts, and then the engine enforces it:

```csharp
[MediaEntity("Product")]
[MediaRole("primary")]                    // one file
[MediaRole("gallery", Multiple = true)]   // many, in order
[MediaRole("seo")]
public class Product { public int Id { get; set; } }
```

```csharp
services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseFileSystem("./uploads")
    .DeclareRolesFrom(typeof(Product).Assembly)   // ← reads every [MediaRole]
    .Build();
```

Without that builder line the attributes are documentation and nothing more. **An entity that
declares nothing is not checked at all**, so adding this breaks nothing that already works.

What it catches:

| | |
|---|---|
| `AddAsync(id, "galery")` | *Product has no media role 'galery'. It declares: primary, gallery (many), seo.* |
| `AddAsync(id, "primary")` when one is held | *'primary' is single-valued… Replace it rather than appending* |
| `SetAsync("gallery", id)` | *'gallery' is multi-valued… Replacing would detach every other file in it* |

That second pair is the point. The verbs alone cannot object: nothing tells `AddAsync` that
`primary` is meant to hold one file, and `SetAsync` on a nine-image gallery silently detaches eight
of them and looks like it worked.

Declarations are read once, at `Build()`, and filed under the entity's **stored name**. That matters
because `media.For("Product", "42")` has no class to read an attribute from, and neither do the
library's own REST endpoints — checking one path and not the other would be worse than not checking,
because you would think you were covered.

For a type you cannot annotate, declare it on the builder instead. Both fill the same registry:

```csharp
.DeclareRoles("Order", r => r.One("invoice").Many("packing-slip"))
```

And a UI that must offer the right roles for whatever it was opened for can ask:

```csharp
foreach (var role in media.RolesFor("Product"))
    Console.WriteLine($"{role.Name}{(role.Multiple ? " (many)" : "")}");
```

### Roles are free text, lower-cased

The library ships **no role names**. `primary`, `gallery`, `seo`, `hero`, `datasheet` — they are all
just strings, and which ones exist is your application's business. `MediaRoles` holds only the rules
for writing one down, as `MediaTags` and `MediaCategories` do for theirs.

Keep your names in your own class, next to the entity that uses them, so a typo is a compile error:

```csharp
public static class ProductRoles
{
    public const string Primary = "primary";
    public const string Gallery = "gallery";
    public const string Seo     = "seo";
}
```

Roles are lower-cased on write, so `Primary` and `primary` are one role rather than two — as
categories and tags are.

A role is never null. It defaults to `MediaRoles.Default` (`"default"`), which is what lets the
uniqueness constraint cover all four columns — a nullable column inside it would behave differently
on each engine this library supports.

## Category is not role

The two are close enough to confuse, and they are different axes:

| | lives on | means | leads the storage path |
|---|---|---|---|
| `Category` | the file | which bucket the bytes are in | yes |
| `Role` | the attachment | what the file does for *this* entity | no |

One file can be `Category = "product-images"` while being `primary` on one product and `gallery` on
another.

Both are lower-cased on write. For a category that matters more than for a role: it leads the
generated storage path, so `Product-Images` and `product-images` would otherwise be two buckets *and*
two directories in the backend holding the same kind of file. A category filter is lower-cased too,
so it matches however it is spelled.

```csharp
await media.For(product).UploadAsync(upload with { Category = "manuals" }, ct);

var manuals = await media.For(product).ListByCategoryAsync("manuals", ct);
```

Use a category for kinds of file — `avatars`, `invoices`, `manuals` — a role for what an entity does
with one, and [tags](querying.md) for everything finer-grained.

## How the entity is identified

Two strings: a **name** and a **key**.

The name is the type's name, or whatever `[MediaEntity]` says:

```csharp
[MediaEntity("Product")]
public class ProductEntity { public int Id { get; set; } }
```

Use the attribute when you might rename the class, or when two unrelated types would otherwise
share a name. **The stored name is what attaches a file to an entity**, so changing it later
strands every file already attached under the old one. Pick it once.

The key is found by convention — a property marked `[Key]`, then `Id`, then `{TypeName}Id`,
considering public instance properties that are readable and not indexers. Two `[Key]` properties
are a composite key and are refused: one column holds one value, and reflection does not order
properties, so picking one would key the entity on whichever this runtime happened to return. With
the EF Core store the model is read instead, so a key configured fluently or as a shadow property
is found too, and the model wins where the two disagree.

That order is a rule of [DotNetBoost Entity Addressing v1](../README.md#dotnetboost-entity-addressing-v1),
not a coincidence, so an application running any library implementing the same version keys its
entities one way — and `EntityKey.AddressingVersion` plus the conformance suite are what hold it.

Keys are formatted invariantly: `int`, `long`, `Guid` and `string` all fit the one column, and a
key formatted under `ar-SA` reads back the same under `en-US`.

### Unsaved entities

`For(entity)` throws if the key is still unassigned. A store-generated key does not exist until
the entity is saved, and a file attached to a key of `0` is attached to nothing.

```csharp
db.Products.Add(product);
await db.SaveChangesAsync(ct);          // now it has a key
await media.For(product).UploadAsync(upload, ct);
```

### When you hold the name and key rather than the object

```csharp
await media.For("Product", id.ToString()).ListAsync(ct);
```

## The draft-form problem

A form that uploads files before the thing they belong to exists is the common case, and it works:
upload unattached, then attach once you have an entity.

```csharp
var file = await media.UploadAsync(upload, ct);   // attached to nothing
// … user fills in the rest of the form, and posts it …
await media.For(product).AddAsync(file.Id, ct);
```

Files that are never claimed keep no trace of the form that abandoned them, so find them by asking
for the unattached ones and sweep them up on a schedule:

```csharp
var abandoned = await media.ListAsync(new MediaQuery
{
    Unattached    = true,
    CreatedBefore = DateTime.UtcNow.AddDays(-1)
}, ct);
```

`Unattached` is a filter to ask for, never a default. An unattached file is an ordinary file that
still appears in every listing; combine it with `CreatedBefore` so the sweep takes the ones old
enough to be abandoned rather than the ones halfway through a form.

## Detaching is not deleting

Three operations, and the difference matters:

| | attachment rows | file row | bytes |
|---|---|---|---|
| `IMediaManager.DeleteAsync(id)` | all cascade | soft or hard per `SoftDelete` | **removed** |
| `For(e).DetachAsync(id, role?)` | that entity's | untouched | **untouched** |
| `For(e).DetachAllAsync()` | all of that entity's | untouched | **untouched** |
| `For(e).DeleteAllAsync()` | all of that entity's | sole-held files deleted | those files' |

`DeleteAllAsync` returns the ids it deleted; the others return a count or a boolean.

`DeleteAsync` removes the file from the library, and therefore from **every** entity using it. A UI
offering "remove this image from the product" wants `DetachAsync`, which removes one link and never
touches the bytes. A `role` of `null` detaches the file from every role on that entity.

Attachments are removed outright even when `SoftDelete` is on. They are links with no independent
value, and a tombstoned file that still read as attached to a live entity would be worse than no
record at all.

## Cleanup is yours

**Deleting an entity does not delete its files.** There is no foreign key from an attachment row to
your table — that is what lets two tables serve every entity — so nothing cascades.

```csharp
public async Task DeleteAsync(int id, CancellationToken ct)
{
    var product = await db.Products.FindAsync([id], ct);
    if (product is null) return;

    await media.For(product).DeleteAllAsync(ct);   // ← the bit that is easy to forget

    db.Products.Remove(product);
    await db.SaveChangesAsync(ct);
}
```

`DeleteAllAsync` deletes the files **this entity was the only holder of** and merely detaches the
ones other entities still reference. A shared image surviving is the point: deleting one product
must not pull an image out of every other product using it.

**It returns the ids it deleted**, not a count — the detached ones are not in the list. That
distinction is the whole value of the return: if your application keeps records of its own against
a file, acting on the wrong half destroys data belonging to a file that is still in use.

```csharp
var deleted = await media.For(product).DeleteAllAsync(ct);
await db.MediaAltText.Where(t => deleted.Contains(t.MediaFileId)).ExecuteDeleteAsync(ct);
```

To ask before acting — for a dialog that has to say "2 of these 5 are used elsewhere and will
stay" — `WhichAreSharedAsync` is the same sole-holder test on its own, in one query whatever the
file count:

```csharp
var attached = await media.For(product).ListAsync(ct);
var staying  = await media.For(product).WhichAreSharedAsync([.. attached.Select(f => f.Id)], ct);
```

If you would rather not remember the cleanup at all, put it in one place: an EF Core `SaveChanges`
interceptor that looks for deleted entities, or a repository base class.

## Cleaning up your own records about a file

Nothing cascades *out* of this library, which is correct — a media row is not a foreign key to
anything, and a storage library guessing at your tables would be worse. But that leaves the cleanup
to be remembered at every call site, and the one that gets missed is the REST endpoint's own
`DELETE /api/media/{id}`, where no application code runs at all.

Register a handler instead, and the cleanup lives where the delete is:

```csharp
services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseFileSystem("./uploads")
    .OnFilesDeleted(async (serviceProvider, ids, ct) =>
    {
        var db = serviceProvider.GetRequiredService<AppDbContext>();
        await db.MediaAltText.Where(t => ids.Contains(t.MediaFileId)).ExecuteDeleteAsync(ct);
    })
    .Build();
```

`.OnFilesDeleted<THandler>()` registers a class instead, for anything longer than a statement.
Handlers are scoped — the `IServiceProvider` passed in is the delete's own scope, so a `DbContext`
resolved from it is the one the delete used.

| | |
|---|---|
| **When** | After the files are gone. A handler is given ids rather than `MediaFileInfo`s because the rows they name no longer exist. |
| **What** | `DeleteAsync` passes the one file it deleted. `DeleteAllAsync` passes the batch it deleted — **not** the files it merely detached. A delete that found nothing notifies nobody. |
| **Order** | Registration order. Register several: one for alt text, one for the search index. |
| **Failures** | Logged and swallowed. The bytes are already gone, so throwing would report a completed delete as a failure and invite a retry of something that cannot be repeated — and would stop the handlers after it from hearing about the delete at all. |

There is a window between finding a file unshared and removing its bytes in which another request
could attach it, leaving that new link pointing at bytes that are gone. Closing it needs a lock this
library does not take anywhere — the Dapper store borrows your connection and may already be inside
your transaction. The window is small, `OpenReadAsync` reports the outcome honestly as
`MediaContentMissingException`, and it is the same trade the engine already makes writing bytes
before rows. See [limitations](limitations.md).

## Composite keys

An attachment row records one key, so a composite primary key needs a decision from you: either
compose the two values yourself and use `For(name, key)`, or register an `IEntityKeyAccessor` that
does it consistently.

```csharp
await media.For("OrderLine", $"{orderId}:{lineNumber}").ListAsync(ct);
```

The EF Core key accessor detects a composite key and says this, rather than silently using the
first column.
