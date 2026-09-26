using Dapper;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using System.Data;
using System.Data.Common;

namespace DotNetBoost.Media.Dapper;

/// <summary>
/// Creates the table this store reads and writes, for applications that have no migrations of
/// their own to hang it off. Enabled with <c>UseDapper(..., migrateSchema: true)</c>, which runs
/// it once when the host starts; call <see cref="EnsureCreatedAsync"/> directly to run it from a
/// deployment step or a test instead.
/// <para>
/// The shape is the one the EF Core store maps — same table and column names, same lengths, same
/// indexes — so a database created here is the database that store expects, and an application
/// can move between them. Column lengths come from the same Core constants that store's
/// configuration uses, so the two cannot drift.
/// </para>
/// <para>
/// Every statement is conditional, so running it against an existing database is a no-op. It only
/// ever CREATEs: it will not alter or drop a table that is already there, which means it can bring
/// a database into existence but cannot migrate one whose shape has changed. That is the job of
/// whatever owns the rest of your schema.
/// </para>
/// </summary>
public static class DapperSchema
{
    /// <summary>Creates the <c>MediaFiles</c> table and its indexes if they are absent. Idempotent.</summary>
    /// <param name="connection">
    /// The connection to run the DDL on. Opened for the duration if it is closed, and left as it
    /// was found. Its runtime type selects the dialect unless <see cref="DapperOptions.Dialect"/>
    /// says otherwise.
    /// </param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <exception cref="NotSupportedException">The connection type matches no supported dialect.</exception>
    /// <exception cref="InvalidOperationException">The DDL failed — typically a role without CREATE rights.</exception>
    public static async Task EnsureCreatedAsync(
        IDbConnection connection,
        DapperOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        options ??= new DapperOptions();
        var dialect = options.Dialect ?? DapperStatements.DialectOf(connection);
        var script  = Script(dialect, options);

        var opened = await UnitOfWork.OpenIfClosedAsync(connection, ct).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(script, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new InvalidOperationException(
                $"Could not create the media table ('{options.MediaFilesTable}'). The database role needs " +
                "CREATE TABLE and CREATE INDEX rights. Pass migrateSchema: false to UseDapper(...) if the " +
                "schema is managed elsewhere.",
                ex);
        }
        finally
        {
            if (opened) connection.Close();
        }
    }

    /// <summary>
    /// The DDL <see cref="EnsureCreatedAsync"/> would run, for review or for checking into a
    /// migrations folder instead of running it at startup.
    /// </summary>
    /// <param name="dialect">The engine to spell the DDL for.</param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    public static string Script(SqlDialect dialect, DapperOptions? options = null)
    {
        options ??= new DapperOptions();

        var table = DapperStatements.Qualify(dialect, options.Schema, options.MediaFilesTable);

        string C(string column) => DapperStatements.Quote(dialect, column);

        // Lengths come from Core so the two stores cannot describe the same column differently.
        var fileName   = MediaPaths.MaxFileNameLength;    // 260
        var path       = MediaPaths.MaxPathLength;        // 1024
        var entityName = MediaEntityNames.MaxLength;      // 128
        var entityId   = EntityKey.MaxLength;             // 64
        var tags       = MediaTags.MaxLength;             // 2081
        var metadata   = MediaMetadata.MaxLength;         // 4000
        const int contentType = 255;
        const int provider    = 64;
        const int container   = 512;
        const int checksum    = 64;   // lowercase hex SHA-256
        const int category    = 64;
        const int uploadedBy  = 256;

        // Index names match what EF Core generates for the same model, so the two stores'
        // databases are comparable object for object.
        var pk            = "PK_" + options.MediaFilesTable;
        var ownerIndex    = $"IX_{options.MediaFilesTable}_Owner";
        var checksumIndex = $"IX_{options.MediaFilesTable}_Checksum";
        var categoryIndex = $"IX_{options.MediaFilesTable}_Category_CreatedAt";

        return dialect switch
        {
            SqlDialect.SqlServer => $"""
                {SqlServerSchema(options.Schema)}
                IF OBJECT_ID(N'{table}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {table} (
                        {C("Id")}               UNIQUEIDENTIFIER       NOT NULL,
                        {C("FileName")}         NVARCHAR({fileName})   NOT NULL,
                        {C("ContentType")}      NVARCHAR({contentType}) NOT NULL,
                        {C("Size")}             BIGINT                 NOT NULL,
                        {C("StorageProvider")}  NVARCHAR({provider})   NOT NULL,
                        {C("StorageContainer")} NVARCHAR({container})  NOT NULL,
                        {C("StoragePath")}      NVARCHAR({path})       NOT NULL,
                        {C("Checksum")}         NVARCHAR({checksum})   NULL,
                        {C("OwnerEntityName")}  NVARCHAR({entityName}) NULL,
                        {C("OwnerEntityId")}    NVARCHAR({entityId})   NULL,
                        {C("Category")}         NVARCHAR({category})   NULL,
                        {C("Tags")}             NVARCHAR({tags})       NULL,
                        {C("Metadata")}         NVARCHAR({metadata})   NULL,
                        {C("UploadedBy")}       NVARCHAR({uploadedBy}) NULL,
                        {C("CreatedAt")}        DATETIME2              NOT NULL,
                        {C("UpdatedAt")}        DATETIME2              NULL,
                        {C("DeletedAt")}        DATETIME2              NULL,
                        CONSTRAINT {C(pk)} PRIMARY KEY ({C("Id")})
                    );
                    CREATE INDEX {C(ownerIndex)}    ON {table} ({C("OwnerEntityName")}, {C("OwnerEntityId")}, {C("CreatedAt")});
                    CREATE INDEX {C(checksumIndex)} ON {table} ({C("Checksum")});
                    CREATE INDEX {C(categoryIndex)} ON {table} ({C("Category")}, {C("CreatedAt")});
                END;
                """,

            SqlDialect.PostgreSql => $"""
                {PostgreSqlSchema(dialect, options.Schema)}
                CREATE TABLE IF NOT EXISTS {table} (
                    {C("Id")}               uuid                              NOT NULL,
                    {C("FileName")}         character varying({fileName})     NOT NULL,
                    {C("ContentType")}      character varying({contentType})  NOT NULL,
                    {C("Size")}             bigint                            NOT NULL,
                    {C("StorageProvider")}  character varying({provider})     NOT NULL,
                    {C("StorageContainer")} character varying({container})    NOT NULL,
                    {C("StoragePath")}      character varying({path})         NOT NULL,
                    {C("Checksum")}         character varying({checksum})     NULL,
                    {C("OwnerEntityName")}  character varying({entityName})   NULL,
                    {C("OwnerEntityId")}    character varying({entityId})     NULL,
                    {C("Category")}         character varying({category})     NULL,
                    {C("Tags")}             character varying({tags})         NULL,
                    {C("Metadata")}         character varying({metadata})     NULL,
                    {C("UploadedBy")}       character varying({uploadedBy})   NULL,
                    {C("CreatedAt")}        timestamp with time zone          NOT NULL,
                    {C("UpdatedAt")}        timestamp with time zone          NULL,
                    {C("DeletedAt")}        timestamp with time zone          NULL,
                    CONSTRAINT {C(pk)} PRIMARY KEY ({C("Id")})
                );
                CREATE INDEX IF NOT EXISTS {C(ownerIndex)}    ON {table} ({C("OwnerEntityName")}, {C("OwnerEntityId")}, {C("CreatedAt")});
                CREATE INDEX IF NOT EXISTS {C(checksumIndex)} ON {table} ({C("Checksum")});
                CREATE INDEX IF NOT EXISTS {C(categoryIndex)} ON {table} ({C("Category")}, {C("CreatedAt")});
                """,

            // SQLite has no schemas and its types are advisory; the lengths are written anyway so
            // that a schema diff against the other two engines reads the same.
            _ => $"""
                CREATE TABLE IF NOT EXISTS {table} (
                    {C("Id")}               TEXT             NOT NULL,
                    {C("FileName")}         TEXT             NOT NULL,
                    {C("ContentType")}      TEXT             NOT NULL,
                    {C("Size")}             INTEGER          NOT NULL,
                    {C("StorageProvider")}  TEXT             NOT NULL,
                    {C("StorageContainer")} TEXT             NOT NULL,
                    {C("StoragePath")}      TEXT             NOT NULL,
                    {C("Checksum")}         TEXT             NULL,
                    {C("OwnerEntityName")}  TEXT             NULL,
                    {C("OwnerEntityId")}    TEXT             NULL,
                    {C("Category")}         TEXT             NULL,
                    {C("Tags")}             TEXT             NULL,
                    {C("Metadata")}         TEXT             NULL,
                    {C("UploadedBy")}       TEXT             NULL,
                    {C("CreatedAt")}        TEXT             NOT NULL,
                    {C("UpdatedAt")}        TEXT             NULL,
                    {C("DeletedAt")}        TEXT             NULL,
                    CONSTRAINT {C(pk)} PRIMARY KEY ({C("Id")})
                );
                CREATE INDEX IF NOT EXISTS {C(ownerIndex)}    ON {table} ({C("OwnerEntityName")}, {C("OwnerEntityId")}, {C("CreatedAt")});
                CREATE INDEX IF NOT EXISTS {C(checksumIndex)} ON {table} ({C("Checksum")});
                CREATE INDEX IF NOT EXISTS {C(categoryIndex)} ON {table} ({C("Category")}, {C("CreatedAt")});
                """
        };
    }

    private static string SqlServerSchema(string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? string.Empty
            : $"""
               IF SCHEMA_ID(N'{schema.Replace("'", "''", StringComparison.Ordinal)}') IS NULL
                   EXEC(N'CREATE SCHEMA {DapperStatements.Quote(SqlDialect.SqlServer, schema)}');
               """;

    private static string PostgreSqlSchema(SqlDialect dialect, string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? string.Empty
            : $"CREATE SCHEMA IF NOT EXISTS {DapperStatements.Quote(dialect, schema)};";
}
