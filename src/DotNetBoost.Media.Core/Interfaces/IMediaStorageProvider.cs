using DotNetBoost.Media.Core.Models;

namespace DotNetBoost.Media.Core.Interfaces;

/// <summary>
/// A place bytes live: local disk, Azure Blob Storage, Amazon S3, or your own. Implement it
/// alongside a <c>Use…()</c> builder method to add a backend — see docs/adding-a-backend.md.
/// <para>
/// Several may be registered at once, and each stored row remembers the <see cref="Name"/> of the
/// one that wrote it. That is what lets an application switch its default backend, or move a
/// single file, without losing track of everything uploaded before.
/// </para>
/// <para>
/// A backend deals in paths and bytes only. It does not know what a media row is, never validates
/// an upload, and is not asked to be transactional — the engine orders the calls so that a
/// failure on either side leaves nothing dangling.
/// </para>
/// </summary>
public interface IMediaStorageProvider
{
    /// <summary>
    /// How this backend is identified in <see cref="MediaFile.StorageProvider"/>:
    /// <c>FileSystem</c>, <c>AzureBlobStorage</c>, <c>AmazonS3</c>. Stable across releases — it is
    /// stored in every row this backend writes, so changing it strands them.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The container, bucket or root directory this backend writes to. Recorded on each row, so a
    /// later read goes to the container the file actually went into rather than the one currently
    /// configured.
    /// </summary>
    string Container { get; }

    /// <summary>
    /// Writes <paramref name="write"/> and reports what landed where. Overwrites any object
    /// already at that path — the engine generates a fresh, id-derived path per upload, so in
    /// practice there never is one.
    /// </summary>
    /// <exception cref="MediaStorageException">The backend refused or failed.</exception>
    Task<StoredObject> SaveAsync(MediaWrite write, CancellationToken ct = default);

    /// <summary>
    /// Opens the object at <paramref name="path"/> in <paramref name="container"/> for reading.
    /// The caller disposes the stream.
    /// </summary>
    /// <exception cref="MediaContentMissingException">There is nothing at that path.</exception>
    /// <exception cref="MediaStorageException">The backend could not be read.</exception>
    Task<Stream> OpenReadAsync(string container, string path, CancellationToken ct = default);

    /// <summary>Removes the object. Returns <c>false</c> when there was nothing there.</summary>
    /// <exception cref="MediaStorageException">The backend refused or failed.</exception>
    Task<bool> DeleteAsync(string container, string path, CancellationToken ct = default);

    /// <summary>Whether an object exists at that path.</summary>
    Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default);

    /// <summary>
    /// A URL a client can fetch the object from directly, or <c>null</c> when this backend has
    /// none — which is the honest answer for local disk, and why
    /// <see cref="IMediaManager.GetUrlAsync"/> is nullable.
    /// </summary>
    Task<Uri?> GetUrlAsync(string container, string path, MediaUrlOptions options, CancellationToken ct = default);
}

/// <summary>
/// The storage backends registered on the builder, and which of them new uploads go to.
/// Resolved as a singleton; the engine uses it to send an upload to the default backend and to
/// send every read to the backend that row names.
/// </summary>
public interface IMediaStorageProviderRegistry
{
    /// <summary>The backend new uploads are written to unless one is named on the upload.</summary>
    IMediaStorageProvider Default { get; }

    /// <summary>Every registered backend, in registration order.</summary>
    IReadOnlyList<IMediaStorageProvider> All { get; }

    /// <summary>The backend with this name, or <c>null</c>.</summary>
    IMediaStorageProvider? Find(string name);

    /// <summary>The backend with this name.</summary>
    /// <exception cref="MediaStorageException">
    /// No backend by that name is registered — typically a row written by a backend that has since
    /// been removed from the application's configuration.
    /// </exception>
    IMediaStorageProvider Get(string name);
}
