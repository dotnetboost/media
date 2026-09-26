global using Xunit;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace DotNetBoost.Media.IntegrationTests;

/// <summary>
/// A PostgreSQL server in a container, shared by every test in its collection. Starting one per
/// test class would spend more time pulling and waiting for images than running assertions.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    // The image is passed to the constructor: Testcontainers 4.15 deprecated the parameterless
    // one, so that a suite cannot silently change engine version when the package updates.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>A SQL Server in a container. Slower to become healthy than the others — see the CI timeout.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>A MongoDB server in a container.</summary>
public sealed class MongoDbFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8").Build();

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyMedia();
    }
}
