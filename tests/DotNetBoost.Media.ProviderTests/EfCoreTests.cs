using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
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
            MediaRoleRegistry.Empty,
            new MediaOptions(),
            clock,
            [],
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
        Assert.Null(await _context.Set<MediaFile>().FindAsync(file.Id));
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
        Assert.Single(deleted);
        Assert.Empty(_storage.Paths);
    }

    /// <summary>
    /// The one media question a known set of ids cannot answer: which owners have media at all,
    /// which has to be asked before paging rather than after.
    /// </summary>
    // p.Id.ToString() inside these queries is deliberate, and the parameterless form is the only
    // one EF Core can translate: it becomes a cast in SQL, done by the engine, so there is no
    // client culture involved for CA1305 to be right about. Calling the invariant overload instead
    // would fail to translate and silently evaluate the whole products table in memory.
#pragma warning disable CA1305
    [Fact]
    public async Task OwnersWithMedia_composes_into_a_query_over_the_applications_own_table()
    {
        var withImage    = new Product { Name = "Lamp" };
        var withoutImage = new Product { Name = "Chair" };

        _context.Products.AddRange(withImage, withoutImage);
        await _context.SaveChangesAsync();

        await _media.For(withImage).UploadAsync(TestMedia.Upload(TestMedia.Png(), "lamp.png"));

        var owners = _context.OwnersWithMedia<Product>();

        // Composed into the products query and run as one statement — no ToList in between, which
        // is the whole point: filtering before paging cannot be done on a materialised list.
        var names = await _context.Products
            .Where(p => owners.Contains(p.Id.ToString()))
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync();

        Assert.Equal(["Lamp"], names);

        // And negated, which is the other half of the screen it exists for.
        var missing = await _context.Products
            .Where(p => !owners.Contains(p.Id.ToString()))
            .Select(p => p.Name)
            .ToListAsync();

        Assert.Equal(["Chair"], missing);
    }

    [Fact]
    public async Task OwnersWithMedia_can_narrow_to_one_role()
    {
        var hero    = new Product { Name = "Lamp" };
        var gallery = new Product { Name = "Chair" };

        _context.Products.AddRange(hero, gallery);
        await _context.SaveChangesAsync();

        var one = await _media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var two = await _media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        await _media.For(hero).SetAsync("primary", one.Id);
        await _media.For(gallery).AddAsync(two.Id, "gallery");

        var primaries = await _context.Products
            .Where(p => _context.OwnersWithMedia<Product>("primary").Contains(p.Id.ToString()))
            .Select(p => p.Name)
            .ToListAsync();

        Assert.Equal(["Lamp"], primaries);
    }

#pragma warning restore CA1305

    [Fact]
    public async Task OwnersWithMedia_normalises_the_role_it_is_given()
    {
        var product = new Product { Name = "Lamp" };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        var file = await _media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await _media.For(product).SetAsync("primary", file.Id);

        // Roles are lower-cased on write, so a filter that was not would silently match nothing —
        // and nothing is indistinguishable from "no product has one".
        Assert.NotEmpty(await _context.OwnersWithMedia<Product>("Primary").ToListAsync());
    }

    [Fact]
    public async Task OwnersWithMedia_names_an_owner_once_however_many_files_it_holds()
    {
        var product = new Product { Name = "Lamp" };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        await _media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        await _media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        // Distinct, so composing it with Contains does not multiply the outer query's rows.
        Assert.Single(await _context.OwnersWithMedia<Product>().ToListAsync());
    }

    /// <summary>
    /// The bulk read through the real store and the real engine, where the one query and the join
    /// inside it are the provider's to get right.
    /// </summary>
    [Fact]
    public async Task ForMany_reads_a_page_of_products_in_one_query()
    {
        var products = Enumerable.Range(1, 20).Select(i => new Product { Name = "P" + i }).ToArray();

        _context.Products.AddRange(products);
        await _context.SaveChangesAsync();

        foreach (var product in products)
        {
            await _media.For(product)
                .UploadAsync(TestMedia.Upload(TestMedia.Png(), product.Name + ".png"));
        }

        var heroes = await _media.ForMany(products).GetByRoleAsync(MediaRoles.Default);

        Assert.Equal(20, heroes.Count);
        Assert.All(products, p => Assert.NotNull(heroes[p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
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
            MediaRoleRegistry.Empty,
            new MediaOptions(),
            clock,
            [],
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
