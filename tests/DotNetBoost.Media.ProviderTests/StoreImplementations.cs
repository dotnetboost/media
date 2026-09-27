using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Dapper;
using DotNetBoost.Media.EntityFrameworkCore;
using DotNetBoost.Media.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Media.ProviderTests;

/// <summary>The contract, against the in-memory double the other test projects are built on.</summary>
public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override Task<IMediaStore> CreateStoreAsync() => Task.FromResult<IMediaStore>(new InMemoryMediaStore());
}

/// <summary>The contract, against the EF Core store over a SQLite database.</summary>
public sealed class EfCoreStoreContractTests : StoreContractTests
{
    private SqliteConnection? _connection;
    private MediaTestContext? _context;

    protected override async Task<IMediaStore> CreateStoreAsync()
    {
        // An in-memory SQLite database lives as long as a connection to it is open, so the
        // connection is held for the test rather than opened per command.
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _context = new MediaTestContext(new DbContextOptionsBuilder<MediaTestContext>()
            .UseSqlite(_connection)
            .Options);

        await _context.Database.EnsureCreatedAsync();

        return new EfCoreMediaStore(_context);
    }

    /// <summary>
    /// A write that fails for any reason other than a duplicate id is reported as itself. The
    /// store used to call every <c>DbUpdateException</c> a conflict, which sent the caller after a
    /// row that was not the problem and made the API answer 409 to something no retry could fix.
    /// </summary>
    [Fact]
    public async Task Add_reports_a_failure_that_is_not_a_duplicate_as_itself()
    {
        await using (var drop = _connection!.CreateCommand())
        {
            drop.CommandText = """DROP TABLE "MediaFiles";""";
            await drop.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => Store.AddAsync(Row()));
    }

    protected override async Task CleanupAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();

        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

/// <summary>The contract, against the Dapper store over a SQLite database.</summary>
public sealed class DapperStoreContractTests : StoreContractTests
{
    private SqliteConnection? _connection;

    protected override async Task<IMediaStore> CreateStoreAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DapperOptions { Dialect = SqlDialect.Sqlite };
        await DapperSchema.EnsureCreatedAsync(_connection, options);

        return new DapperMediaStore(_connection, options);
    }

    /// <inheritdoc cref="EfCoreStoreContractTests.Add_reports_a_failure_that_is_not_a_duplicate_as_itself"/>
    [Fact]
    public async Task Add_reports_a_failure_that_is_not_a_duplicate_as_itself()
    {
        await using (var drop = _connection!.CreateCommand())
        {
            drop.CommandText = """DROP TABLE "MediaFiles";""";
            await drop.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => Store.AddAsync(Row()));
    }

    protected override async Task CleanupAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

/// <summary>A context with the media table and one entity of the application's own.</summary>
public sealed class MediaTestContext(DbContextOptions<MediaTestContext> options) : DbContext(options), IMediaDbContext
{
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("Products");
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).HasMaxLength(128);
        });

        modelBuilder.ApplyMedia();
    }
}

/// <summary>An ordinary entity, to prove the media table needs nothing from it.</summary>
public sealed class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
