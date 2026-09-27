using System.Collections.Concurrent;
using System.Data;

namespace DotNetBoost.Media.Dapper;

/// <summary>
/// Every statement the store issues, spelled for one dialect and one pair of table names. Built
/// once per (dialect, schema, tables) and cached: the store is scoped, so composing a dozen strings in each
/// constructor would be per-request work for a result that never varies.
/// </summary>
internal sealed class DapperStatements
{
    private static readonly ConcurrentDictionary<(SqlDialect, string?, string), DapperStatements> Cache = new();

    /// <summary>Every column, in a fixed order, so INSERT and SELECT cannot disagree.</summary>
    internal const string ColumnList =
        "Id, FileName, ContentType, Size, StorageProvider, StorageContainer, StoragePath, Checksum, " +
        "Category, Tags, Metadata, UploadedBy, CreatedAt, UpdatedAt, DeletedAt";

    /// <summary>Every attachment column, in a fixed order, for the same reason.</summary>
    internal const string AttachmentColumnList =
        "Id, MediaFileId, OwnerEntityName, OwnerEntityId, Role, SortOrder, CreatedAt";

    private DapperStatements(SqlDialect dialect, DapperOptions options)
    {
        Dialect         = dialect;
        Table           = Qualify(dialect, options.Schema, options.MediaFilesTable);
        AttachmentTable = Qualify(dialect, options.Schema, options.MediaAttachmentsTable);

        var columns = string.Join(", ", ColumnList.Split(',', StringSplitOptions.TrimEntries).Select(c => Quote(dialect, c)));
        var values  = string.Join(", ", ColumnList.Split(',', StringSplitOptions.TrimEntries).Select(c => "@" + c));

        var attachmentColumns = string.Join(", ", AttachmentColumnList.Split(',', StringSplitOptions.TrimEntries).Select(c => Quote(dialect, c)));
        var attachmentValues  = string.Join(", ", AttachmentColumnList.Split(',', StringSplitOptions.TrimEntries).Select(c => "@" + c));

        string C(string column) => Quote(dialect, column);

        Insert = $"INSERT INTO {Table} ({columns}) VALUES ({values})";

        Select = $"SELECT {columns} FROM {Table} ";

        SelectById = $"{Select}WHERE {C("Id")} = @Id";

        Update =
            $"UPDATE {Table} SET " +
            $"{C("FileName")} = @FileName, {C("ContentType")} = @ContentType, " +
            $"{C("Category")} = @Category, {C("Tags")} = @Tags, {C("Metadata")} = @Metadata, " +
            $"{C("UpdatedAt")} = @UpdatedAt, {C("DeletedAt")} = @DeletedAt " +
            $"WHERE {C("Id")} = @Id";

        Delete = $"DELETE FROM {Table} WHERE {C("Id")} = @Id";

        SelectByChecksum =
            $"{Select}WHERE {C("Checksum")} = @Checksum AND {C("Size")} = @Size AND {C("DeletedAt")} IS NULL " +
            $"ORDER BY {C("CreatedAt")} {Limit(dialect, 1)}";

        InsertAttachment = $"INSERT INTO {AttachmentTable} ({attachmentColumns}) VALUES ({attachmentValues})";

        SelectAttachments = $"SELECT {attachmentColumns} FROM {AttachmentTable} ";

        SelectAttachmentsOfFile =
            $"{SelectAttachments}WHERE {C("MediaFileId")} = @MediaFileId " +
            $"ORDER BY {C("CreatedAt")} ASC, {C("Id")} ASC";

        SelectAttachmentById = $"{SelectAttachments}WHERE {C("Id")} = @Id";

        UpdateAttachment =
            $"UPDATE {AttachmentTable} SET " +
            $"{C("Role")} = @Role, {C("SortOrder")} = @SortOrder " +
            $"WHERE {C("Id")} = @Id";

        DeleteAttachment = $"DELETE FROM {AttachmentTable} WHERE {C("Id")} = @Id";

        DeleteAttachmentsOfFile = $"DELETE FROM {AttachmentTable} WHERE {C("MediaFileId")} = @MediaFileId";

        // The owner predicate, reused by the listing, the bulk delete, the EXISTS behind an
        // owner-filtered file query and the subqueries that order one. Written once so a filter and
        // the count or the ordering beside it cannot disagree about which links they mean.
        OwnerPredicate = $"{C("OwnerEntityName")} = @OwnerEntityName AND {C("OwnerEntityId")} = @OwnerEntityId";

        RolePredicate = $"{C("Role")} = @Role";
    }

    public SqlDialect Dialect { get; }
    public string Table { get; }
    public string AttachmentTable { get; }

    public string Insert { get; }
    public string Select { get; }
    public string SelectById { get; }
    public string Update { get; }
    public string Delete { get; }
    public string SelectByChecksum { get; }

    public string InsertAttachment { get; }
    public string SelectAttachments { get; }
    public string SelectAttachmentsOfFile { get; }
    public string SelectAttachmentById { get; }
    public string UpdateAttachment { get; }
    public string DeleteAttachment { get; }
    public string DeleteAttachmentsOfFile { get; }
    public string OwnerPredicate { get; }
    public string RolePredicate { get; }

    public static DapperStatements For(SqlDialect dialect, DapperOptions options) =>
        Cache.GetOrAdd(
            (dialect, options.Schema, options.MediaFilesTable + "|" + options.MediaAttachmentsTable),
            static (_, state) => new DapperStatements(state.Dialect, state.Options),
            (Dialect: dialect, Options: options));

    /// <summary>
    /// The dialect a connection speaks, from its runtime type name. Deliberately a name match
    /// rather than a reference to each provider's package: this library would otherwise have to
    /// depend on all three to support any one of them.
    /// </summary>
    /// <exception cref="NotSupportedException">The connection matches none of the supported engines.</exception>
    public static SqlDialect DialectOf(IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var name = connection.GetType().Name;

        return name switch
        {
            "SqlConnection"    => SqlDialect.SqlServer,
            "NpgsqlConnection" => SqlDialect.PostgreSql,
            "SqliteConnection" or "SQLiteConnection" => SqlDialect.Sqlite,
            _ => throw new NotSupportedException(
                $"Cannot infer a SQL dialect from connection type '{name}'. Supported: SQL Server, PostgreSQL " +
                "and SQLite. Set DapperOptions.Dialect explicitly — which is also what to do when the " +
                "connection is a wrapper around one of them.")
        };
    }

    /// <summary>An identifier, quoted the way its engine quotes one.</summary>
    public static string Quote(SqlDialect dialect, string identifier) => dialect switch
    {
        // Doubling the closing delimiter is what makes an identifier containing one safe. Neither
        // is expected in a table name; not doing it would be an injection point in a method whose
        // whole job is to make one safe.
        SqlDialect.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
        _                    => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
    };

    /// <summary>A table name, with its schema when there is one.</summary>
    public static string Qualify(SqlDialect dialect, string? schema, string table) =>
        string.IsNullOrWhiteSpace(schema)
            ? Quote(dialect, table)
            : $"{Quote(dialect, schema)}.{Quote(dialect, table)}";

    /// <summary>A row limit, spelled for the dialect. Must follow an ORDER BY on SQL Server.</summary>
    public static string Limit(SqlDialect dialect, int take, int skip = 0) => dialect switch
    {
        SqlDialect.SqlServer => $"OFFSET {skip} ROWS FETCH NEXT {take} ROWS ONLY",
        _                    => $"LIMIT {take} OFFSET {skip}"
    };

    /// <summary>
    /// The literal for a boolean. SQL Server has no boolean type, so its bit columns take 1 and 0.
    /// </summary>
    public static object Bool(SqlDialect dialect, bool value) =>
        dialect == SqlDialect.SqlServer ? value ? 1 : 0 : value;
}
