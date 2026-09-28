# Adding a storage backend

A backend holds bytes at a path. Implement `IMediaStorageProvider`, add a `Use…()` method, done —
nothing in Core changes, and the engine treats yours exactly as it treats the ones that ship.

## The contract

```csharp
public interface IMediaStorageProvider
{
    string Name { get; }
    string Container { get; }

    Task<StoredObject> SaveAsync(MediaWrite write, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string container, string path, CancellationToken ct = default);
    Task<bool> DeleteAsync(string container, string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default);
    Task<Uri?> GetUrlAsync(string container, string path, MediaUrlOptions options, CancellationToken ct = default);
}
```

## Seven things to get right

**1. `Name` is permanent.** It is stored in every row your backend writes. Changing it in a later
release strands every file already there.

**2. Take the container from the argument, not from your options.** Reads pass the container the
row recorded, which may not be the one currently configured — that is exactly what lets an
application change containers without losing what came before. Only `SaveAsync` uses your own.

**3. Validate every path.** Call `MediaPaths.Validate(path)` on the way in, including for paths
that came out of the database. A row is data like any other.

```csharp
var safe = MediaPaths.Validate(write.Path);
```

**4. Never buffer the stream.** `MediaWrite.Content` is read once, forward-only, and its length is
not known up front. Hand it to your SDK's streaming upload. Buffering turns a 2 GB upload into
2 GB of memory and defeats the size limit, which is enforced *as the stream is read*.

**5. Return a seekable stream from `OpenReadAsync` if you possibly can.** HTTP `Range` is built
on it: ASP.NET Core serves a partial response only when the stream can report its `Length` and
seek, and answers the whole object with a `200` when it cannot. A forward-only stream therefore
loses range requests *silently* — no error, just a client that downloads the whole file every time
it seeks in a video.

If your SDK only reads forward, do what the S3 backend does: note the object's length when you
open it, let `Seek` move nothing but a counter, and issue a fresh ranged request when a read lands
somewhere the open response cannot reach. `StorageContractTests` asserts both the seekability and
the windows, so a backend added there is checked for it.

**6. Throw the right exception.**

| | |
|---|---|
| `MediaContentMissingException` | Nothing at that path — from `OpenReadAsync` |
| `MediaStorageException` | Anything else that went wrong, with the SDK's exception as the inner one |
| `InvalidMediaException` | A path that could escape the container |

Let `MediaContentMissingException` through unwrapped: the API package turns it into a 404, and
wrapping it in a `MediaStorageException` would make a missing file look like an outage.

**7. Return `null` from `GetUrlAsync`** when you have no direct link. That is the honest answer,
and the caller falls back to streaming through the application. A URL that would 404 for the
client is worse than none.

## Reporting what you wrote

```csharp
return new StoredObject(Container, safe, Size: written, Checksum: null);
```

`Size: 0` tells the engine to use what its own measuring stream counted, which is the same number
and already in hand — return that rather than spending a round trip asking the service. Leave
`Checksum` null unless your backend genuinely computed a SHA-256; the engine has one either way.

## The startup probe

Optionally implement `IMediaStorageHealthCheck` and the engine will call it as the host starts:

```csharp
public async Task CheckAsync(CancellationToken ct = default)
{
    if (!await ReachableAsync(ct))
        throw new MediaStorageException($"Cannot reach '{Container}'. Check …", Name);
}
```

Check what will actually fail later. The local backend writes and deletes a probe file, because a
read-only mount passes an existence check and fails every upload.

## Registering it

```csharp
public static MediaBuilder UseMyBackend(
    this MediaBuilder builder,
    string container,
    bool isDefault = false,
    Action<MyOptions>? configure = null)
{
    ArgumentNullException.ThrowIfNull(builder);

    var options = new MyOptions { Container = container };
    configure?.Invoke(options);

    MediaBuilderGuard.AddStorageBackend(builder, "MyBackend", isDefault);

    builder.Services.TryAddSingleton(options);
    builder.Services.AddSingleton<IMediaStorageProvider>(sp => new MyStorageProvider(options));

    return builder;
}
```

Backends are singletons. The name passed to `AddStorageBackend` must match what the instance
reports — the builder needs it before the container is built, to reject a duplicate and to resolve
the default.

Without a `Use…()` method of your own:

```csharp
.UseCustomStorage<MyStorageProvider>("MyBackend")
```

## Testing it

Derive from `StorageContractTests` in `DotNetBoost.Media.IntegrationTests` and point it at a real
server — a container, ideally, as Azurite and LocalStack are used for the two that ship. The suite
covers round-tripping, multipart-sized uploads, missing paths, deletion semantics, path traversal,
and whether a minted URL is one an unauthenticated client can actually fetch.

A mocked SDK proves only that the mock was written to match the test.

## Adding a metadata store

Same shape: implement `IMediaStore`, register it with
`MediaBuilderGuard.EnsureStoreNotConfigured(builder, "MyStore")`, and run `StoreContractTests`
against it. Stores are deliberately dumb — validation, checksums, deduplication, soft delete and
the ordering of the two writes all live in the engine. A store persists rows.
