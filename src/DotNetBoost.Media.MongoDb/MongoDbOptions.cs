namespace DotNetBoost.Media.MongoDb;

/// <summary>
/// Collection naming and index behaviour for the one collection this store adds to your database.
/// Configured through the <c>configure</c> argument of <c>UseMongoDb(...)</c>.
/// </summary>
public sealed class MongoDbOptions
{
    /// <summary>Collection holding the media documents. Default: <c>mediaFiles</c>.</summary>
    public string MediaFilesCollection { get; set; } = "mediaFiles";

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
