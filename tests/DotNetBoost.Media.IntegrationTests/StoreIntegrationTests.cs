using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Dapper;
using DotNetBoost.Media.EntityFrameworkCore;
using DotNetBoost.Media.MongoDb;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using Npgsql;

namespace DotNetBoost.Media.IntegrationTests;

/// <summary>
/// The same store contract the provider tests run against SQLite, here against the engines an
/// application actually deploys on. SQLite is forgiving about types in a way none of these are —
/// a GUID that is really text, a timestamp that is really a string — so this is where those
/// differences show up.
/// </summary>
public abstract class StoreIntegrationTests : ProviderTests.StoreContractTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlEfCoreTests(PostgreSqlFixture fixture) : StoreIntegrationTests
{
    private IntegrationContext? _context;

    protected override async Task<IMediaStore> CreateStoreAsync()
    {
        _context = new IntegrationContext(new DbContextOptionsBuilder<IntegrationContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable("__media_test"))
            // A schema per test, so tests in the same class do not see each other's rows.
            .Options);

        await _context.Database.EnsureCreatedAsync();
        await ClearAsync(_context);

        return new EfCoreMediaStore(_context);
    }

    protected override async Task CleanupAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();
    }

    private static Task ClearAsync(IntegrationContext context) =>
        context.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "MediaFiles";""");
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlDapperTests(PostgreSqlFixture fixture) : StoreIntegrationTests
{
    private NpgsqlConnection? _connection;

    protected override async Task<IMediaStore> CreateStoreAsync()
    {
        var options = new DapperOptions { MediaFilesTable = "MediaFilesDapper", Dialect = SqlDialect.PostgreSql };

        _connection = new NpgsqlConnection(fixture.ConnectionString);
        await _connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(_connection, options);

        await using (var truncate = _connection.CreateCommand())
        {
            truncate.CommandText = """TRUNCATE TABLE "MediaFilesDapper";""";
            await truncate.ExecuteNonQueryAsync();
        }

        return new DapperMediaStore(_connection, options);
    }

    /// <summary>
    /// A write that fails for any reason other than a duplicate id is reported as itself, not as a
    /// conflict. PostgreSQL is the engine the store reads SQLSTATE for, so this is where a wrong
    /// reading would show.
    /// </summary>
    [Fact]
    public async Task Add_reports_a_failure_that_is_not_a_duplicate_as_itself()
    {
        await using (var drop = _connection!.CreateCommand())
        {
            drop.CommandText = """DROP TABLE "MediaFilesDapper";""";
            await drop.ExecuteNonQueryAsync();
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Store.AddAsync(Row()));
        Assert.Equal("42P01", ex.SqlState);
    }

    /// <summary>
    /// A duplicate id is still a conflict inside the application's own transaction. This is why the
    /// store reads SQLSTATE on PostgreSQL rather than looking the row up: the failed insert leaves
    /// the transaction aborted, so a follow-up query inside it would fail too and the conflict
    /// would come back as whatever that failure was.
    /// </summary>
    [Fact]
    public async Task Add_refuses_a_duplicate_id_inside_an_ambient_transaction()
    {
        var row = Row();
        await Store.AddAsync(row);

        var clash = Row();
        clash.Id = row.Id;

        await using var transaction = await _connection!.BeginTransactionAsync();
        var store = ((DapperMediaStore)Store).WithTransaction(transaction);

        await Assert.ThrowsAsync<MediaConflictException>(() => store.AddAsync(clash));

        await transaction.RollbackAsync();
    }

    protected override async Task CleanupAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerEfCoreTests(SqlServerFixture fixture) : StoreIntegrationTests
{
    private IntegrationContext? _context;

    protected override async Task<IMediaStore> CreateStoreAsync()
    {
        _context = new IntegrationContext(new DbContextOptionsBuilder<IntegrationContext>()
            .UseSqlServer(fixture.ConnectionString)
            .Options);

        await _context.Database.EnsureCreatedAsync();
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM [MediaFiles];");

        return new EfCoreMediaStore(_context);
    }

    protected override async Task CleanupAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerDapperTests(SqlServerFixture fixture) : StoreIntegrationTests
{
    private SqlConnection? _connection;

    protected override async Task<IMediaStore> CreateStoreAsync()
    {
        var options = new DapperOptions { MediaFilesTable = "MediaFilesDapper", Dialect = SqlDialect.SqlServer };

        _connection = new SqlConnection(fixture.ConnectionString);
        await _connection.OpenAsync();

        await DapperSchema.EnsureCreatedAsync(_connection, options);

        await using (var delete = _connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM [MediaFilesDapper];";
            await delete.ExecuteNonQueryAsync();
        }

        return new DapperMediaStore(_connection, options);
    }

    protected override async Task CleanupAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

[Collection(MongoDbCollection.Name)]
public sealed class MongoStoreTests(MongoDbFixture fixture) : StoreIntegrationTests
{
    protected override Task<IMediaStore> CreateStoreAsync()
    {
        MediaFileClassMapAccess.Register();

        var database = new MongoClient(fixture.ConnectionString)
            // A database per test: dropping a collection is cheaper than deleting its documents,
            // and it guarantees nothing is left over from a test that failed part-way.
            .GetDatabase($"media-{Guid.NewGuid():N}");

        return Task.FromResult<IMediaStore>(new MongoMediaStore(database, new MongoDbOptions()));
    }
}

/// <summary>
/// Registers the driver's class map for the tests, which construct the store directly rather than
/// going through <c>UseMongoDb()</c>.
/// </summary>
internal static class MediaFileClassMapAccess
{
    private static readonly object Gate = new();
    private static bool _done;

    public static void Register()
    {
        lock (Gate)
        {
            if (_done)
                return;

            if (!MongoDB.Bson.Serialization.BsonClassMap.IsClassMapRegistered(typeof(MediaFile)))
            {
                MongoDB.Bson.Serialization.BsonClassMap.RegisterClassMap<MediaFile>(map =>
                {
                    map.AutoMap();
                    map.MapIdMember(f => f.Id)
                        .SetSerializer(new MongoDB.Bson.Serialization.Serializers.GuidSerializer(MongoDB.Bson.GuidRepresentation.Standard));
                    map.UnmapMember(f => f.IsDeleted);

                    foreach (var member in map.DeclaredMemberMaps)
                        member.SetIgnoreIfNull(true);
                });
            }

            _done = true;
        }
    }
}
