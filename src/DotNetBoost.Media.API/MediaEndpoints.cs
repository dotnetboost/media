using DotNetBoost.Media.API;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DotNetBoost.Media.API
{
    /// <summary>A stored file as the endpoints return it.</summary>
    /// <param name="Id">The file's identity.</param>
    /// <param name="FileName">The name it was uploaded under.</param>
    /// <param name="ContentType">The media type it is served as.</param>
    /// <param name="Size">Size of the stored bytes.</param>
    /// <param name="Checksum">Lowercase hex SHA-256 of the bytes, when one was computed.</param>
    /// <param name="Category">The bucket it was sorted into.</param>
    /// <param name="Tags">Its labels.</param>
    /// <param name="Metadata">Whatever the application kept alongside it.</param>
    /// <param name="Attachments">
    /// Everything using this file, on the single-file read. <c>null</c> in a listing, which does not
    /// carry them: a page of fifty files would need a query per file to fill, and a library browser
    /// does not need them to draw a grid. Read one file to see them.
    /// </param>
    /// <param name="UploadedBy">Who uploaded it.</param>
    /// <param name="CreatedAt">When it was uploaded.</param>
    /// <param name="UpdatedAt">When its metadata last changed.</param>
    /// <param name="Url">
    /// A link the client can fetch the bytes from directly, when the backend mints one. <c>null</c>
    /// for a backend that does not — fetch <c>{prefix}/{id}/content</c> instead.
    /// </param>
    public sealed record MediaFileResponse(
        Guid Id,
        string FileName,
        string ContentType,
        long Size,
        string? Checksum,
        string? Category,
        IReadOnlyList<string> Tags,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<MediaAttachmentResponse>? Attachments,
        string? UploadedBy,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        string? Url)
    {
        /// <summary>
        /// Maps a snapshot to its wire shape.
        /// <para>
        /// Deliberately without <c>StorageProvider</c>, <c>StorageContainer</c> and
        /// <c>StoragePath</c>: they are a description of the storage account's layout, and
        /// publishing them tells every client where the bytes are and how the paths are built.
        /// The row keeps them; the response does not.
        /// </para>
        /// </summary>
        public static MediaFileResponse From(
            MediaFileInfo file,
            Uri? url = null,
            IReadOnlyList<MediaAttachmentInfo>? attachments = null)
        {
            ArgumentNullException.ThrowIfNull(file);

            return new MediaFileResponse(
                file.Id,
                file.FileName,
                file.ContentType,
                file.Size,
                file.Checksum,
                file.Category,
                file.Tags,
                file.Metadata,
                attachments is null ? null : [.. attachments.Select(MediaAttachmentResponse.From)],
                file.UploadedBy,
                file.CreatedAt,
                file.UpdatedAt,
                // AbsoluteUri rather than ToString(): ToString() un-escapes the path and query for
                // display, which would corrupt an escaped path segment and, worse, a SAS or
                // pre-signed token whose signature covers the escaped form.
                url?.AbsoluteUri);
        }
    }

    /// <summary>One entity's use of a file, as the endpoints return it.</summary>
    /// <param name="Id">The link's identity.</param>
    /// <param name="OwnerEntityName">The entity type using the file, e.g. <c>Product</c>.</param>
    /// <param name="OwnerEntityId">That entity's key.</param>
    /// <param name="Role">What the file does for it — <c>primary</c>, <c>gallery</c>, <c>seo</c>.</param>
    /// <param name="SortOrder">Its position within that role.</param>
    /// <param name="CreatedAt">When it was attached.</param>
    public sealed record MediaAttachmentResponse(
        Guid Id,
        string OwnerEntityName,
        string OwnerEntityId,
        string Role,
        int SortOrder,
        DateTime CreatedAt)
    {
        /// <summary>Maps a snapshot to its wire shape.</summary>
        public static MediaAttachmentResponse From(MediaAttachmentInfo attachment)
        {
            ArgumentNullException.ThrowIfNull(attachment);

            return new MediaAttachmentResponse(
                attachment.Id,
                attachment.Owner.EntityName,
                attachment.Owner.EntityId,
                attachment.Role,
                attachment.SortOrder,
                attachment.CreatedAt);
        }
    }

    /// <summary>Body of <c>POST {prefix}/{id}/attachments</c>.</summary>
    public sealed record AttachMediaRequest
    {
        /// <summary>The entity type to attach the file to, e.g. <c>Product</c>. Required.</summary>
        public string? OwnerEntityName { get; init; }

        /// <summary>That entity's key. Required.</summary>
        public string? OwnerEntityId { get; init; }

        /// <summary>
        /// What the file is for — <c>primary</c>, <c>gallery</c>, <c>seo</c>. Defaults to
        /// <c>default</c>. Lower-cased, so <c>Primary</c> and <c>primary</c> are one role.
        /// </summary>
        public string? Role { get; init; }

        /// <summary>
        /// Whether this file becomes the <b>only</b> holder of that role, detaching whatever held it
        /// before — <c>true</c> for a single-valued role like <c>primary</c>. Default: <c>false</c>,
        /// which appends, as a gallery does.
        /// <para>
        /// The files it displaces are only detached, never deleted: they may well be in use
        /// elsewhere.
        /// </para>
        /// </summary>
        public bool Replace { get; init; }
    }

    /// <summary>Body of <c>PUT {prefix}/attachments/order</c>.</summary>
    public sealed record ReorderAttachmentsRequest
    {
        /// <summary>The entity type whose files are being reordered. Required.</summary>
        public string? OwnerEntityName { get; init; }

        /// <summary>That entity's key. Required.</summary>
        public string? OwnerEntityId { get; init; }

        /// <summary>The role to reorder. Defaults to <c>default</c>.</summary>
        public string? Role { get; init; }

        /// <summary>
        /// Every file attached to that entity in that role, in the order wanted. All of them: a
        /// partial list has no unambiguous meaning, since the files left out would have no defined
        /// position.
        /// </summary>
        public IReadOnlyList<Guid>? MediaFileIds { get; init; }
    }

    /// <summary>One page of a listing.</summary>
    /// <param name="Items">The page's files, newest first unless asked otherwise.</param>
    /// <param name="Total">How many files match the filter.</param>
    /// <param name="Skip">The offset this page starts at.</param>
    /// <param name="Take">The page size that was applied, after capping.</param>
    public sealed record MediaPageResponse(IReadOnlyList<MediaFileResponse> Items, int Total, int Skip, int Take);

    /// <summary>Body of <c>PATCH {prefix}/{id}</c>. Omitted properties are left alone.</summary>
    public sealed record UpdateMediaRequest
    {
        /// <summary>A new display name. The stored bytes do not move.</summary>
        public string? FileName { get; init; }

        /// <summary>A new bucket.</summary>
        public string? Category { get; init; }

        /// <summary>Replaces the file's labels outright. An empty array clears them.</summary>
        public IReadOnlyList<string>? Tags { get; init; }

        /// <summary>Replaces the file's metadata outright. An empty object clears it.</summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        // Attaching is not a metadata patch: it creates or removes a link, and one file may be
        // attached to many entities. POST and DELETE {prefix}/{id}/attachments do that.
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Maps the media endpoints.</summary>
    public static class MediaEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps upload, download and management under <paramref name="prefix"/>:
        /// <code>
        /// POST   {prefix}                 upload (multipart/form-data, field "file")
        /// GET    {prefix}                 list, filtered and paged
        /// GET    {prefix}/{id}            one file's metadata
        /// GET    {prefix}/{id}/content    download the bytes (supports Range)
        /// GET    {prefix}/{id}/url        a direct link, when the backend mints one
        /// PATCH  {prefix}/{id}            change name, category, tags or metadata
        /// DELETE {prefix}/{id}            delete the file, and every link to it
        ///
        /// POST   {prefix}/{id}/attachments   attach the file to an entity
        /// DELETE {prefix}/{id}/attachments   detach it from one, leaving the file alone
        /// PUT    {prefix}/attachments/order  reorder one entity's files within a role
        /// </code>
        /// <para>
        /// Note what the two deletes mean. <c>DELETE {prefix}/{id}</c> removes the file from the
        /// library and therefore from every entity using it; <c>DELETE {prefix}/{id}/attachments</c>
        /// removes it from one entity and touches neither the file nor its bytes. A UI offering
        /// "remove this image from the product" wants the second.
        /// </para>
        /// <para>
        /// There is deliberately no "delete everything this entity has" endpoint.
        /// <c>IMediaAccessor.DeleteAllAsync</c> exists for when an entity is deleted, and that is a
        /// decision the application makes in its own code, next to deleting the entity — not
        /// something to expose as one request that destroys an arbitrary entity's media.
        /// </para>
        /// <para>
        /// <b>They are anonymous</b>, as every <c>Map…</c> in ASP.NET Core is: a library cannot
        /// know which authorization policies an application has declared, so requiring one here
        /// would either invent a policy name every consumer has to define or fail at startup for
        /// any application that has not configured authentication. Protecting them is the
        /// application's decision. The route group is returned so that it takes one line:
        /// <code>
        /// app.MapMediaEndpoints().RequireAuthorization();
        /// </code>
        /// This matters more here than for most endpoint sets. Left open, <c>POST {prefix}</c>
        /// lets anyone on the internet fill your storage account, and <c>GET {prefix}/{id}/content</c>
        /// serves every uploaded file to anyone holding — or guessing at — an id. Secure them
        /// before production.
        /// </para>
        /// </summary>
        public static RouteGroupBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/api/media")
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

            var basePath = prefix.TrimEnd('/');
            var group = endpoints.MapGroup(basePath)
                .WithTags("Media")
                .AddEndpointFilter<MediaProblemFilter>();

            group.MapPost("/", (HttpRequest request, IMediaManager media, CancellationToken ct)
                    => UploadAsync(request, media, basePath, ct))
                .WithSummary("Uploads a file. multipart/form-data, with the file in a field named \"file\".")
                .DisableAntiforgery()
                .Accepts<IFormFile>("multipart/form-data")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

            group.MapGet("/", ListAsync)
                .WithSummary("Lists stored files, filtered and paged, newest first.");

            group.MapGet("/{id:guid}", GetAsync)
                .WithSummary("Gets one file's metadata.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("/{id:guid}/content", DownloadAsync)
                .WithSummary("Downloads the file's bytes. Supports Range requests.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("/{id:guid}/url", GetUrlAsync)
                .WithSummary("A direct link to the bytes, when the storage backend mints one.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPatch("/{id:guid}", UpdateAsync)
                .WithSummary("Changes a file's name, category, tags or metadata.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapDelete("/{id:guid}", DeleteAsync)
                .WithSummary("Deletes a file and every link to it: its bytes always, its row unless soft delete is on.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPost("/{id:guid}/attachments", AttachAsync)
                .WithSummary("Attaches the file to an entity, appended within a role or replacing it.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapDelete("/{id:guid}/attachments", DetachAsync)
                .WithSummary("Detaches the file from one entity. The file and its bytes are untouched.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPut("/attachments/order", ReorderAsync)
                .WithSummary("Reorders one entity's files within a role.")
                .ProducesProblem(StatusCodes.Status400BadRequest);

            return group;
        }

        private static async Task<Results<Created<MediaFileResponse>, BadRequest<string>>> UploadAsync(
            HttpRequest request,
            IMediaManager media,
            string basePath,
            CancellationToken ct)
        {
            if (!request.HasFormContentType)
                return TypedResults.BadRequest("Send the file as multipart/form-data, in a field named \"file\".");

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (InvalidDataException ex)
            {
                // A truncated or malformed multipart body. The client got the request wrong, so
                // this is a 400 — without the catch it surfaces as an unhandled 500 and looks
                // like the server's fault.
                return TypedResults.BadRequest($"The multipart body could not be read: {ex.Message}");
            }
            var file = form.Files["file"] ?? (form.Files.Count > 0 ? form.Files[0] : null);

            if (file is null || file.Length == 0)
                return TypedResults.BadRequest("No file was sent. Include one in a field named \"file\".");

            // OpenReadStream is the buffered-or-spooled body ASP.NET Core already has; nothing is
            // read into memory here beyond what the form parser did.
            await using var content = file.OpenReadStream();

            var upload = new MediaUpload
            {
                Content     = content,
                FileName    = file.FileName,
                ContentType = file.ContentType,
                Category    = form["category"].FirstOrDefault(),
                Tags        = ParseTags(form["tags"].FirstOrDefault()),
                UploadedBy  = form["uploadedBy"].FirstOrDefault(),
                Role        = form["role"].FirstOrDefault()
            };

            // Sending an owner uploads and attaches in one request, which is what a form that
            // already knows its product does. Without one the file lands in the library unattached,
            // waiting for a POST to {id}/attachments — the draft-form case.
            var stored = form["ownerEntityName"].FirstOrDefault() is { Length: > 0 } ownerName
                         && form["ownerEntityId"].FirstOrDefault() is { Length: > 0 } ownerId
                ? await media.For(ownerName, ownerId).UploadAsync(upload, ct)
                : await media.UploadAsync(upload, ct);

            return TypedResults.Created($"{basePath}/{stored.Id}", MediaFileResponse.From(stored));
        }

        private static async Task<Ok<MediaPageResponse>> ListAsync(
            IMediaManager media,
            CancellationToken ct,
            string? ownerEntityName = null,
            string? ownerEntityId = null,
            string? role = null,
            bool unattached = false,
            string? category = null,
            string? tag = null,
            string? contentType = null,
            string? uploadedBy = null,
            string? search = null,
            bool includeDeleted = false,
            string? sortBy = null,
            bool descending = true,
            int skip = 0,
            int take = 50)
        {
            var page = await media.ListAsync(new MediaQuery
            {
                OwnerEntityName   = ownerEntityName,
                OwnerEntityId     = ownerEntityId,
                Role              = role,
                Unattached        = unattached,
                Category          = category,
                Tag               = tag,
                ContentTypePrefix = contentType,
                UploadedBy        = uploadedBy,
                Search            = search,
                IncludeDeleted    = includeDeleted,
                SortBy            = ParseSortBy(sortBy),
                Descending        = descending,
                Skip              = Math.Max(0, skip),
                Take              = Math.Clamp(take, 1, MediaQuery.MaxTake)
            }, ct);

            return TypedResults.Ok(new MediaPageResponse(
                [.. page.Items.Select(f => MediaFileResponse.From(f))],
                page.Total,
                page.Skip,
                page.Take));
        }

        private static async Task<Ok<MediaFileResponse>> GetAsync(Guid id, IMediaManager media, CancellationToken ct)
        {
            var file = await media.GetAsync(id, ct);

            // The links come with the single-file read, so a client can say "used by 4 entities"
            // before offering a delete button that would remove it from all of them.
            var attachments = await media.GetAttachmentsAsync(id, ct);

            return TypedResults.Ok(MediaFileResponse.From(file, url: null, attachments));
        }

        private static async Task<IResult> DownloadAsync(
            Guid id,
            IMediaManager media,
            CancellationToken ct,
            bool download = false)
        {
            var content = await media.OpenReadAsync(id, ct);

            // TypedResults.Stream disposes the stream once the response is written, so the handle
            // this opened is released whether the client reads it all or hangs up halfway.
            return TypedResults.Stream(
                content.Stream,
                contentType: content.ContentType,
                // A name only when asked for: with one, ASP.NET Core sets Content-Disposition to
                // "attachment" and the browser downloads rather than displays, which is wrong for
                // an <img> and right for a link.
                fileDownloadName: download ? content.FileName : null,
                lastModified: content.File.UpdatedAt ?? content.File.CreatedAt,
                // The checksum is a hash of the content, which is exactly what a strong ETag is
                // for. It makes conditional requests work, and the video and audio seeking that
                // needs Range requests.
                entityTag: content.File.Checksum is { } checksum
                    ? new Net.Http.Headers.EntityTagHeaderValue($"\"{checksum}\"")
                    : null,
                enableRangeProcessing: true);
        }

        private static async Task<Results<Ok<MediaUrlResponse>, NoContent>> GetUrlAsync(
            Guid id,
            IMediaManager media,
            CancellationToken ct,
            int? expiresInSeconds = null,
            bool download = false)
        {
            var url = await media.GetUrlAsync(
                id,
                new MediaUrlOptions
                {
                    ExpiresIn    = expiresInSeconds is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null,
                    AsAttachment = download
                },
                ct);

            // 204 rather than 404: the file exists, this backend simply has no direct link for it.
            // A 404 would say the file is gone, and send the client looking for the wrong problem.
            return url is null
                ? TypedResults.NoContent()
                : TypedResults.Ok(new MediaUrlResponse(url.AbsoluteUri));
        }

        private static async Task<Ok<MediaFileResponse>> UpdateAsync(
            Guid id,
            DotNetBoost.Media.API.UpdateMediaRequest request,
            IMediaManager media,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var updated = await media.UpdateAsync(id, new MediaUpdate
            {
                FileName = request.FileName,
                Category = request.Category,
                Tags     = request.Tags,
                Metadata = request.Metadata
            }, ct);

            return TypedResults.Ok(MediaFileResponse.From(updated));
        }

        private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, IMediaManager media, CancellationToken ct) =>
            await media.DeleteAsync(id, ct)
                ? TypedResults.NoContent()
                : TypedResults.NotFound();

        private static async Task<Ok<MediaFileResponse>> AttachAsync(
            Guid id,
            DotNetBoost.Media.API.AttachMediaRequest request,
            IMediaManager media,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var accessor = AccessorFor(media, request.OwnerEntityName, request.OwnerEntityId);

            // Replace is what makes a single-valued role single-valued, and it is the caller's to
            // say: no schema can know that "primary" holds one file and "gallery" holds many.
            var attached = request.Replace
                ? await accessor.SetAsync(request.Role ?? MediaRoles.Default, id, ct)
                : await accessor.AddAsync(id, request.Role, ct);

            // 200 rather than 201, and with the links: attaching is idempotent, so a repeat is not a
            // new resource, and the response is what the client needs to redraw either way.
            var attachments = await media.GetAttachmentsAsync(attached.Id, ct);

            return TypedResults.Ok(MediaFileResponse.From(attached, url: null, attachments));
        }

        private static async Task<Results<Ok<MediaFileResponse>, NotFound>> DetachAsync(
            Guid id,
            IMediaManager media,
            CancellationToken ct,
            string? ownerEntityName = null,
            string? ownerEntityId = null,
            string? role = null)
        {
            var accessor = AccessorFor(media, ownerEntityName, ownerEntityId);

            // Left out, role removes the file from every role on this entity — "take this image off
            // the product", however it was being used there.
            if (!await accessor.DetachAsync(id, role, ct))
                return TypedResults.NotFound();

            // The file itself is untouched, so it is still there to return — and its remaining links
            // are what the caller wants to see.
            var file        = await media.GetAsync(id, ct);
            var attachments = await media.GetAttachmentsAsync(id, ct);

            return TypedResults.Ok(MediaFileResponse.From(file, url: null, attachments));
        }

        private static async Task<NoContent> ReorderAsync(
            DotNetBoost.Media.API.ReorderAttachmentsRequest request,
            IMediaManager media,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (request.MediaFileIds is null)
            {
                throw new InvalidMediaException(
                    "Send mediaFileIds: every file attached to that entity in that role, in the order wanted. " +
                    "A partial order has no meaning, since the files left out would have no defined position.");
            }

            await AccessorFor(media, request.OwnerEntityName, request.OwnerEntityId)
                .ReorderAsync(request.Role ?? MediaRoles.Default, request.MediaFileIds, ct);

            return TypedResults.NoContent();
        }

        /// <summary>
        /// The accessor for an owner a request named, as a 400 rather than an unhandled 500 when it
        /// named half of one. <c>IMediaManager.For</c> throws <see cref="ArgumentException"/> for a
        /// missing name or key, which is the right answer for a programming error and the wrong one
        /// for a request body.
        /// </summary>
        private static IMediaAccessor AccessorFor(IMediaManager media, string? entityName, string? entityId)
        {
            if (entityName is not { Length: > 0 } || entityId is not { Length: > 0 })
            {
                throw new InvalidMediaException(
                    "Send both ownerEntityName and ownerEntityId. An entity type without a key, or a key " +
                    "without its type, does not identify anything to attach to.");
            }

            return media.For(entityName, entityId);
        }

        /// <summary>
        /// The <c>sortBy</c> query value. An unrecognised one falls back to the default rather
        /// than failing the request: a listing is a read, and a typo in a query string should not
        /// turn it into an error page.
        /// </summary>
        private static MediaSortBy ParseSortBy(string? value) => value?.Trim().ToUpperInvariant() switch
        {
            "FILENAME" or "NAME"        => MediaSortBy.FileName,
            "SIZE"                      => MediaSortBy.Size,
            // Needs an owner, and the engine says so as a 400 rather than this silently ignoring it:
            // a gallery drawn in the wrong order with a 200 is worse than a refused request.
            "SORTORDER" or "ORDER"      => MediaSortBy.SortOrder,
            _                           => MediaSortBy.CreatedAt
        };

        /// <summary>Splits the comma-separated <c>tags</c> form field. <c>null</c> when it was not sent.</summary>
        private static string[]? ParseTags(string? tags) =>
            string.IsNullOrWhiteSpace(tags)
                ? null
                : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

namespace DotNetBoost.Media.API
{
    /// <summary>A direct link to a file's bytes.</summary>
    /// <param name="Url">The link. Time-limited on the backends that sign one.</param>
    public sealed record MediaUrlResponse(string Url);
}
