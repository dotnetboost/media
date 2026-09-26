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
modelBuilder.ApplyMedia(o => { o.MediaFilesTable = "Uploads"; o.Schema = "media"; });
```

### `DapperOptions`

| | Default | |
|---|---|---|
| `MediaFilesTable` | `MediaFiles` | Matches the EF Core store, so one database serves both. |
| `Schema` | `null` | SQLite has none. |
| `Dialect` | *(inferred)* | Set it when the connection is a wrapper. |

### `MongoDbOptions`

| | Default | |
|---|---|---|
| `MediaFilesCollection` | `mediaFiles` | |
| `CreateIndexes` | `true` | |

## Deduplication

Off by default, and worth understanding before turning on. When an upload's bytes are already
stored, the existing row is returned instead of a second copy — so two callers who "uploaded" a
file share **one row and one lifetime**, and the row carries the *first* upload's name, category,
owner and uploader. Deleting it deletes it for both.

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
