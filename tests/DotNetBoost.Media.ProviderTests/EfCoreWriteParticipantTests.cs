using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.EntityFrameworkCore;
using DotNetBoost.Media.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.Media.ProviderTests;

/// <summary>
/// Write participants over the EF Core store, wired through the container as an application
/// wires them: what a participant enlists through <see cref="EfCoreMediaWriteScope"/> is saved by
/// the store's own SaveChanges, in the same transaction as the media rows — and is taken back off
/// the context whenever that write does not commit.
/// </summary>
public sealed class EfCoreWriteParticipantTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    /// <summary>Which change kind the journal participant refuses, for the abort cases.</summary>
    private static readonly AsyncLocal<MediaChangeKind?> Refuse = new();

    /// <summary>Run by the participant after it enlists, to change the world underneath the write.</summary>
    private static readonly AsyncLocal<Func<Task>?> AfterEnlisting = new();

    private sealed class JournalParticipant(EfCoreMediaWriteScope scope) : IMediaWriteParticipant
    {
        public Task OnWritingAsync(MediaChange change, CancellationToken ct = default)
        {
            scope.Add(new MediaJournalEntry
            {
                Kind          = change.Kind.ToString(),
                MediaFileId   = change.MediaFileId,
                Owner         = change.Owner is { } o ? $"{o.EntityName}/{o.EntityId}" : null,
                CorrelationId = change.CorrelationId
            });

            if (change.Kind == Refuse.Value)
                throw new InvalidOperationException($"Refused {change.Kind}.");

            return AfterEnlisting.Value?.Invoke() ?? Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        Refuse.Value         = null;
        AfterEnlisting.Value = null;

        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<MediaTestContext>(o => o.UseSqlite(_connection));

        services.AddMedia(o => o.ValidateStorageAtStartup = false)
            .UseEntityFrameworkCore<MediaTestContext>()
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider(), isDefault: true)
            .UseWriteParticipant<JournalParticipant>()
            .Build();

        _services = services.BuildServiceProvider(validateScopes: true);

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MediaTestContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>The journal as the database holds it, read on a context of its own.</summary>
    private async Task<List<MediaJournalEntry>> JournalAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MediaTestContext>()
            .Set<MediaJournalEntry>().AsNoTracking().ToListAsync();
    }

    private static int PendingJournalEntries(MediaTestContext context) =>
        context.ChangeTracker.Entries<MediaJournalEntry>().Count(e => e.State != EntityState.Unchanged);

    [Fact]
    public async Task Each_write_saves_its_journal_entry_with_it()
    {
        using var scope = _services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();

        var file = await media.For("Product", "42").UploadAsync(TestMedia.Upload(TestMedia.Png(), role: "gallery"));
        await media.For("Product", "42").DetachAsync(file.Id);

        var journal = await JournalAsync();

        Assert.Equal(["Attached", "Detached", "Uploaded"], journal.Select(e => e.Kind).Order(StringComparer.Ordinal));
        Assert.All(journal, e => Assert.Equal(file.Id, e.MediaFileId));
        Assert.Equal(2, journal.Count(e => e.Owner == "Product/42"));
    }

    [Fact]
    public async Task A_delete_saves_the_links_the_tombstone_and_every_entry_together()
    {
        using var scope = _services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();

        var file = await media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await media.For("Product", "1").AddAsync(file.Id);
        await media.For("Product", "2").AddAsync(file.Id);

        Assert.True(await media.DeleteAsync(file.Id));

        // One operation, one entry: the delete, not a detach per product before it.
        var journal = await JournalAsync();
        Assert.DoesNotContain(journal, e => e.Kind == "Detached");
        Assert.Equal(file.Id, Assert.Single(journal, e => e.Kind == "Deleted").MediaFileId);

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<MediaTestContext>();
        Assert.Empty(await db.Set<MediaAttachment>().ToListAsync());
        Assert.NotNull((await db.Set<MediaFile>().SingleAsync()).DeletedAt);
    }

    [Fact]
    public async Task A_replacement_saves_the_new_holder_and_its_entry_in_one_write()
    {
        using var scope = _services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<IMediaManager>();

        var first  = await media.For("Product", "1").UploadAsync(TestMedia.Upload(TestMedia.Png(), role: "primary"));
        var second = await media.UploadAsync(TestMedia.Upload(TestMedia.Png(32)));

        await media.For("Product", "1").SetAsync("primary", second.Id);

        Assert.Equal(second.Id, Assert.Single(await JournalAsync(), e => e.Kind == "Replaced").MediaFileId);

        using var check = _services.CreateScope();
        var links = await check.ServiceProvider.GetRequiredService<MediaTestContext>()
            .Set<MediaAttachment>().AsNoTracking().ToListAsync();
        Assert.Equal([second.Id], links.Select(l => l.MediaFileId));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task A_replacement_that_fails_keeps_the_old_holder_in_the_role()
    {
        using var scope = _services.CreateScope();
        var media   = scope.ServiceProvider.GetRequiredService<IMediaManager>();
        var context = scope.ServiceProvider.GetRequiredService<MediaTestContext>();

        var first  = await media.For("Product", "1").UploadAsync(TestMedia.Upload(TestMedia.Png(), role: "primary"));
        var second = await media.UploadAsync(TestMedia.Upload(TestMedia.Png(32)));

        // The INSERT of the new link is refused by the database — after the DELETE of the old
        // holder has run, in the same transaction.
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER refuse_link BEFORE INSERT ON MediaAttachments
            BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;
            """);

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => media.For("Product", "1").SetAsync("primary", second.Id));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_link;");
        }

        // The DELETE rolled back with the INSERT: the old holder is still there.
        var links = await context.Set<MediaAttachment>().AsNoTracking().ToListAsync();
        Assert.Contains(links, l => l.MediaFileId == first.Id);
        Assert.Equal(0, PendingJournalEntries(context));
        Assert.DoesNotContain(context.ChangeTracker.Entries<MediaAttachment>(), e => e.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task A_refused_attach_leaves_nothing_on_the_context_for_the_next_save()
    {
        using var scope = _services.CreateScope();
        var media   = scope.ServiceProvider.GetRequiredService<IMediaManager>();
        var context = scope.ServiceProvider.GetRequiredService<MediaTestContext>();

        var file = await media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Refuse.Value = MediaChangeKind.Attached;
        await Assert.ThrowsAsync<InvalidOperationException>(() => media.For("Product", "1").AddAsync(file.Id));
        Refuse.Value = null;

        Assert.Equal(0, PendingJournalEntries(context));

        // The application's own next save must not carry the refused write's entry along.
        context.Products.Add(new Product { Name = "Lamp" });
        await context.SaveChangesAsync();

        Assert.Equal(["Uploaded"], (await JournalAsync()).Select(e => e.Kind));
        Assert.Empty(await context.Set<MediaAttachment>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_refused_edit_does_not_leave_the_tracked_row_modified()
    {
        using var scope = _services.CreateScope();
        var media   = scope.ServiceProvider.GetRequiredService<IMediaManager>();
        var context = scope.ServiceProvider.GetRequiredService<MediaTestContext>();

        var file = await media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "keep.png"));

        Refuse.Value = MediaChangeKind.Updated;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => media.UpdateAsync(file.Id, new MediaUpdate { FileName = "lost.png" }));
        Refuse.Value = null;

        // The row is tracked by this context. Had the rename been applied before the participant
        // refused it, this unrelated save would write it.
        context.Products.Add(new Product { Name = "Lamp" });
        await context.SaveChangesAsync();

        var stored = await context.Set<MediaFile>().AsNoTracking().SingleAsync();
        Assert.Equal("keep.png", stored.FileName);
        Assert.Equal(0, PendingJournalEntries(context));
    }

    [Fact]
    public async Task A_refused_delete_keeps_the_row_the_links_and_the_bytes()
    {
        using var scope = _services.CreateScope();
        var media   = scope.ServiceProvider.GetRequiredService<IMediaManager>();
        var context = scope.ServiceProvider.GetRequiredService<MediaTestContext>();
        var storage = (InMemoryStorageProvider)scope.ServiceProvider.GetRequiredService<IMediaStorageProvider>();

        var file = await media.For("Product", "1").UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Refuse.Value = MediaChangeKind.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => media.DeleteAsync(file.Id));
        Refuse.Value = null;

        Assert.NotNull(await media.FindAsync(file.Id));
        Assert.Single(storage.Paths);
        Assert.Single(await context.Set<MediaAttachment>().AsNoTracking().ToListAsync());
        Assert.Equal(0, PendingJournalEntries(context));
    }

    [Fact]
    public async Task A_store_failure_after_enlisting_discards_the_entry()
    {
        using var scope = _services.CreateScope();
        var media   = scope.ServiceProvider.GetRequiredService<IMediaManager>();
        var context = scope.ServiceProvider.GetRequiredService<MediaTestContext>();

        var file = await media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        // The row goes behind the engine's back after the participant has enlisted, so the delete's
        // single save affects nothing and EF reports a concurrency failure.
        AfterEnlisting.Value = async () =>
        {
            using var other = _services.CreateScope();
            await other.ServiceProvider.GetRequiredService<MediaTestContext>()
                .Set<MediaFile>().Where(f => f.Id == file.Id).ExecuteDeleteAsync();
        };

        Assert.False(await media.DeleteAsync(file.Id));
        AfterEnlisting.Value = null;

        Assert.Equal(0, PendingJournalEntries(context));
        Assert.Empty(context.ChangeTracker.Entries<MediaFile>());
        Assert.DoesNotContain(await JournalAsync(), e => e.Kind == "Deleted");
    }

    [Fact]
    public void The_write_scope_and_the_store_wrap_the_same_context()
    {
        using var scope = _services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IMediaStore>();

        Assert.IsAssignableFrom<IEnlistingMediaStore>(store);

        using var other = new MediaTestContext(new DbContextOptionsBuilder<MediaTestContext>()
            .UseSqlite(_connection).Options);

        var error = Assert.Throws<ArgumentException>(() => new EfCoreMediaStore(
            scope.ServiceProvider.GetRequiredService<MediaTestContext>(), new EfCoreMediaWriteScope(other)));

        Assert.Contains("different DbContext", error.Message, StringComparison.Ordinal);
    }
}
