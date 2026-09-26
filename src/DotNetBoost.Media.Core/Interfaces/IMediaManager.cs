using DotNetBoost.Media.Core.Models;

namespace DotNetBoost.Media.Core.Interfaces;

/// <summary>
/// The one service application code injects. Everything the library does goes through it:
/// uploading, reading back, linking, listing and deleting. It sits over a metadata store (the
/// table) and one or more storage backends (the bytes), and keeps the two consistent — a row is
/// never written for bytes that failed to store, and bytes are never orphaned by a row that
/// failed to save.
/// <code>
/// public sealed class AvatarService(IMediaManager media)
/// {
///     public async Task&lt;Guid&gt; SetAsync(User user, IFormFile upload, CancellationToken ct)
///     {
///         await using var stream = upload.OpenReadStream();
///
///         var file = await media.For(user).UploadAsync(new MediaUpload
///         {
///             Content     = stream,
///             FileName    = upload.FileName,
///             ContentType = upload.ContentType,
///             Category    = "avatars"
///         }, ct);
///
///         return file.Id;
///     }
/// }
/// </code>
/// </summary>
public interface IMediaManager
{
    /// <summary>
    /// Stores <paramref name="upload"/> and returns its row.
    /// <para>
    /// The bytes go to the default backend (or <see cref="MediaUpload.StorageProvider"/>) first,
    /// then the row is saved. If the row fails to save, the bytes just written are removed again,
    /// so a failed upload leaves nothing behind. The reverse — a row with no bytes — cannot
    /// happen at all.
    /// </para>
    /// <para>
    /// With <c>MediaOptions.DeduplicateByChecksum</c> on, an upload whose bytes already exist
    /// returns the existing row instead of storing a second copy.
    /// </para>
    /// </summary>
    /// <exception cref="MediaTooLargeException">Larger than <c>MediaOptions.MaxFileSizeBytes</c>.</exception>
    /// <exception cref="UnsupportedMediaTypeException">Its type or extension is not allowed.</exception>
    /// <exception cref="MediaContentMismatchException">Its bytes are not what its declared type says.</exception>
    /// <exception cref="MediaStorageException">The backend could not write it.</exception>
    Task<MediaFileInfo> UploadAsync(MediaUpload upload, CancellationToken ct = default);

    /// <summary>The file with this id, or <c>null</c>. Soft-deleted files read as <c>null</c>.</summary>
    Task<MediaFileInfo?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>The file with this id.</summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    Task<MediaFileInfo> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Opens the file's bytes for reading. Dispose the result; it holds an open file handle or
    /// network stream. Stream it straight to the response rather than buffering it.
    /// </summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    /// <exception cref="MediaContentMissingException">The row exists but its bytes do not.</exception>
    /// <exception cref="MediaStorageException">The backend could not be read.</exception>
    Task<MediaContent> OpenReadAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// A URL a client can fetch the bytes from directly, so they never pass through the
    /// application. Time-limited and signed on the backends that can do that (Azure SAS,
    /// S3 pre-signed).
    /// <para>
    /// <c>null</c> when the backend has no direct URL — local disk, typically. Serve those
    /// through <c>OpenReadAsync</c> and your own route; <c>MapMediaEndpoints()</c> in
    /// DotNetBoost.Media.API already does.
    /// </para>
    /// </summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    Task<Uri?> GetUrlAsync(Guid id, MediaUrlOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Changes a file's metadata. Properties left unset on <paramref name="update"/> are left
    /// alone; the bytes and where they live are never touched.
    /// </summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    Task<MediaFileInfo> UpdateAsync(Guid id, MediaUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Deletes a file: its bytes always, its row only when <c>MediaOptions.SoftDelete</c> is off.
    /// Soft delete keeps the row with <see cref="MediaFile.DeletedAt"/> set, as a record that the
    /// file existed — not as a way to get it back.
    /// <para>
    /// The bytes go first. A backend that reports them already gone is treated as success, so a
    /// delete retried after a partial failure completes rather than sticking.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> if a live file was deleted, <c>false</c> if there was none.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>One page of the files matching <paramref name="query"/>, with the total behind it.</summary>
    Task<MediaPage> ListAsync(MediaQuery? query = null, CancellationToken ct = default);

    /// <summary>
    /// The files attached to <paramref name="entity"/>, and the operations that attach more.
    /// The entity's name and key are read the same way DotNetBoost.MultiLingual reads them —
    /// <c>[MediaEntity("…")]</c> or the type name, and <c>[Key]</c>, <c>Id</c> or
    /// <c>{TypeName}Id</c>:
    /// <code>
    /// var images = await media.For(product).ListAsync(ct);
    /// await media.For(product).DeleteAllAsync(ct);   // when the product goes
    /// </code>
    /// </summary>
    /// <exception cref="InvalidOperationException">The type has no discoverable single key, or its key is unset.</exception>
    IMediaAccessor For(object entity);

    /// <summary>
    /// As <see cref="For(object)"/>, for an entity you hold the name and key of rather than the
    /// object — a key read from a route parameter, say.
    /// </summary>
    IMediaAccessor For(string entityName, string entityId);
}

/// <summary>
/// The media attached to one entity instance. Returned by <see cref="IMediaManager.For(object)"/>;
/// every operation on it is already scoped to that entity, so none of them can reach another's
/// files.
/// </summary>
public interface IMediaAccessor
{
    /// <summary>The entity these files belong to.</summary>
    MediaOwner Owner { get; }

    /// <summary>
    /// Uploads a file already attached to this entity — <see cref="MediaUpload.Owner"/> is
    /// overwritten with <see cref="Owner"/>, so it cannot be set to anything else by accident.
    /// </summary>
    Task<MediaFileInfo> UploadAsync(MediaUpload upload, CancellationToken ct = default);

    /// <summary>Every live file attached to this entity, newest first.</summary>
    Task<IReadOnlyList<MediaFileInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>Its live files in <paramref name="category"/>, newest first.</summary>
    Task<IReadOnlyList<MediaFileInfo>> ListAsync(string category, CancellationToken ct = default);

    /// <summary>
    /// Attaches an already-uploaded file to this entity — a file uploaded before the entity
    /// existed, typically, as a draft form does.
    /// </summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    Task<MediaFileInfo> AttachAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Deletes every file attached to this entity. Call it when the entity goes: nothing in the
    /// database refers to a media row, so no cascade will do it for you.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    Task<int> DeleteAllAsync(CancellationToken ct = default);
}
