# Validation and security

File upload is the feature most often responsible for a breach, so the defaults here are the
cautious ones. You opt out of them, not in.

## What happens to an upload, in order

```
1. the name        sanitised; extension checked against the block-list, then the allow-list
2. the first bytes read and matched against known signatures
3. the type        the declared one reconciled with the detected one
4. the allow-list  the resolved media type checked
5. the path        generated from the file's id, then validated
6. the bytes       streamed to the backend, counted and hashed, stopped at the size limit
```

Steps 1 and 2 are in that order on purpose: the extension check is free, and the block-list is
absolute. Steps 2 and 4 are in that order for a different reason — sniffing **before**
allow-listing is what stops `evil.exe` renamed to `photo.png` from being admitted by an `image/*`
rule.

## Size

Enforced while the stream is read, so an upload that lies about its length is stopped **at** the
limit rather than after it.

```csharp
services.AddMedia(o => o.MaxFileSizeBytes = 25 * 1024 * 1024)
```

ASP.NET Core has its own limits — `MaxRequestBodySize` (30 MB by default) and Kestrel's own cap —
and **the smaller of the two applies**. Raise both or neither:

```csharp
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 25 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 25 * 1024 * 1024);
```

## Extensions

Executable and script extensions are blocked out of the box:

```
.exe .dll .com .scr .pif .msi .msp .cpl
.bat .cmd .ps1 .psm1 .vbs .vbe .js .jse .wsf .wsh
.jar .app .dmg .sh .bash .zsh .run
```

This is a floor, not a security boundary — the block-list can only refuse what it has heard of. An
**allow-list of the few types you actually serve is the stronger control**:

```csharp
.AllowExtensions(".png", ".jpg", ".jpeg", ".webp", ".pdf")
```

The block-list is checked first, so an allow-list cannot accidentally re-admit `.exe`.

## Media types

```csharp
.AllowContentTypes("image/*", "application/pdf")
```

An empty allow-list — the default — accepts anything the extension rules allow. `image/*` matches
a family.

## Content verification

On by default. The upload's first 32 bytes are matched against known signatures, and a declared
type the bytes contradict is refused:

```
photo.png declaring image/png, containing "MZ…"
  → 415 MediaContentMismatchException
    declared: image/png
    detected: application/vnd.microsoft.portable-executable
```

This is what matters the moment a browser is pointed at a stored file.

Two things it is careful about:

- **Container formats.** A `.docx` really is a ZIP, and so is every OOXML and OpenDocument format;
  an `.m4a` really is ISO base media, and so is an `.mp4`. Being strict about those would reject
  correct uploads, so the detected type and the declared one are checked for *compatibility*, not
  equality — and where they are compatible, the **declared** type wins, because it is the more
  specific of the two.
- **Unrecognised formats are accepted.** The check can only speak about signatures it knows, and
  refusing everything else would make the library useless for anything niche.

`application/octet-stream` is treated as "no claim" rather than a claim, because that is what a
browser sends when it has no idea.

Turn it off only where uploads are not user-supplied:

```csharp
.DisableContentTypeVerification()
```

## Paths

**An uploader never chooses where a file lands.** The storage path is generated from the file's own
id:

```
invoices/2026/09/27/9f1cb3e0d4a94f7ab0e2c1d5f6a78901.pdf
```

A path derived from user input is one the user can collide with, guess, or aim at another
directory. A path derived from an id can be none of those. The original name still reaches the
user — it is in the row, and the download sets `Content-Disposition` from it.

Three layers back this up:

1. The uploaded name is sanitised: directory components dropped, control and metacharacters
   stripped, `.` and `..` rejected, length capped with the extension preserved.
2. Every path is validated before it is used — including one read back from a row, because a row
   is data like any other and may have been written by an older version or by something that got
   at the database.
3. The local backend proves containment against the **resolved** root, which is the only
   comparison a file system actually honours — through a symlink, on a case-insensitive volume, or
   past whatever encoding trick defeats a textual check.

## Authorization

**`MapMediaEndpoints()` maps anonymous endpoints**, as every `Map…` in ASP.NET Core does. A library
cannot know which policies your application has declared.

Left open:

- `POST /api/media` lets anyone on the internet fill your storage account.
- `GET /api/media/{id}/content` serves every uploaded file to anyone holding — or guessing at — an
  id.

The route group is returned so this takes one line:

```csharp
app.MapMediaEndpoints().RequireAuthorization("media");
```

Per-file authorization — "this user may read this file" — is yours: the library records who uploaded
a file and which entities are attached to it so you can decide, but it does not decide for you. Note
that a file may be attached to several entities, so the question is about all of them rather than
one. For anything beyond "signed in", write your own routes over `IMediaManager` and put your
checks in them.

## Serving stored files safely

Two rules for anything user-supplied:

- **Serve from a different origin** than your application, or with
  `Content-Security-Policy: sandbox`. An HTML or SVG file served from your own origin runs script
  in your origin.
- **Send `X-Content-Type-Options: nosniff`.** Without it a browser may sniff the content and
  execute something you served as `text/plain`.

`MapMediaEndpoints()` serves the stored content type, which has been verified against the bytes.
That is a useful part of the answer and not all of it.

## Storage account posture

- The Azure backend creates its container **private**, and this is not configurable through the
  library.
- The S3 backend **never creates a bucket**: public-access and encryption defaults belong in your
  infrastructure code, not in a library's guess.
- Neither backend puts a file name or anything else user-supplied in a path.

## Checksums

SHA-256 of the stored bytes, on by default and free — the bytes are already streaming past. It
detects corruption and backs optional deduplication.

`DeduplicateByChecksum()` is **off** by default, and the reason is worth knowing before you turn it
on: the returned row carries the *first* upload's name, category and uploader, so two callers who
"uploaded" a file share one row and one lifetime — deleting it deletes it for both. Attaching is
unaffected: an upload through `media.For(entity)` attaches the deduplicated file, so both entities
have it. Right for interchangeable content-addressed assets; wrong for somebody's document.
