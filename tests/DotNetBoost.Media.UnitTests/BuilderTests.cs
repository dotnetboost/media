using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Services;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Media.UnitTests;

public class BuilderTests
{
    private static MediaBuilder Configured(IServiceCollection services) =>
        services.AddMedia().UseCustomStore(_ => new InMemoryMediaStore());

    [Fact]
    public void Build_refuses_a_configuration_with_no_store()
    {
        var builder = new ServiceCollection().AddMedia()
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider());

        var error = Assert.Throws<InvalidOperationException>(builder.Build);

        Assert.Contains("No media metadata store configured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_refuses_a_configuration_with_no_backend()
    {
        var builder = Configured(new ServiceCollection());

        var error = Assert.Throws<InvalidOperationException>(builder.Build);

        Assert.Contains("No media storage backend configured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_stores_cannot_be_configured()
    {
        var builder = Configured(new ServiceCollection());

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.UseCustomStore(_ => new InMemoryMediaStore()));

        Assert.Contains("already configured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_backends_may_be_configured()
    {
        var services = new ServiceCollection();

        Configured(services)
            .UseCustomStorage("A", _ => new InMemoryStorageProvider("A"))
            .UseCustomStorage("B", _ => new InMemoryStorageProvider("B"))
            .Build();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IMediaStorageProviderRegistry>();

        Assert.Equal(2, registry.All.Count);
        // No backend claimed the default, so the first registered wins.
        Assert.Equal("A", registry.Default.Name);
    }

    [Fact]
    public void The_backend_marked_default_receives_uploads()
    {
        var services = new ServiceCollection();

        Configured(services)
            .UseCustomStorage("A", _ => new InMemoryStorageProvider("A"))
            .UseCustomStorage("B", _ => new InMemoryStorageProvider("B"), isDefault: true)
            .Build();

        using var provider = services.BuildServiceProvider();

        Assert.Equal("B", provider.GetRequiredService<IMediaStorageProviderRegistry>().Default.Name);
    }

    [Fact]
    public void Two_backends_cannot_both_be_the_default()
    {
        var builder = Configured(new ServiceCollection())
            .UseCustomStorage("A", _ => new InMemoryStorageProvider("A"), isDefault: true);

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.UseCustomStorage("B", _ => new InMemoryStorageProvider("B"), isDefault: true));

        Assert.Contains("Exactly one backend", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_backends_cannot_share_a_name()
    {
        var builder = Configured(new ServiceCollection())
            .UseCustomStorage("A", _ => new InMemoryStorageProvider("A"));

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.UseCustomStorage("A", _ => new InMemoryStorageProvider("A")));

        Assert.Contains("already configured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deduplication_cannot_be_turned_on_without_checksums()
    {
        var builder = Configured(new ServiceCollection()).DisableChecksums();

        var error = Assert.Throws<InvalidOperationException>(() => builder.DeduplicateByChecksum());

        Assert.Contains("needs checksums", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_methods_configure_the_options()
    {
        var services = new ServiceCollection();

        Configured(services)
            .WithMaxFileSize(1234)
            .AllowContentTypes("image/*", "application/pdf")
            .AllowExtensions("png", ".pdf")
            .BlockExtensions(".svg")
            .WithDefaultUrlExpiry(TimeSpan.FromMinutes(5))
            .DisableSoftDelete()
            .UseCustomStorage("A", _ => new InMemoryStorageProvider("A"))
            .Build();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MediaOptions>();

        Assert.Equal(1234, options.MaxFileSizeBytes);
        Assert.Contains("image/*", options.AllowedContentTypes);
        // A leading dot is added when the caller leaves it off.
        Assert.Contains(".png", options.AllowedExtensions);
        Assert.Contains(".pdf", options.AllowedExtensions);
        Assert.Contains(".svg", options.BlockedExtensions);
        Assert.Equal(TimeSpan.FromMinutes(5), options.DefaultUrlExpiry);
        Assert.False(options.SoftDelete);
    }

    [Fact]
    public void The_engine_resolves_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Configured(services)
            .UseCustomStorage("A", _ => new InMemoryStorageProvider("A"))
            .DisableStartupValidation()
            .Build();

        using var provider = services.BuildServiceProvider();
        using var scope    = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMediaManager>());
    }

    [Fact]
    public void A_registry_with_no_backend_says_so()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new MediaStorageProviderRegistry([], defaultProvider: null));

        Assert.Contains("No media storage backend", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_naming_an_unregistered_backend_is_reported_clearly()
    {
        var registry = new MediaStorageProviderRegistry([new InMemoryStorageProvider("A")], defaultProvider: null);

        var error = Assert.Throws<MediaStorageException>(() => registry.Get("Gone"));

        Assert.Contains("Keep the backend registered", error.Message, StringComparison.Ordinal);
        Assert.Equal("Gone", error.StorageProvider);
    }

    [Fact]
    public void An_unknown_default_backend_is_reported_clearly()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new MediaStorageProviderRegistry([new InMemoryStorageProvider("A")], defaultProvider: "B"));

        Assert.Contains("no backend by that name is registered", error.Message, StringComparison.Ordinal);
    }
}

public class OptionsTests
{
    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("application/pdf", true)]
    [InlineData("text/plain", false)]
    public void IsContentTypeAllowed_understands_families(string contentType, bool expected)
    {
        var options = new MediaOptions();
        options.AllowedContentTypes.Add("image/*");
        options.AllowedContentTypes.Add("application/pdf");

        Assert.Equal(expected, options.IsContentTypeAllowed(contentType));
    }

    [Fact]
    public void An_empty_allow_list_allows_everything() =>
        Assert.True(new MediaOptions().IsContentTypeAllowed("anything/at-all"));

    [Fact]
    public void Executables_are_blocked_out_of_the_box()
    {
        var options = new MediaOptions();

        Assert.Contains(".exe", options.BlockedExtensions);
        Assert.Contains(".sh", options.BlockedExtensions);
        Assert.Contains(".ps1", options.BlockedExtensions);
    }

    [Fact]
    public void A_non_positive_size_limit_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaOptions { MaxFileSizeBytes = 0 });

    [Fact]
    public void A_non_positive_url_expiry_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaOptions { DefaultUrlExpiry = TimeSpan.Zero });
}
