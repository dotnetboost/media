# DotNetBoost.Media

[![NuGet](https://img.shields.io/nuget/vpre/DotNetBoost.Media.Core?label=nuget)](https://www.nuget.org/packages/DotNetBoost.Media.Core)
[![CI](https://github.com/dotnetboost/media/actions/workflows/ci.yml/badge.svg)](https://github.com/dotnetboost/media/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/dotnetboost/media/blob/main/LICENSE)

**Upload a file once. Know what it is, where it is, and what it belongs to — forever.**

You inject one service. DotNetBoost.Media stores the bytes wherever you tell it — local disk,
Azure Blob Storage, Amazon S3 — and records every upload in **two tables**, whatever you attach it
to: no new column on your entities, no table per entity type, and no migration when you add media
to something new. One file can belong to several entities at once, each deciding what it uses the
file for.

```csharp
var file = await media.For(product).UploadAsync(new MediaUpload
{
    Content     = upload.OpenReadStream(),
    FileName    = upload.FileName,
    ContentType = upload.ContentType,
    Category    = "product-images"
});

// file.Id            → the only handle you keep
// file.StoragePath   → where the bytes actually are
// file.Size          → measured, not taken on trust
// file.Checksum      → SHA-256, computed on the way past
```

```
POST /api/media                     -> 201, the row
GET  /api/media/{id}/content        -> the bytes, with Range and ETag support
GET  /api/media/{id}/url            -> a signed, expiring link straight to Azure or S3
GET  /api/media?ownerEntityName=Product&ownerEntityId=42
```

Switch from local disk to Azure by changing one line at startup. **Files uploaded before the
switch keep working**, because every row remembers which backend wrote it.

Works with **.NET 8 and .NET 10**, on **SQL Server, PostgreSQL, SQLite or MongoDB**, storing bytes
on **local disk, Azure Blob Storage or Amazon S3** (and anything else that speaks S3 — MinIO,
Cloudflare R2, DigitalOcean Spaces).

> **Status:** preview. The packages are on NuGet as `1.0.0-preview.1`, so installing them needs
> `--prerelease` — every command below has it. The API may still change before 1.0.0.

---

## Get started in 5 minutes

This builds a minimal web app that stores files on local disk and records them in a SQLite file.
Nothing else to install.

**1. Create a project and add the packages**

```bash
dotnet new web -n MyShop
cd MyShop
dotnet add package DotNetBoost.Media.Core --prerelease
dotnet add package DotNetBoost.Media.EntityFrameworkCore --prerelease
dotnet add package DotNetBoost.Media.FileSystem --prerelease
dotnet add package DotNetBoost.Media.API --prerelease
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
```

**2. Replace `Program.cs` with this**

```csharp
using DotNetBoost.Media.API;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=shop.db"));

builder.Services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()                          // the table
    .UseFileSystem(Path.Combine(builder.Environment.ContentRootPath, "uploads"))  // the bytes
    .Build();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MediaExceptionHandler>();

var app = builder.Build();

// Create the database on first run. In a real app, use EF Core migrations instead.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();
app.MapMediaEndpoints();          // upload, download, list, attach, detach, delete

// Attach a file to one of your own entities.
app.MapPost("/products/{id:int}/images", async (
    int id, IFormFile file, AppDbContext db, IMediaManager media) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();

    await using var content = file.OpenReadStream();

    var stored = await media.For(product).UploadAsync(new MediaUpload
    {
        Content     = content,
        FileName    = file.FileName,
        ContentType = file.ContentType,
        Category    = "product-images"
    });

    return Results.Ok(stored.Id);
}).DisableAntiforgery();

app.MapGet("/products/{id:int}/images", async (int id, AppDbContext db, IMediaManager media) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();

    var files = await media.For(product).ListByCategoryAsync("product-images");

    // Project, rather than returning the rows as they are: MediaFileInfo carries the storage
    // provider, container and path, and publishing those hands every client a map of your
    // storage account. MapMediaEndpoints() omits them for the same reason.
    return Results.Ok(files.Select(f => new
    {
        f.Id,
        f.FileName,
        f.ContentType,
        f.Size,
        ContentUrl = $"/api/media/{f.Id}/content"
    }));
});

app.Run();

// Your entity. Note what is NOT here: no ImageUrl, no Attachments collection.
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyMedia();     // adds the two tables — the whole opt-in
    }
}
```

**3. Run it**

```bash
dotnet run
```

```bash
curl -F "file=@photo.png" http://localhost:5000/api/media
```

The response is the row. The bytes are under `uploads/2026/09/27/…png`. Fetch them back at
`/api/media/{id}/content`.

---

## What you get

| | |
|---|---|
| **Two tables for everything** | Adding media to a new entity costs no table, no column and no migration. An attachment row points at your entity by name and key — and one file can be attached to as many entities as you like, each with its own role and order. |
| **Bytes anywhere** | Local disk, Azure Blob Storage, Amazon S3 — or several at once. Each row remembers its own backend, so switching does not strand what came before. |
| **Nothing is taken on trust** | Size is measured while writing. The declared content type is checked against the file's own leading bytes. Executables are refused by default. The storage path is derived from the file's id, never from its name. |
| **Single-pass streaming** | A 2 GB upload costs 2 GB of network and no memory: the size, the SHA-256 and the size limit all fall out of the one read the backend does. |
| **Nothing dangling** | Bytes are written first and the row second; if the row fails, the bytes are removed again. Deletes go the other way. |
| **Listings cost one query** | `ForMany(products)` reads the media for a whole page of entities in a single round trip, instead of one — or two — per row. `db.OwnersWithMedia<Product>()` composes into your own query for the "has an image" filter that has to run *before* paging. |
| **Direct downloads** | `GetUrlAsync` mints a time-limited SAS or pre-signed URL, so large files never pass through your application. |
| **Honest failures** | 413 for an oversized upload, 415 for a rejected type, 404 for a missing file, 502 when the storage account is the problem — as RFC 7807 problem details. |

---

## Packages

Take Core, one metadata store, and at least one storage backend. The rest are optional.

| Package | What it is |
|---|---|
| `DotNetBoost.Media.Core` | The engine and `IMediaManager`. Required. |
| `DotNetBoost.Media.EntityFrameworkCore` | The table, in your own `DbContext`. |
| `DotNetBoost.Media.Dapper` | The table, through Dapper, for applications without EF Core. |
| `DotNetBoost.Media.MongoDb` | The rows as documents in one collection. |
| `DotNetBoost.Media.FileSystem` | Bytes on local disk. No dependencies. |
| `DotNetBoost.Media.AzureBlobStorage` | Bytes in a blob container, with SAS URLs. |
| `DotNetBoost.Media.AmazonS3` | Bytes in an S3 bucket, with pre-signed URLs. Works with any S3-compatible store. |
| `DotNetBoost.Media.API` | ASP.NET Core endpoints and problem-details mapping. |

Exactly one metadata store. **Any number of storage backends** — that is what makes a migration
from local disk to the cloud a one-line change rather than a data migration:

```csharp
services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseFileSystem("./uploads")                              // still serves everything already here
    .UseAzureBlobStorage(connectionString, isDefault: true)  // new uploads go here
    .Build();
```

---

## The two rows

`MediaFiles` — the bytes live in the backend; this is everything else about them.

| Column | |
|---|---|
| `Id` | A GUID, assigned before the bytes are written — the storage path is derived from it. |
| `FileName` `ContentType` `Size` `Checksum` | What it is. Measured and sniffed, not taken from the request. |
| `StorageProvider` `StorageContainer` `StoragePath` | **Where the bytes are.** The path, not the bytes. |
| `Category` `Tags` `Metadata` | How you find it again. |
| `UploadedBy` `CreatedAt` `UpdatedAt` `DeletedAt` | Who, when, and whether it is still live. |

`MediaAttachments` — who is using it, and for what.

| Column | |
|---|---|
| `MediaFileId` | The file. A file may have no attachments, one, or many. |
| `OwnerEntityName` `OwnerEntityId` | The entity using it. |
| `Role` | What that entity uses it for — `primary`, `gallery`, `seo`, or whatever you mean. |
| `SortOrder` | Its position within that role. |

The file records nothing about who uses it, which is what lets the same image be one product's main
photo and another's third gallery shot — neither answer belongs to the file.

There is **no foreign key** from an attachment row to your entity. That is deliberate: it is what
lets two tables serve every entity in the application. The price is that deleting an entity does not
cascade — call `media.For(entity).DeleteAllAsync()` when it goes, which deletes the files that
entity alone was using and unlinks the rest, and returns the ids it deleted.

To ask *before* acting — a confirmation that has to say "2 of these 5 images are used by other
products and will stay" — `WhichAreSharedAsync(ids)` is that same sole-holder test on its own, in
one query however many files it is handed.

Nothing cascades *out* of this library either, for the same reason. If your application keeps
records of its own about a file — per-language alt text, a search index entry, a CDN edge — register
a handler rather than remembering the cleanup at each call site, because the REST endpoint's own
`DELETE` is a call site no application code runs on:

```csharp
.OnFilesDeleted(async (services, ids, ct) =>
{
    var db = services.GetRequiredService<AppDbContext>();
    await db.MediaAltText.Where(t => ids.Contains(t.MediaFileId)).ExecuteDeleteAsync(ct);
})
```

`.OnFilesDeleted<THandler>()` registers an `IMediaFilesDeletedHandler` class instead, for anything
longer than a statement. Either form runs in the delete's own scope, so a `DbContext` resolved from
the `IServiceProvider` passed in is the one the delete used. Handlers hear about the files that were
**deleted** — never the ones merely detached, which are still in use and whose records must survive.

---

## Reading a listing

`For(entity)` is one owner at a time by construction, so a page of fifty products drawn through it
costs fifty queries — a hundred, once the files are wanted too. `ForMany` asks the same questions
about many owners, each answered in **one** store round trip whatever the owner count:

```csharp
var products = await db.Products.OrderBy(p => p.Name).Skip(skip).Take(50).ToListAsync(ct);

var heroes = await media.ForMany(products).GetByRoleAsync("primary", ct);

foreach (var product in products)
    Draw(product, heroes[product.Id.ToString(CultureInfo.InvariantCulture)]);
```

`ForMany(entities)` — or `ForMany("Product", [7, 8, 9])` if keys are what you hold — returns an
`IMediaBulkAccessor` with the three questions a listing actually asks:

| | |
|---|---|
| `ListWithRolesAsync()` | each owner's files with the role each holds, per link |
| `GetByRoleAsync(role)` | each owner's file in a single-valued role, or `null` |
| `WhichHaveAttachmentsAsync(role?)` | which of the asked-about owners have anything at all — a set of the ones that do, not an entry each |

Results are keyed by the owner's formatted entity id — the same invariant string an attachment row
stores — and in the two that return a dictionary **every id you asked about is present**: an owner
with nothing attached maps to an empty list or to `null`, so nothing needs `GetValueOrDefault` and
the first row without a photo does not throw. `ForMany` reads only; a role and a position belong to
one link, so attaching, detaching and reordering stay on `For(entity)`.

Filtering *before* paging is a different question — "the first 20 products that have an image" is
not a subset of the first 20 products — so it has to be asked where both sets live. In the EF Core
package:

```csharp
var withImage = db.OwnersWithMedia<Product>("primary");   // IQueryable<string>, not run yet

var page = await db.Products.Where(p => withImage.Contains(p.Id.ToString()))
    .OrderBy(p => p.Name).Skip(skip).Take(take).ToListAsync(ct);
```

It has not run, so it folds into your own query as a subquery rather than returning a list of ids to
pass back in. Behind both, `IMediaStore.ListAttachmentsForOwnersAsync` returns each attachment with
the live file it points at: **the join is the store's, not the engine's**, which is what makes the
read one statement. A custom store that loops over the single-owner method will pass the contract
tests and defeat the point.

---

## Configuration

Defaults are the safe ones; you opt out of them, not in.

```csharp
services.AddMedia(options => options.MaxFileSizeBytes = 25 * 1024 * 1024)
    .UseEntityFrameworkCore<AppDbContext>()
    .UseAzureBlobStorage(connectionString, "media")
    .AllowContentTypes("image/*", "application/pdf")   // default: anything not blocked
    .AllowExtensions(".png", ".jpg", ".pdf")           // default: anything not blocked
    .BlockExtensions(".svg")                           // on top of the built-in executable list
    .DeduplicateByChecksum()                           // default: off — read why first
    .WithDefaultUrlExpiry(TimeSpan.FromMinutes(15))    // default: 1 hour
    .Build();
```

| Default | Why |
|---|---|
| 100 MB limit | Enforced as the stream is read, so a lying `Content-Length` is still stopped. |
| Executables blocked | `.exe`, `.dll`, `.sh`, `.ps1`, `.js` and the rest. A file store should not hand users malware. |
| Content type verified against the bytes | An executable renamed `photo.png` is refused, with a 415 that says what it really is. |
| SHA-256 computed | Free — the bytes are already streaming past. |
| Soft delete on | The bytes always go; the row stays as the record that the file existed. |
| Deduplication off | Two callers sharing one row also share its lifetime. Right for assets, wrong for documents. |

Full list: [docs/configuration-reference.md](https://github.com/dotnetboost/media/blob/main/docs/configuration-reference.md).

---

## Security

Three things are worth knowing before this reaches production.

**The endpoints are anonymous.** `MapMediaEndpoints()` maps them without authorization, as every
`Map…` in ASP.NET Core does — a library cannot know your policies. Left open, `POST /api/media`
lets anyone fill your storage account and `GET /api/media/{id}/content` serves every file to
anyone with an id. The route group is returned so this takes one line:

```csharp
app.MapMediaEndpoints().RequireAuthorization("media");
```

**Containers are private.** The Azure backend creates its container with no public access, and the
S3 backend never creates a bucket at all — bucket creation carries public-access and encryption
decisions a library should not be making for you.

**Paths cannot be chosen.** The storage path is built from the file's own id, so an uploader
cannot pick where a file lands, collide with another, or aim at a directory. Every path is
re-validated on the way back out of the database, and the local backend proves containment against
the resolved root as well.

**A stored file cannot script your origin.** Downloads always carry
`X-Content-Type-Options: nosniff`, and the types a browser runs script from — HTML, SVG, XML,
JavaScript — are served as attachments and sandboxed rather than inline, whatever `?download`
says. Embedding still works: `Content-Disposition` governs navigations, not `<img>`, so a stored
SVG logo draws where a page asks for it. Serving user uploads from a separate origin is still
stronger, and `GET /api/media/{id}/url` already does.

---

## Documentation

| | |
|---|---|
| [Architecture](https://github.com/dotnetboost/media/blob/main/docs/architecture.md) | The two halves, and why the ordering between them is what it is |
| [Uploading and reading](https://github.com/dotnetboost/media/blob/main/docs/uploading-and-reading.md) | `IMediaManager` in full |
| [Attaching media to entities](https://github.com/dotnetboost/media/blob/main/docs/attaching-to-entities.md) | `For(entity)` and `ForMany(entities)`, roles and ordering, one file on many entities, and cleanup |
| [Storage backends](https://github.com/dotnetboost/media/blob/main/docs/storage-backends.md) | Local disk, Azure, S3 — and running several |
| [Metadata stores](https://github.com/dotnetboost/media/blob/main/docs/metadata-stores.md) | EF Core, Dapper, MongoDB — and implementing `IMediaStore` yourself |
| [Validation and security](https://github.com/dotnetboost/media/blob/main/docs/validation-and-security.md) | Limits, allow-lists, sniffing, path safety |
| [REST API](https://github.com/dotnetboost/media/blob/main/docs/rest-api.md) | Every endpoint, with its status codes |
| [Dashboard](https://github.com/dotnetboost/media/blob/main/docs/dashboard.md) | The Nuxt client in `clients/dashboard` |
| [Querying](https://github.com/dotnetboost/media/blob/main/docs/querying.md) | Filtering, paging, tags |
| [Configuration reference](https://github.com/dotnetboost/media/blob/main/docs/configuration-reference.md) | Every option and builder method |
| [Adding a backend](https://github.com/dotnetboost/media/blob/main/docs/adding-a-backend.md) | Implementing `IMediaStorageProvider` |
| [Aspire](https://github.com/dotnetboost/media/blob/main/docs/aspire.md) | Running the sample stack |
| [Limitations](https://github.com/dotnetboost/media/blob/main/docs/limitations.md) | What this does not do, and what to use instead |

---

## Running the sample

```bash
dotnet run --project aspire/DotNetBoost.Media.AppHost
```

Starts PostgreSQL in a container, the sample API beside it, and a **Nuxt dashboard** over both —
browse the library, search it, sort it, upload, and give a product its images from a page with **a
box per role** — empty with a `+`, or holding the image with an `x` that detaches it. The picker a
box opens is the same browser as the Media page, with the role already fixed.

See [docs/aspire.md](https://github.com/dotnetboost/media/blob/main/docs/aspire.md) and
[docs/dashboard.md](https://github.com/dotnetboost/media/blob/main/docs/dashboard.md).

---

## Relationship to the other DotNetBoost libraries

Same shape as [DotNetBoost.Settings](https://github.com/dotnetboost/settings) and
[DotNetBoost.MultiLingual](https://github.com/dotnetboost/multilingual): one service to inject,
one builder chain to configure, the same storage providers, the same `[Key]` / `Id` /
`{Type}Id` key convention, and problem details on the same terms. They are independent packages —
use one, or all three together.

### DotNetBoost Entity Addressing v1

This library and DotNetBoost.MultiLingual address an entity **identically**, which is what lets an
application join a media row to a translation row on the pair of columns. The rules:

| | |
|---|---|
| **Entity name** | The name fixed by `[MediaEntity("…")]` — `[MultiLingualEntity("…")]` on the other side — if the type carries one; otherwise the CLR type's `Name`, not its full name, so a namespace move changes nothing. Runtime-generated types — EF Core's lazy-loading and change-tracking proxies, any dynamic-assembly subclass — are unwrapped to the application's own type first, so a proxied instance addresses the same rows as a plain one. |
| **Name length** | At most 128 characters. Stored and compared as written: resolution yields one casing for a given type, so case only matters for a name passed by hand, where whether `"mediafile"` reaches `"MediaFile"`'s rows is the database collation's business. Pass the resolved name. |
| **Key discovery** | The single property marked `[Key]`, else one named `Id`, else one named `{TypeName}Id` — public, instance, readable, and not an indexer. More than one `[Key]` is a composite key and is **refused**: one column holds one value, and reflection does not order properties, so picking one would key the entity on whichever this runtime happened to return. A provider with a model to read replaces this — both EF Core providers do — and is authoritative where the two disagree. |
| **Key format** | Invariant culture, always. `Guid` → `"D"` (lower case, hyphenated, no braces). `int`, `long`, `short`, `byte` → their invariant decimal form. `string` → itself, case preserved. Any other `IFormattable` → invariant `ToString`. A key must not be empty or whitespace. |
| **Key length** | At most 64 characters — a `Guid` in `"D"` form is 36, and every numeric key fits with room to spare. A longer key is refused where it is written, not silently truncated by the database. |
| **Unset keys** | `null`, `0` (of any integer width), `Guid.Empty` and `""` mean "not assigned yet" and address nothing. A provider whose keys are a custom struct must return `null` from its `IEntityKeyAccessor` for its own unassigned value — Core cannot recognise a type it may not reference. |

Every library that conforms publishes the version as a constant under the same name, so an
application can assert the two agree and fail at startup rather than discover a mismatch as rows
that quietly stopped joining:

```csharp
Assert.Equal(
    DotNetBoost.Media.Core.Metadata.EntityKey.AddressingVersion,        // "1.0"
    DotNetBoost.MultiLingual.Core.Metadata.EntityKey.AddressingVersion);
```

**The version is bumped for any change to name resolution, key formatting or either length limit**,
including one that looks harmless from inside a single library. So that one equality is the whole of
the dependency: there is nothing further to install and nothing to keep in step by hand.

`EntityKey.MaxLength` and `MediaEntityNames.MaxLength` are public as well, for a consumer that wants
to compare the limits it stores keys and names under rather than trust the version alone. So is
`EntityKey.IsUnset(value)`, the unset test from the table above — a provider writing its own
`IEntityKeyAccessor` applies the rule rather than a copy of it that can drift.

## Contributing

See [CONTRIBUTING.md](https://github.com/dotnetboost/media/blob/main/CONTRIBUTING.md). Licensed under the [MIT License](LICENSE).
