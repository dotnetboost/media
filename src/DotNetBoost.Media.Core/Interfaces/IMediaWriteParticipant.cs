using DotNetBoost.Media.Core.Models;

namespace DotNetBoost.Media.Core.Interfaces;

/// <summary>
/// Takes part in every write to the media tables, before the store saves it. This is the hook for
/// work that must commit together with the change, or not at all: an audit entry is the reason it
/// exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure policy: an exception aborts the write.</b> The store is not called and the
/// exception reaches the caller unchanged. An upload's bytes are removed again; a delete has not
/// touched the bytes yet, because participants run before it does. This is the opposite of
/// <see cref="IMediaFilesDeletedHandler"/>'s policy, and the reason both hooks exist: the handler
/// runs after the delete and cannot fail it, a participant runs before every write and can.
/// </para>
/// <para>
/// <b>Enlist, never save.</b> A participant adds what it wants committed to the store's own unit
/// of work, and the store saves it with the rows. For Entity Framework Core that is
/// <c>EfCoreMediaWriteScope.Add</c>. Calling <c>SaveChangesAsync</c> yourself, or doing anything
/// with an effect outside that unit of work — sending a message, calling a service — defeats the
/// point: the write can still fail after the participant has run, and only what was enlisted is
/// undone.
/// </para>
/// <para>
/// Only a store that implements <see cref="IEnlistingMediaStore"/> accepts participants.
/// <c>Build()</c> refuses a participant for a store that has not declared support, and a write
/// refuses one at run time if the store cannot enlist. Of the shipped stores, Entity Framework Core
/// does; Dapper and MongoDB do not.
/// </para>
/// <para>
/// <b>One call per operation.</b> Each <see cref="MediaChange"/> is one thing a caller asked for,
/// however many rows it touches: deleting a file attached to two entities is one
/// <see cref="MediaChangeKind.Deleted"/> listing both links, replacing a banner is one
/// <see cref="MediaChangeKind.Replaced"/>, reordering a gallery is one
/// <see cref="MediaChangeKind.Reordered"/>. Several participants may be registered; all run, in
/// registration order, and the first to throw stops the rest. A call that changes nothing —
/// attaching a file already attached, detaching one that is not, a reorder that moves nothing —
/// calls none of them.
/// </para>
/// <para>
/// <b>Calls that are several operations.</b> Uploading straight to an entity is an upload and then
/// an attach; <c>DetachAllAsync</c> is a detach per link; <c>DeleteAllAsync</c> is a detach per
/// link to a file somebody else still uses, then a delete per file nobody else does. Each change
/// commits with its own write and shares the call's <see cref="MediaChange.CorrelationId"/>; a
/// failure part-way leaves the earlier writes, and their changes, committed.
/// </para>
/// </remarks>
public interface IMediaWriteParticipant
{
    /// <summary>
    /// Called once per change, before the store saves it. Throwing aborts the write.
    /// </summary>
    /// <param name="change">The change about to be committed.</param>
    /// <param name="ct">The operation's own cancellation token.</param>
    Task OnWritingAsync(MediaChange change, CancellationToken ct = default);
}

/// <summary>
/// A store that commits what <see cref="IMediaWriteParticipant"/>s enlist together with its own
/// writes. Implement it only when that is true: the engine refuses participants for a store that
/// does not implement it, rather than run them with no atomicity.
/// </summary>
/// <remarks>
/// How a participant enlists is the store's own API, since only the store knows its unit of work.
/// Every write method of <see cref="IMediaStore"/> must commit what was enlisted since its last
/// write, in the same transaction as its rows, and forget it once committed.
/// </remarks>
public interface IEnlistingMediaStore : IMediaStore
{
    /// <summary>
    /// Drops everything participants enlisted that has not been committed, without saving it.
    /// Called after every participated write: when a participant threw, when the store's write
    /// threw, and when the store found nothing to write. Must be safe to call when nothing was
    /// enlisted.
    /// </summary>
    void DiscardEnlisted();

    /// <summary>
    /// Removes every link to <paramref name="file"/> and then the file's row — or, when
    /// <paramref name="softDelete"/> is set, saves the row with the <see cref="MediaFile.DeletedAt"/>
    /// the engine has already set — as <b>one</b> write, so a participant's record of the delete
    /// commits with all of it or none of it.
    /// </summary>
    /// <param name="file">The file being deleted, as the engine read it.</param>
    /// <param name="softDelete">Keep the row as a tombstone rather than removing it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="MediaNotFoundException">The row was deleted underneath the caller.</exception>
    Task DeleteFileAsync(MediaFile file, bool softDelete, CancellationToken ct = default);

    /// <summary>
    /// Removes every link <paramref name="link"/>'s owner has in <paramref name="link"/>'s role and
    /// adds <paramref name="link"/> as <b>one</b> write, so a role is never left empty by a
    /// replacement that failed half-way, and a participant's record of the replacement commits with
    /// both halves or neither.
    /// </summary>
    /// <param name="link">The new link. Its owner and role name the role being replaced.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many links it displaced.</returns>
    /// <exception cref="MediaConflictException">
    /// The role changed between the caller reading it and this writing it.
    /// </exception>
    Task<int> ReplaceAttachmentsAsync(MediaAttachment link, CancellationToken ct = default);
}
