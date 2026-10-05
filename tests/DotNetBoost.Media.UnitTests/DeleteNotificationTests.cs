using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// <c>IMediaFilesDeletedHandler</c>: the one notification out of this library, so an application
/// can clean up records it keeps about a file — alt text, a search entry — wherever the delete came
/// from, including the REST endpoint no application code runs on.
/// </summary>
public class DeleteNotificationTests
{
    [MediaEntity("NotifiedProduct")]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    [Fact]
    public async Task Deleting_a_file_tells_the_handler_which_file_went()
    {
        var recorder = new RecordingDeletedHandler();
        var harness  = Harness.Create(deletedHandlers: [recorder]);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        Assert.Equal([file.Id], Assert.Single(recorder.Calls));
    }

    [Fact]
    public async Task Soft_delete_is_still_a_delete_as_far_as_the_handler_is_concerned()
    {
        // The bytes are gone either way, and the row that survives is a record that the file
        // existed rather than a way to get it back. An application's alt text for it is as stale
        // under soft delete as under hard.
        var recorder = new RecordingDeletedHandler();
        var harness  = Harness.Create(o => o.SoftDelete = true, deletedHandlers: [recorder]);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        Assert.Equal([file.Id], Assert.Single(recorder.Calls));
    }

    [Fact]
    public async Task Deleting_a_file_that_was_already_gone_tells_nobody()
    {
        var recorder = new RecordingDeletedHandler();
        var harness  = Harness.Create(deletedHandlers: [recorder]);

        // Nothing was destroyed, so there is nothing for a handler to act on — and a notification
        // here would have it clearing records some earlier delete already reported.
        Assert.False(await harness.Media.DeleteAsync(Guid.NewGuid()));
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task DeleteAll_tells_the_handler_the_batch_in_one_call()
    {
        var recorder = new RecordingDeletedHandler();
        var harness  = Harness.Create(deletedHandlers: [recorder]);
        var product  = new Product { Id = 1 };

        var one = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var two = await harness.Media.For(product).UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        await harness.Media.For(product).DeleteAllAsync();

        // One notification holding both, not one per file: a handler clearing its own table wants
        // a single statement, which is the whole reason the batch is passed as a batch.
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(new[] { one.Id, two.Id }.Order(), call.Order());
    }

    /// <summary>
    /// The distinction that makes the notification safe to act on: a file another entity still
    /// holds was only detached, and a handler told about it would delete records belonging to a
    /// file that is still in use.
    /// </summary>
    [Fact]
    public async Task DeleteAll_does_not_report_the_files_it_only_detached()
    {
        var recorder = new RecordingDeletedHandler();
        var harness  = Harness.Create(deletedHandlers: [recorder]);

        var shared = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "shared.png"));
        var mine   = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "mine.png"));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(shared.Id);
        await harness.Media.For(new Product { Id = 1 }).AddAsync(mine.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(shared.Id);

        var deleted = await harness.Media.For(new Product { Id = 1 }).DeleteAllAsync();

        Assert.Equal([mine.Id], Assert.Single(recorder.Calls));

        // And the return value says the same thing, so a caller that would rather handle it inline
        // gets the same list.
        Assert.Equal([mine.Id], deleted);
    }

    [Fact]
    public async Task DeleteAll_with_nothing_deleted_tells_nobody()
    {
        var recorder = new RecordingDeletedHandler();
        var harness  = Harness.Create(deletedHandlers: [recorder]);

        var shared = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.For(new Product { Id = 1 }).AddAsync(shared.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(shared.Id);

        // Everything this entity held is held by somebody else, so nothing was destroyed.
        Assert.Empty(await harness.Media.For(new Product { Id = 1 }).DeleteAllAsync());
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task Handlers_run_in_registration_order()
    {
        var order = new List<string>();

        var harness = Harness.Create(deletedHandlers:
        [
            new OrderingHandler("first", order),
            new OrderingHandler("second", order),
            new OrderingHandler("third", order)
        ]);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        Assert.Equal(["first", "second", "third"], order);
    }

    /// <summary>
    /// A handler that throws must not turn a completed delete into a failure. The bytes are already
    /// gone — a retry cannot undo them, and a caller told the delete failed would try anyway.
    /// </summary>
    [Fact]
    public async Task A_failing_handler_does_not_break_the_delete_or_the_handlers_after_it()
    {
        var after   = new RecordingDeletedHandler("after");
        var harness = Harness.Create(deletedHandlers: [new RecordingDeletedHandler("broken", fail: true), after]);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.True(await harness.Media.DeleteAsync(file.Id));

        // The file really went, and the second handler still heard about it.
        Assert.Null(await harness.Media.FindAsync(file.Id));
        Assert.Equal([file.Id], Assert.Single(after.Calls));
    }

    [Fact]
    public async Task An_engine_with_no_handlers_deletes_exactly_as_before()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.True(await harness.Media.DeleteAsync(file.Id));
        Assert.Empty(harness.Storage.Paths);
    }

    [Fact]
    public void OnFilesDeleted_registers_a_handler_type()
    {
        var services = new ServiceCollection();

        services.AddMedia()
            .UseCustomStore<InMemoryMediaStore>()
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider())
            .OnFilesDeleted<RecordedHandler>()
            .Build();

        using var provider = services.BuildServiceProvider();
        using var scope    = provider.CreateScope();

        Assert.IsType<RecordedHandler>(
            Assert.Single(scope.ServiceProvider.GetServices<IMediaFilesDeletedHandler>()));
    }

    [Fact]
    public void OnFilesDeleted_keeps_every_handler_registered_rather_than_the_first()
    {
        var services = new ServiceCollection();

        services.AddMedia()
            .UseCustomStore<InMemoryMediaStore>()
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider())
            .OnFilesDeleted<RecordedHandler>()
            .OnFilesDeleted<SecondHandler>()
            .Build();

        using var provider = services.BuildServiceProvider();
        using var scope    = provider.CreateScope();

        // Several handlers is the normal case: one for alt text, one for the search index. A
        // TryAdd here would have silently kept only the first.
        Assert.Equal(
            [typeof(RecordedHandler), typeof(SecondHandler)],
            scope.ServiceProvider.GetServices<IMediaFilesDeletedHandler>().Select(h => h.GetType()));
    }

    [Fact]
    public async Task An_inline_handler_is_given_the_deletes_own_scope()
    {
        var services = new ServiceCollection();
        var seen     = new List<Guid>();

        services.AddSingleton(seen);
        services.AddLogging();

        services.AddMedia()
            .UseCustomStore<InMemoryMediaStore>()
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider(), isDefault: true)
            .DisableStartupValidation()
            .OnFilesDeleted((sp, ids, _) =>
            {
                // Resolved from the scope the delete ran in, which is what lets a one-line handler
                // reach the application's own unit of work.
                sp.GetRequiredService<List<Guid>>().AddRange(ids);
                return Task.CompletedTask;
            })
            .Build();

        using var provider = services.BuildServiceProvider();
        using var scope    = provider.CreateScope();

        var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();
        var file  = await media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await media.DeleteAsync(file.Id);

        Assert.Equal([file.Id], seen);
    }

    private sealed class RecordedHandler : IMediaFilesDeletedHandler
    {
        public Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class SecondHandler : IMediaFilesDeletedHandler
    {
        public Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Records only that it ran, and when relative to the others.</summary>
    private sealed class OrderingHandler(string name, List<string> order) : IMediaFilesDeletedHandler
    {
        public Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default)
        {
            order.Add(name);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// <c>WhichAreSharedAsync</c>: the sole-holder test <c>DeleteAllAsync</c> makes, asked before
/// acting rather than read back afterwards.
/// </summary>
public class SharedFileTests
{
    [MediaEntity("SharedProduct")]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    [Fact]
    public async Task It_names_the_files_another_entity_still_holds()
    {
        var harness = Harness.Create();

        var shared = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "shared.png"));
        var mine   = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "mine.png"));

        await harness.Media.For(new Product { Id = 1 }).AddAsync(shared.Id);
        await harness.Media.For(new Product { Id = 1 }).AddAsync(mine.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(shared.Id);

        var found = await harness.Media.For(new Product { Id = 1 }).WhichAreSharedAsync([shared.Id, mine.Id]);

        // "Shared" means held by somebody OTHER than this entity — which is why the question is
        // asked through an accessor rather than off the manager.
        Assert.Equal([shared.Id], found);
    }

    [Fact]
    public async Task This_entitys_own_second_role_does_not_make_a_file_shared()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).SetAsync("primary", file.Id);
        await harness.Media.For(product).SetAsync("seo", file.Id);

        Assert.Empty(await harness.Media.For(product).WhichAreSharedAsync([file.Id]));
    }

    [Fact]
    public async Task It_agrees_with_what_DeleteAll_then_does()
    {
        var harness = Harness.Create();
        var product = new Product { Id = 1 };

        var shared = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "shared.png"));
        var mine   = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "mine.png"));

        await harness.Media.For(product).AddAsync(shared.Id);
        await harness.Media.For(product).AddAsync(mine.Id);
        await harness.Media.For(new Product { Id = 2 }).AddAsync(shared.Id);

        var attached = await harness.Media.For(product).ListAsync();
        var ids      = attached.Select(f => f.Id).ToArray();

        var predicted = await harness.Media.For(product).WhichAreSharedAsync(ids);
        var deleted   = await harness.Media.For(product).DeleteAllAsync();

        // The dialog that says "2 of these 5 will stay" has to be telling the truth: what the
        // probe called shared is exactly what the delete left alone.
        Assert.Equal(ids.Where(id => !predicted.Contains(id)).Order(), deleted.Order());
    }

    [Fact]
    public async Task An_unknown_file_is_not_shared()
    {
        var harness = Harness.Create();

        // Nothing holds it, including nothing other than this owner — the same answer as for a
        // file that exists and nobody else uses.
        Assert.Empty(await harness.Media.For(new Product { Id = 1 }).WhichAreSharedAsync([Guid.NewGuid()]));
    }

    [Fact]
    public async Task Asking_about_nothing_is_nothing()
    {
        var harness = Harness.Create();

        Assert.Empty(await harness.Media.For(new Product { Id = 1 }).WhichAreSharedAsync([]));
        await Task.CompletedTask;
    }
}
