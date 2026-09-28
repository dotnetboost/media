# Uploading and reading

Everything goes through `IMediaManager`. Inject it wherever you need it; it is scoped, like the
store behind it.

## Uploading

```csharp
await using var content = formFile.OpenReadStream();

var file = await media.UploadAsync(new MediaUpload
{
    Content     = content,
    FileName    = formFile.FileName,
    ContentType = formFile.ContentType,   // optional — sniffed when absent or useless
    Category    = "invoices",             // optional — leads the storage path
    Tags        = ["2026", "paid"],       // optional
    Metadata    = new Dictionary<string, string> { ["pages"] = "3" },
    UploadedBy  = User.Identity?.Name
}, ct);
```

The stream is **read once, from its current position to the end, and not disposed** — the caller
owns it, because the caller usually got it from a request it is still serving.

What comes back is a `MediaFileInfo`: an immutable snapshot of the row.

```csharp
file.Id            // the only handle you keep
file.FileName      // sanitised: "../../etc/passwd" became "passwd"
file.ContentType   // what the bytes actually are
file.Size          // measured while writing, not taken from Content-Length
file.Checksum      // SHA-256, lowercase hex
file.StoragePath   // where the bytes are: "invoices/2026/09/27/9f1c….pdf"
```

### What the library does not take on trust

| Claimed | What is stored |
|---|---|
| `Content-Length` | The bytes actually written, counted as they stream past |
| `Content-Type` | Reconciled with the file's own leading bytes; a contradiction is refused |
| The file name | Sanitised, and never used to build the storage path |

See [validation-and-security.md](validation-and-security.md).

## Reading the metadata

```csharp
var file = await media.GetAsync(id, ct);     // throws MediaNotFoundException
var maybe = await media.FindAsync(id, ct);   // null instead
```

Soft-deleted files read as `null` / throw, as though they were gone — because as far as their
content is concerned, they are.

## Reading the bytes

```csharp
using var content = await media.OpenReadAsync(id, ct);

return Results.Stream(content.Stream, content.ContentType, content.FileName);
```

Dispose it: it holds an open file handle or network stream. Stream it straight to the response
rather than buffering — that is the whole point of it being a stream.

If the row exists but its bytes do not, this throws `MediaContentMissingException` rather than
returning an empty stream. The API package answers that with a 404.

The stream is seekable on all three shipped backends, which is what lets you pass
`enableRangeProcessing: true` and have it mean something — ASP.NET Core serves a partial response
only when the stream can report a length and seek. A custom backend need not be seekable, so check
`CanSeek` if you support one. `content.File.Size` is the length either way, and is the number to
trust: it was measured while the bytes were written.

Serving user uploads from your own route puts them in your own origin, so do what
`MapMediaEndpoints()` does: send `X-Content-Type-Options: nosniff`, and never serve
`MediaContentTypes.IsActiveContent(content.ContentType)` inline — HTML and SVG run script.
[validation-and-security.md](validation-and-security.md#serving-stored-files-safely) has the
details.

## Handing out a direct link

```csharp
var url = await media.GetUrlAsync(id, new MediaUrlOptions
{
    ExpiresIn    = TimeSpan.FromMinutes(15),
    AsAttachment = true
}, ct);
```

On Azure and S3 this is a signed, expiring URL the client fetches **directly from storage** — the
bytes never pass through your application, which for large files is the difference between a
request that costs you nothing and one that occupies a thread for a minute.

It returns `null` when the backend has no direct link, which is the honest answer for local disk
without a public base URL. Serve those through your own route.

```csharp
var url = await media.GetUrlAsync(id, ct: ct);

return url is not null
    ? Results.Redirect(url.AbsoluteUri)
    : Results.Stream((await media.OpenReadAsync(id, ct)).Stream);
```

Use `AbsoluteUri`, not `ToString()`: `ToString()` un-escapes for display, which corrupts a
signature computed over the escaped form.

## Changing the metadata

`MediaUpdate` is a patch. Properties left unset are left alone.

```csharp
await media.UpdateAsync(id, new MediaUpdate
{
    FileName = "invoice-final.pdf",
    Tags     = ["2026", "paid", "final"]   // replaces the whole set; [] clears it
}, ct);
```

The bytes, their size, their checksum and their location are not editable. Replacing content means
a new upload — which is also what keeps a checksum meaning something.

## Deleting

```csharp
var deleted = await media.DeleteAsync(id, ct);   // false if there was nothing to delete
```

The bytes always go. Whether the row goes depends on `SoftDelete`, which is on by default: the row
stays with `DeletedAt` set, as the record that the file existed. It is **not** a recycle bin — the
content is gone either way.

Deleting twice is not an error, and neither is deleting a file whose bytes a backend says are
already missing. Both make a retried delete complete rather than stick.

## Listing

```csharp
var page = await media.ListAsync(new MediaQuery
{
    Category          = "invoices",
    Tag               = "paid",
    ContentTypePrefix = "application/pdf",
    CreatedAfter      = DateTime.UtcNow.AddDays(-30),
    Skip              = 0,
    Take              = 50
}, ct);

page.Items   // the page
page.Total   // how many match the filter
```

See [querying.md](querying.md).

## Errors

Every expected failure derives from `MediaException`, so an application can catch that one type.

| | When | HTTP |
|---|---|---|
| `MediaNotFoundException` | No live file with that id | 404 |
| `MediaContentMissingException` | The row is there, the bytes are not | 404 |
| `MediaTooLargeException` | Over `MaxFileSizeBytes` | 413 |
| `UnsupportedMediaTypeException` | Extension or media type not allowed | 415 |
| `MediaContentMismatchException` | The bytes contradict the declared type | 415 |
| `MediaConflictException` | A duplicate id | 409 |
| `MediaStorageException` | The backend failed or is misconfigured | 502 |
| `InvalidMediaException` | Something about the request itself is unusable | 400 |

The API package maps all of these; see [rest-api.md](rest-api.md).
