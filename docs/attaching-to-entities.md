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

The key is found by convention — a property marked `[Key]`, then `Id`, then `{TypeName}Id` — the
same order DotNetBoost.MultiLingual uses, so an application running both libraries keys its
entities one way. With the EF Core store the model is read instead, so a key configured fluently
or as a shadow property is found too.

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
must not pull an image out of every other product using it. Its return value counts only the files
that were actually destroyed.

If you would rather not remember, put it in one place: an EF Core `SaveChanges` interceptor that
looks for deleted entities, or a repository base class.

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
