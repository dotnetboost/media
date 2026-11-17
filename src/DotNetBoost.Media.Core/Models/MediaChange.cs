namespace DotNetBoost.Media.Core.Models;

/// <summary>What a <see cref="MediaChange"/> is about to do.</summary>
public enum MediaChangeKind
{
    /// <summary>A new file's row is about to be recorded. Its bytes are already stored.</summary>
    Uploaded,

    /// <summary>A file's name, category, tags or metadata are about to change.</summary>
    Updated,

    /// <summary>A file is about to be linked to an entity, in a role that does not already hold another.</summary>
    Attached,

    /// <summary>
    /// A file's link to an entity is about to be removed by a detach. The file itself survives.
    /// A delete does not report detaches: the links it removes are listed on the
    /// <see cref="Deleted"/> change.
    /// </summary>
    Detached,

    /// <summary>
    /// A file is about to take a single-valued role from the file or files that held it. The
    /// displaced files are listed in <see cref="MediaChange.Displaced"/>; they are detached, not
    /// deleted.
    /// </summary>
    Replaced,

    /// <summary>
    /// The files in one of an entity's roles are about to be put in a new order. One change for the
    /// whole role, carrying the order before and after.
    /// </summary>
    Reordered,

    /// <summary>
    /// A file is about to be deleted, with every link it still has. The links are listed in
    /// <see cref="MediaChange.Attachments"/>.
    /// </summary>
    Deleted
}

/// <summary>
/// One operation an <see cref="Interfaces.IMediaWriteParticipant"/> is told about, before the store
/// commits it.
/// <para>
/// Each change is one thing a caller asked for — an upload, an edit, an attach, a detach, a
/// replacement, a reorder, a delete — however many rows it touches. A delete of a file attached to
/// two products is one <see cref="MediaChangeKind.Deleted"/> listing both links; replacing a banner
/// is one <see cref="MediaChangeKind.Replaced"/> naming the file it displaced. Calls that are
/// several operations — uploading straight to an entity is an upload and then an attach — share
/// one <see cref="CorrelationId"/> across their changes.
/// </para>
/// </summary>
public sealed record MediaChange
{
    /// <summary>What is about to happen.</summary>
    public required MediaChangeKind Kind { get; init; }

    /// <summary>
    /// The file the change is about. Set for every kind but <see cref="MediaChangeKind.Reordered"/>,
    /// which is about a role and lists its files in <see cref="Order"/>.
    /// </summary>
    public Guid? MediaFileId { get; init; }

    /// <summary>
    /// The file as it will be once the change commits: the new row for
    /// <see cref="MediaChangeKind.Uploaded"/>, the edited one for
    /// <see cref="MediaChangeKind.Updated"/>, the file taking the role for
    /// <see cref="MediaChangeKind.Replaced"/>, and the row as it was for
    /// <see cref="MediaChangeKind.Deleted"/> — the row is still there when a participant runs, so its
    /// name can be recorded before it goes.
    /// <para>
    /// <c>null</c> for <see cref="MediaChangeKind.Reordered"/>, and for a detached link whose file row
    /// no longer exists, which deleting a file through this library never leaves behind.
    /// </para>
    /// </summary>
    public MediaFileInfo? File { get; init; }

    /// <summary>
    /// The file before the edit, for <see cref="MediaChangeKind.Updated"/>; <c>null</c> for every
    /// other kind.
    /// </summary>
    public MediaFileInfo? Previous { get; init; }

    /// <summary>
    /// The entity whose link changes, for <see cref="MediaChangeKind.Attached"/>,
    /// <see cref="MediaChangeKind.Detached"/>, <see cref="MediaChangeKind.Replaced"/> and
    /// <see cref="MediaChangeKind.Reordered"/>; <c>null</c> for the changes that are about the file
    /// alone.
    /// <para>
    /// The same <c>(entityName, entityId)</c> address every DotNetBoost library uses (Entity
    /// Addressing v1), so another library's record of that entity can be found by it.
    /// </para>
    /// </summary>
    public MediaOwner? Owner { get; init; }

    /// <summary>The link's role, normalized, whenever <see cref="Owner"/> is set.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// The link's position within its role once the change commits: set for
    /// <see cref="MediaChangeKind.Attached"/> and <see cref="MediaChangeKind.Replaced"/>.
    /// </summary>
    public int? SortOrder { get; init; }

    /// <summary>The link's position before it was removed: set for <see cref="MediaChangeKind.Detached"/>.</summary>
    public int? PreviousSortOrder { get; init; }

    /// <summary>
    /// For <see cref="MediaChangeKind.Replaced"/>: the files that held the role and are about to be
    /// detached from it, in their order. Normally one — a single-valued role holds one file — but
    /// every holder is listed. Empty for every other kind.
    /// </summary>
    public IReadOnlyList<MediaAttachedFile> Displaced { get; init; } = [];

    /// <summary>
    /// For <see cref="MediaChangeKind.Reordered"/>: every file in the role, in the order it is about
    /// to have. Empty for every other kind.
    /// </summary>
    public IReadOnlyList<MediaFileInfo> Order { get; init; } = [];

    /// <summary>
    /// For <see cref="MediaChangeKind.Reordered"/>: the same files, in the order they had. Empty for
    /// every other kind.
    /// </summary>
    public IReadOnlyList<MediaFileInfo> PreviousOrder { get; init; } = [];

    /// <summary>
    /// For <see cref="MediaChangeKind.Deleted"/>: every link the file still has, which goes with it.
    /// Empty for a file attached to nothing, and for every other kind.
    /// </summary>
    public IReadOnlyList<MediaAttachmentInfo> Attachments { get; init; } = [];

    /// <summary>
    /// For <see cref="MediaChangeKind.Deleted"/>: whether the row is kept as a tombstone (soft
    /// delete, the default) rather than removed. The bytes go either way.
    /// </summary>
    public bool SoftDeleted { get; init; }

    /// <summary>
    /// Shared by every change one call makes. The ambient <c>Activity</c>'s id when there is one,
    /// so the changes join the surrounding trace; otherwise a fresh id.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>When the engine made the change, by its own clock. Always UTC.</summary>
    public required DateTime OccurredAt { get; init; }
}
