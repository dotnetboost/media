using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Services;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// What a files-deleted handler's failure leaves behind, and what the engine says about its
/// handlers. A handler's failure is swallowed by design, so these signals are the only way anyone
/// finds out that an application's records about deleted files have gone stale.
/// </summary>
public class DeleteHandlerObservabilityTests
{
    [MediaEntity("ObservedProduct")]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    private const int HandlerFailedEvent = 7004;

    [Fact]
    public async Task A_failed_handler_logs_the_ids_it_missed()
    {
        var logs    = new CapturingLoggerProvider();
        using var factory = logs.CreateFactory();

        var harness = Harness.Create(
            deletedHandlers: [new RecordingDeletedHandler("broken", fail: true)],
            logger: factory.CreateLogger<MediaManager>());

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        Assert.True(await harness.Media.DeleteAsync(file.Id));

        var entry = Assert.Single(logs.Entries, e => e.EventId.Id == HandlerFailedEvent);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Equal(nameof(RecordingDeletedHandler), entry.Properties["Handler"]);
        Assert.Equal(1, entry.Properties["Count"]);
        Assert.Equal([file.Id], Assert.IsAssignableFrom<IEnumerable<Guid>>(entry.Properties["MediaIds"]));

        // And in the text, for a log sink that keeps only the message.
        Assert.Contains(file.Id.ToString(), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_large_batch_logs_the_first_ids_and_the_total()
    {
        var logs    = new CapturingLoggerProvider();
        using var factory = logs.CreateFactory();

        var harness = Harness.Create(
            deletedHandlers: [new RecordingDeletedHandler("broken", fail: true)],
            logger: factory.CreateLogger<MediaManager>());

        var product = new Product { Id = 1 };
        var count   = MediaManager.LoggedIdLimit + 5;

        for (var i = 0; i < count; i++)
            await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(16 + i), $"f{i}.png"));

        var deleted = await harness.Media.For(product).DeleteAllAsync();
        Assert.Equal(count, deleted.Count);

        var entry = Assert.Single(logs.Entries, e => e.EventId.Id == HandlerFailedEvent);
        var ids   = Assert.IsAssignableFrom<IEnumerable<Guid>>(entry.Properties["MediaIds"]).ToArray();

        Assert.Equal(count, entry.Properties["Count"]);
        Assert.Equal(MediaManager.LoggedIdLimit, ids.Length);
        Assert.Subset(deleted.ToHashSet(), ids.ToHashSet());
    }

    [Fact]
    public async Task A_failed_handler_counts_once_tagged_with_its_name_and_the_next_handler_still_runs()
    {
        using var meters = new RecordingMeterFactory();
        using var metrics = new MediaMetrics(meters);

        var after   = new RecordingDeletedHandler("after");
        var harness = Harness.Create(
            deletedHandlers: [new RecordingDeletedHandler("broken", fail: true), after],
            metrics: metrics);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        Assert.True(await harness.Media.DeleteAsync(file.Id));

        var (instrument, value, tags) = Assert.Single(meters.Measurements);
        Assert.Equal(MediaTelemetry.DeletedHandlerFailures, instrument);
        Assert.Equal(1, value);
        Assert.Equal(nameof(RecordingDeletedHandler), tags[MediaTelemetry.TagHandler]);

        Assert.Equal([file.Id], Assert.Single(after.Calls));
    }

    [Fact]
    public async Task A_handler_that_succeeds_counts_nothing()
    {
        using var meters  = new RecordingMeterFactory();
        using var metrics = new MediaMetrics(meters);

        var harness = Harness.Create(deletedHandlers: [new RecordingDeletedHandler()], metrics: metrics);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        Assert.Empty(meters.Measurements);
    }

    /// <summary>
    /// The delete has happened by the time handlers run, so the caller giving up — a client
    /// disconnecting from the REST endpoint — must not cancel the cleanup. The in-memory store
    /// ignores the token, which stands in for a cancellation that arrives after the store committed.
    /// </summary>
    [Fact]
    public async Task Handlers_are_not_given_the_callers_token()
    {
        var handler = new TokenRecordingHandler();
        var harness = Harness.Create(deletedHandlers: [handler]);
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.True(await harness.Media.DeleteAsync(file.Id, cancelled.Token));

        var token = Assert.Single(handler.Tokens);
        Assert.False(token.CanBeCanceled);
    }

    [Fact]
    public async Task DeleteAll_failing_part_way_still_reports_the_files_already_deleted()
    {
        var inner    = new InMemoryMediaStore();
        var storage  = new InMemoryStorageProvider();
        var recorder = new RecordingDeletedHandler();
        var product  = new Product { Id = 1 };

        // Upload and attach through a healthy engine over the same rows.
        var setup = Engine(inner, storage, []);
        var one   = await setup.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var two   = await setup.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        // The second re-read throws, as a cancelled request or a dropped connection would: the
        // first file is gone by then, the second is not.
        var failing = Engine(new FailingFindStore(inner, failOnCall: 2, new OperationCanceledException()), storage, [recorder]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => failing.For(product).DeleteAllAsync());

        var reported = Assert.Single(recorder.Calls);
        var gone     = Assert.Single(reported);

        Assert.Contains(gone, new[] { one.Id, two.Id });
        Assert.Null(await setup.FindAsync(gone));

        // The one not reported is the one still there, which a retry of the delete reports then.
        var survivor = gone == one.Id ? two.Id : one.Id;
        Assert.NotNull(await setup.FindAsync(survivor));
    }

    [Fact]
    public void Inline_handlers_are_named_by_the_name_given_else_by_where_they_were_written()
    {
        var report = BuildReport(media => media
            .OnFilesDeleted((_, _, _) => Task.CompletedTask, name: "alt-text")
            .OnFilesDeleted((_, _, _) => Task.CompletedTask)
            .OnFilesDeleted((_, _, _) => Task.CompletedTask));

        Assert.Equal(
            [
                "alt-text",
                $"inline handler #2 in {nameof(DeleteHandlerObservabilityTests)}",
                $"inline handler #3 in {nameof(DeleteHandlerObservabilityTests)}"
            ],
            report.Handlers);
    }

    [Fact]
    public async Task A_failing_inline_handler_is_logged_and_counted_under_its_own_name()
    {
        var logs = new CapturingLoggerProvider();
        using var meters = new RecordingMeterFactory();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => logs.CreateFactory());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory>(meters);

        services.AddMedia()
            .UseCustomStore<InMemoryMediaStore>()
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider(), isDefault: true)
            .DisableStartupValidation()
            .OnFilesDeleted((_, _, _) => throw new InvalidOperationException("alt text table is gone"), name: "alt-text")
            .OnFilesDeleted((_, _, _) => throw new InvalidOperationException("index is down"), name: "search-index")
            .Build();

        await using var provider = services.BuildServiceProvider();
        await using var scope    = provider.CreateAsyncScope();

        var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();
        var file  = await media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await media.DeleteAsync(file.Id);

        Assert.Equal(
            ["alt-text", "search-index"],
            logs.Entries.Where(e => e.EventId.Id == HandlerFailedEvent).Select(e => (string?)e.Properties["Handler"]));

        Assert.Equal(
            ["alt-text", "search-index"],
            meters.Measurements.Select(m => (string?)m.Tags[MediaTelemetry.TagHandler]));
    }

    [Fact]
    public async Task The_startup_line_names_every_handler_however_it_was_registered()
    {
        var logs = new CapturingLoggerProvider();

        var report = BuildReport(media =>
        {
            media.OnFilesDeleted<RecordingTypedHandler>()
                 .OnFilesDeleted((_, _, _) => Task.CompletedTask, name: "search-index");

            // Registered straight on the container rather than through the builder.
            media.Services.AddScoped<IMediaFilesDeletedHandler, DirectlyRegisteredHandler>();
        }, logs);

        Assert.Equal(
            [nameof(RecordingTypedHandler), "search-index", nameof(DirectlyRegisteredHandler)],
            report.Handlers);

        await report.StartAsync(CancellationToken.None);

        var entry = Assert.Single(logs.Entries, e => e.EventId.Id == 7101);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal(3, entry.Properties["Count"]);
        Assert.Contains("RecordingTypedHandler, search-index, DirectlyRegisteredHandler", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_startup_line_says_when_there_are_no_handlers()
    {
        var logs   = new CapturingLoggerProvider();
        var report = BuildReport(_ => { }, logs);

        Assert.Empty(report.Handlers);

        await report.StartAsync(CancellationToken.None);

        var entry = Assert.Single(logs.Entries, e => e.EventId.Id == 7102);
        Assert.Equal(LogLevel.Debug, entry.Level);
    }

    /// <summary>Builds a container and returns the startup report <c>Build()</c> registered.</summary>
    private static MediaDeletedHandlersReport BuildReport(
        Action<MediaBuilder> configure, CapturingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();

        if (logs is not null)
        {
            services.AddSingleton<ILoggerFactory>(_ => logs.CreateFactory());
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        }

        var media = services.AddMedia()
            .UseCustomStore<InMemoryMediaStore>()
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider(), isDefault: true)
            .DisableStartupValidation();

        configure(media);
        media.Build();

        var provider = services.BuildServiceProvider();
        return Assert.Single(provider.GetServices<IHostedService>().OfType<MediaDeletedHandlersReport>());
    }

    private static MediaManager Engine(
        IMediaStore store, InMemoryStorageProvider storage, IEnumerable<IMediaFilesDeletedHandler> handlers)
    {
        var clock = TimeProvider.System;

        return new MediaManager(
            store,
            new MediaStorageProviderRegistry([storage], defaultProvider: null),
            new DateHierarchyKeyGenerator(clock),
            new ConventionEntityKeyAccessor(),
            MediaRoleRegistry.Empty,
            new MediaOptions(),
            clock,
            handlers,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaManager>.Instance);
    }

    private sealed class RecordingTypedHandler : IMediaFilesDeletedHandler
    {
        public Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class DirectlyRegisteredHandler : IMediaFilesDeletedHandler
    {
        public Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
