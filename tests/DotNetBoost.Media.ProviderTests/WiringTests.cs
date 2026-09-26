using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.Media.ProviderTests;

/// <summary>
/// The library assembled the way the README says to assemble it — through the container, with the
/// EF Core store and the local-disk backend — rather than by constructing the engine by hand. The
/// other tests would all still pass if <c>AddMedia()</c> registered the wrong lifetimes.
/// </summary>
public sealed class WiringTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotnetboost-media-wiring", Guid.NewGuid().ToString("N"));
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddDbContext<MediaTestContext>(o => o.UseSqlite(_connection));

        services.AddMedia(o => o.MaxFileSizeBytes = 1024 * 1024)
            .UseEntityFrameworkCore<MediaTestContext>()
            .UseFileSystem(_root)
            .AllowContentTypes("image/*")
            .Build();

        _services = services.BuildServiceProvider(validateScopes: true);

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MediaTestContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task The_startup_check_runs_and_creates_the_media_directory()
    {
        foreach (var hosted in _services.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void The_engine_is_scoped_and_its_backends_are_singletons()
    {
        // validateScopes: true on the provider means resolving a scoped service from the root
        // would already have thrown. This asserts the intent directly.
        using var first  = _services.CreateScope();
        using var second = _services.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IMediaManager>(),
            second.ServiceProvider.GetRequiredService<IMediaManager>());

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IMediaStorageProviderRegistry>(),
            second.ServiceProvider.GetRequiredService<IMediaStorageProviderRegistry>());
    }

    [Fact]
    public async Task A_file_uploaded_in_one_scope_is_readable_in_the_next()
    {
        Guid id;
        var content = TestMedia.Png(2048);

        using (var scope = _services.CreateScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();
            id = (await media.UploadAsync(TestMedia.Upload(content, "scoped.png"))).Id;
        }

        using (var scope = _services.CreateScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();

            var file = await media.GetAsync(id);
            Assert.Equal("scoped.png", file.FileName);
            Assert.Equal("FileSystem", file.StorageProvider);

            using var opened = await media.OpenReadAsync(id);
            using var buffer = new MemoryStream();
            await opened.Stream.CopyToAsync(buffer);
            Assert.Equal(content, buffer.ToArray());
        }
    }

    [Fact]
    public async Task The_options_configured_on_the_builder_are_the_ones_in_force()
    {
        using var scope = _services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();

        await Assert.ThrowsAsync<Core.UnsupportedMediaTypeException>(
            () => media.UploadAsync(TestMedia.Upload(TestMedia.Unrecognised(), "notes.txt", "text/plain")));

        await Assert.ThrowsAsync<Core.MediaTooLargeException>(
            () => media.UploadAsync(TestMedia.Upload(TestMedia.Png(2 * 1024 * 1024))));
    }
}
