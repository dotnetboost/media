# DotNetBoost.Media

[![NuGet](https://img.shields.io/nuget/vpre/DotNetBoost.Media.Core?label=nuget)](https://www.nuget.org/packages/DotNetBoost.Media.Core)
[![CI](https://github.com/dotnetboost/media/actions/workflows/ci.yml/badge.svg)](https://github.com/dotnetboost/media/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/dotnetboost/media/blob/main/LICENSE)

**Upload a file once. Know what it is, where it is, and what it belongs to — forever.**

You inject one service. DotNetBoost.Media stores the bytes wherever you tell it — local disk,
Azure Blob Storage, Amazon S3 — and records every upload as a row in **one table**, with no new
column on your entities, no join table per attachment, and no migration when you add media to
something new.

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
app.MapMediaEndpoints();          // POST/GET/PATCH/DELETE /api/media

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

    var files = await media.For(product).ListAsync("product-images");

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

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IMediaDbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyMedia();     // adds the MediaFiles table
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
| **One table for everything** | Adding media to a new entity costs no table, no column and no migration. The row points at your entity by name and key. |
| **Bytes anywhere** | Local disk, Azure Blob Storage, Amazon S3 — or several at once. Each row remembers its own backend, so switching does not strand what came before. |
| **Nothing is taken on trust** | Size is measured while writing. The declared content type is checked against the file's own leading bytes. Executables are refused by default. The storage path is derived from the file's id, never from its name. |
| **Single-pass streaming** | A 2 GB upload costs 2 GB of network and no memory: the size, the SHA-256 and the size limit all fall out of the one read the backend does. |
| **Nothing dangling** | Bytes are written first and the row second; if the row fails, the bytes are removed again. Deletes go the other way. |
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

## The row

One table, `MediaFiles`. The bytes live in the backend; this is everything else about them.

| Column | |
|---|---|
| `Id` | A GUID, assigned before the bytes are written — the storage path is derived from it. |
| `FileName` `ContentType` `Size` `Checksum` | What it is. Measured and sniffed, not taken from the request. |
| `StorageProvider` `StorageContainer` `StoragePath` | **Where the bytes are.** The path, not the bytes. |
| `OwnerEntityName` `OwnerEntityId` | What it is attached to, if anything. |
| `Category` `Tags` `Metadata` | How you find it again. |
| `UploadedBy` `CreatedAt` `UpdatedAt` `DeletedAt` | Who, when, and whether it is still live. |

There is **no foreign key** from a media row to your entity. That is deliberate: it is what lets
one table serve every entity in the application. The price is that deleting an entity does not
cascade — call `media.For(entity).DeleteAllAsync()` when it goes.

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

---

## Documentation

| | |
|---|---|
| [Architecture](https://github.com/dotnetboost/media/blob/main/docs/architecture.md) | The two halves, and why the ordering between them is what it is |
| [Uploading and reading](https://github.com/dotnetboost/media/blob/main/docs/uploading-and-reading.md) | `IMediaManager` in full |
| [Attaching media to entities](https://github.com/dotnetboost/media/blob/main/docs/attaching-to-entities.md) | `For(entity)`, names, keys, and cleanup |
| [Storage backends](https://github.com/dotnetboost/media/blob/main/docs/storage-backends.md) | Local disk, Azure, S3 — and running several |
| [Metadata stores](https://github.com/dotnetboost/media/blob/main/docs/metadata-stores.md) | EF Core, Dapper, MongoDB |
| [Validation and security](https://github.com/dotnetboost/media/blob/main/docs/validation-and-security.md) | Limits, allow-lists, sniffing, path safety |
| [REST API](https://github.com/dotnetboost/media/blob/main/docs/rest-api.md) | Every endpoint, with its status codes |
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

Starts PostgreSQL in a container and the sample API beside it, with the Aspire dashboard for logs
and traces. See [docs/aspire.md](https://github.com/dotnetboost/media/blob/main/docs/aspire.md).

---

## Relationship to the other DotNetBoost libraries

Same shape as [DotNetBoost.Settings](https://github.com/dotnetboost/settings) and
[DotNetBoost.MultiLingual](https://github.com/dotnetboost/multilingual): one service to inject,
one builder chain to configure, the same storage providers, the same `[Key]` / `Id` /
`{Type}Id` key convention, and problem details on the same terms. They are independent packages —
use one, or all three together.

## Contributing

See [CONTRIBUTING.md](https://github.com/dotnetboost/media/blob/main/CONTRIBUTING.md). Licensed under the [MIT License](LICENSE).
