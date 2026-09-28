using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using System.Collections.Concurrent;

namespace DotNetBoost.Media.TestSupport;

/// <summary>
/// An <see cref="IMediaStore"/> in a dictionary — both of its tables: the files, and the
/// attachments linking them to entities. Runs the same contract suite as the real stores (see
/// ProviderTests), so a test built on it is testing against something known to behave like a
/// database, which is the only reason a fake store is worth having.
/// </summary>
public sealed class InMemoryMediaStore : IMediaStore
{
    private readonly ConcurrentDictionary<Guid, MediaFile> _files = new();
    private readonly ConcurrentDictionary<Guid, MediaAttachment> _attachments = new();

    /// <summary>Every row, live and deleted, for a test that wants to look directly.</summary>
    public IReadOnlyCollection<MediaFile> All => [.. _files.Values];

    /// <summary>How many rows exist, including soft-deleted ones.</summary>
    public int Count => _files.Count;

    /// <summary>Every attachment row, for a test asserting on the links themselves.</summary>
    public IReadOnlyCollection<MediaAttachment> Attachments => [.. _attachments.Values];

    private int _queries;
    private int _counts;

    /// <summary>
    /// How many listing queries have been issued, and how many counts beside them. A real store
    /// pays a round trip for each, so a test can assert that a read costs what it should —
    /// whether an extra aggregate is being run for a total nobody reads, say.
    /// </summary>
    public int Queries => Volatile.Read(ref _queries);

    /// <inheritdoc cref="Queries"/>
    public int Counts => Volatile.Read(ref _counts);

    private int _attachmentWrites;

    /// <summary>
    /// How many attachment writes have been issued, counting a batch as the one command it is.
    /// A test can then assert that saving a reorder costs one round trip rather than one per file.
    /// </summary>
    public int AttachmentWrites => Volatile.Read(ref _attachmentWrites);

    /// <summary>Zeroes the counters, so a test can measure one call rather than its setup.</summary>
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _queries, 0);
        Interlocked.Exchange(ref _counts, 0);
        Interlocked.Exchange(ref _attachmentWrites, 0);
    }

    /// <inheritdoc/>
    public Task AddAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!_files.TryAdd(file.Id, Clone(file)))
            throw new MediaConflictException($"Could not record media file '{file.Id}'. A row with that id already exists.");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default)
    {
        if (!_files.TryGetValue(id, out var file) || (!includeDeleted && file.DeletedAt is not null))
            return Task.FromResult<MediaFile?>(null);

        // A copy, as a real store hands back a fresh instance per read. Returning the stored
        // object would let a caller mutate the "database" without saving, and hide the bugs this
        // double exists to catch.
        return Task.FromResult<MediaFile?>(Clone(file));
    }

    /// <inheritdoc/>
    public Task UpdateAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!_files.ContainsKey(file.Id))
            throw new MediaNotFoundException(file.Id);

        _files[file.Id] = Clone(file);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_files.TryRemove(id, out _));

    /// <inheritdoc/>
    public Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _queries);

        ArgumentNullException.ThrowIfNull(query);

        var matched = Filter(query);

        // Sorting by the entity's own order means sorting on the attachment, not the file, so the
        // key comes from the links the filter matched. A file attached under several roles sorts by
        // its first, which is what keeps a row that appears once from having two positions.
        if (query.SortBy == MediaSortBy.SortOrder)
        {
            var keys = OrderingKeys(query);

            matched = query.Descending
                ? matched.OrderByDescending(f => keys.GetValueOrDefault(f.Id)).ThenByDescending(f => f.Id)
                : matched.OrderBy(f => keys.GetValueOrDefault(f.Id)).ThenBy(f => f.Id);

            return Task.FromResult<IReadOnlyList<MediaFile>>(
                [.. matched.Skip(query.Skip).Take(query.Take).Select(Clone)]);
        }

        matched = (query.SortBy, query.Descending) switch
        {
            (MediaSortBy.FileName, true)  => matched.OrderByDescending(f => f.FileName, StringComparer.Ordinal).ThenByDescending(f => f.Id),
            (MediaSortBy.FileName, false) => matched.OrderBy(f => f.FileName, StringComparer.Ordinal).ThenBy(f => f.Id),
            (MediaSortBy.Size, true)      => matched.OrderByDescending(f => f.Size).ThenByDescending(f => f.Id),
            (MediaSortBy.Size, false)     => matched.OrderBy(f => f.Size).ThenBy(f => f.Id),
            (_, false)                    => matched.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            _                             => matched.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
        };

        return Task.FromResult<IReadOnlyList<MediaFile>>(
            [.. matched.Skip(query.Skip).Take(query.Take).Select(Clone)]);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(MediaQuery query, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _counts);
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(Filter(query).Count());
    }

    /// <inheritdoc/>
    public Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default)
    {
        var match = _files.Values
            .Where(f => f.Checksum == checksum && f.Size == size && f.DeletedAt is null)
            .OrderBy(f => f.CreatedAt)
            .FirstOrDefault();

        return Task.FromResult(match is null ? null : Clone(match));
    }

    /// <inheritdoc/>
    public Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        // The unique index the real stores carry, enforced here so a test can rely on it: one file
        // may hold several roles on one entity, but not the same role twice.
        var duplicate = _attachments.Values.Any(a =>
            a.MediaFileId == attachment.MediaFileId
            && string.Equals(a.OwnerEntityName, attachment.OwnerEntityName, StringComparison.Ordinal)
            && string.Equals(a.OwnerEntityId, attachment.OwnerEntityId, StringComparison.Ordinal)
            && string.Equals(a.Role, attachment.Role, StringComparison.Ordinal));

        if (duplicate)
        {
            throw new MediaConflictException(
                $"Media file '{attachment.MediaFileId}' is already attached to {attachment.OwnerEntityName} " +
                $"{attachment.OwnerEntityId} as '{attachment.Role}'.");
        }

        if (!_attachments.TryAdd(attachment.Id, Clone(attachment)))
            throw new MediaConflictException($"Could not record attachment '{attachment.Id}'. A row with that id already exists.");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default) =>
        Task.FromResult(_attachments.TryRemove(attachmentId, out _));

    /// <inheritdoc/>
    public Task<int> RemoveAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default)
    {
        var doomed = _attachments.Values.Where(a => Owned(a, owner) && RoleMatches(a, role)).ToArray();

        var removed = 0;
        foreach (var attachment in doomed)
        {
            if (_attachments.TryRemove(attachment.Id, out _))
                removed++;
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default)
    {
        var doomed = _attachments.Values.Where(a => a.MediaFileId == mediaFileId).ToArray();

        var removed = 0;
        foreach (var attachment in doomed)
        {
            if (_attachments.TryRemove(attachment.Id, out _))
                removed++;
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _attachmentWrites);
        Apply(attachment);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateAttachmentsAsync(
        IReadOnlyCollection<MediaAttachment> attachments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count == 0)
            return Task.CompletedTask;

        // Counted as one, because that is what it is against a real store: one command, one
        // transaction. Counting it per row would hide the very thing a test asserting on this
        // wants to see.
        Interlocked.Increment(ref _attachmentWrites);

        // Checked before any of them is applied, so a batch naming a row that is gone leaves the
        // rest alone — the all-or-nothing a real store gets from its transaction.
        foreach (var attachment in attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);

            if (!_attachments.ContainsKey(attachment.Id))
                throw new MediaNotFoundException(attachment.Id);
        }

        foreach (var attachment in attachments)
            Apply(attachment);

        return Task.CompletedTask;
    }

    private void Apply(MediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (!_attachments.ContainsKey(attachment.Id))
            throw new MediaNotFoundException(attachment.Id);

        _attachments[attachment.Id] = Clone(attachment);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(
        Guid mediaFileId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaAttachment>>(
            [.. _attachments.Values
                .Where(a => a.MediaFileId == mediaFileId)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .Select(Clone)]);

    /// <inheritdoc/>
    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaAttachment>>(
            [.. _attachments.Values
                .Where(a => Owned(a, owner) && RoleMatches(a, role))
                .OrderBy(a => a.Role, StringComparer.Ordinal).ThenBy(a => a.SortOrder).ThenBy(a => a.Id)
                .Select(Clone)]);

    /// <inheritdoc/>
    public Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaFileIds);

        var wanted = mediaFileIds.ToHashSet();

        return Task.FromResult<IReadOnlySet<Guid>>(
            _attachments.Values
                .Where(a => wanted.Contains(a.MediaFileId) && !Owned(a, excluding))
                .Select(a => a.MediaFileId)
                .ToHashSet());
    }

    private static bool Owned(MediaAttachment attachment, MediaOwner owner) =>
        string.Equals(attachment.OwnerEntityName, owner.EntityName, StringComparison.Ordinal)
        && string.Equals(attachment.OwnerEntityId, owner.EntityId, StringComparison.Ordinal);

    private static bool RoleMatches(MediaAttachment attachment, string? role) =>
        role is null || string.Equals(attachment.Role, role, StringComparison.Ordinal);

    /// <summary>
    /// The attachments an owner-filtered query matches, which is where both the join and the
    /// entity's own ordering come from.
    /// </summary>
    private IEnumerable<MediaAttachment> Matching(MediaQuery query)
    {
        var owner = new MediaOwner(query.OwnerEntityName!, query.OwnerEntityId ?? string.Empty);

        return _attachments.Values.Where(a =>
            string.Equals(a.OwnerEntityName, owner.EntityName, StringComparison.Ordinal)
            && (query.OwnerEntityId is null || string.Equals(a.OwnerEntityId, owner.EntityId, StringComparison.Ordinal))
            && RoleMatches(a, query.Role));
    }

    /// <summary>
    /// Each matched file's position in the owner's order: its lowest role, then its lowest position.
    /// A file the entity attached twice therefore still has a single place in the results.
    /// <para>
    /// The two minima are taken independently rather than as the pair belonging to the lowest role,
    /// because that is what the relational stores can express — the pair would need a correlated
    /// subquery ordered and limited inside an ORDER BY. The two agree wherever it matters: narrowed
    /// to one role, which is the ordering anyone asks for, there is only one row per file.
    /// </para>
    /// </summary>
    private Dictionary<Guid, (string Role, int SortOrder)> OrderingKeys(MediaQuery query) =>
        Matching(query)
            .GroupBy(a => a.MediaFileId)
            .ToDictionary(
                g => g.Key,
                g => (Role: g.Select(a => a.Role).Min(StringComparer.Ordinal)!, SortOrder: g.Min(a => a.SortOrder)));

    private IEnumerable<MediaFile> Filter(MediaQuery query)
    {
        var files = _files.Values.AsEnumerable();

        if (!query.IncludeDeleted)
            files = files.Where(f => f.DeletedAt is null);

        // The join, and only when it is asked for. An unfiltered listing never touches the
        // attachment table, so a file linked to nine entities appears once rather than nine times,
        // and a file linked to none appears at all.
        if (query.OwnerEntityName is not null)
        {
            var attached = Matching(query).Select(a => a.MediaFileId).ToHashSet();
            files = files.Where(f => attached.Contains(f.Id));
        }

        if (query.Unattached)
        {
            var linked = _attachments.Values.Select(a => a.MediaFileId).ToHashSet();
            files = files.Where(f => !linked.Contains(f.Id));
        }

        if (query.Category is { } category)
            files = files.Where(f => f.Category == category);

        if (query.UploadedBy is { } uploadedBy)
            files = files.Where(f => f.UploadedBy == uploadedBy);

        if (query.Checksum is { } checksum)
            files = files.Where(f => f.Checksum == checksum);

        if (query.CreatedAfter is { } after)
            files = files.Where(f => f.CreatedAt >= after);

        if (query.CreatedBefore is { } before)
            files = files.Where(f => f.CreatedAt < before);

        if (query.ContentTypePrefix is { } prefix)
            files = files.Where(f => f.ContentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (query.Search is { Length: > 0 } search)
            files = files.Where(f => f.FileName.Contains(search, StringComparison.OrdinalIgnoreCase));

        if (query.Tag is { } tag)
        {
            var fragment = MediaTags.Fragment(tag);
            files = files.Where(f => f.Tags is not null && f.Tags.Contains(fragment, StringComparison.Ordinal));
        }

        return files;
    }

    private static MediaFile Clone(MediaFile file) => new()
    {
        Id               = file.Id,
        FileName         = file.FileName,
        ContentType      = file.ContentType,
        Size             = file.Size,
        StorageProvider  = file.StorageProvider,
        StorageContainer = file.StorageContainer,
        StoragePath      = file.StoragePath,
        Checksum         = file.Checksum,
        Category         = file.Category,
        Tags             = file.Tags,
        Metadata         = file.Metadata,
        UploadedBy       = file.UploadedBy,
        CreatedAt        = file.CreatedAt,
        UpdatedAt        = file.UpdatedAt,
        DeletedAt        = file.DeletedAt
    };

    private static MediaAttachment Clone(MediaAttachment attachment) => new()
    {
        Id              = attachment.Id,
        MediaFileId     = attachment.MediaFileId,
        OwnerEntityName = attachment.OwnerEntityName,
        OwnerEntityId   = attachment.OwnerEntityId,
        Role            = attachment.Role,
        SortOrder       = attachment.SortOrder,
        CreatedAt       = attachment.CreatedAt
    };
}

/// <summary>
/// An <see cref="IMediaStorageProvider"/> holding bytes in a dictionary. Enough of a backend to
/// exercise the engine's ordering and cleanup without touching a disk, and it can be told to fail
/// on demand — which is how the "row written, bytes lost" and "bytes written, row lost" paths get
/// tested at all.
/// </summary>
public sealed class InMemoryStorageProvider : IMediaStorageProvider
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>Creates a backend under a name of its own, so several can be registered at once.</summary>
    public InMemoryStorageProvider(string name = "InMemory", string container = "test")
    {
        Name      = name;
        Container = container;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string Container { get; }

    /// <summary>Makes the next and every subsequent <see cref="SaveAsync"/> throw.</summary>
    public bool FailWrites { get; set; }

    /// <summary>Makes the next and every subsequent <see cref="DeleteAsync"/> throw.</summary>
    public bool FailDeletes { get; set; }

    /// <summary>A link to hand out from <see cref="GetUrlAsync"/>, or <c>null</c> for a backend that has none.</summary>
    public Uri? Url { get; set; }

    /// <summary>The paths currently holding bytes.</summary>
    public IReadOnlyCollection<string> Paths => [.. _objects.Keys];

    /// <summary>The bytes at a path, for asserting on what was actually written.</summary>
    public byte[]? Read(string path) => _objects.GetValueOrDefault(path);

    /// <summary>Removes the bytes at a path behind the library's back, as a lost object would be.</summary>
    public void Corrupt(string path) => _objects.TryRemove(path, out _);

    /// <inheritdoc/>
    public async Task<StoredObject> SaveAsync(MediaWrite write, CancellationToken ct = default)
    {
        if (FailWrites)
            throw new MediaStorageException($"The '{Name}' backend was told to fail writes.", Name);

        var path = MediaPaths.Validate(write.Path);

        using var buffer = new MemoryStream();
        await write.Content.CopyToAsync(buffer, ct).ConfigureAwait(false);

        var bytes = buffer.ToArray();
        _objects[path] = bytes;

        return new StoredObject(Container, path, bytes.LongLength, Checksum: null);
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(string container, string path, CancellationToken ct = default)
    {
        var safe = MediaPaths.Validate(path);

        if (!_objects.TryGetValue(safe, out var bytes))
            throw new MediaContentMissingException(Name, safe);

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string container, string path, CancellationToken ct = default)
    {
        if (FailDeletes)
            throw new MediaStorageException($"The '{Name}' backend was told to fail deletes.", Name);

        return Task.FromResult(_objects.TryRemove(MediaPaths.Validate(path), out _));
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default) =>
        Task.FromResult(_objects.ContainsKey(MediaPaths.Validate(path)));

    /// <inheritdoc/>
    public Task<Uri?> GetUrlAsync(string container, string path, MediaUrlOptions options, CancellationToken ct = default) =>
        Task.FromResult(Url);
}

/// <summary>Builds media the tests upload, so each one says only what it actually cares about.</summary>
public static class TestMedia
{
    /// <summary>The eight bytes that make a file a PNG, followed by whatever else is asked for.</summary>
    public static byte[] Png(int totalBytes = 64)
    {
        var bytes = new byte[Math.Max(8, totalBytes)];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    /// <summary>The two bytes that make a file a Windows executable — what a renamed .png looks like.</summary>
    public static byte[] Executable(int totalBytes = 64)
    {
        var bytes = new byte[Math.Max(2, totalBytes)];
        bytes[0] = 0x4D; // M
        bytes[1] = 0x5A; // Z
        return bytes;
    }

    /// <summary>Bytes no signature recognises, so the content-type check has nothing to object to.</summary>
    public static byte[] Unrecognised(int totalBytes = 64) =>
        [.. Enumerable.Range(0, totalBytes).Select(i => (byte)(i % 251 + 3))];

    /// <summary>An upload over the given bytes.</summary>
    public static MediaUpload Upload(
        byte[] content,
        string fileName = "photo.png",
        string? contentType = "image/png",
        string? category = null,
        IReadOnlyList<string>? tags = null,
        string? role = null) =>
        new()
        {
            Content     = new MemoryStream(content, writable: false),
            FileName    = fileName,
            ContentType = contentType,
            Category    = category,
            Tags        = tags,
            Role        = role
        };
}
