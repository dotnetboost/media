global using Xunit;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace DotNetBoost.Media.IntegrationTests;

/// <summary>
/// The databases a fixture has handed out, so the first test in a class pays for the CREATE and
/// every test after it reuses the result. Tests in a collection run one at a time, but a fixture
/// is shared state regardless, so the gate is cheap insurance.
/// </summary>
internal sealed class DatabaseLedger
{
    private readonly Dictionary<string, string> _connectionStrings = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs <paramref name="create"/> the first time <paramref name="name"/> is asked for, and
    /// returns what it produced every time after.
    /// </summary>
    public async Task<string> GetOrCreateAsync(string name, Func<Task<string>> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        // Callers interpolate the name straight into DDL — CREATE DATABASE takes no parameters —
        // so it has to be an identifier and nothing else.
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"'{name}' is not a usable database name.", nameof(name));

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_connectionStrings.TryGetValue(name, out var connectionString))
                _connectionStrings[name] = connectionString = await create().ConfigureAwait(false);

            return connectionString;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// A PostgreSQL server in a container, shared by every test in its collection. Starting one per
/// test class would spend more time pulling and waiting for images than running assertions.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    // The image is passed to the constructor: Testcontainers 4.15 deprecated the parameterless
    // one, so that a suite cannot silently change engine version when the package updates.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly DatabaseLedger _databases = new();

    /// <summary>The server's default database, for suites that create their tables by name.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// A connection string for a database of this server that only <paramref name="name"/> uses.
    /// EF Core's <c>EnsureCreated</c> creates tables only for a database that has none at all, so
    /// two suites sharing one database means whichever runs second gets no tables — and every one
    /// of its tests fails on a table that was never created.
    /// </summary>
    public Task<string> CreateDatabaseAsync(string name) =>
        _databases.GetOrCreateAsync(name, async () =>
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            // PostgreSQL has no CREATE DATABASE IF NOT EXISTS; the ledger guarantees one attempt.
            command.CommandText = $"""CREATE DATABASE "{name}";""";
            await command.ExecuteNonQueryAsync();

            return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        });

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>A SQL Server in a container. Slower to become healthy than the others — see the CI timeout.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly DatabaseLedger _databases = new();

    /// <summary>The server's master database, for suites that create their tables by name.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc cref="PostgreSqlFixture.CreateDatabaseAsync"/>
    public Task<string> CreateDatabaseAsync(string name) =>
        _databases.GetOrCreateAsync(name, async () =>
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{name}];";
            await command.ExecuteNonQueryAsync();

            return new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = name }.ConnectionString;
        });

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>A MongoDB server in a container.</summary>
public sealed class MongoDbFixture : IAsyncLifetime
{
    // 8.2, not the 8.0 line behind the bare "mongo:8" tag: 8.0 refuses to start at all on Linux
    // kernel 6.19 or newer (SERVER-121912), which rules out any host whose container runtime ships
    // a current kernel — OrbStack and Colima among them. 8.2 carries the fix.
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8.2").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}

[CollectionDefinition(Name)]
public sealed class MongoDbCollection : ICollectionFixture<MongoDbFixture>
{
    public const string Name = "MongoDB";
}

/// <summary>The context the EF Core tests map their media table into.</summary>
public sealed class IntegrationContext(DbContextOptions<IntegrationContext> options) : DbContext(options), IMediaDbContext
{
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    public DbSet<MediaAttachment> MediaAttachments => Set<MediaAttachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyMedia();
    }
}
