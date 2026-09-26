# Attaching media to entities

A media row can point at one of your entities. Nothing on your entity changes — no column, no
navigation, no migration.

```csharp
var file  = await media.For(product).UploadAsync(upload, ct);
var files = await media.For(product).ListAsync(ct);
await media.For(product).DeleteAllAsync(ct);
```

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
var file = await media.UploadAsync(upload, ct);   // no owner yet
// … user fills in the rest of the form, and posts it …
await media.For(product).AttachAsync(file.Id, ct);
```

Unattached files that are never claimed are ordinary rows with a null owner — find them with
`new MediaQuery { CreatedBefore = cutoff }` and sweep them up on a schedule.

## Composite keys

A media row records one key, so a composite primary key needs a decision from you: either compose
the two values yourself and use `For(name, key)`, or register an `IEntityKeyAccessor` that does it
consistently.

```csharp
await media.For("OrderLine", $"{orderId}:{lineNumber}").ListAsync(ct);
```

The EF Core key accessor detects a composite key and says this, rather than silently using the
first column.

## Cleanup is yours

**Deleting an entity does not delete its files.** There is no foreign key from a media row to your
table — that is what lets one table serve every entity — so nothing cascades.

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

If you would rather not remember, put it in one place: an EF Core `SaveChanges` interceptor that
looks for deleted entities, or a repository base class.

`DeleteAllAsync` pages internally, so an entity with ten thousand files does not load ten thousand
rows at once.

## Categories

A category is a coarse bucket within an entity's files, and it leads the storage path:

```csharp
await media.For(product).UploadAsync(upload with { Category = "product-images" }, ct);
await media.For(product).UploadAsync(upload with { Category = "manuals" }, ct);

var images = await media.For(product).ListAsync("product-images", ct);
```

Use it for kinds of file — `avatars`, `invoices`, `manuals` — and [tags](querying.md) for
everything finer-grained.
