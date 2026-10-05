# Configuration reference

```csharp
services.AddMedia(options => { /* … */ })
    .UseEntityFrameworkCore<AppDbContext>()   // exactly one metadata store
    .UseFileSystem("./uploads")               // one or more storage backends
    .Build();                                 // validates, and registers the startup probe
```

`Build()` refuses a configuration with no metadata store or no storage backend, and says which is
missing.

## `MediaOptions`

| Option | Default | |
|---|---|---|
| `MaxFileSizeBytes` | 100 MB | Enforced as the stream is read. ASP.NET Core's own limit also applies; the smaller wins. |
| `AllowedContentTypes` | *(empty — all)* | `image/*` matches a family. |
| `AllowedExtensions` | *(empty — all)* | Checked after the block-list. |
| `BlockedExtensions` | executables and scripts | Always refused, before the allow-list. |
| `ComputeChecksum` | `true` | SHA-256, computed on the stream already passing through. |
| `DeduplicateByChecksum` | `false` | Requires checksums. [Read why it is off.](#deduplication) |
| `SoftDelete` | `true` | The bytes always go; the row stays with `DeletedAt` set. |
| `VerifyContentType` | `true` | The declared type is checked against the file's own bytes. |
| `DefaultUrlExpiry` | 1 hour | For backends that sign links. |
| `ValidateStorageAtStartup` | `true` | Probes every backend as the host starts. |
| `FallbackContentType` | `application/octet-stream` | When nothing better can be determined. |

## Builder methods

| | |
|---|---|
| `WithMaxFileSize(bytes)` | |
| `AllowContentTypes(params string[])` | Adds on repeated calls. |
| `AllowExtensions(params string[])` | A leading dot is added when you leave it off. |
| `BlockExtensions(params string[])` | On top of the built-in list. |
| `DeduplicateByChecksum()` | Throws if checksums are off. |
| `DisableChecksums()` | Also disables deduplication. |
| `DisableSoftDelete()` | |
| `DisableContentTypeVerification()` | |
| `WithDefaultUrlExpiry(TimeSpan)` | |
| `DisableStartupValidation()` | Run it yourself with `services.ValidateMediaAsync()`. |
| `UseKeyGenerator<T>()` | Replaces how storage paths are chosen. |
| `UseEntityKeyAccessor<T>()` | Replaces how entity keys are read. |
| `OnFilesDeleted<THandler>()` | Registers an `IMediaFilesDeletedHandler`, scoped. Call it more than once for several. |
| `OnFilesDeleted((services, ids, ct) => …)` | The same, written inline. `services` is the delete's own scope. |

### Files-deleted handlers

Nothing cascades out of this library, so an application keeping records of its own about a file —
per-language alt text, a search index entry, a CDN edge — registers a handler rather than
remembering the cleanup at every call site. The one that gets missed otherwise is
`DELETE /api/media/{id}`, where no application code runs.

```csharp
.OnFilesDeleted(async (serviceProvider, ids, ct) =>
{
    var db = serviceProvider.GetRequiredService<AppDbContext>();
    await db.MediaAltText.Where(t => ids.Contains(t.MediaFileId)).ExecuteDeleteAsync(ct);
})
```

Raised after the files are gone, with the ids that actually went — `DeleteAllAsync` passes the
batch it deleted and **not** the files it merely detached. Handlers run in registration order, and
each one's exceptions are logged and swallowed: the bytes are already gone, so a throw would report
a completed delete as a failure and stop the handlers after it.

## Metadata stores — one of

| | |
|---|---|
| `UseEntityFrameworkCore<TContext>()` | Rows in your own `DbContext`. Add `modelBuilder.ApplyMedia()`. |
| `UseDapper(connectionFactory, migrateSchema, configure)` | Rows through Dapper. |
| `UseMongoDb(configure)` | Rows as documents. Also takes a connection string or a factory. |
| `UseCustomStore<TStore>()` | Your own `IMediaStore`. |

## Storage backends — one or more

| | |
|---|---|
| `UseFileSystem(rootPath, isDefault, configure)` | Local disk. |
| `UseAzureBlobStorage(connectionString, containerName, isDefault, configure)` | Also takes a registered `BlobServiceClient` or a factory. |
| `UseAmazonS3(bucketName, isDefault, configure)` | Also takes an `AmazonS3Config` or a factory. |
| `UseCustomStorage<TProvider>(name, isDefault)` | Your own `IMediaStorageProvider`. |

`isDefault` picks which backend new uploads go to. With one backend registered it makes no
difference — the only backend is the default. Two backends both claiming it is an error, as is two
sharing a name.

### `FileSystemOptions`

| | Default | |
|---|---|---|
| `RootPath` | *(required)* | Prefer an absolute path on a durable volume. |
| `CreateRootIfMissing` | `true` | Off makes a missing mount fail the boot. |
| `PublicBaseUrl` | `null` | Where a web server serves the directory. **Anything served this way is public.** |
| `BufferSize` | 80 KB | |

### `AzureBlobOptions`

| | Default | |
|---|---|---|
| `ContainerName` | `media` | |
| `CreateContainerIfMissing` | `true` | Created **private**; not configurable. |
| `BlockSize` | 4 MB | |
| `MaxConcurrency` | 4 | |
| `GenerateSasUrls` | `true` | Needs a credential that can sign. |
| `PublicBaseUrl` | `null` | CDN or custom domain; the SAS is kept. |

### `AmazonS3Options`

| | Default | |
|---|---|---|
| `BucketName` | `media` | **Never created** by this backend. |
| `KeyPrefix` | `null` | Partitions a shared bucket. |
| `ValidateBucketAtStartup` | `true` | Checks only; needs `s3:ListBucket`. |
| `GeneratePresignedUrls` | `true` | |
| `PublicBaseUrl` | `null` | See the option's note about signatures and CloudFront. |
| `StorageClass` | `null` | e.g. `STANDARD_IA`. |
| `ServerSideEncryption` | `null` | `AES256` or `aws:kms`; `null` uses the bucket default. |

### `MediaModelOptions` (EF Core)

```csharp
modelBuilder.ApplyMedia(o =>
{
    o.MediaFilesTable       = "Uploads";
    o.MediaAttachmentsTable = "UploadLinks";
    o.Schema                = "media";
});
```

| | Default | |
|---|---|---|
| `MediaFilesTable` | `MediaFiles` | |
| `MediaAttachmentsTable` | `MediaAttachments` | The links between files and your entities. Two tables in total, whatever you attach media to. |
| `Schema` | `null` | The model's default schema. |

### `DapperOptions`

| | Default | |
|---|---|---|
| `MediaFilesTable` | `MediaFiles` | Matches the EF Core store, so one database serves both. |
| `MediaAttachmentsTable` | `MediaAttachments` | Likewise. |
| `Schema` | `null` | SQLite has none. |
| `Dialect` | *(inferred)* | Set it when the connection is a wrapper. |

### `MongoDbOptions`

| | Default | |
|---|---|---|
| `MediaFilesCollection` | `mediaFiles` | |
| `MediaAttachmentsCollection` | `mediaAttachments` | A collection of its own rather than an array inside each file: MongoDB sorts an array field by its smallest element, not the one a filter matched, so "this product's gallery in order" would have ordered by the lowest position the file holds for *any* owner. |
| `CreateIndexes` | `true` | Creates both collections' indexes at startup. `MongoMediaIndexes.EnsureCreatedAsync` does the same from a deployment step. |

## Declaring an entity's roles

Opt-in. An entity that declares nothing accepts any role, as every entity did before this existed.

```csharp
[MediaEntity("Product")]
[MediaRole("primary")]
[MediaRole("gallery", Multiple = true)]
[MediaRole("seo")]
public class Product { public int Id { get; set; } }
```

| Builder method | |
|---|---|
| `DeclareRolesFrom(assembly)` | reads every `[MediaRole]` in that assembly |
| `DeclareRoles<TEntity>()` | reads one type's attributes |
| `DeclareRoles(name, r => r.One("…").Many("…"))` | declares on the builder, for a type you cannot annotate |

The declarations are read at `Build()`, not when the method is called, so these lines may sit
anywhere in the chain. They are filed under the entity's **stored name**, which is what lets
`media.For("Product", "42")` and the REST endpoints be checked too — neither has a class to read an
attribute from.

Declaring the same entity twice is refused rather than resolved: two sources disagreeing has no
sensible winner, and silently picking one would leave the other's roles unenforced.

## Deduplication

Off by default, and worth understanding before turning on. When an upload's bytes are already
stored, the existing row is returned instead of a second copy — so two callers who "uploaded" a
file share **one row and one lifetime**, and the row carries the *first* upload's name, category and
uploader. Deleting it deletes it for both.

Attaching is unaffected, and this is where the attachment table earns its keep: an upload through
`media.For(entity)` stores or deduplicates the bytes and then attaches, so identical bytes mean one
file and a second link rather than a second copy. Both entities have it, and neither took it off the
other.

Right for interchangeable content-addressed assets. Wrong for somebody's document.

## Startup validation

`Build()` registers a hosted service that probes every configured backend, so a misspelled
container or an expired credential fails the boot rather than the first upload of the day.

```csharp
.DisableStartupValidation()   // then, at a moment you choose:
await app.Services.ValidateMediaAsync();
```

What "probe" means is up to the backend: creating a missing directory and proving it is writable,
creating a missing private container, listing a bucket.
