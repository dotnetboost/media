using DotNetBoost.Media.Core.Models;

namespace DotNetBoost.Media.Core.Interfaces;

/// <summary>
/// Persistence contract for the media table. Implement it to put the rows somewhere this library
/// does not ship a provider for. Implementations are deliberately dumb: validation, content-type
/// sniffing, checksums, deduplication and the ordering of "bytes first, row second" all live in
/// the engine behind <see cref="IMediaManager"/>.
/// <para>
/// Nothing here deletes bytes — including the attachment operations, which only ever remove links.
/// A store removes rows; the engine removes the bytes through the backend that wrote them, and does
/// so first.
/// </para>
/// <para>
/// Two tables, then: the files and the attachments linking them to entities. Their consistency is
/// the engine's business, not the store's — an attachment whose file is gone is a bug in
/// <see cref="IMediaManager"/>, not something a store is asked to prevent.
/// </para>
/// </summary>
public interface IMediaStore
{
    /// <summary>
    /// Stores a new row. The id is already assigned — the engine needs it to derive the storage
    /// path before the bytes are written.
    /// </summary>
    /// <exception cref="MediaConflictException">A row with that id already exists.</exception>
    Task AddAsync(MediaFile file, CancellationToken ct = default);

    /// <summary>
    /// The row with this id, or <c>null</c>.
    /// </summary>
    /// <param name="id">The file's id.</param>
    /// <param name="includeDeleted">
    /// Return a soft-deleted row rather than <c>null</c>. The engine passes <c>true</c> only where
    /// it genuinely means to see one.
    /// </param>
    /// <param name="ct">Cancels the operation.</param>
    Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>
    /// Saves changes to an existing row, matched on its id. Every mutable column is written —
    /// the engine has already applied the patch to the instance it read.
    /// </summary>
    /// <exception cref="MediaNotFoundException">No row has that id.</exception>
    Task UpdateAsync(MediaFile file, CancellationToken ct = default);

    /// <summary>Removes the row outright. Succeeds silently when it does not exist.</summary>
    /// <returns><c>true</c> if a row was removed.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>The rows matching <paramref name="query"/>, in its order, over its page.</summary>
    Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default);

    /// <summary>How many rows match <paramref name="query"/>, ignoring its <c>Skip</c> and <c>Take</c>.</summary>
    Task<int> CountAsync(MediaQuery query, CancellationToken ct = default);

    /// <summary>
    /// A live row whose bytes hash to <paramref name="checksum"/> and are exactly
    /// <paramref name="size"/> long, or <c>null</c>. Backs deduplication; the size is compared as
    /// well as the hash so that a hash collision cannot serve one file's bytes as another's.
    /// </summary>
    Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default);

    /// <summary>
    /// Stores a new attachment row, linking a file to an entity.
    /// </summary>
    /// <exception cref="MediaConflictException">
    /// That file is already attached to that entity in that role. The uniqueness of
    /// (file, owner, role) is the store's to enforce — the engine checks first so that attaching
    /// twice is a no-op rather than an error, and this is the backstop under a race.
    /// </exception>
    Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default);

    /// <summary>Removes one attachment row by its id. Succeeds silently when it does not exist.</summary>
    /// <returns><c>true</c> if a row was removed.</returns>
    Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default);

    /// <summary>
    /// Removes every attachment belonging to <paramref name="owner"/>, or only those in
    /// <paramref name="role"/>. Bytes and file rows are untouched.
    /// </summary>
    /// <returns>How many rows were removed.</returns>
    Task<int> RemoveAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default);

    /// <summary>
    /// Removes every attachment of one file, whoever it belonged to. The cascade behind deleting a
    /// file; nothing else should call it.
    /// </summary>
    /// <returns>How many rows were removed.</returns>
    Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default);

    /// <summary>Saves a changed attachment row, matched on its id.</summary>
    /// <exception cref="MediaNotFoundException">No attachment has that id.</exception>
    Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default);

    /// <summary>
    /// Saves several changed attachment rows at once, each matched on its id. Backs reordering,
    /// where one call per row is the wrong shape twice over: a fifty-image gallery would pay fifty
    /// round trips to save one drag, and — because a store commits per call — a failure partway
    /// through would leave the gallery in an order nobody asked for and no one can name.
    /// <para>
    /// An empty collection does nothing. Rows whose values already match may or may not be
    /// rewritten; the contract is about the state afterwards, not about how many rows were
    /// touched.
    /// </para>
    /// </summary>
    /// <exception cref="MediaNotFoundException">One of them no longer exists.</exception>
    Task UpdateAttachmentsAsync(IReadOnlyCollection<MediaAttachment> attachments, CancellationToken ct = default);

    /// <summary>Every attachment of one file, whoever it belongs to, oldest first.</summary>
    Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(
        Guid mediaFileId, CancellationToken ct = default);

    /// <summary>
    /// Every attachment belonging to <paramref name="owner"/> — or only those in
    /// <paramref name="role"/> — ordered by role, then by <see cref="MediaAttachment.SortOrder"/>.
    /// </summary>
    Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default);

    /// <summary>
    /// Every attachment belonging to <b>any</b> of <paramref name="entityIds"/> under one entity
    /// type — or only those in <paramref name="role"/> — each paired with the file it points at,
    /// ordered by owner, then by role, then by <see cref="MediaAttachment.SortOrder"/>.
    /// <para>
    /// <b>One round trip, whatever the owner count.</b> That is the whole reason this sits beside
    /// <see cref="ListAttachmentsAsync"/> rather than being left to the caller: a listing page
    /// drawing fifty products would otherwise pay a query per row — two, once the files are wanted
    /// as well — and a filter printed on a scan is not a filter. Implementations must issue a
    /// single statement; looping <see cref="ListAttachmentsAsync"/> over the ids is exactly the
    /// cost this method exists to remove.
    /// </para>
    /// <para>
    /// The file is joined here rather than read afterwards for the same reason. Soft-deleted files
    /// are excluded, and so are the links pointing at them — though that pairing cannot normally
    /// arise, because deleting a file removes its attachments outright even under soft delete.
    /// </para>
    /// <para>
    /// An id with nothing attached is simply absent from the result; the engine is the one that
    /// turns that into an empty entry, since only it knows which ids were asked about.
    /// </para>
    /// </summary>
    /// <param name="entityName">The owning entity type, as stored.</param>
    /// <param name="entityIds">The owners' keys, already formatted. Empty returns nothing.</param>
    /// <param name="role">One role, or <c>null</c> for every role.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<IReadOnlyList<MediaOwnerAttachment>> ListAttachmentsForOwnersAsync(
        string entityName,
        IReadOnlyCollection<string> entityIds,
        string? role = null,
        CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="mediaFileIds"/> are still attached to some owner other than
    /// <paramref name="excluding"/>.
    /// <para>
    /// This is the sole-holder test behind <c>IMediaAccessor.DeleteAllAsync</c>: the files it
    /// returns are shared and must only be detached, and the rest are this entity's alone and go
    /// with it. One statement on purpose — deleting an entity with fifty images must not fire fifty
    /// separate probes.
    /// </para>
    /// </summary>
    Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default);
}
