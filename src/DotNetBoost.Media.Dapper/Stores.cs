using Dapper;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace DotNetBoost.Media.Dapper;

/// <summary>
/// Keeps media rows, and the attachments linking them to entities, in relational tables through
/// Dapper, for applications that do not use EF Core.
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
        var (where, p) = BuildWhere(sql, query);

        // Every ordering ends on the id: two rows that tie on the sort column would otherwise page
        // unstably, repeating one and skipping another.
        var direction = query.Descending ? "DESC" : "ASC";
        var id        = DapperStatements.Quote(sql.Dialect, "Id");

        // The entity's own order lives on the attachment, so the key is two correlated subqueries
        // over the links this query matched: the lowest role, then the lowest position. Narrowed to
        // one role — the ordering anyone actually asks for — the first is constant and this is
        // simply that role's SortOrder. The engine already refused SortOrder without an owner, so
        // the subqueries always have one to correlate on.
        var order = query.SortBy == MediaSortBy.SortOrder
            ? $"ORDER BY ({Correlated(sql, query, $"MIN({AttachmentAlias}.{DapperStatements.Quote(sql.Dialect, "Role")})")}) {direction}, " +
              $"({Correlated(sql, query, $"MIN({AttachmentAlias}.{DapperStatements.Quote(sql.Dialect, "SortOrder")})")}) {direction}, " +
              $"{sql.Table}.{id} {direction} "
            : $"ORDER BY {DapperStatements.Quote(sql.Dialect, query.SortBy switch
            {
                MediaSortBy.FileName => "FileName",
                MediaSortBy.Size     => "Size",
                _                    => "CreatedAt"
            })} {direction}, {id} {direction} ";

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
        var (where, p) = BuildWhere(sql, query);
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
    private static (string Where, DynamicParameters Parameters) BuildWhere(DapperStatements sql, MediaQuery query)
    {
        var dialect    = sql.Dialect;
        var clauses    = new List<string>();
        var parameters = new DynamicParameters();

        string C(string column) => DapperStatements.Quote(dialect, column);

        if (!query.IncludeDeleted)
            clauses.Add($"{C("DeletedAt")} IS NULL");

        // EXISTS rather than a join: a file linked to several of this entity's roles must come back
        // once, and EXISTS says so without a DISTINCT over every selected column — which on
        // SQL Server would also have to contend with the NVARCHAR(max) columns a DISTINCT cannot
        // include.
        //
        // Only when an owner is asked for. An unfiltered listing never touches the attachment table,
        // which is what keeps unattached files — uploads waiting for the form that will claim them —
        // in the library rather than quietly out of it.
        if (query.OwnerEntityName is { } ownerName)
        {
            clauses.Add($"EXISTS ({Correlated(sql, query, "1")})");
            parameters.Add("OwnerEntityName", ownerName);

            if (query.OwnerEntityId is { } ownerId)
                parameters.Add("OwnerEntityId", ownerId);

            if (query.Role is { } role)
                parameters.Add("Role", role);
        }

        if (query.Unattached)
        {
            clauses.Add(
                $"NOT EXISTS (SELECT 1 FROM {sql.AttachmentTable} {AttachmentAlias} " +
                $"WHERE {AttachmentAlias}.{C("MediaFileId")} = {sql.Table}.{C("Id")})");
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

    /// <summary>The alias the attachment table takes inside a subquery.</summary>
    private const string AttachmentAlias = "ma";

    /// <summary>
    /// A subquery over the attachments a query's owner filter matches, correlated to the file row
    /// outside it. One place, so the EXISTS that joins and the MIN()s that order cannot end up
    /// talking about different sets of links.
    /// </summary>
    private static string Correlated(DapperStatements sql, MediaQuery query, string projection)
    {
        string C(string column) => DapperStatements.Quote(sql.Dialect, column);

        var where = $"{AttachmentAlias}.{C("MediaFileId")} = {sql.Table}.{C("Id")}"
                    + $" AND {AttachmentAlias}.{C("OwnerEntityName")} = @OwnerEntityName";

        if (query.OwnerEntityId is not null)
            where += $" AND {AttachmentAlias}.{C("OwnerEntityId")} = @OwnerEntityId";

        if (query.Role is not null)
            where += $" AND {AttachmentAlias}.{C("Role")} = @Role";

        return $"SELECT {projection} FROM {sql.AttachmentTable} {AttachmentAlias} WHERE {where}";
    }

    /// <inheritdoc/>
    public async Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        try
        {
            await ExecuteAsync(Sql.InsertAttachment, Parameters(attachment), ct).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            if (!await IsDuplicateAttachmentAsync(ex, attachment, ct).ConfigureAwait(false))
                throw;

            throw new MediaConflictException(
                $"Media file '{attachment.MediaFileId}' is already attached to {attachment.OwnerEntityName} " +
                $"{attachment.OwnerEntityId} as '{attachment.Role}'.",
                ex);
        }
    }

    /// <summary>
    /// Whether the insert failed because that exact link is already there, read the same way
    /// <see cref="IsDuplicateAsync"/> reads it and for the same reasons: SQLSTATE on PostgreSQL,
    /// because a failed statement leaves its transaction aborted and the probe below could not run;
    /// a probe on the other two, whose transactions survive a constraint violation and whose error
    /// codes live on provider types this package does not reference.
    /// </summary>
    private async Task<bool> IsDuplicateAttachmentAsync(DbException ex, MediaAttachment attachment, CancellationToken ct)
    {
        if (Dialect == SqlDialect.PostgreSql)
            return string.Equals(ex.SqlState, PostgresUniqueViolation, StringComparison.Ordinal);

        try
        {
            var sql       = Sql;
            var statement = $"{sql.SelectAttachments}WHERE {DapperStatements.Quote(sql.Dialect, "MediaFileId")} = @MediaFileId " +
                            $"AND {sql.OwnerPredicate} AND {sql.RolePredicate}";

            var found = await QueryAttachmentsAsync(
                statement,
                new
                {
                    attachment.MediaFileId,
                    attachment.OwnerEntityName,
                    attachment.OwnerEntityId,
                    attachment.Role
                }, ct).ConfigureAwait(false);

            return found.Count > 0;
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default) =>
        await ExecuteAsync(Sql.DeleteAttachment, new { Id = attachmentId }, ct).ConfigureAwait(false) > 0;

    /// <inheritdoc/>
    public async Task<int> RemoveAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default)
    {
        var sql       = Sql;
        var statement = $"DELETE FROM {sql.AttachmentTable} WHERE {sql.OwnerPredicate}"
                        + (role is null ? string.Empty : $" AND {sql.RolePredicate}");

        return await ExecuteAsync(
            statement,
            new { OwnerEntityName = owner.EntityName, OwnerEntityId = owner.EntityId, Role = role },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default) =>
        await ExecuteAsync(Sql.DeleteAttachmentsOfFile, new { MediaFileId = mediaFileId }, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var affected = await ExecuteAsync(
            Sql.UpdateAttachment,
            new { attachment.Id, attachment.Role, attachment.SortOrder },
            ct).ConfigureAwait(false);

        if (affected == 0)
            throw new MediaNotFoundException(attachment.Id);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(
        Guid mediaFileId, CancellationToken ct = default) =>
        await QueryAttachmentsAsync(Sql.SelectAttachmentsOfFile, new { MediaFileId = mediaFileId }, ct)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default)
    {
        var sql = Sql;

        string C(string column) => DapperStatements.Quote(sql.Dialect, column);

        var statement = $"{sql.SelectAttachments}WHERE {sql.OwnerPredicate}"
                        + (role is null ? string.Empty : $" AND {sql.RolePredicate}")
                        + $" ORDER BY {C("Role")} ASC, {C("SortOrder")} ASC, {C("Id")} ASC";

        return await QueryAttachmentsAsync(
            statement,
            new { OwnerEntityName = owner.EntityName, OwnerEntityId = owner.EntityId, Role = role },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ids per statement. Under SQL Server's 2,100-parameter ceiling and SQLite's default 999, with
    /// room to spare — an entity with more attachments than this is unusual, and it costs it one
    /// extra round trip rather than a failed statement.
    /// </summary>
    private const int ShareProbeBatch = 500;

    /// <inheritdoc/>
    public async Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaFileIds);

        var shared = new HashSet<Guid>();

        if (mediaFileIds.Count == 0)
            return shared;

        var sql = Sql;

        string C(string column) => DapperStatements.Quote(sql.Dialect, column);

        // Batched, and one statement per batch rather than a probe per file: deleting a product with
        // fifty images must not become fifty round trips.
        foreach (var batch in mediaFileIds.Distinct().Chunk(ShareProbeBatch))
        {
            // The ids are bound one parameter at a time rather than left to Dapper's IN expansion.
            // Dapper asks the provider whether it supports arrays, and for Npgsql the answer is yes:
            // it then passes the whole list as a single array parameter, which PostgreSQL will take
            // only as "= ANY(@ids)" and rejects as a syntax error after IN. Naming each id keeps one
            // statement correct on all three engines instead of one spelling per dialect.
            var parameters = new DynamicParameters();
            parameters.Add("OwnerEntityName", excluding.EntityName);
            parameters.Add("OwnerEntityId", excluding.EntityId);

            var names = new string[batch.Length];

            for (var i = 0; i < batch.Length; i++)
            {
                names[i] = "@id" + i.ToString(CultureInfo.InvariantCulture);
                parameters.Add("id" + i.ToString(CultureInfo.InvariantCulture), batch[i]);
            }

            var statement =
                $"SELECT DISTINCT {C("MediaFileId")} FROM {sql.AttachmentTable} " +
                $"WHERE {C("MediaFileId")} IN ({string.Join(", ", names)}) " +
                $"AND ({C("OwnerEntityName")} <> @OwnerEntityName OR {C("OwnerEntityId")} <> @OwnerEntityId)";

            // Read through the reader rather than Dapper's own mapping: the id comes back as a Guid
            // on SQL Server and PostgreSQL and as text on SQLite, and Query<Guid> cannot make that
            // conversion — the same reason MediaFileMapper reads every column by hand.
            var found = await UnitOfWork.RunAsync(
                _connection,
                async () =>
                {
                    await using var reader = await _connection.ExecuteReaderAsync(
                        new CommandDefinition(statement, parameters, transaction, cancellationToken: ct)).ConfigureAwait(false) as DbDataReader
                        ?? throw new InvalidOperationException("The connection did not return a DbDataReader.");

                    var ids = new List<Guid>();

                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        ids.Add(MediaFileMapper.Guid(reader, 0));

                    return ids;
                },
                ct).ConfigureAwait(false);

            shared.UnionWith(found);
        }

        return shared;
    }

    private Task<IReadOnlyList<MediaAttachment>> QueryAttachmentsAsync(
        string sql, object parameters, CancellationToken ct) =>
        UnitOfWork.RunAsync(
            _connection,
            async () =>
            {
                await using var reader = await _connection.ExecuteReaderAsync(
                    new CommandDefinition(sql, parameters, transaction, cancellationToken: ct)).ConfigureAwait(false) as DbDataReader
                    ?? throw new InvalidOperationException("The connection did not return a DbDataReader.");

                return (IReadOnlyList<MediaAttachment>)await MediaAttachmentMapper.ReadAllAsync(reader, ct).ConfigureAwait(false);
            },
            ct);

    private static object Parameters(MediaAttachment attachment) => new
    {
        attachment.Id,
        attachment.MediaFileId,
        attachment.OwnerEntityName,
        attachment.OwnerEntityId,
        attachment.Role,
        attachment.SortOrder,
        attachment.CreatedAt
    };

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
