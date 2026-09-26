using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using System.Collections.Concurrent;

namespace DotNetBoost.Media.TestSupport;

/// <summary>
/// An <see cref="IMediaStore"/> in a dictionary. Runs the same contract suite as the real stores
/// (see ProviderTests), so a test built on it is testing against something known to behave like a
/// database — which is the only reason a fake store is worth having.
/// </summary>
public sealed class InMemoryMediaStore : IMediaStore
{
    private readonly ConcurrentDictionary<Guid, MediaFile> _files = new();

    /// <summary>Every row, live and deleted, for a test that wants to look directly.</summary>
    public IReadOnlyCollection<MediaFile> All => [.. _files.Values];

    /// <summary>How many rows exist, including soft-deleted ones.</summary>
    public int Count => _files.Count;

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
        ArgumentNullException.ThrowIfNull(query);

        var matched = Filter(query);

        matched = query.OldestFirst
            ? matched.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            : matched.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id);

        return Task.FromResult<IReadOnlyList<MediaFile>>(
            [.. matched.Skip(query.Skip).Take(query.Take).Select(Clone)]);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(MediaQuery query, CancellationToken ct = default)
    {
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

    private IEnumerable<MediaFile> Filter(MediaQuery query)
    {
        var files = _files.Values.AsEnumerable();

        if (!query.IncludeDeleted)
            files = files.Where(f => f.DeletedAt is null);

        if (query.OwnerEntityName is { } ownerName)
            files = files.Where(f => f.OwnerEntityName == ownerName);

        if (query.OwnerEntityId is { } ownerId)
            files = files.Where(f => f.OwnerEntityId == ownerId);

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
        OwnerEntityName  = file.OwnerEntityName,
        OwnerEntityId    = file.OwnerEntityId,
        Category         = file.Category,
        Tags             = file.Tags,
        Metadata         = file.Metadata,
        UploadedBy       = file.UploadedBy,
        CreatedAt        = file.CreatedAt,
        UpdatedAt        = file.UpdatedAt,
        DeletedAt        = file.DeletedAt
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
        MediaOwner? owner = null) =>
        new()
        {
            Content     = new MemoryStream(content, writable: false),
            FileName    = fileName,
            ContentType = contentType,
            Category    = category,
            Tags        = tags,
            Owner       = owner
        };
}
