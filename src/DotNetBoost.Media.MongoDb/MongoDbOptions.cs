namespace DotNetBoost.Media.MongoDb;

/// <summary>
/// Collection naming and index behaviour for the two collections this store adds to your database.
/// Configured through the <c>configure</c> argument of <c>UseMongoDb(...)</c>.
/// </summary>
public sealed class MongoDbOptions
{
    /// <summary>Collection holding the media documents. Default: <c>mediaFiles</c>.</summary>
    public string MediaFilesCollection { get; set; } = "mediaFiles";

    /// <summary>
    /// Collection holding the links between files and the entities using them. Default:
    /// <c>mediaAttachments</c>.
    /// <para>
    /// A collection of its own rather than an array inside each file document, which is the more
    /// obviously document-shaped choice and the wrong one here. MongoDB sorts an array field by its
    /// smallest or largest element, not by the element a filter matched, so "this product's gallery
    /// in order" would order by the lowest position the file holds for <i>any</i> owner. Separate
    /// documents also keep this store's indexes the same shape as the relational ones, which is what
    /// lets an application move between backends and see the same queries served the same way.
    /// </para>
    /// </summary>
    public string MediaAttachmentsCollection { get; set; } = "mediaAttachments";

    /// <summary>
    /// Whether the store creates its indexes when the host starts. Default: <c>true</c>.
    /// <para>
    /// Without them, listing an entity's files is a collection scan, which is fine at a thousand
    /// documents and not at a million. Turn this off only when the indexes are created out of band
    /// (a migration, an ops script), and never to work around a failure: creating them needs
    /// <c>createIndex</c> on the database, and a role without it should fail the host at startup
    /// rather than run unindexed.
    /// </para>
    /// </summary>
    public bool CreateIndexes { get; set; } = true;
}
