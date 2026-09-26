# Storage backends

A backend holds bytes at a path. Which one holds a given file is recorded on its row, so the
choice is per-file rather than global — and changing it does not strand what came before.

## Local disk — `DotNetBoost.Media.FileSystem`

```csharp
.UseFileSystem(Path.Combine(builder.Environment.ContentRootPath, "uploads"))
```

No dependencies beyond Core. The backend to develop against, and a reasonable production one for a
single host with a durable volume — **not** for several hosts behind a load balancer, where one
node's disk is not another's.

Two things to get right:

- **Put the directory on a volume that outlives the process.** A path inside the application
  directory loses every upload on the next deployment.
- **Nothing is reachable over HTTP** unless you say where a web server serves it from. Without
  `PublicBaseUrl`, `GetUrlAsync` returns `null` and downloads go through your application, where
  your authorization applies. That is the right default for anything that is not public.

```csharp
.UseFileSystem("/srv/media", configure: o =>
{
    // Only for genuinely public assets: the URL is the storage path, it never expires,
    // and nothing checks who is asking.
    o.PublicBaseUrl = new Uri("https://cdn.example.com/media");
})
```

At startup the backend creates the directory if it is missing and writes a probe file to prove it
is writable — a read-only mount then fails the boot rather than the first upload.

Every path is resolved and compared against the resolved root before anything is touched, so a
stored path that has been tampered with cannot reach a file outside the media directory. When a
file is deleted, the date directories it emptied are pruned.

## Azure Blob Storage — `DotNetBoost.Media.AzureBlobStorage`

```csharp
.UseAzureBlobStorage(config.GetConnectionString("storage")!, "media")
```

Or over a client the application already registered, which is how to use a managed identity:

```csharp
services.AddSingleton(new BlobServiceClient(new Uri(accountUrl), new DefaultAzureCredential()));

services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseAzureBlobStorage("media")
    .Build();
```

**The container is created private**, and that is not configurable here. A public container makes
every upload readable by anyone with the URL for as long as it exists, and that is not something a
library should be able to arrange by default. If you want public blobs, set the access level
deliberately, in the portal or your infrastructure code.

**SAS URLs need a signing credential.** A connection string or account key can sign; a bare token
credential cannot, and `GetUrlAsync` then returns `null` rather than an unusable link. Either give
the client an account key, or serve downloads through the application.

```csharp
.UseAzureBlobStorage(connectionString, "media", configure: o =>
{
    o.BlockSize      = 8 * 1024 * 1024;                     // default 4 MB
    o.MaxConcurrency = 8;                                   // default 4
    o.PublicBaseUrl  = new Uri("https://cdn.example.com");  // CDN in front; the SAS is kept
})
```

## Amazon S3 — `DotNetBoost.Media.AmazonS3`

```csharp
services.AddDefaultAWSOptions(config.GetAWSOptions());
services.AddAWSService<IAmazonS3>();

services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseAmazonS3("my-app-media")
    .Build();
```

Uploads go through the SDK's transfer utility, which switches to a multipart upload on its own for
large files. Downloads can be handed out as pre-signed URLs.

**This backend never creates a bucket.** On S3 that is a region-bound, globally-unique,
account-level decision with public-access and encryption settings attached, and a library guessing
at those is how a bucket ends up world-readable. Create it in your infrastructure code; the
startup check verifies it exists and is reachable.

Any S3-compatible store works — MinIO, Cloudflare R2, DigitalOcean Spaces, Backblaze B2:

```csharp
.UseAmazonS3(
    new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true },
    "media")
```

Share one bucket between applications or tenants with a key prefix:

```csharp
.UseAmazonS3("shared-media", configure: o => o.KeyPrefix = $"tenant-{tenantId}/")
```

A CDN in front of S3 is a special case worth reading the option's own note on: an S3 signature is
bound to the host it was computed for, so a rewritten pre-signed URL will not authenticate against
CloudFront. Use `PublicBaseUrl` with `GeneratePresignedUrls = false`, for a distribution that does
its own access control.

## Several backends at once

This is how you migrate.

```csharp
services.AddMedia()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseFileSystem("/srv/media")                             // keeps serving everything already here
    .UseAzureBlobStorage(connectionString, isDefault: true)  // new uploads go here
    .Build();
```

Each row records the backend that wrote it, so reads continue to find their bytes. New uploads go
to whichever backend is marked `isDefault`; with a single backend registered, it is the default and
the flag makes no difference.

Send one upload somewhere specific with `MediaUpload.StorageProvider`.

**Keep a backend registered for as long as rows reference it.** Removing one strands its files, and
the registry says so — `MediaStorageException`, naming the backend — rather than failing obscurely.

Moving files between backends is a copy and a three-column update; the library has no built-in
migrator, and [limitations.md](limitations.md) says why.

## Choosing

| | Local disk | Azure Blob Storage | Amazon S3 |
|---|---|---|---|
| Several application hosts | ✗ unless shared storage | ✓ | ✓ |
| Direct client downloads | only if a web server serves the directory | ✓ SAS | ✓ pre-signed |
| Extra dependency | none | `Azure.Storage.Blobs` | `AWSSDK.S3` |
| Creates its container | ✓ the directory | ✓ private | ✗ by design |
| Good for | development, single host | Azure | AWS, and any S3-compatible store |

## Writing your own

See [adding-a-backend.md](adding-a-backend.md).
