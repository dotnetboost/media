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
    /// <param name="OwnerEntityName">The entity it is attached to.</param>
    /// <param name="OwnerEntityId">That entity's key.</param>
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
        string? OwnerEntityName,
        string? OwnerEntityId,
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
        public static MediaFileResponse From(MediaFileInfo file, Uri? url = null)
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
                file.OwnerEntityName,
                file.OwnerEntityId,
                file.UploadedBy,
                file.CreatedAt,
                file.UpdatedAt,
                // AbsoluteUri rather than ToString(): ToString() un-escapes the path and query for
                // display, which would corrupt an escaped path segment and, worse, a SAS or
                // pre-signed token whose signature covers the escaped form.
                url?.AbsoluteUri);
        }
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

        /// <summary>The entity to attach it to. Needs <see cref="OwnerEntityId"/> as well.</summary>
        public string? OwnerEntityName { get; init; }

        /// <summary>That entity's key.</summary>
        public string? OwnerEntityId { get; init; }

        /// <summary>Detaches the file from whatever it was attached to.</summary>
        public bool ClearOwner { get; init; }
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
        /// PATCH  {prefix}/{id}            change name, category, tags, metadata or owner
        /// DELETE {prefix}/{id}            delete
        /// </code>
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
                .WithSummary("Changes a file's name, category, tags, metadata or owner.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapDelete("/{id:guid}", DeleteAsync)
                .WithSummary("Deletes a file: its bytes always, its row unless soft delete is on.")
                .ProducesProblem(StatusCodes.Status404NotFound);

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

            var owner = form["ownerEntityName"].FirstOrDefault() is { Length: > 0 } ownerName
                        && form["ownerEntityId"].FirstOrDefault() is { Length: > 0 } ownerId
                ? new MediaOwner(ownerName, ownerId)
                : (MediaOwner?)null;

            // OpenReadStream is the buffered-or-spooled body ASP.NET Core already has; nothing is
            // read into memory here beyond what the form parser did.
            await using var content = file.OpenReadStream();

            var stored = await media.UploadAsync(new MediaUpload
            {
                Content     = content,
                FileName    = file.FileName,
                ContentType = file.ContentType,
                Category    = form["category"].FirstOrDefault(),
                Tags        = ParseTags(form["tags"].FirstOrDefault()),
                UploadedBy  = form["uploadedBy"].FirstOrDefault(),
                Owner       = owner
            }, ct);

            return TypedResults.Created($"{basePath}/{stored.Id}", MediaFileResponse.From(stored));
        }

        private static async Task<Ok<MediaPageResponse>> ListAsync(
            IMediaManager media,
            CancellationToken ct,
            string? ownerEntityName = null,
            string? ownerEntityId = null,
            string? category = null,
            string? tag = null,
            string? contentType = null,
            string? uploadedBy = null,
            bool includeDeleted = false,
            bool oldestFirst = false,
            int skip = 0,
            int take = 50)
        {
            var page = await media.ListAsync(new MediaQuery
            {
                OwnerEntityName   = ownerEntityName,
                OwnerEntityId     = ownerEntityId,
                Category          = category,
                Tag               = tag,
                ContentTypePrefix = contentType,
                UploadedBy        = uploadedBy,
                IncludeDeleted    = includeDeleted,
                OldestFirst       = oldestFirst,
                Skip              = Math.Max(0, skip),
                Take              = Math.Clamp(take, 1, MediaQuery.MaxTake)
            }, ct);

            return TypedResults.Ok(new MediaPageResponse(
                [.. page.Items.Select(f => MediaFileResponse.From(f))],
                page.Total,
                page.Skip,
                page.Take));
        }

        private static async Task<Ok<MediaFileResponse>> GetAsync(Guid id, IMediaManager media, CancellationToken ct) =>
            TypedResults.Ok(MediaFileResponse.From(await media.GetAsync(id, ct)));

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

            var owner = request is { OwnerEntityName: { Length: > 0 } name, OwnerEntityId: { Length: > 0 } key }
                ? new MediaOwner(name, key)
                : (MediaOwner?)null;

            var updated = await media.UpdateAsync(id, new MediaUpdate
            {
                FileName   = request.FileName,
                Category   = request.Category,
                Tags       = request.Tags,
                Metadata   = request.Metadata,
                Owner      = owner,
                ClearOwner = request.ClearOwner
            }, ct);

            return TypedResults.Ok(MediaFileResponse.From(updated));
        }

        private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, IMediaManager media, CancellationToken ct) =>
            await media.DeleteAsync(id, ct)
                ? TypedResults.NoContent()
                : TypedResults.NotFound();

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
