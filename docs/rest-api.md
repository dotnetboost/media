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
| `PATCH` | `/api/media/{id}` | Change name, category, tags or metadata |
| `DELETE` | `/api/media/{id}` | Delete the file, and every link to it |
| `POST` | `/api/media/{id}/attachments` | Attach the file to an entity |
| `DELETE` | `/api/media/{id}/attachments/{attachmentId}` | Remove one link, leaving the file alone |
| `PUT` | `/api/media/attachments/order` | Reorder one entity's files within a role |
| `GET` | `/api/media/roles/{entityName}` | The roles that entity declares, if it declares any |

Note what the two deletes mean. `DELETE /api/media/{id}` removes the file from the library and
therefore from every entity using it; `DELETE /api/media/{id}/attachments/{attachmentId}` removes
one link and touches neither the file nor its bytes. A UI offering "remove this image from the
product" wants the second, and gets the attachment ids from `GET /api/media/{id}`.

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
| `ownerEntityName` + `ownerEntityId` | an entity to attach it to as well; both or neither |
| `role` | what that entity uses it for — `primary`, `gallery`, `seo`. Needs an owner |

With an owner the file is stored and attached in one request, which is what a form that already
knows its product does. Without one it lands in the library attached to nothing, waiting for a
`POST` to `{id}/attachments` — the draft-form case.

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
  "attachments": null,
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
| `ownerEntityName`, `ownerEntityId` | files attached to one entity. A file that entity attached under several roles is still returned once |
| `role` | narrows that to one role. Needs an owner: a role belongs to a link, so on its own it would match every entity's gallery at once |
| `unattached` | only files nothing is using — for sweeping up uploads nobody ever claimed. Cannot be combined with an owner, which asks the opposite question |
| `category` | one bucket |
| `tag` | one whole tag — not a prefix |
| `contentType` | a prefix, so `image/` selects every image |
| `uploadedBy` | one uploader |
| `search` | matches anywhere in the file name, case-insensitively |
| `includeDeleted` | include soft-deleted files (default `false`) |
| `sortBy` | `createdAt` (default), `fileName`, `size`, or `sortOrder` for the order an entity put its own files in. An unrecognised value falls back to the default rather than failing the request; `sortOrder` needs an owner and is refused without one |
| `descending` | default `true`, so the default order is newest first |
| `skip`, `take` | paging; `take` is capped at 500 |

```
GET /api/media?search=invoice&category=invoices&sortBy=size&descending=true&take=20
```

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
clear.

The bytes, their size, their checksum and their location are not editable. Neither are attachments:
they are rows of their own, and one file may be attached to many entities, which no field on a
`PATCH` body could express. See the three routes below.

## DELETE /api/media/{id}

`204` when a live file was deleted, `404` when there was none. The bytes always go; the row stays
with `deletedAt` set unless soft delete is off.

**Every attachment of the file goes with it**, so the file leaves every entity that was using it.
That is what deleting a file means; `DELETE /api/media/{id}/attachments` is what removes it from one
entity. Attachment rows are removed outright even under soft delete — a tombstoned file that still
read as attached to a live entity would be worse than no record at all.

`GET /api/media/{id}` returns the links, so a client can say "used by 4 entities" before offering
this.

## GET /api/media/{id}

The row, plus an `attachments` array — the one read that carries it:

```json
{
  "id": "9f1cb3e0-d4a9-4f7a-b0e2-c1d5f6a78901",
  "fileName": "hero.png",
  "attachments": [
    { "id": "…", "ownerEntityName": "Product", "ownerEntityId": "1", "role": "primary", "sortOrder": 0, "createdAt": "2026-09-27T10:00:00Z" },
    { "id": "…", "ownerEntityName": "Product", "ownerEntityId": "9", "role": "gallery", "sortOrder": 2, "createdAt": "2026-09-27T10:05:00Z" }
  ]
}
```

A listing returns `null` there rather than filling it: a page of fifty files would need a query per
file, and a library grid does not need them.

## POST /api/media/{id}/attachments

```json
{ "ownerEntityName": "Product", "ownerEntityId": "1", "role": "primary", "replace": true }
```

`200` with the file and its attachments. Not `201`: attaching is idempotent, so a repeat is not a
new resource.

`replace` is what makes a single-valued role single-valued — `true` takes the role over from
whatever held it, `false` appends as a gallery does. Nothing in the schema knows that `primary`
holds one file and `gallery` holds many, so the request says which it means. A file it displaces is
only detached, never deleted.

`role` defaults to `default` and is lower-cased. An owner named by halves — a type with no key, or a
key with no type — is a `400`.

## DELETE /api/media/{id}/attachments/{attachmentId}

`200` with the file and its remaining attachments, or `404` when that file has no such link. **The
file and its bytes are untouched**, and it stays attached to every other entity using it.

One link per request. An entity that holds the same file under two roles has two attachments, and
removing both takes two calls — which is what addressing a link by its own id means.

The ids come from `GET /api/media/{id}`. The file id in the route is **checked**, not decorative: an
attachment belonging to a different file is a `404` rather than a quiet removal of a link the caller
never named.

Application code that knows the entity rather than the link should use the C# API instead —
`media.For(product).DetachAsync(fileId, role)` is scoped to that entity and cannot reach another's
links at all.

## PUT /api/media/attachments/order

```json
{ "ownerEntityName": "Product", "ownerEntityId": "1", "role": "gallery", "mediaFileIds": ["…", "…"] }
```

`204`. Send **every** file attached to that entity in that role, in the order wanted: a partial list
has no unambiguous meaning, since the files left out would have no defined position. Anything else
is a `400`.

## GET /api/media/roles/{entityName}

```json
[
  { "name": "primary", "multiple": false },
  { "name": "gallery", "multiple": true  },
  { "name": "seo",     "multiple": false }
]
```

What the entity declared through `[MediaRole]` or `DeclareRoles`, for a client that has to offer the
right roles rather than hard-code one entity's vocabulary. `multiple: false` means a client attaching
there should send `"replace": true`.

An entity that declares nothing returns `[]`, not a `404` — it is a real entity that accepts any
role, so offer a free-text box rather than concluding it does not exist.

When roles are declared, attaching is checked against them and a bad one is a `400`:

```
Product has no media role 'galery'. It declares: primary, gallery (many), seo.
'primary' is single-valued on Product, and a file already holds it. Replace it rather than
appending — SetAsync in code, or "replace": true through the REST API.
```

## Malformed bodies and `UseExceptionHandler()`

`MediaExceptionHandler` claims media problems and leaves everything else alone, which is correct —
but it means nothing claims a body the binder could not read. `UseExceptionHandler()` rethrows what
no handler takes, and in **Development** the developer exception page then renders that as a **500**,
though `BadHttpRequestException` already carries `400`.

The result is a request that answers `400` in Production and `500` on the machine it was written on.
Register a handler that honours the status the exception carries — `samples/SampleApp` has a
ten-line `BadRequestExceptionHandler` to copy:

```csharp
services.AddExceptionHandler<BadRequestExceptionHandler>();   // honours badRequest.StatusCode
services.AddExceptionHandler<MediaExceptionHandler>();
```

You then get a problem-details body naming the offending field, which beats a stack trace:

```json
{ "title": "Malformed request body", "status": 400,
  "detail": "The JSON value could not be converted to System.Guid. Path: $.mediaFileIds[0]…" }
```

## No "delete everything this entity has"

There is deliberately no endpoint for `IMediaAccessor.DeleteAllAsync`. It exists for when an entity
is deleted, and that belongs in your own code next to deleting the entity — not in one request that
destroys an arbitrary entity's media.

## Status codes

| | | |
|---|---|---|
| `400` | `InvalidMediaException` | A malformed multipart body, a missing `file` field, an unusable tag, an undeclared role |
| `400` | `BadHttpRequestException` | A JSON body the binder could not read — see the note below |
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

    var files = await media.For(invoice).ListByCategoryAsync("pdf", ct);
    if (files.Count == 0) return Results.NotFound();

    var content = await media.OpenReadAsync(files[0].Id, ct);
    return Results.Stream(content.Stream, content.ContentType, content.FileName);
}).RequireAuthorization();
```

`MediaExceptionHandler` covers those too — it answers any `MediaException` thrown anywhere in the
application.
