using Amazon.S3;
using Azure.Storage.Blobs;
using DotNetBoost.Media.AmazonS3;
using DotNetBoost.Media.AzureBlobStorage;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using DotNetBoost.Media.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Azurite;
using Testcontainers.LocalStack;

namespace DotNetBoost.Media.IntegrationTests;

/// <summary>
/// The behaviour every <see cref="IMediaStorageProvider"/> has to have, run against a real server
/// for each of them — Azurite for Azure Blob Storage, LocalStack for S3. Mocking a cloud SDK proves
/// only that the mock was written to match the test; these run the same bytes through the same
/// wire protocol the production backend uses.
/// </summary>
public abstract class StorageContractTests : IAsyncLifetime
{
    protected IMediaStorageProvider Storage { get; private set; } = null!;

    protected abstract Task<IMediaStorageProvider> CreateStorageAsync();

    protected virtual Task CleanupAsync() => Task.CompletedTask;

    /// <summary>Whether this backend mints direct URLs, so the URL assertions know what to expect.</summary>
    protected abstract bool MintsUrls { get; }

    public async Task InitializeAsync() => Storage = await CreateStorageAsync();

    public Task DisposeAsync() => CleanupAsync();

    private static MediaWrite Write(byte[] content, string path) =>
        new(new MemoryStream(content, writable: false), path, "image/png", ComputeChecksum: false);

    private static string Path() => $"tests/{Guid.NewGuid():N}.png";

    [Fact]
    public async Task Save_then_OpenRead_round_trips_the_bytes()
    {
        var content = TestMedia.Png(4096);
        var path    = Path();

        var stored = await Storage.SaveAsync(Write(content, path));

        Assert.Equal(path, stored.Path);

        await using var stream = await Storage.OpenReadAsync(stored.Container, stored.Path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(content, buffer.ToArray());
    }

    [Fact]
    public async Task Save_handles_a_file_large_enough_to_go_multipart()
    {
        // Over the block size both backends use, so the SDK switches to a chunked or multipart
        // upload — a different code path, and the one that carries real files.
        var content = TestMedia.Unrecognised(5 * 1024 * 1024);
        var path    = Path();

        await Storage.SaveAsync(Write(content, path));

        await using var stream = await Storage.OpenReadAsync(Storage.Container, path);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(content.Length, buffer.Length);
    }

    /// <summary>
    /// A backend that can only read forward loses HTTP <c>Range</c> silently: ASP.NET Core serves
    /// a partial response only when the stream can report a length, and answers the whole object
    /// with a <c>200</c> when it cannot. So seekability is part of the contract, not an accident
    /// of which SDK is underneath — and it is asserted here rather than at the endpoint, where an
    /// in-memory test double would seek happily and prove nothing about S3.
    /// </summary>
    [Fact]
    public async Task OpenRead_returns_a_stream_a_range_can_be_served_from()
    {
        var content = TestMedia.Unrecognised(64 * 1024);
        var path    = Path();

        await Storage.SaveAsync(Write(content, path));

        await using var stream = await Storage.OpenReadAsync(Storage.Container, path);

        Assert.True(stream.CanSeek);
        Assert.Equal(content.Length, stream.Length);
    }

    /// <summary>
    /// The read pattern ASP.NET Core actually performs for <c>Range: bytes=from-to</c>: measure,
    /// seek, copy exactly the window. Three windows, in an order that exercises a short forward
    /// seek, a long one, and a seek backwards — which on S3 is what forces a fresh ranged request
    /// rather than more of the response already open.
    /// </summary>
    [Fact]
    public async Task OpenRead_serves_any_window_of_the_object()
    {
        // Bytes that differ from one another, so a window landing at the wrong offset cannot pass
        // by looking like the right one.
        var content = new byte[96 * 1024];
        Random.Shared.NextBytes(content);

        var path = Path();
        await Storage.SaveAsync(Write(content, path));

        await using var stream = await Storage.OpenReadAsync(Storage.Container, path);

        foreach (var (from, length) in new[] { (10, 100), (80 * 1024, 4096), (32, 64) })
        {
            stream.Seek(from, SeekOrigin.Begin);

            var window = new byte[length];
            await stream.ReadExactlyAsync(window);

            Assert.Equal(content[from..(from + length)], window);
        }

        // Past the end reads as an end of stream rather than an error, which is what a stream
        // contract promises and what a copy loop relies on.
        stream.Seek(0, SeekOrigin.End);
        Assert.Equal(0, await stream.ReadAsync(new byte[16]));
    }

    [Fact]
    public async Task OpenRead_reports_a_path_with_nothing_at_it() =>
        await Assert.ThrowsAsync<MediaContentMissingException>(
            () => Storage.OpenReadAsync(Storage.Container, "tests/not-there.png"));

    [Fact]
    public async Task Exists_answers_both_ways()
    {
        var path = Path();
        await Storage.SaveAsync(Write(TestMedia.Png(), path));

        Assert.True(await Storage.ExistsAsync(Storage.Container, path));
        Assert.False(await Storage.ExistsAsync(Storage.Container, "tests/never-written.png"));
    }

    [Fact]
    public async Task Delete_removes_the_object_and_says_whether_it_did()
    {
        var path = Path();
        await Storage.SaveAsync(Write(TestMedia.Png(), path));

        Assert.True(await Storage.DeleteAsync(Storage.Container, path));
        Assert.False(await Storage.DeleteAsync(Storage.Container, path));
        Assert.False(await Storage.ExistsAsync(Storage.Container, path));
    }

    [Fact]
    public async Task A_traversing_path_is_refused_before_it_reaches_the_service() =>
        await Assert.ThrowsAsync<InvalidMediaException>(
            () => Storage.SaveAsync(Write(TestMedia.Png(), "../escaped.png")));

    [Fact]
    public async Task GetUrl_hands_out_a_link_a_client_can_actually_fetch()
    {
        var content = TestMedia.Png(128);
        var path    = Path();
        await Storage.SaveAsync(Write(content, path));

        var url = await Storage.GetUrlAsync(Storage.Container, path, new MediaUrlOptions { ExpiresIn = TimeSpan.FromMinutes(5) });

        if (!MintsUrls)
        {
            Assert.Null(url);
            return;
        }

        Assert.NotNull(url);

        // The point of a signed URL is that an unauthenticated client can use it. Anything less
        // and the application may as well have streamed the bytes itself.
        using var anonymous = new HttpClient();
        var response = await anonymous.GetAsync(url);

        response.EnsureSuccessStatusCode();
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Check_passes_against_a_reachable_container() => await ((IMediaStorageHealthCheck)Storage).CheckAsync();
}

/// <summary>The contract, against Azurite — Microsoft's own Blob Storage emulator.</summary>
public sealed class AzureBlobStorageTests : StorageContractTests, IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();

    protected override bool MintsUrls => true;

    protected override async Task<IMediaStorageProvider> CreateStorageAsync()
    {
        await _container.StartAsync();

        var provider = new AzureBlobStorageProvider(
            new BlobServiceClient(_container.GetConnectionString()),
            new AzureBlobOptions { ContainerName = "media" });

        await provider.CheckAsync();
        return provider;
    }

    protected override Task CleanupAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// The contract, against LocalStack — an S3-compatible server. LocalStack rather than MinIO
/// because MinIO stopped publishing an anonymously pullable image: both <c>minio/minio</c> on
/// Docker Hub and <c>quay.io/minio/minio</c> answer 401 without credentials, so a MinIO-based
/// suite fails at the pull on any machine not logged in to them, CI included.
/// </summary>
public sealed class AmazonS3StorageTests : StorageContractTests, IAsyncLifetime
{
    private const string Bucket = "media";

    private readonly LocalStackContainer _container = new LocalStackBuilder("localstack/localstack:4").Build();
    private AmazonS3Client? _client;

    protected override bool MintsUrls => true;

    protected override async Task<IMediaStorageProvider> CreateStorageAsync()
    {
        await _container.StartAsync();

        _client = new AmazonS3Client(
            // LocalStack verifies nothing beyond the request being signed, so any non-empty pair
            // works; these are the ones its own documentation uses. Unlike MinIO's module, the
            // LocalStack one exposes no keys of its own to read.
            new Amazon.Runtime.BasicAWSCredentials("test", "test"),
            new AmazonS3Config
            {
                ServiceURL     = _container.GetConnectionString(),
                // LocalStack serves buckets as a path, not a subdomain, so virtual-hosted-style
                // addressing would resolve to a host that does not exist.
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1"
            });

        // The S3 backend deliberately never creates a bucket, so the test has to.
        await _client.PutBucketAsync(Bucket);

        var provider = new AmazonS3StorageProvider(_client, new AmazonS3Options { BucketName = Bucket });
        await provider.CheckAsync();
        return provider;
    }

    protected override async Task CleanupAsync()
    {
        _client?.Dispose();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// The whole library end to end: a real database for the rows and a real object store for the
/// bytes, with the engine in between. Everything else tests one half.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class EndToEndTests(PostgreSqlFixture postgres) : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
    private IntegrationContext? _context;
    private IMediaManager _media = null!;

    public async Task InitializeAsync()
    {
        await _azurite.StartAsync();

        // Its own database, for the reason PostgreSqlEfCoreTests uses one — and a separate one
        // from that suite's, whose per-test TRUNCATE would otherwise carry off this row.
        var connectionString = await postgres.CreateDatabaseAsync("media_end_to_end");

        _context = new IntegrationContext(new DbContextOptionsBuilder<IntegrationContext>()
            .UseNpgsql(connectionString)
            .Options);

        await _context.Database.EnsureCreatedAsync();

        var storage = new AzureBlobStorageProvider(
            new BlobServiceClient(_azurite.GetConnectionString()),
            new AzureBlobOptions { ContainerName = "end-to-end" });

        await storage.CheckAsync();

        var clock = TimeProvider.System;

        _media = new MediaManager(
            new EntityFrameworkCore.EfCoreMediaStore(_context),
            new MediaStorageProviderRegistry([storage], defaultProvider: null),
            new DateHierarchyKeyGenerator(clock),
            new ConventionEntityKeyAccessor(),
            MediaRoleRegistry.Empty,
            new MediaOptions(),
            clock,
            [],
            NullLogger<MediaManager>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();

        await _azurite.DisposeAsync();
    }

    [Fact]
    public async Task A_file_uploaded_to_Azure_is_recorded_in_PostgreSQL_and_reads_back()
    {
        var content = TestMedia.Png(2048);

        var file = await _media.UploadAsync(TestMedia.Upload(content, "end-to-end.png", category: "photos", tags: ["real"]));

        Assert.Equal("AzureBlobStorage", file.StorageProvider);
        Assert.Equal(2048, file.Size);
        Assert.NotNull(file.Checksum);

        _context!.ChangeTracker.Clear();

        var read = await _media.GetAsync(file.Id);
        Assert.Equal("end-to-end.png", read.FileName);
        Assert.Equal(["real"], read.Tags);
        Assert.Equal(DateTimeKind.Utc, read.CreatedAt.Kind);

        using var opened = await _media.OpenReadAsync(file.Id);
        using var buffer = new MemoryStream();
        await opened.Stream.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());

        var url = await _media.GetUrlAsync(file.Id);
        Assert.NotNull(url);

        await _media.DeleteAsync(file.Id);
        Assert.Null(await _media.FindAsync(file.Id));
    }
}
