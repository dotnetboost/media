using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using DotNetBoost.Media.EntityFrameworkCore;
using DotNetBoost.Media.FileSystem;
using DotNetBoost.Media.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Media.ProviderTests;

/// <summary>
/// The EF Core store as an application meets it: a real context, a real migration-created schema,
/// and the engine on top of both.
/// </summary>
public sealed class EfCoreTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private MediaTestContext _context = null!;
    private IMediaManager _media = null!;
    private InMemoryStorageProvider _storage = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _context = new MediaTestContext(new DbContextOptionsBuilder<MediaTestContext>()
            .UseSqlite(_connection)
            .Options);

        await _context.Database.EnsureCreatedAsync();

        _storage = new InMemoryStorageProvider();

        var clock = TimeProvider.System;

        _media = new MediaManager(
            new EfCoreMediaStore(_context),
            new MediaStorageProviderRegistry([_storage], defaultProvider: null),
            new DateHierarchyKeyGenerator(clock),
            new EfCoreEntityKeyAccessor(_context),
            new MediaOptions(),
            clock,
            NullLogger<MediaManager>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ApplyMedia_creates_the_table_without_touching_the_applications_own()
    {
        // The application's entity is mapped exactly as it was declared: media rows point at it by
        // name and key, and add nothing to it.
        var product = _context.Model.FindEntityType(typeof(Product))!;

        Assert.Equal(["Id", "Name"], product.GetProperties().Select(p => p.Name));
        Assert.Empty(product.GetNavigations());
        Assert.NotNull(_context.Model.FindEntityType(typeof(MediaFile)));
    }

    [Fact]
    public async Task An_upload_and_its_entity_commit_in_one_transaction()
    {
        var product = new Product { Name = "Desk lamp" };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        await using var transaction = await _context.Database.BeginTransactionAsync();

        product.Name = "Desk lamp, brass";
        var file = await _media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "lamp.png"));
        await _context.SaveChangesAsync();

        await transaction.RollbackAsync();

        // Rolling back takes the media row with it — which is the point of the row living in the
        // application's own context rather than a store of its own.
        _context.ChangeTracker.Clear();
        Assert.Null(await _context.MediaFiles.FindAsync(file.Id));
    }

    [Fact]
    public async Task The_key_accessor_reads_a_store_generated_key_after_the_save()
    {
        var product = new Product { Name = "Chair" };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        var file = await _media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var attachment = Assert.Single(await _media.GetAttachmentsAsync(file.Id));
        Assert.Equal("Product", attachment.Owner.EntityName);
        Assert.Equal(
            product.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            attachment.Owner.EntityId);
    }

    [Fact]
    public void The_key_accessor_refuses_an_unsaved_entity()
    {
        Assert.Throws<InvalidOperationException>(() => _media.For(new Product { Name = "Unsaved" }));
    }

    [Fact]
    public async Task The_key_accessor_falls_back_for_a_type_the_model_does_not_contain()
    {
        // A DTO, or an entity from another context. The convention accessor still knows what to do
        // with it, and refusing would fail an attach that works perfectly well.
        var file = await _media.For(new PlainThing { Id = 5 }).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var attachment = Assert.Single(await _media.GetAttachmentsAsync(file.Id));
        Assert.Equal("PlainThing", attachment.Owner.EntityName);
        Assert.Equal("5", attachment.Owner.EntityId);
    }

    [Fact]
    public async Task The_engine_round_trips_through_a_real_database()
    {
        var content = TestMedia.Png(300);
        var file    = await _media.UploadAsync(TestMedia.Upload(content, "shot.png", category: "gallery", tags: ["hero"]));

        _context.ChangeTracker.Clear();

        var read = await _media.GetAsync(file.Id);

        Assert.Equal("shot.png", read.FileName);
        Assert.Equal("gallery", read.Category);
        Assert.Equal(["hero"], read.Tags);
        Assert.Equal(300, read.Size);

        using var opened = await _media.OpenReadAsync(file.Id);
        using var buffer = new MemoryStream();
        await opened.Stream.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
    }

    [Fact]
    public async Task Deleting_an_entity_does_not_cascade_to_its_files()
    {
        var product = new Product { Name = "Going away" };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        await _media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();

        // There is no foreign key — that is what lets one table serve every entity — so the files
        // outlive the entity until the application says otherwise. Documented, and asserted here
        // so the documentation cannot quietly become untrue.
        var orphans = await _media.ListAsync(new MediaQuery { OwnerEntityName = "Product", OwnerEntityId = product.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Single(orphans.Items);

        var deleted = await _media.For(product).DeleteAllAsync();
        Assert.Equal(1, deleted);
        Assert.Empty(_storage.Paths);
    }

    private sealed class PlainThing
    {
        public int Id { get; set; }
    }
}

/// <summary>The local backend under the engine, over a real temporary directory.</summary>
public sealed class FileSystemEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotnetboost-media-provider", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task An_upload_lands_on_disk_at_the_path_the_row_records()
    {
        var clock   = TimeProvider.System;
        var storage = new FileSystemStorageProvider(new FileSystemOptions { RootPath = _root });
        await storage.CheckAsync();

        var media = new MediaManager(
            new InMemoryMediaStore(),
            new MediaStorageProviderRegistry([storage], defaultProvider: null),
            new DateHierarchyKeyGenerator(clock),
            new ConventionEntityKeyAccessor(),
            new MediaOptions(),
            clock,
            NullLogger<MediaManager>.Instance);

        var content = TestMedia.Png(1024);
        var file    = await media.UploadAsync(TestMedia.Upload(content, "on-disk.png", category: "photos"));

        var absolute = Path.Combine(_root, file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(absolute));
        Assert.Equal(content, await File.ReadAllBytesAsync(absolute));

        await media.DeleteAsync(file.Id);
        Assert.False(File.Exists(absolute));
    }
}
