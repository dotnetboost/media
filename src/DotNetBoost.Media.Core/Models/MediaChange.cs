namespace DotNetBoost.Media.Core.Models;

/// <summary>What a <see cref="MediaChange"/> is about to do.</summary>
public enum MediaChangeKind
{
    /// <summary>A new file's row is about to be recorded. Its bytes are already stored.</summary>
    Uploaded,

    /// <summary>A file's name, category, tags or metadata are about to change.</summary>
    Updated,

    /// <summary>A file is about to be linked to an entity, in a role.</summary>
    Attached,

    /// <summary>
    /// A file's link to an entity is about to be removed. The file itself survives unless a
    /// <see cref="Deleted"/> change for it follows in the same write.
    /// </summary>
    Detached,

    /// <summary>A file is about to move to a different position within an entity's role.</summary>
    Reordered,

    /// <summary>
    /// A file is about to be deleted. Every link it still has is reported first, as a
    /// <see cref="Detached"/> change each, in the same write.
    /// </summary>
    Deleted
}

/// <summary>
/// One change an <see cref="Interfaces.IMediaWriteParticipant"/> is told about, before the store
/// commits it.
/// <para>
/// One operation can be several changes. Deleting a file attached to two products is a
/// <see cref="MediaChangeKind.Detached"/> for each product followed by a
/// <see cref="MediaChangeKind.Deleted"/>; replacing a single-valued role is a detach of the old
/// holder and an attach of the new one. Every change of one call shares its
/// <see cref="CorrelationId"/>.
/// </para>
/// </summary>
public sealed record MediaChange
{
    /// <summary>What is about to happen.</summary>
    public required MediaChangeKind Kind { get; init; }

    /// <summary>The file the change is about. Always set.</summary>
    public required Guid MediaFileId { get; init; }

    /// <summary>
    /// The file as it will be once the change commits: the new row for
    /// <see cref="MediaChangeKind.Uploaded"/>, the edited one for
    /// <see cref="MediaChangeKind.Updated"/>, and the row as it was for
    /// <see cref="MediaChangeKind.Deleted"/> — the row is still there when a participant runs, so
    /// its name can be recorded before it goes.
    /// <para>
    /// <c>null</c> only for a link whose file row no longer exists, which deleting a file through
    /// this library never leaves behind.
    /// </para>
    /// </summary>
    public MediaFileInfo? File { get; init; }

    /// <summary>
    /// The file before the edit, for <see cref="MediaChangeKind.Updated"/>; <c>null</c> for every
    /// other kind.
    /// </summary>
    public MediaFileInfo? Previous { get; init; }

    /// <summary>
    /// The entity the link belongs to, for <see cref="MediaChangeKind.Attached"/>,
    /// <see cref="MediaChangeKind.Detached"/> and <see cref="MediaChangeKind.Reordered"/>;
    /// <c>null</c> for the changes that are about the file alone.
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
    /// <see cref="MediaChangeKind.Attached"/> and <see cref="MediaChangeKind.Reordered"/>.
    /// </summary>
    public int? SortOrder { get; init; }

    /// <summary>
    /// The link's position before the change: set for <see cref="MediaChangeKind.Detached"/> and
    /// <see cref="MediaChangeKind.Reordered"/>.
    /// </summary>
    public int? PreviousSortOrder { get; init; }

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
