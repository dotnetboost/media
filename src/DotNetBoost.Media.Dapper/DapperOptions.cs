namespace DotNetBoost.Media.Dapper;

/// <summary>
/// Table naming for the two tables this store reads and writes. The defaults match
/// <c>MediaModelOptions</c> in the EF Core store, so the same database serves both. Configured
/// through the <c>configure</c> argument of <c>UseDapper(...)</c>.
/// </summary>
public sealed class DapperOptions
{
    /// <summary>Table holding the media rows. Default: <c>MediaFiles</c>.</summary>
    public string MediaFilesTable { get; set; } = "MediaFiles";

    /// <summary>
    /// Table holding the links between files and the entities using them. Default:
    /// <c>MediaAttachments</c>.
    /// <para>
    /// Two tables in total, whatever the application attaches media to — which is what lets one
    /// file belong to several entities and still costs no migration per entity type.
    /// </para>
    /// </summary>
    public string MediaAttachmentsTable { get; set; } = "MediaAttachments";

    /// <summary>
    /// Schema for the table, or <c>null</c> for the connection's default. SQLite has no schemas;
    /// leave this unset there.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Overrides the SQL dialect instead of inferring it from the connection type. Set this when
    /// the connection is a wrapper — a profiler, a resiliency decorator — whose type name no
    /// longer says which engine is behind it.
    /// </summary>
    public SqlDialect? Dialect { get; set; }
}

/// <summary>
/// The SQL dialects this store writes. They differ in identifier quoting, in how a boolean and a
/// timestamp are spelled, and in how a page is limited; everything else is the same statement.
/// </summary>
public enum SqlDialect
{
    /// <summary>Microsoft SQL Server. Identifiers in <c>[brackets]</c>, paging via <c>OFFSET … FETCH</c>.</summary>
    SqlServer,

    /// <summary>PostgreSQL. Identifiers in <c>"quotes"</c>, paging via <c>LIMIT … OFFSET</c>.</summary>
    PostgreSql,

    /// <summary>SQLite. Identifiers in <c>"quotes"</c>, paging via <c>LIMIT … OFFSET</c>.</summary>
    Sqlite
}
