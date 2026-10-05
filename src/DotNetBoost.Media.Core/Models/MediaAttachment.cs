namespace DotNetBoost.Media.Core.Models;

/// <summary>
/// One link between a stored file and an entity that uses it.
/// <para>
/// This table is what lets a single upload serve many entities at once. A <see cref="MediaFile"/>
/// is the bytes and the facts about them — inherently one thing; an attachment is what some entity
/// uses those bytes <i>for</i> — inherently many. Keeping them apart is what makes "the same image
/// is product 7's main photo and product 9's third gallery shot" expressible at all: the role and
/// the order differ per link, so neither can live on the file.
/// </para>
/// <para>
/// A file with no attachments is perfectly ordinary — an upload waiting for the form that will
/// claim it, or one whose last owner let it go. Nothing prunes it implicitly, and it still appears
/// in every unfiltered listing.
/// </para>
/// <para>
/// Not sealed, for the same reason <see cref="MediaFile"/> is not: EF Core's lazy-loading proxies
/// refuse a model containing a sealed entity type.
/// </para>
/// </summary>
public class MediaAttachment
{
    /// <summary>
    /// The link's own identity, so one attachment can be addressed and removed by itself without
    /// naming the file, the entity and the role to identify it.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>The file being attached. Not a foreign key the database enforces — the media table
    /// stands alone, and the engine cascades on delete.</summary>
    public Guid MediaFileId { get; set; }

    /// <summary>
    /// Logical name of the entity type, e.g. <c>Product</c> — the same value the owner column on
    /// the file used to hold, read the same way by <c>IMediaManager.For(entity)</c>.
    /// </summary>
    public string OwnerEntityName { get; set; } = string.Empty;

    /// <summary>
    /// The owning entity's key, as a string so one table serves <c>int</c>, <c>long</c>,
    /// <c>Guid</c> and <c>string</c> keys. Formatted invariantly by
    /// <see cref="Metadata.EntityKey.ToKeyString"/>.
    /// </summary>
    public string OwnerEntityId { get; set; } = string.Empty;

    /// <summary>
    /// What the file does for this entity: <c>primary</c>, <c>gallery</c>, <c>seo</c>, or whatever
    /// the application means by it. Lower-cased on write.
    /// <para>
    /// <b>Never null</b>, defaulting to <see cref="MediaRoles.Default"/>. A nullable column inside
    /// the uniqueness constraint below would behave differently on each engine this library
    /// supports — SQL Server treats two NULLs as equal and rejects the second row, PostgreSQL and
    /// SQLite treat them as distinct and allow unlimited duplicates. The same DDL would enforce
    /// the constraint on one of three dialects. A sentinel avoids the question.
    /// </para>
    /// </summary>
    public string Role { get; set; } = MediaRoles.Default;

    /// <summary>
    /// Display order <b>within</b> <see cref="Role"/> for this owner, ascending. Meaningless for a
    /// single-valued role like <c>primary</c>; it is what orders a gallery.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>When the link was made, from the engine's <see cref="TimeProvider"/>. Always UTC.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// The rules for writing a role down — as <see cref="MediaTags"/> and
/// <see cref="MediaCategories"/> are for theirs.
/// <para>
/// Deliberately no vocabulary. An application's roles are its own — <c>primary</c>, <c>gallery</c>,
/// <c>seo</c>, <c>hero</c>, <c>datasheet</c> — and a library shipping constants for two of them
/// would suggest those two are somehow its concept, when the only opinion it has is about the rules
/// below. Put your own names in your own class, next to the entity that uses them, and declare them
/// with <c>[MediaRole]</c>.
/// </para>
/// </summary>
public static class MediaRoles
{
    /// <summary>
    /// The role an attachment takes when the caller names none.
    /// <para>
    /// A sentinel rather than <c>null</c>, which is what lets the uniqueness constraint cover the
    /// role column: a nullable column inside it rejects the second NULL on SQL Server and allows
    /// unlimited NULLs on PostgreSQL and SQLite.
    /// </para>
    /// </summary>
    public const string Default = "default";

    /// <summary>The longest a role may be, and the width of the column holding it.</summary>
    public const int MaxRoleLength = 64;

    /// <summary>
    /// The stored form of <paramref name="role"/>: trimmed and lower-cased, so <c>Primary</c> and
    /// <c>primary</c> are one role rather than two. <c>null</c> and whitespace become
    /// <see cref="Default"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Longer than <see cref="MaxRoleLength"/> characters.</exception>
    public static string Normalize(string? role)
    {
        var trimmed = role?.Trim();

        if (string.IsNullOrEmpty(trimmed))
            return Default;

        if (trimmed.Length > MaxRoleLength)
            throw new ArgumentException($"Role '{trimmed}' is longer than {MaxRoleLength} characters.", nameof(role));

        return trimmed.ToLowerInvariant();
    }
}

/// <summary>
/// One of an entity's files together with what that entity uses it for — what a product page needs
/// to draw a hero image, an ordered gallery and an SEO picture from one read.
/// <para>
/// Note that this is per <i>link</i>, not per file: a file an entity attached as both its
/// <c>primary</c> and its <c>seo</c> image appears twice, once under each role. That is the
/// difference between this and <c>IMediaAccessor.ListAsync</c>, which answers "what files does this
/// entity use" and returns each of them once.
/// </para>
/// </summary>
/// <param name="File">The file.</param>
/// <param name="Role">What the entity uses it for.</param>
/// <param name="SortOrder">Its position within that role.</param>
public sealed record MediaAttachedFile(MediaFileInfo File, string Role, int SortOrder);

/// <summary>
/// An immutable snapshot of one attachment — what the manager hands back, alongside the
/// <see cref="MediaFileInfo"/> it links to.
/// </summary>
public sealed record MediaAttachmentInfo
{
    /// <summary>The link's identity.</summary>
    public required Guid Id { get; init; }

    /// <summary>The file it attaches.</summary>
    public required Guid MediaFileId { get; init; }

    /// <summary>The entity it attaches that file to.</summary>
    public required MediaOwner Owner { get; init; }

    /// <summary>What the file does for that entity.</summary>
    public required string Role { get; init; }

    /// <summary>Its order within the role.</summary>
    public required int SortOrder { get; init; }

    /// <summary>When the link was made. Always UTC.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>Snapshots a stored <see cref="MediaAttachment"/>.</summary>
    public static MediaAttachmentInfo From(MediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        return new MediaAttachmentInfo
        {
            Id          = attachment.Id,
            MediaFileId = attachment.MediaFileId,
            Owner       = new MediaOwner(attachment.OwnerEntityName, attachment.OwnerEntityId),
            Role        = attachment.Role,
            SortOrder   = attachment.SortOrder,
            CreatedAt   = attachment.CreatedAt
        };
    }
}

/// <summary>
/// One attachment row together with the live file row it points at — the shape of a bulk owner
/// read, where the join has to happen in the store rather than in the engine.
/// <para>
/// The single-owner reads pair links and files in application code, which costs two round trips
/// and is fine for one entity. A listing page asking about fifty entities cannot afford the same
/// shape per row, so <see cref="Interfaces.IMediaStore.ListAttachmentsForOwnersAsync"/> returns the
/// pair and the engine does no second read at all.
/// </para>
/// <para>
/// <see cref="File"/> is always live. An attachment of a deleted file does not exist — every
/// delete removes its links outright, even under soft delete — so there is no third state here
/// for a link to a tombstone.
/// </para>
/// </summary>
/// <param name="Attachment">The link: who holds the file, in what role, at what position.</param>
/// <param name="File">The file the link points at.</param>
public sealed record MediaOwnerAttachment(MediaAttachment Attachment, MediaFile File);
