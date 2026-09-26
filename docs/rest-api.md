# REST API

`DotNetBoost.Media.API` maps the endpoints and translates the library's failures into RFC 7807
problem details.

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MediaExceptionHandler>();

var app = builder.Build();
app.UseExceptionHandler();
app.MapMediaEndpoints();            // default prefix: /api/media
```

> **These endpoints are anonymous.** See [validation-and-security.md](validation-and-security.md#authorization)
> before this reaches production. `app.MapMediaEndpoints().RequireAuthorization("media");`

## Endpoints

| | | |
|---|---|---|
| `POST` | `/api/media` | Upload — `multipart/form-data`, file in a field named `file` |
| `GET` | `/api/media` | List, filtered and paged |
| `GET` | `/api/media/{id}` | One file's metadata |
| `GET` | `/api/media/{id}/content` | The bytes — supports `Range` and `ETag` |
| `GET` | `/api/media/{id}/url` | A direct link, when the backend mints one |
| `PATCH` | `/api/media/{id}` | Change name, category, tags, metadata or owner |
| `DELETE` | `/api/media/{id}` | Delete |

## POST /api/media

```bash
curl -F "file=@invoice.pdf" \
     -F "category=invoices" \
     -F "tags=2026,paid" \
     -F "ownerEntityName=Invoice" \
     -F "ownerEntityId=77" \
     https://api.example.com/api/media
```

| Field | |
|---|---|
| `file` | **required** — the file |
| `category` | the bucket to sort it into |
| `tags` | comma-separated |
| `uploadedBy` | who uploaded it |
| `ownerEntityName` + `ownerEntityId` | the entity to attach it to; both or neither |

`201 Created`, with `Location: /api/media/{id}` and the row:

```json
{
  "id": "9f1cb3e0-d4a9-4f7a-b0e2-c1d5f6a78901",
  "fileName": "invoice.pdf",
  "contentType": "application/pdf",
  "size": 182344,
  "checksum": "e3b0c44298fc1c149afbf4c8996fb924…",
  "category": "invoices",
  "tags": ["2026", "paid"],
  "metadata": {},
  "ownerEntityName": "Invoice",
  "ownerEntityId": "77",
  "uploadedBy": null,
  "createdAt": "2026-09-27T10:00:00Z",
  "updatedAt": null,
  "url": null
}
```

The response deliberately says **nothing about where the bytes are** — no provider, no container,
no path. That is a description of your storage account's layout, and publishing it hands every
client a map of it.

## GET /api/media

| Query | |
|---|---|
| `ownerEntityName`, `ownerEntityId` | files attached to one entity |
| `category` | one bucket |
| `tag` | one whole tag — not a prefix |
| `contentType` | a prefix, so `image/` selects every image |
| `uploadedBy` | one uploader |
| `includeDeleted` | include soft-deleted files (default `false`) |
| `oldestFirst` | reverse the order (default: newest first) |
| `skip`, `take` | paging; `take` is capped at 500 |

```json
{ "items": [ … ], "total": 128, "skip": 0, "take": 50 }
```

## GET /api/media/{id}/content

Streams the bytes with the stored content type, `Last-Modified`, and an `ETag` — the file's
checksum, which is a hash of the content and therefore a valid strong tag. That is what makes
conditional requests and the range-based seeking a video or audio player needs work.

`?download=true` sets `Content-Disposition: attachment` with the original file name. Without it
the file is served inline, which is what an `<img>` wants.

`404` when the file is not there, and also when the row is there but its bytes are not.

## GET /api/media/{id}/url

```json
{ "url": "https://account.blob.core.windows.net/media/2026/09/27/9f1c….pdf?sv=…&sig=…" }
```

`?expiresInSeconds=900` overrides the configured default; `?download=true` asks for an attachment
disposition on the signed response.

**`204 No Content`** when the backend has no direct link — local disk without a public base URL,
or an Azure client that cannot sign. Not a `404`: the file exists, and a `404` would send the
client looking for the wrong problem. Fall back to `/content`.

## PATCH /api/media/{id}

```json
{ "fileName": "invoice-final.pdf", "tags": ["2026", "paid", "final"] }
```

Omitted properties are left alone. `tags` and `metadata` replace outright — send `[]` or `{}` to
clear. `clearOwner: true` detaches the file.

The bytes, their size, their checksum and their location are not editable.

## DELETE /api/media/{id}

`204` when a live file was deleted, `404` when there was none. The bytes always go; the row stays
with `deletedAt` set unless soft delete is off.

## Status codes

| | | |
|---|---|---|
| `400` | `InvalidMediaException` | A malformed multipart body, a missing `file` field, an unusable tag |
| `404` | `MediaNotFoundException` | No live file with that id |
| `404` | `MediaContentMissingException` | The row is there, the bytes are not |
| `409` | `MediaConflictException` | A duplicate id |
| `413` | `MediaTooLargeException` | Over the size limit — carries `size` and `limit` |
| `415` | `UnsupportedMediaTypeException` | Extension or media type not allowed — carries `extension` / `contentType` |
| `415` | `MediaContentMismatchException` | Bytes contradict the declared type — carries `declaredContentType` and `detectedContentType` |
| `502` | `MediaStorageException` | The storage backend failed |

A `502` deliberately does **not** include the exception's message: it names the container, the
path and whatever the cloud SDK said, none of which is the caller's business. The full detail is
logged server-side.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.14",
  "title": "Media file too large",
  "status": 413,
  "detail": "The upload is 52,428,800 bytes; the limit is 26,214,400.",
  "instance": "/api/media",
  "size": 52428800,
  "limit": 26214400
}
```

## Your own endpoints

The mapped endpoints are a starting point, not the only way in. Anything beyond "signed in" —
per-file authorization, a bespoke response shape, an upload that also writes your own entity — is
your own route over `IMediaManager`:

```csharp
app.MapGet("/invoices/{id:int}/pdf", async (int id, AppDbContext db, IMediaManager media, CancellationToken ct) =>
{
    var invoice = await db.Invoices.FindAsync([id], ct);
    if (invoice is null || !User.CanSee(invoice)) return Results.NotFound();

    var files = await media.For(invoice).ListAsync("pdf", ct);
    if (files.Count == 0) return Results.NotFound();

    var content = await media.OpenReadAsync(files[0].Id, ct);
    return Results.Stream(content.Stream, content.ContentType, content.FileName);
}).RequireAuthorization();
```

`MediaExceptionHandler` covers those too — it answers any `MediaException` thrown anywhere in the
application.
