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

    /// <summary>The engine's clock, for the accessor that writes attachment rows beside it.</summary>
    internal DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

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
    public async Task<IReadOnlyList<MediaAttachmentInfo>> GetAttachmentsAsync(
        Guid id, CancellationToken ct = default)
    {
        // Requires the file rather than returning an empty list for an unknown id: "nothing uses
        // this" and "there is no such file" are different answers, and a caller about to offer a
        // delete button needs to tell them apart.
        await RequireAsync(id, ct).ConfigureAwait(false);

        var rows = await store.ListAttachmentsOfFileAsync(id, ct).ConfigureAwait(false);
        return [.. rows.Select(MediaAttachmentInfo.From)];
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

        var hasOwner = resolved.OwnerEntityName is not null;

        if (resolved.Role is not null && !hasOwner)
        {
            throw new InvalidMediaException(
                "MediaQuery.Role needs an owner as well: a role belongs to a link between a file and one " +
                "entity, so on its own it would match every entity's files in that role at once.");
        }

        if (resolved.SortBy == MediaSortBy.SortOrder && !hasOwner)
        {
            throw new InvalidMediaException(
                "MediaQuery.SortBy = SortOrder needs an owner as well: it sorts on the attachment rather " +
                "than on the file, and a file attached to several entities has a position in each of them.");
        }

        if (resolved.Unattached && hasOwner)
        {
            throw new InvalidMediaException(
                "MediaQuery.Unattached and an owner filter ask opposite questions: the first wants files " +
                "nothing is using, the second wants the files one entity is using.");
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

        // The links go whether the row is tombstoned or removed. They carry no information of their
        // own — a soft-deleted file that still read as attached to a live product would be a lie the
        // "record that the file existed" is not worth telling.
        await store.RemoveAttachmentsOfFileAsync(file.Id, ct).ConfigureAwait(false);

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
/// <para>
/// Its job is the attachment table. It creates and removes links; only
/// <see cref="DeleteAllAsync"/> destroys anything, and then only the files nothing else is using.
/// </para>
/// </summary>
internal sealed class MediaAccessor(MediaManager manager, IMediaStore store, MediaOwner owner) : IMediaAccessor
{
    /// <inheritdoc/>
    public MediaOwner Owner => owner;

    /// <inheritdoc/>
    public async Task<MediaFileInfo> UploadAsync(MediaUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        // Two steps, and deliberately in this order: the bytes exist before anything claims them,
        // so a failed attach leaves an unattached file — an ordinary row the library already
        // understands — rather than a link to nothing.
        //
        // This is also what makes deduplication behave. The upload may return a file that already
        // existed, and attaching it is then exactly right. While a file carried its own single
        // owner, the deduplicated upload had nowhere to put this entity and silently dropped it.
        var file = await manager.UploadAsync(upload, ct).ConfigureAwait(false);

        return await AddAsync(file.Id, upload.Role, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaFileInfo>> ListAsync(CancellationToken ct = default) =>
        await ListCoreAsync(role: null, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaFileInfo>> ListByRoleAsync(string role, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return await ListCoreAsync(MediaRoles.Normalize(role), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaAttachedFile>> ListWithRolesAsync(CancellationToken ct = default)
    {
        // Two reads, not one per file: the links in the entity's own order, and the live files they
        // point at. Pairing them here is what keeps an application from asking per role or per file.
        var links = await store.ListAttachmentsAsync(owner, role: null, ct).ConfigureAwait(false);
        var files = await ListCoreAsync(role: null, ct).ConfigureAwait(false);

        var byId = files.ToDictionary(f => f.Id);

        // A link whose file is missing from the live listing is a link to a soft-deleted file. It is
        // skipped rather than returned with a tombstone, because every other read here means "live".
        return
        [
            .. links
                .Where(link => byId.ContainsKey(link.MediaFileId))
                .Select(link => new MediaAttachedFile(byId[link.MediaFileId], link.Role, link.SortOrder))
        ];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaFileInfo>> ListByCategoryAsync(
        string category, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var page = await manager.ListAsync(
            new MediaQuery
            {
                OwnerEntityName = owner.EntityName,
                OwnerEntityId   = owner.EntityId,
                Category        = category,
                Take            = MediaQuery.MaxTake
            }, ct).ConfigureAwait(false);

        return page.Items;
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo?> GetByRoleAsync(string role, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var normalized = MediaRoles.Normalize(role);
        var files      = await ListCoreAsync(normalized, ct).ConfigureAwait(false);

        if (files.Count > 1)
        {
            throw new InvalidMediaException(
                $"{files.Count} files hold the '{normalized}' role on {owner.EntityName} {owner.EntityId}, so it " +
                "is not a single-valued role. Read it with ListByRoleAsync, and attach with SetAsync rather " +
                "than AddAsync if only one was meant to hold it.");
        }

        return files.Count == 0 ? null : files[0];
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo> AddAsync(Guid id, string? role = null, CancellationToken ct = default)
    {
        var normalized = MediaRoles.Normalize(role);

        // Requires a live file before writing a link, so an attach cannot invent a reference to
        // something deleted or never uploaded.
        var file     = await manager.GetAsync(id, ct).ConfigureAwait(false);
        var existing = await store.ListAttachmentsAsync(owner, normalized, ct).ConfigureAwait(false);

        // Already attached here in this role: nothing to do. A double-clicked button and a retried
        // request both land here, and neither should be an error or a second row.
        if (existing.Any(a => a.MediaFileId == id))
            return file;

        await AttachAsync(id, normalized, Next(existing), ct).ConfigureAwait(false);
        return file;
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo> SetAsync(string role, Guid id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var normalized = MediaRoles.Normalize(role);
        var file       = await manager.GetAsync(id, ct).ConfigureAwait(false);
        var holders    = await store.ListAttachmentsAsync(owner, normalized, ct).ConfigureAwait(false);

        // Already the sole holder: leave the row alone rather than removing and rewriting it, which
        // would move its CreatedAt for no change at all.
        if (holders.Count == 1 && holders[0].MediaFileId == id)
            return file;

        foreach (var holder in holders)
            await store.RemoveAttachmentAsync(holder.Id, ct).ConfigureAwait(false);

        // Position zero: a role that holds one file has no ordering to preserve, and leaving the
        // displaced file's index behind would start the next gallery at 4.
        await AttachAsync(id, normalized, sortOrder: 0, ct).ConfigureAwait(false);
        return file;
    }

    /// <inheritdoc/>
    public async Task ReorderAsync(string role, IReadOnlyList<Guid> idsInOrder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(idsInOrder);

        var normalized = MediaRoles.Normalize(role);
        var attached   = await store.ListAttachmentsAsync(owner, normalized, ct).ConfigureAwait(false);
        var byFile     = attached.ToDictionary(a => a.MediaFileId);

        if (idsInOrder.Count != byFile.Count || idsInOrder.Distinct().Count() != idsInOrder.Count
            || idsInOrder.Any(id => !byFile.ContainsKey(id)))
        {
            throw new InvalidMediaException(
                $"Reordering the '{normalized}' role on {owner.EntityName} {owner.EntityId} needs every file " +
                $"attached in it exactly once; {byFile.Count} are attached and {idsInOrder.Count} were given. " +
                "A partial order has no meaning — the files left out would have no defined position.");
        }

        for (var i = 0; i < idsInOrder.Count; i++)
        {
            var attachment = byFile[idsInOrder[i]];

            if (attachment.SortOrder == i)
                continue;

            attachment.SortOrder = i;
            await store.UpdateAttachmentAsync(attachment, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DetachAsync(Guid id, string? role = null, CancellationToken ct = default)
    {
        // Deliberately no file lookup. Detaching is about the link, and a file that has since been
        // deleted leaves links this should still be able to clear.
        var normalized = role is null ? null : MediaRoles.Normalize(role);
        var attached   = await store.ListAttachmentsAsync(owner, normalized, ct).ConfigureAwait(false);

        var removed = false;

        foreach (var attachment in attached.Where(a => a.MediaFileId == id))
            removed |= await store.RemoveAttachmentAsync(attachment.Id, ct).ConfigureAwait(false);

        return removed;
    }

    /// <inheritdoc/>
    public async Task<int> DetachAllAsync(CancellationToken ct = default) =>
        await store.RemoveAttachmentsAsync(owner, role: null, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        var attached = await store.ListAttachmentsAsync(owner, role: null, ct).ConfigureAwait(false);

        // Distinct: one file attached as both this entity's primary and its seo image is one file to
        // decide about, not two.
        var fileIds = attached.Select(a => a.MediaFileId).Distinct().ToArray();

        if (fileIds.Length == 0)
            return 0;

        // One question for the whole set. Asked before anything is removed, because the answer is
        // "does somebody OTHER than this owner hold it" and removing this owner's links first would
        // not change it — but reading it first keeps that independence obvious.
        var shared = await store.FindSharedAsync(fileIds, owner, ct).ConfigureAwait(false);

        // This entity's links go either way: the ones on shared files are the detach, and the ones
        // on sole-held files would be cascaded by the delete anyway.
        await store.RemoveAttachmentsAsync(owner, role: null, ct).ConfigureAwait(false);

        var deleted = 0;

        foreach (var fileId in fileIds)
        {
            if (shared.Contains(fileId))
                continue;

            // Re-read rather than trust the list: another request may have deleted the file between
            // the two, and DeleteFileAsync needs the row to find the bytes.
            var file = await store.FindAsync(fileId, includeDeleted: false, ct).ConfigureAwait(false);
            if (file is null)
                continue;

            await manager.DeleteFileAsync(file, ct).ConfigureAwait(false);
            deleted++;
        }

        return deleted;
    }

    /// <summary>Writes one link, with the id and timestamp the engine assigns.</summary>
    private async Task AttachAsync(Guid fileId, string role, int sortOrder, CancellationToken ct) =>
        await store.AddAttachmentAsync(
            new MediaAttachment
            {
                Id              = Guid.NewGuid(),
                MediaFileId     = fileId,
                OwnerEntityName = owner.EntityName,
                OwnerEntityId   = owner.EntityId,
                Role            = role,
                SortOrder       = sortOrder,
                CreatedAt       = manager.Now
            }, ct).ConfigureAwait(false);

    /// <summary>
    /// The next free position in a role. Computed from the rows rather than kept as a counter, so it
    /// stays right after a detach from the middle of a gallery.
    /// </summary>
    private static int Next(IReadOnlyList<MediaAttachment> existing) =>
        existing.Count == 0 ? 0 : existing.Max(a => a.SortOrder) + 1;

    private async Task<IReadOnlyList<MediaFileInfo>> ListCoreAsync(string? role, CancellationToken ct)
    {
        var page = await manager.ListAsync(
            new MediaQuery
            {
                OwnerEntityName = owner.EntityName,
                OwnerEntityId   = owner.EntityId,
                Role            = role,
                SortBy          = MediaSortBy.SortOrder,
                Descending      = false,
                Take            = MediaQuery.MaxTake
            }, ct).ConfigureAwait(false);

        return page.Items;
    }
}
