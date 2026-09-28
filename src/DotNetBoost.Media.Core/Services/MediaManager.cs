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
    IMediaRoleRegistry roles,
    MediaOptions options,
    TimeProvider timeProvider,
    ILogger<MediaManager> logger) : IMediaManager
{
    private readonly MediaValidator _validator = new(options);

    /// <summary>The engine's clock, for the accessor that writes attachment rows beside it.</summary>
    internal DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>What each entity declares its roles to be. Empty unless the application said so.</summary>
    internal IMediaRoleRegistry Roles => roles;

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
        var category = MediaCategories.Normalize(upload.Category);

        // The id is assigned here, before anything is written, because the storage path is derived
        // from it — which is what keeps the path free of anything the uploader chose. The category
        // is normalized first, so the path and the column agree about which bucket this is.
        var id   = Guid.NewGuid();
        var path = MediaPaths.Validate(keyGenerator.Generate(id, fileName, category));

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
            Category         = category,
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

        // An empty string is how a caller says "no category", and Normalize turns it into the NULL
        // the column wants. The stored bytes do not move: the path was fixed at upload.
        if (update.Category is not null)
            file.Category = MediaCategories.Normalize(update.Category);

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
    public async Task<bool> RemoveAttachmentAsync(
        Guid mediaFileId, Guid attachmentId, CancellationToken ct = default)
    {
        // Checked against the file rather than removed on the id alone. The id identifies exactly one
        // link, so trusting it would work — and would also let a caller that muddled two ids remove a
        // link belonging to a file it never named.
        var links = await store.ListAttachmentsOfFileAsync(mediaFileId, ct).ConfigureAwait(false);

        if (!links.Any(link => link.Id == attachmentId))
            return false;

        return await store.RemoveAttachmentAsync(attachmentId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MediaPage> ListAsync(MediaQuery? query = null, CancellationToken ct = default)
    {
        var resolved = Resolve(query);

        var rows  = await store.QueryAsync(resolved, ct).ConfigureAwait(false);
        var total = await store.CountAsync(resolved, ct).ConfigureAwait(false);

        return new MediaPage([.. rows.Select(MediaFileInfo.From)], total, resolved.Skip, resolved.Take);
    }

    /// <summary>
    /// One page of matching files, <b>without</b> the total behind it.
    /// <para>
    /// The count is a second query — an aggregate, and over the EXISTS subquery as well once an
    /// owner is involved. <see cref="ListAsync"/> owes its caller a total and pays for it; the
    /// accessor's own listings do not use one, and running it anyway would double the cost of
    /// drawing a product's gallery to produce a number nobody reads.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<MediaFileInfo>> ListItemsAsync(MediaQuery query, CancellationToken ct)
    {
        var rows = await store.QueryAsync(Resolve(query), ct).ConfigureAwait(false);
        return [.. rows.Select(MediaFileInfo.From)];
    }

    /// <summary>
    /// Checks a query's filters against each other and normalises what the stores are kept too
    /// dumb to normalise themselves. Shared, so that a listing and the count beside it cannot end
    /// up asking different questions.
    /// </summary>
    private static MediaQuery Resolve(MediaQuery? query)
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

        // Normalized here rather than in each store, which the contract keeps deliberately dumb:
        // the column is lower-cased on write, so a filter that was not would silently match nothing.
        if (resolved.Category is not null)
            resolved = resolved with { Category = MediaCategories.Normalize(resolved.Category) };

        return resolved;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaEntityRole> RolesFor(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return roles.RolesFor(entityName);
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

        var normalized = MediaRoles.Normalize(role);
        Declared(normalized);

        return await ListCoreAsync(normalized, ct).ConfigureAwait(false);
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
        //
        // That is the only reason a link can go missing, and only because ListCoreAsync refuses to
        // truncate. It reads one page of files while the links above are read whole, so a quietly
        // capped listing would drop live links here too — and they would be dropped as though they
        // had been deleted, which is a different and much less obvious lie.
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

        var query = new MediaQuery
        {
            OwnerEntityName = owner.EntityName,
            OwnerEntityId   = owner.EntityId,
            Category        = category,
            Take            = MediaQuery.MaxTake
        };

        var files = await manager.ListItemsAsync(query, ct).ConfigureAwait(false);
        await RefuseToTruncateAsync(files, query, $"in the '{category}' category", ct).ConfigureAwait(false);

        return files;
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo?> GetByRoleAsync(string role, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var normalized = MediaRoles.Normalize(role);

        if (Declared(normalized) is { Multiple: true })
        {
            throw new InvalidMediaException(
                $"'{normalized}' is multi-valued on {owner.EntityName}, so asking for the one file holding it " +
                "has no answer. List the role instead — ListByRoleAsync in code, or the role filter on the " +
                "listing endpoint.");
        }

        var files = await ListCoreAsync(normalized, ct).ConfigureAwait(false);

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
        var declared   = Declared(normalized);

        // Requires a live file before writing a link, so an attach cannot invent a reference to
        // something deleted or never uploaded.
        var file     = await manager.GetAsync(id, ct).ConfigureAwait(false);
        var existing = await store.ListAttachmentsAsync(owner, normalized, ct).ConfigureAwait(false);

        // Already attached here in this role: nothing to do. A double-clicked button and a retried
        // request both land here, and neither should be an error or a second row. Checked before the
        // single-valued rule below, because re-attaching the file that already holds the role is a
        // no-op rather than an attempt at a second one.
        if (existing.Any(a => a.MediaFileId == id))
            return file;

        if (declared is { Multiple: false } && existing.Count > 0)
        {
            throw new InvalidMediaException(
                $"'{normalized}' is single-valued on {owner.EntityName}, and a file already holds it. Replace " +
                "it rather than appending — SetAsync in code, or \"replace\": true through the REST API. The " +
                "file it displaces is detached, not deleted. If the role is meant to hold several, declare it " +
                $"as [MediaRole(\"{normalized}\", Multiple = true)].");
        }

        await AttachAsync(id, normalized, Next(existing), ct).ConfigureAwait(false);
        return file;
    }

    /// <inheritdoc/>
    public async Task<MediaFileInfo> SetAsync(string role, Guid id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var normalized = MediaRoles.Normalize(role);

        if (Declared(normalized) is { Multiple: true })
        {
            throw new InvalidMediaException(
                $"'{normalized}' is multi-valued on {owner.EntityName}. Replacing would detach every other file " +
                "in it, leaving one — append instead (AddAsync in code, or omit \"replace\" through the REST " +
                "API), and use ReorderAsync to change the order.");
        }

        var file       = await manager.GetAsync(id, ct).ConfigureAwait(false);
        var holders    = await store.ListAttachmentsAsync(owner, normalized, ct).ConfigureAwait(false);

        // Already the sole holder: leave the row alone rather than removing and rewriting it, which
        // would move its CreatedAt for no change at all.
        if (holders.Count == 1 && holders[0].MediaFileId == id)
            return file;

        // One statement rather than one per holder. A single-valued role normally has exactly one
        // to displace, but the role filter already exists and removing them one at a time would
        // leave a window in which the role had some of its old holders and none of its new one.
        if (holders.Count > 0)
            await store.RemoveAttachmentsAsync(owner, normalized, ct).ConfigureAwait(false);

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
        Declared(normalized);
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

        var moved = new List<MediaAttachment>(idsInOrder.Count);

        for (var i = 0; i < idsInOrder.Count; i++)
        {
            var attachment = byFile[idsInOrder[i]];

            if (attachment.SortOrder == i)
                continue;

            attachment.SortOrder = i;
            moved.Add(attachment);
        }

        if (moved.Count == 0)
            return;

        // One call for the whole gallery. Saved row by row this would be one round trip per image
        // — and one commit per image, so a failure halfway would leave the gallery in an order the
        // user never chose and cannot describe. The rows that did not move are left out: they are
        // already where they belong, and writing them would only widen what a failure can spoil.
        await store.UpdateAttachmentsAsync(moved, ct).ConfigureAwait(false);
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

    /// <summary>
    /// What this entity declared <paramref name="role"/> to be, or <c>null</c> when the entity
    /// declares no roles at all and is therefore not checked.
    /// </summary>
    /// <exception cref="InvalidMediaException">
    /// The entity declares roles and this is not one of them — a misspelling, caught here rather
    /// than found later as an attachment nothing reads.
    /// </exception>
    private MediaEntityRole? Declared(string role)
    {
        if (!manager.Roles.Declares(owner.EntityName))
            return null;

        return manager.Roles.Find(owner.EntityName, role)
               ?? throw new InvalidMediaException(
                   $"{owner.EntityName} has no media role '{role}'. It declares: " +
                   $"{string.Join(", ", manager.Roles.RolesFor(owner.EntityName).Select(r => r.Multiple ? r.Name + " (many)" : r.Name))}.");
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
        var query = new MediaQuery
        {
            OwnerEntityName = owner.EntityName,
            OwnerEntityId   = owner.EntityId,
            Role            = role,
            SortBy          = MediaSortBy.SortOrder,
            Descending      = false,
            Take            = MediaQuery.MaxTake
        };

        var files = await manager.ListItemsAsync(query, ct).ConfigureAwait(false);
        await RefuseToTruncateAsync(files, query, role is null ? "attached" : $"in the '{role}' role", ct)
            .ConfigureAwait(false);

        return files;
    }

    /// <summary>
    /// Refuses to answer "everything attached here" with a page of it.
    /// <para>
    /// These methods promise every file, and one query cannot return more than
    /// <see cref="MediaQuery.MaxTake"/> of them. Returning the first page and saying nothing would
    /// hide the rest from a caller with no way to know they existed — a gallery quietly missing
    /// its tail, or a screen that offers to delete "all" of something it never saw. So an entity
    /// holding more than a page of files is reported, and the caller is pointed at the paged
    /// listing that can actually express the question.
    /// </para>
    /// <para>
    /// The probe costs nothing in the ordinary case, because it only runs when the page came back
    /// exactly full — which is the one situation where a full page and a truncated one look the
    /// same from here. An entity with five images never reaches it.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidMediaException">More files match than one page can hold.</exception>
    private async Task RefuseToTruncateAsync(
        IReadOnlyList<MediaFileInfo> page,
        MediaQuery query,
        string description,
        CancellationToken ct)
    {
        if (page.Count < MediaQuery.MaxTake)
            return;

        var beyond = await manager
            .ListItemsAsync(query with { Skip = MediaQuery.MaxTake, Take = 1 }, ct)
            .ConfigureAwait(false);

        if (beyond.Count == 0)
            return;

        throw new InvalidMediaException(
            $"{owner.EntityName} {owner.EntityId} has more than {MediaQuery.MaxTake} files {description}, which is " +
            "more than one query returns — so this would have handed back the first page as though it were all of " +
            "them. Page it instead: IMediaManager.ListAsync with OwnerEntityName, OwnerEntityId and Role set, and " +
            "Skip and Take of your own, which reports the total as well.");
    }
}
