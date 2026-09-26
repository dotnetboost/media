using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// The engine behind <see cref="IMediaManager"/>. Everything that makes the library correct
/// rather than merely functional lives here: validation, the order of the two writes, the cleanup
/// when the second one fails, and sending each read to the backend its own row names.
/// </summary>
internal sealed partial class MediaManager(
    IMediaStore store,
    IMediaStorageProviderRegistry storage,
    IMediaKeyGenerator keyGenerator,
    IEntityKeyAccessor keyAccessor,
    MediaOptions options,
    TimeProvider timeProvider,
    ILogger<MediaManager> logger) : IMediaManager
{
    private readonly MediaValidator _validator = new(options);

    // A field, not the primary-constructor parameter: the net8.0 [LoggerMessage] generator (from
    // the 8.0 logging packages) only finds a logger held in a field, and writes invalid code for a
    // captured parameter. The parameter is used here, in the initializer, and nowhere else.
    private readonly ILogger<MediaManager> _logger = logger;

    /// <inheritdoc/>
    public async Task<MediaFileInfo> UploadAsync(MediaUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(upload.Content);

        if (!upload.Content.CanRead)
            throw new InvalidMediaException("The upload's content stream cannot be read.");

        var fileName            = _validator.ValidateName(upload.FileName);
        var (tags, metadata)    = MediaValidator.ValidateAnnotations(upload.Tags, upload.Metadata);
        var (contentType, body) = await _validator
            .ResolveContentTypeAsync(upload.Content, fileName, upload.ContentType, ct)
            .ConfigureAwait(false);

        var provider = upload.StorageProvider is { } named ? storage.Get(named) : storage.Default;

        // The id is assigned here, before anything is written, because the storage path is derived
        // from it — which is what keeps the path free of anything the uploader chose.
        var id   = Guid.NewGuid();
        var path = MediaPaths.Validate(keyGenerator.Generate(id, fileName, upload.Category));

        var measured = new MeasuringStream(body, options.MaxFileSizeBytes, options.ComputeChecksum);

        StoredObject stored;
        try
        {
            stored = await provider
                .SaveAsync(new MediaWrite(measured, path, contentType, options.ComputeChecksum), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (Unwrap(ex) is { } known)
        {
            // A size overrun surfaces from inside the backend's own read, so it arrives wrapped in
            // whatever that backend throws. The caller asked to upload a file and wants to hear
            // that it was too large, not that an HTTP PUT failed.
            await TryRemoveAsync(provider, provider.Container, path, id: null, ct).ConfigureAwait(false);
            throw known;
        }

        // The backend reports what it wrote; the measuring stream is the fallback for a backend
        // that does not count (and the only source of the hash, since the backend never sees one).
        var size     = stored.Size > 0 ? stored.Size : measured.BytesRead;
        var checksum = stored.Checksum ?? measured.Checksum;

        if (options is { DeduplicateByChecksum: true, ComputeChecksum: true } && checksum is not null)
        {
            var existing = await store.FindByChecksumAsync(checksum, size, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                // Identical bytes are already stored. Drop the copy just written — not the
                // existing object, which is the one every row points at.
                await TryRemoveAsync(provider, stored.Container, stored.Path, id: null, ct).ConfigureAwait(false);
                LogDeduplicated(fileName, existing.Id);
                return MediaFileInfo.From(existing);
            }
        }

        var file = new MediaFile
        {
            Id               = id,
            FileName         = fileName,
            ContentType      = contentType,
            Size             = size,
            StorageProvider  = provider.Name,
            StorageContainer = stored.Container,
            StoragePath      = stored.Path,
            Checksum         = checksum,
            OwnerEntityName  = upload.Owner?.EntityName,
            OwnerEntityId    = upload.Owner?.EntityId,
            Category         = upload.Category,
            Tags             = tags,
            Metadata         = metadata,
            UploadedBy       = upload.UploadedBy,
            CreatedAt        = timeProvider.GetUtcNow().UtcDateTime
        };

        try
        {
            await store.AddAsync(file, ct).ConfigureAwait(false);
        }
        catch
        {
            // The bytes are written but the row is not, so nothing can ever find them. Remove
            // them rather than leave an object no part of the system knows about. Best-effort:
            // the original failure is what the caller needs to hear about.
            await TryRemoveAsync(provider, stored.Container, stored.Path, id, ct).ConfigureAwait(false);
            throw;
        }

        LogStored(id, size, provider.Name, stored.Path);
        return MediaFileInfo.From(file);
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo?> FindAsync(Guid id, CancellationToken ct = default)
    {
        var file = await store.FindAsync(id, includeDeleted: false, ct).ConfigureAwait(false);
        return file is null ? null : MediaFileInfo.From(file);
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo> GetAsync(Guid id, CancellationToken ct = default) =>
        await FindAsync(id, ct).ConfigureAwait(false) ?? throw new MediaNotFoundException(id);

    /// <inheritdoc/>
    public async Task<MediaContent> OpenReadAsync(Guid id, CancellationToken ct = default)
    {
        var file     = await RequireAsync(id, ct).ConfigureAwait(false);
        var provider = storage.Get(file.StorageProvider);
        var path     = MediaPaths.Validate(file.StoragePath);

        var stream = await provider.OpenReadAsync(file.StorageContainer, path, ct).ConfigureAwait(false);
        return new MediaContent(stream, MediaFileInfo.From(file));
    }

    /// <inheritdoc/>
    public async Task<Uri?> GetUrlAsync(Guid id, MediaUrlOptions? urlOptions = null, CancellationToken ct = default)
    {
        var file     = await RequireAsync(id, ct).ConfigureAwait(false);
        var provider = storage.Get(file.StorageProvider);
        var path     = MediaPaths.Validate(file.StoragePath);

        var resolved = urlOptions ?? new MediaUrlOptions();
        resolved = resolved with
        {
            ExpiresIn        = resolved.ExpiresIn ?? options.DefaultUrlExpiry,
            DownloadFileName = resolved.DownloadFileName ?? file.FileName
        };

        return await provider.GetUrlAsync(file.StorageContainer, path, resolved, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo> UpdateAsync(Guid id, MediaUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var file = await RequireAsync(id, ct).ConfigureAwait(false);

        if (update.FileName is not null)
            file.FileName = _validator.ValidateName(update.FileName);

        if (update.Category is not null)
            file.Category = update.Category;

        if (update.Tags is not null)
            file.Tags = MediaValidator.ValidateAnnotations(update.Tags, null).Tags;

        if (update.Metadata is not null)
            file.Metadata = MediaValidator.ValidateAnnotations(null, update.Metadata).Metadata;

        if (update.Owner is { } owner)
        {
            file.OwnerEntityName = owner.EntityName;
            file.OwnerEntityId   = owner.EntityId;
        }
        else if (update.ClearOwner)
        {
            file.OwnerEntityName = null;
            file.OwnerEntityId   = null;
        }

        file.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await store.UpdateAsync(file, ct).ConfigureAwait(false);
        return MediaFileInfo.From(file);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var file = await store.FindAsync(id, includeDeleted: false, ct).ConfigureAwait(false);
        if (file is null)
            return false;

        await DeleteFileAsync(file, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<MediaPage> ListAsync(MediaQuery? query = null, CancellationToken ct = default)
    {
        var resolved = query ?? new MediaQuery();

        if (resolved.OwnerEntityId is not null && resolved.OwnerEntityName is null)
        {
            throw new InvalidMediaException(
                "MediaQuery.OwnerEntityId needs OwnerEntityName as well: on its own it would match files " +
                "belonging to unrelated entity types that happen to share the key.");
        }

        var rows  = await store.QueryAsync(resolved, ct).ConfigureAwait(false);
        var total = await store.CountAsync(resolved, ct).ConfigureAwait(false);

        return new MediaPage([.. rows.Select(MediaFileInfo.From)], total, resolved.Skip, resolved.Take);
    }

    /// <inheritdoc/>
    public IMediaAccessor For(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var name = MediaEntityNames.Of(entity);
        var key  = keyAccessor.GetKey(entity)
                   ?? throw new InvalidOperationException(
                       $"The {name} being attached to has no key yet. Save it first — media rows record the " +
                       "key, and one that is still unassigned would attach the file to nothing.");

        return For(name, key);
    }

    /// <inheritdoc/>
    public IMediaAccessor For(string entityName, string entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        return new MediaAccessor(this, store, new MediaOwner(entityName, entityId));
    }

    /// <summary>Removes one file's bytes and then its row, honouring <c>SoftDelete</c>.</summary>
    internal async Task DeleteFileAsync(MediaFile file, CancellationToken ct)
    {
        // Bytes first. The other order leaves an object nothing points at if the second step
        // fails; this order leaves a row whose bytes are gone, which OpenReadAsync reports
        // honestly and a retry of the delete completes.
        var provider = storage.Find(file.StorageProvider);

        if (provider is not null)
        {
            await TryRemoveAsync(provider, file.StorageContainer, MediaPaths.Validate(file.StoragePath), file.Id, ct)
                .ConfigureAwait(false);
        }
        else
        {
            LogUnknownBackendOnDelete(file.Id, file.StorageProvider);
        }

        if (options.SoftDelete)
        {
            file.DeletedAt = timeProvider.GetUtcNow().UtcDateTime;
            await store.UpdateAsync(file, ct).ConfigureAwait(false);
        }
        else
        {
            await store.DeleteAsync(file.Id, ct).ConfigureAwait(false);
        }
    }

    private async Task<MediaFile> RequireAsync(Guid id, CancellationToken ct) =>
        await store.FindAsync(id, includeDeleted: false, ct).ConfigureAwait(false)
        ?? throw new MediaNotFoundException(id);

    /// <summary>
    /// Removes an object, swallowing whatever the backend says about it. Used on the cleanup paths
    /// only, where something has already gone wrong and the caller is owed the original failure —
    /// the leftover object is logged so it can be collected later.
    /// </summary>
    private async Task TryRemoveAsync(
        IMediaStorageProvider provider,
        string container,
        string path,
        Guid? id,
        CancellationToken ct)
    {
        try
        {
            // CancellationToken.None, deliberately: the usual reason to be here is a cancelled
            // request, and cleaning up is exactly what must still happen when the caller has gone.
            await provider.DeleteAsync(container, path, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup must not replace the failure that brought us here.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogOrphanedContent(ex, provider.Name, container, path, id);
        }

        _ = ct;
    }

    /// <summary>
    /// The library's own exception inside whatever a backend wrapped it in. A backend reading the
    /// upload stream will surface <see cref="MediaTooLargeException"/> through its own error type,
    /// and the caller should see the cause rather than the wrapper.
    /// </summary>
    private static MediaException? Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MediaException known)
                return known;
        }

        return null;
    }

    [LoggerMessage(EventId = 7000, Level = LogLevel.Debug,
        Message = "Stored media {MediaId} ({Size} bytes) at {Provider}:{Path}.")]
    private partial void LogStored(Guid mediaId, long size, string provider, string path);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Debug,
        Message = "Upload '{FileName}' matched existing media {MediaId} by checksum; reusing it instead of " +
                  "storing a second copy.")]
    private partial void LogDeduplicated(string fileName, Guid mediaId);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning,
        Message = "Could not remove orphaned media content at {Provider}:{Container}/{Path} (media {MediaId}). " +
                  "Nothing references it and it can be deleted.")]
    private partial void LogOrphanedContent(Exception exception, string provider, string container, string path, Guid? mediaId);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning,
        Message = "Media {MediaId} names storage backend '{Provider}', which is not registered; its row is " +
                  "being deleted but its content is not. Register the backend to delete the content too.")]
    private partial void LogUnknownBackendOnDelete(Guid mediaId, string provider);
}

/// <summary>
/// <see cref="IMediaAccessor"/> over one entity. Every operation carries the owner, so none of
/// them can reach another entity's files however they are called.
/// </summary>
internal sealed class MediaAccessor(MediaManager manager, IMediaStore store, MediaOwner owner) : IMediaAccessor
{
    /// <inheritdoc/>
    public MediaOwner Owner => owner;

    /// <inheritdoc/>
    public Task<MediaFileInfo> UploadAsync(MediaUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        return manager.UploadAsync(upload with { Owner = owner }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MediaFileInfo>> ListAsync(CancellationToken ct = default) =>
        ListCoreAsync(category: null, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<MediaFileInfo>> ListAsync(string category, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        return ListCoreAsync(category, ct);
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo> AttachAsync(Guid id, CancellationToken ct = default) =>
        await manager.UpdateAsync(id, new MediaUpdate { Owner = owner }, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        var deleted = 0;

        // Paged rather than loaded whole: an entity can accumulate more files than anyone expects,
        // and a delete-everything call is exactly when that shows up. Skip stays at zero because
        // each page is removed from the result set before the next query.
        while (true)
        {
            var page = await store.QueryAsync(
                new MediaQuery
                {
                    OwnerEntityName = owner.EntityName,
                    OwnerEntityId   = owner.EntityId,
                    Take            = 100
                }, ct).ConfigureAwait(false);

            if (page.Count == 0)
                break;

            foreach (var file in page)
            {
                await manager.DeleteFileAsync(file, ct).ConfigureAwait(false);
                deleted++;
            }

            // Soft delete leaves the rows in place but out of the (IncludeDeleted: false) query,
            // so the next page is genuinely the next batch either way. A page short of the limit
            // was the last one.
            if (page.Count < 100)
                break;
        }

        return deleted;
    }

    private async Task<IReadOnlyList<MediaFileInfo>> ListCoreAsync(string? category, CancellationToken ct)
    {
        var rows = await store.QueryAsync(
            new MediaQuery
            {
                OwnerEntityName = owner.EntityName,
                OwnerEntityId   = owner.EntityId,
                Category        = category,
                Take            = MediaQuery.MaxTake
            }, ct).ConfigureAwait(false);

        return [.. rows.Select(MediaFileInfo.From)];
    }
}
