using Dapper;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using System.Data;
using System.Data.Common;
using System.Text;

namespace DotNetBoost.Media.Dapper;

/// <summary>
/// Keeps media rows in a relational table, through Dapper, for applications that do not use
/// EF Core.
/// <para>
/// The connection comes from the scope, so an application that begins a transaction on it and
/// registers that transaction in the scope gets its entity and its media rows committed together
/// — the same guarantee the EF Core store gets from sharing a context.
/// </para>
/// </summary>
/// <param name="connection">The scope's connection. Borrowed, never owned.</param>
/// <param name="options">Table naming and an optional explicit dialect.</param>
/// <param name="transaction">The application's ambient transaction, when it has one.</param>
public sealed class DapperMediaStore(
    IDbConnection connection,
    DapperOptions options,
    IDbTransaction? transaction = null) : IMediaStore
{
    private readonly IDbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly DapperOptions _options    = options ?? throw new ArgumentNullException(nameof(options));

    private SqlDialect Dialect => _options.Dialect ?? DapperStatements.DialectOf(_connection);

    private DapperStatements Sql => DapperStatements.For(Dialect, _options);

    /// <summary>
    /// Runs this store's statements inside <paramref name="ambient"/>, so an application's own
    /// transaction covers its media rows too:
    /// <code>
    /// using var tx = connection.BeginTransaction();
    /// var store = new DapperMediaStore(connection, options).WithTransaction(tx);
    /// // … write your entity and upload its files …
    /// tx.Commit();
    /// </code>
    /// </summary>
    public DapperMediaStore WithTransaction(IDbTransaction ambient)
    {
        ArgumentNullException.ThrowIfNull(ambient);
        return new DapperMediaStore(_connection, _options, ambient);
    }

    /// <inheritdoc/>
    public async Task AddAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            await ExecuteAsync(Sql.Insert, Parameters(file), ct).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            if (!await IsDuplicateAsync(ex, file.Id, ct).ConfigureAwait(false))
                throw;

            throw new MediaConflictException(
                $"Could not record media file '{file.Id}'. Another row with that id already exists.",
                ex);
        }
    }

    /// <summary>SQLSTATE <c>23505</c>, <c>unique_violation</c>.</summary>
    private const string PostgresUniqueViolation = "23505";

    /// <summary>
    /// Whether <paramref name="ex"/> is this engine's way of saying a row with this id is already
    /// there, rather than a missing table, a dropped connection or a value the column will not
    /// take. Everything that is not a duplicate is reported as itself: calling it a conflict sends
    /// the caller after a row that is not the problem, and makes the API answer 409 to something
    /// no retry will ever fix.
    /// <para>
    /// PostgreSQL states it in SQLSTATE, which <see cref="DbException.SqlState"/> carries, and it
    /// has to be read there rather than asked about: a failed statement leaves a PostgreSQL
    /// transaction aborted, so inside an application's ambient transaction the follow-up query
    /// below would fail too. SQL Server and SQLite put their codes on provider types this package
    /// deliberately does not reference — <see cref="DapperStatements.DialectOf"/> explains why —
    /// but a constraint violation leaves a transaction of theirs usable, so on those the row can
    /// simply be looked for.
    /// </para>
    /// </summary>
    private async Task<bool> IsDuplicateAsync(DbException ex, Guid id, CancellationToken ct)
    {
        if (Dialect == SqlDialect.PostgreSql)
            return string.Equals(ex.SqlState, PostgresUniqueViolation, StringComparison.Ordinal);

        try
        {
            // SelectById, not FindAsync: a soft-deleted row still holds the id.
            return await QuerySingleAsync(Sql.SelectById, new { Id = id }, ct).ConfigureAwait(false) is not null;
        }
        catch (DbException)
        {
            // The probe could not run either, so nothing here says the insert hit a duplicate.
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default)
    {
        var row = await QuerySingleAsync(Sql.SelectById, new { Id = id }, ct).ConfigureAwait(false);
        return row is null || (!includeDeleted && row.DeletedAt is not null) ? null : row;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var affected = await ExecuteAsync(Sql.Update, Parameters(file), ct).ConfigureAwait(false);

        if (affected == 0)
            throw new MediaNotFoundException(file.Id);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        await ExecuteAsync(Sql.Delete, new { Id = id }, ct).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql        = Sql;
        var (where, p) = BuildWhere(sql.Dialect, query);

        // Every ordering ends on the id: two rows that tie on the sort column would otherwise page
        // unstably, repeating one and skipping another.
        var column    = DapperStatements.Quote(sql.Dialect, query.SortBy switch
        {
            MediaSortBy.FileName => "FileName",
            MediaSortBy.Size     => "Size",
            _                    => "CreatedAt"
        });
        var direction = query.Descending ? "DESC" : "ASC";
        var order     = $"ORDER BY {column} {direction}, {DapperStatements.Quote(sql.Dialect, "Id")} {direction} ";

        var statement = sql.Select + where + order + DapperStatements.Limit(sql.Dialect, query.Take, query.Skip);

        return await UnitOfWork.RunAsync(
            _connection,
            async () =>
            {
                await using var reader = await _connection.ExecuteReaderAsync(
                    new CommandDefinition(statement, p, transaction, cancellationToken: ct)).ConfigureAwait(false) as DbDataReader
                    ?? throw new InvalidOperationException("The connection did not return a DbDataReader.");

                return (IReadOnlyList<MediaFile>)await MediaFileMapper.ReadAllAsync(reader, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql        = Sql;
        var (where, p) = BuildWhere(sql.Dialect, query);
        var statement  = $"SELECT COUNT(*) FROM {sql.Table} {where}";

        return await UnitOfWork.RunAsync(
            _connection,
            () => _connection.ExecuteScalarAsync<int>(
                new CommandDefinition(statement, p, transaction, cancellationToken: ct)),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        return QuerySingleAsync(Sql.SelectByChecksum, new { Checksum = checksum, Size = size }, ct);
    }

    /// <summary>
    /// The WHERE clause for a query, with its parameters. Shared by the page and the count so the
    /// two cannot drift — a total that does not match the rows it is a total of is worse than no
    /// total at all.
    /// </summary>
    private static (string Where, DynamicParameters Parameters) BuildWhere(SqlDialect dialect, MediaQuery query)
    {
        var clauses    = new List<string>();
        var parameters = new DynamicParameters();

        string C(string column) => DapperStatements.Quote(dialect, column);

        if (!query.IncludeDeleted)
            clauses.Add($"{C("DeletedAt")} IS NULL");

        if (query.OwnerEntityName is { } ownerName)
        {
            clauses.Add($"{C("OwnerEntityName")} = @OwnerEntityName");
            parameters.Add("OwnerEntityName", ownerName);
        }

        if (query.OwnerEntityId is { } ownerId)
        {
            clauses.Add($"{C("OwnerEntityId")} = @OwnerEntityId");
            parameters.Add("OwnerEntityId", ownerId);
        }

        if (query.Category is { } category)
        {
            clauses.Add($"{C("Category")} = @Category");
            parameters.Add("Category", category);
        }

        if (query.UploadedBy is { } uploadedBy)
        {
            clauses.Add($"{C("UploadedBy")} = @UploadedBy");
            parameters.Add("UploadedBy", uploadedBy);
        }

        if (query.Checksum is { } checksum)
        {
            clauses.Add($"{C("Checksum")} = @Checksum");
            parameters.Add("Checksum", checksum);
        }

        if (query.CreatedAfter is { } after)
        {
            clauses.Add($"{C("CreatedAt")} >= @CreatedAfter");
            parameters.Add("CreatedAfter", after);
        }

        if (query.CreatedBefore is { } before)
        {
            clauses.Add($"{C("CreatedAt")} < @CreatedBefore");
            parameters.Add("CreatedBefore", before);
        }

        if (query.ContentTypePrefix is { } prefix)
        {
            clauses.Add($"{C("ContentType")} LIKE @ContentTypePrefix ESCAPE '\\'");
            parameters.Add("ContentTypePrefix", Escape(prefix) + "%");
        }

        if (query.Search is { Length: > 0 } search)
        {
            // Both sides lowered, because LIKE is case-SENSITIVE on PostgreSQL — matching the
            // column's collation would make "case-insensitive" true on SQL Server and false on
            // Postgres, which is worse than either. Unanchored, so it matches anywhere in the
            // name, and escaped so a search for "report_final" does not also match "reportxfinal".
            // No index is lost: an unanchored LIKE could not use one anyway.
            clauses.Add($"LOWER({C("FileName")}) LIKE @Search ESCAPE '\\'");
            parameters.Add("Search", "%" + Escape(search.ToLowerInvariant()) + "%");
        }

        if (query.Tag is { } tag)
        {
            // Tags are stored comma-surrounded, so ",invoice," matches a whole tag and never a
            // longer one that starts with it.
            clauses.Add($"{C("Tags")} LIKE @Tag ESCAPE '\\'");
            parameters.Add("Tag", "%" + Escape(MediaTags.Fragment(tag)) + "%");
        }

        if (clauses.Count == 0)
            return (string.Empty, parameters);

        var where = new StringBuilder("WHERE ");
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0) where.Append(" AND ");
            where.Append(clauses[i]);
        }

        return (where.Append(' ').ToString(), parameters);
    }

    /// <summary>
    /// Neutralises the wildcards in a value used inside a LIKE. A content-type prefix or a tag
    /// containing <c>%</c> would otherwise match far more than the caller asked for, and one
    /// containing <c>_</c> would match a tag it is not. Escaping the escape character first is
    /// what keeps the transformation reversible.
    /// <para>
    /// The statements that use these values carry an explicit <c>ESCAPE '\'</c>. Without it
    /// SQL Server and SQLite have no escape character at all, and the backslashes added here
    /// would be matched literally — turning a correct query into one that finds nothing.
    /// </para>
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static object Parameters(MediaFile file) => new
    {
        file.Id,
        file.FileName,
        file.ContentType,
        file.Size,
        file.StorageProvider,
        file.StorageContainer,
        file.StoragePath,
        file.Checksum,
        file.OwnerEntityName,
        file.OwnerEntityId,
        file.Category,
        file.Tags,
        file.Metadata,
        file.UploadedBy,
        file.CreatedAt,
        file.UpdatedAt,
        file.DeletedAt
    };

    private Task<int> ExecuteAsync(string sql, object parameters, CancellationToken ct) =>
        UnitOfWork.RunAsync(
            _connection,
            () => _connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct)),
            ct);

    private Task<MediaFile?> QuerySingleAsync(string sql, object parameters, CancellationToken ct) =>
        UnitOfWork.RunAsync(
            _connection,
            async () =>
            {
                await using var reader = await _connection.ExecuteReaderAsync(
                    new CommandDefinition(sql, parameters, transaction, cancellationToken: ct)).ConfigureAwait(false) as DbDataReader
                    ?? throw new InvalidOperationException("The connection did not return a DbDataReader.");

                return await MediaFileMapper.ReadFirstAsync(reader, ct).ConfigureAwait(false);
            },
            ct);
}
