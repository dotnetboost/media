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
    /// <b>Every attachment of the file goes with it</b>, so the file disappears from every entity
    /// that was using it. That is the point of deleting a file rather than detaching it: this is the
    /// "remove it from the library" operation, and
    /// <see cref="IMediaAccessor.DetachAsync(Guid, string?, CancellationToken)"/> is the one that
    /// removes it from a single entity. Attachment rows are removed outright even under soft delete —
    /// they are links with no independent value, and a tombstoned file that still read as attached
    /// to a live entity would be worse than no record at all.
    /// </para>
    /// <para>
    /// The bytes go first. A backend that reports them already gone is treated as success, so a
    /// delete retried after a partial failure completes rather than sticking.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> if a live file was deleted, <c>false</c> if there was none.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Everything this file is attached to, ordered oldest link first — what a library UI needs to
    /// say "used by 4 entities" before offering to delete it.
    /// </summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    Task<IReadOnlyList<MediaAttachmentInfo>> GetAttachmentsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Removes one link by its own id — for a caller holding an attachment from
    /// <see cref="GetAttachmentsAsync"/> rather than an entity to scope by. <b>The file and its
    /// bytes are untouched</b>, and it stays attached to everything else using it.
    /// <para>
    /// <paramref name="mediaFileId"/> is not redundant: it is checked, so an attachment id belonging
    /// to a different file is refused rather than quietly removing a link the caller was not looking
    /// at. Application code that knows the entity instead should use
    /// <see cref="IMediaAccessor.DetachAsync"/>, which cannot reach another entity's links at all.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> if that file had such a link, <c>false</c> if it did not.</returns>
    Task<bool> RemoveAttachmentAsync(Guid mediaFileId, Guid attachmentId, CancellationToken ct = default);

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

    /// <summary>
    /// The roles this entity declared through <c>[MediaRole]</c> or <c>DeclareRoles</c>, and whether
    /// each holds one file or many. Empty when it declared none, which means it is not checked.
    /// <para>
    /// For a UI that has to offer the right roles for whatever it was opened for, rather than
    /// hard-coding one entity's vocabulary.
    /// </para>
    /// </summary>
    IReadOnlyList<Metadata.MediaEntityRole> RolesFor(string entityName);
}

/// <summary>
/// The media attached to one entity instance. Returned by <see cref="IMediaManager.For(object)"/>;
/// every operation on it is already scoped to that entity, so none of them can reach another's
/// files.
/// <para>
/// <b>Roles carry their own cardinality.</b> A product has one main image, many gallery images and
/// one SEO image, and no schema can express all three at once — a uniqueness rule that fits
/// <c>primary</c> would forbid a second gallery shot. So the caller says which it means:
/// <see cref="SetAsync"/> replaces whatever holds the role, <see cref="AddAsync"/> appends to it.
/// <code>
/// await media.For(product).SetAsync("primary", heroId, ct);   // replaces the old hero
/// await media.For(product).AddAsync(shotId, "gallery", ct);   // appended at the end
/// await media.For(product).ReorderAsync("gallery", ids, ct);
/// </code>
/// </para>
/// </summary>
public interface IMediaAccessor
{
    /// <summary>The entity these files belong to.</summary>
    MediaOwner Owner { get; }

    /// <summary>
    /// Uploads a file and attaches it to this entity in one step, under
    /// <see cref="MediaUpload.Role"/> or <see cref="MediaRoles.Default"/>, appended within that
    /// role.
    /// <para>
    /// With <c>MediaOptions.DeduplicateByChecksum</c> on, bytes already in the library are not
    /// stored twice: the existing file is attached to this entity instead, which is exactly the
    /// right answer and was impossible while a file could only record one owner.
    /// </para>
    /// </summary>
    Task<MediaFileInfo> UploadAsync(MediaUpload upload, CancellationToken ct = default);

    /// <summary>
    /// Every live file attached to this entity, in the order the entity put them in: by role, then
    /// by <see cref="MediaAttachment.SortOrder"/>. A file attached under two roles is returned once,
    /// at its lowest role and position.
    /// </summary>
    Task<IReadOnlyList<MediaFileInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>Its live files in one role, by <see cref="MediaAttachment.SortOrder"/>.</summary>
    Task<IReadOnlyList<MediaFileInfo>> ListByRoleAsync(string role, CancellationToken ct = default);

    /// <summary>
    /// Every live file attached to this entity <b>with the role it holds</b>, by role then position —
    /// what a product page needs to draw its hero image, its ordered gallery and its SEO picture from
    /// one read instead of one request per role.
    /// <para>
    /// Per link rather than per file: a file attached as both <c>primary</c> and <c>seo</c> is listed
    /// twice, once under each role, which is the question being asked. <see cref="ListAsync"/> asks
    /// the other one and returns each file once.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<MediaAttachedFile>> ListWithRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// Its live files in one storage category, newest first. Note that a category is a property of
    /// the file — the bucket its bytes live in — while a role is a property of this link; see
    /// <see cref="ListByRoleAsync"/>.
    /// </summary>
    Task<IReadOnlyList<MediaFileInfo>> ListByCategoryAsync(string category, CancellationToken ct = default);

    /// <summary>
    /// The one file in a single-valued role — the product's <c>primary</c> image — or <c>null</c>
    /// when nothing holds it.
    /// </summary>
    /// <exception cref="InvalidMediaException">
    /// Several files hold that role, so it is not single-valued. Read it with
    /// <see cref="ListByRoleAsync"/> instead.
    /// </exception>
    Task<MediaFileInfo?> GetByRoleAsync(string role, CancellationToken ct = default);

    /// <summary>
    /// Attaches an already-uploaded file to this entity — a file uploaded before the entity existed,
    /// as a draft form does, or one already in use elsewhere. Appended at the end of
    /// <paramref name="role"/>.
    /// <para>
    /// Attaching a file that is already attached here in this role changes nothing and returns it,
    /// so a double click and a retried request are both harmless.
    /// </para>
    /// </summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    Task<MediaFileInfo> AddAsync(Guid id, string? role = null, CancellationToken ct = default);

    /// <summary>
    /// Attaches a file as the single holder of <paramref name="role"/>, detaching whatever held it
    /// before. For "make this the main image", which as two calls would leave a window with two
    /// primaries or none.
    /// <para>
    /// The files it displaces are only detached, never deleted — they may well be in use elsewhere.
    /// </para>
    /// </summary>
    /// <exception cref="MediaNotFoundException">No live file has that id.</exception>
    Task<MediaFileInfo> SetAsync(string role, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Renumbers a role so its files sit in the order given — what a drag-and-drop gallery saves.
    /// </summary>
    /// <param name="role">The role to reorder.</param>
    /// <param name="idsInOrder">
    /// Every file currently attached in that role, in the order wanted. All of them, because a
    /// partial list has no unambiguous meaning: the positions of the ones left out would be
    /// anyone's guess.
    /// </param>
    /// <param name="ct">Cancels the operation.</param>
    /// <exception cref="InvalidMediaException">
    /// The ids are not exactly the files attached in that role — one is missing, unknown, or listed
    /// twice.
    /// </exception>
    Task ReorderAsync(string role, IReadOnlyList<Guid> idsInOrder, CancellationToken ct = default);

    /// <summary>
    /// Removes the link between this entity and a file. <b>The file and its bytes are untouched</b>
    /// — it stays in the library, and stays attached to every other entity using it. This is what a
    /// product's "remove this image" does; <see cref="IMediaManager.DeleteAsync"/> is what deleting
    /// the file itself does.
    /// </summary>
    /// <param name="id">The file to unlink.</param>
    /// <param name="role">
    /// Only this role's link, or — left <c>null</c> — the file's links in every role on this entity.
    /// </param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns><c>true</c> if a link was removed, <c>false</c> if there was none.</returns>
    Task<bool> DetachAsync(Guid id, string? role = null, CancellationToken ct = default);

    /// <summary>
    /// Removes every link from this entity, deleting nothing. Leaves the files in the library,
    /// unattached, ready to be attached again.
    /// </summary>
    /// <returns>How many links were removed.</returns>
    Task<int> DetachAllAsync(CancellationToken ct = default);

    /// <summary>
    /// For when the entity itself goes: deletes the files <b>this entity was the only holder of</b>,
    /// and merely detaches the ones other entities still reference. Call it when the entity is
    /// deleted — nothing in the database refers to a media row, so no cascade will do it for you.
    /// <para>
    /// A shared file surviving is the point. Deleting one product must not pull an image out of
    /// every other product using it.
    /// </para>
    /// <para>
    /// There is a window between finding a file unshared and deleting its bytes in which another
    /// request could attach it, leaving that new link pointing at bytes that are gone. Closing it
    /// needs a lock this library does not take anywhere — the Dapper store borrows the application's
    /// own connection and may already be inside its transaction. The window is small, the outcome is
    /// reported honestly by <see cref="IMediaManager.OpenReadAsync"/> as
    /// <see cref="MediaContentMissingException"/>, and the trade is the same one the engine already
    /// makes writing bytes before rows.
    /// </para>
    /// </summary>
    /// <returns>
    /// How many files were deleted. Files that were only detached because something else still uses
    /// them are not counted — nothing was destroyed.
    /// </returns>
    Task<int> DeleteAllAsync(CancellationToken ct = default);
}
