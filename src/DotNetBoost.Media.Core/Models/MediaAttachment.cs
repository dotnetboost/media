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
/// The roles an attachment can take, and the rules for writing one down. The set is deliberately
/// open — an application's roles are its own (<c>hero</c>, <c>thumbnail</c>, <c>datasheet</c>), and
/// an enum here would force this library's vocabulary onto it.
/// </summary>
public static class MediaRoles
{
    /// <summary>The role an attachment takes when the caller names none.</summary>
    public const string Default = "default";

    /// <summary>A product's main image, by convention. Single-valued: attach it with <c>SetAsync</c>.</summary>
    public const string Primary = "primary";

    /// <summary>The further images behind the primary one, ordered. Multi-valued: use <c>AddAsync</c>.</summary>
    public const string Gallery = "gallery";

    /// <summary>The longest a role may be.</summary>
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
