using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// <c>IMediaWriteParticipant</c>: told about every write before the store commits it, so what it
/// enlists — an audit entry — commits with the change or not at all, and a refusal aborts the write.
/// </summary>
public class WriteParticipantTests
{
    [MediaEntity("AuditedProduct")]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    private static (ParticipantHarness Harness, RecordingParticipant Recorder) Create(
        MediaChangeKind? failOn = null, Action<MediaOptions>? configure = null)
    {
        RecordingParticipant recorder = null!;

        var harness = ParticipantHarness.Create(
            store => [recorder = new RecordingParticipant(store, failOn)],
            configure);

        return (harness, recorder);
    }

    // ── What participants are told ──────────────────────────────────────────

    [Fact]
    public async Task An_upload_is_one_Uploaded_change_committed_with_the_row()
    {
        var (harness, recorder) = Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "logo.png"));

        var change = Assert.Single(recorder.Changes);
        Assert.Equal(MediaChangeKind.Uploaded, change.Kind);
        Assert.Equal(file.Id, change.MediaFileId);
        Assert.Equal("logo.png", change.File!.FileName);
        Assert.Null(change.Owner);

        Assert.Equal([change], harness.Store.Committed);
        Assert.Empty(harness.Store.Pending);
    }

    [Fact]
    public async Task Uploading_to_an_entity_is_an_upload_then_an_attach_under_one_correlation_id()
    {
        var (harness, recorder) = Create();

        var file = await harness.Media.For(new Product { Id = 42 })
            .UploadAsync(TestMedia.Upload(TestMedia.Png(), role: "gallery"));

        Assert.Equal([MediaChangeKind.Uploaded, MediaChangeKind.Attached], recorder.Kinds);

        var attached = recorder.Changes[1];
        Assert.Equal(new MediaOwner("AuditedProduct", "42"), attached.Owner);
        Assert.Equal("gallery", attached.Role);
        Assert.Equal(0, attached.SortOrder);
        Assert.Equal(file.Id, attached.File!.Id);

        Assert.Single(recorder.Changes.Select(c => c.CorrelationId).Distinct());

        // Two writes, each carrying its own change.
        Assert.Equal(2, harness.Store.CommittingWrites);
    }

    [Fact]
    public async Task Attaching_a_file_that_is_already_attached_tells_nobody()
    {
        var (harness, recorder) = Create();
        var product = harness.Media.For(new Product { Id = 1 });

        var file = await product.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        var before = recorder.Changes.Count;

        await product.AddAsync(file.Id);

        Assert.Equal(before, recorder.Changes.Count);
    }

    [Fact]
    public async Task A_detach_names_the_owner_the_role_the_position_and_the_file()
    {
        var (harness, recorder) = Create();
        var product = harness.Media.For(new Product { Id = 7 });

        await product.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png", role: "gallery"));
        var b = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png", role: "gallery"));

        Assert.True(await product.DetachAsync(b.Id));

        var detached = recorder.Changes[^1];
        Assert.Equal(MediaChangeKind.Detached, detached.Kind);
        Assert.Equal(new MediaOwner("AuditedProduct", "7"), detached.Owner);
        Assert.Equal("gallery", detached.Role);
        Assert.Equal(1, detached.PreviousSortOrder);
        Assert.Equal("b.png", detached.File!.FileName);
    }

    [Fact]
    public async Task Detaching_what_is_not_attached_tells_nobody_and_enlists_nothing()
    {
        var (harness, recorder) = Create();

        Assert.False(await harness.Media.For(new Product { Id = 1 }).DetachAsync(Guid.NewGuid()));
        Assert.Equal(0, await harness.Media.For(new Product { Id = 1 }).DetachAllAsync());

        Assert.Empty(recorder.Changes);
        Assert.Empty(harness.Store.Pending);
    }

    [Fact]
    public async Task Deleting_a_file_is_one_change_listing_every_link_it_takes_with_it()
    {
        var (harness, recorder) = Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "shared.png"));
        await harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id, "primary");
        await harness.Media.For(new Product { Id = 2 }).AddAsync(file.Id, "gallery");

        var before       = recorder.Changes.Count;
        var writesBefore = harness.Store.CommittingWrites;

        Assert.True(await harness.Media.DeleteAsync(file.Id));

        // One operation, one change — no detaches of its own.
        var deleted = Assert.Single(recorder.Changes.Skip(before));
        Assert.Equal(MediaChangeKind.Deleted, deleted.Kind);
        Assert.Equal("shared.png", deleted.File!.FileName);
        Assert.True(deleted.SoftDeleted);
        Assert.Equal(
            [("1", "primary"), ("2", "gallery")],
            deleted.Attachments.Select(a => (a.Owner.EntityId, a.Role)).Order());

        // It commits with the one store write that removes the links and the row.
        Assert.Equal(1, harness.Store.FileDeletes);
        Assert.Equal(writesBefore + 1, harness.Store.CommittingWrites);
        Assert.Empty(harness.Store.Pending);
    }

    [Fact]
    public async Task Deleting_a_file_attached_to_nothing_lists_no_links()
    {
        var (harness, recorder) = Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        Assert.Empty(recorder.Changes[^1].Attachments);
    }

    [Fact]
    public async Task A_hard_delete_says_so()
    {
        var (harness, recorder) = Create(configure: o => o.SoftDelete = false);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        Assert.False(recorder.Changes[^1].SoftDeleted);
        Assert.Null(await harness.Store.FindAsync(file.Id, includeDeleted: true));
    }

    [Fact]
    public async Task An_edit_is_told_the_file_before_and_after()
    {
        var (harness, recorder) = Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "old.png"));
        await harness.Media.UpdateAsync(file.Id, new MediaUpdate { FileName = "new.png", Category = "Logos" });

        var updated = recorder.Changes[^1];
        Assert.Equal(MediaChangeKind.Updated, updated.Kind);
        Assert.Equal("old.png", updated.Previous!.FileName);
        Assert.Equal("new.png", updated.File!.FileName);
        Assert.Equal("logos", updated.File.Category);
    }

    [Fact]
    public async Task Replacing_a_role_is_one_change_naming_the_file_it_displaced_in_one_write()
    {
        var (harness, recorder) = Create();
        var product = harness.Media.For(new Product { Id = 3 });

        var first  = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(), "first.png", role: "primary"));
        var second = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "second.png"));
        var before = recorder.Changes.Count;

        await product.SetAsync("primary", second.Id);

        var replaced = Assert.Single(recorder.Changes.Skip(before));
        Assert.Equal(MediaChangeKind.Replaced, replaced.Kind);
        Assert.Equal(second.Id, replaced.MediaFileId);
        Assert.Equal(new MediaOwner("AuditedProduct", "3"), replaced.Owner);
        Assert.Equal("primary", replaced.Role);
        Assert.Equal("first.png", Assert.Single(replaced.Displaced).File.FileName);

        // Both halves in one store write.
        Assert.Equal(1, harness.Store.Replacements);
        Assert.Equal([second.Id], harness.Store.Inner.Attachments.Select(a => a.MediaFileId));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Setting_a_role_nobody_holds_is_an_attach_not_a_replacement()
    {
        var (harness, recorder) = Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        var before = recorder.Changes.Count;

        await harness.Media.For(new Product { Id = 3 }).SetAsync("primary", file.Id);

        Assert.Equal(MediaChangeKind.Attached, Assert.Single(recorder.Changes.Skip(before)).Kind);
        Assert.Equal(0, harness.Store.Replacements);
    }

    [Fact]
    public async Task A_refused_replacement_leaves_the_old_holder_in_the_role()
    {
        var (harness, _) = Create(failOn: MediaChangeKind.Replaced);
        var product = harness.Media.For(new Product { Id = 3 });

        var first  = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(), role: "primary"));
        var second = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => product.SetAsync("primary", second.Id));

        Assert.Equal([first.Id], harness.Store.Inner.Attachments.Select(a => a.MediaFileId));
        Assert.Empty(harness.Store.Pending);
    }

    [Fact]
    public async Task A_reorder_is_one_change_with_the_whole_role_before_and_after()
    {
        var (harness, recorder) = Create();
        var product = harness.Media.For(new Product { Id = 5 });

        var a = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png", role: "gallery"));
        var b = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png", role: "gallery"));
        var c = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(48), "c.png", role: "gallery"));
        var before = recorder.Changes.Count;

        await product.ReorderAsync("gallery", [c.Id, a.Id, b.Id]);

        var reordered = Assert.Single(recorder.Changes.Skip(before));
        Assert.Equal(MediaChangeKind.Reordered, reordered.Kind);
        Assert.Null(reordered.MediaFileId);
        Assert.Equal("gallery", reordered.Role);
        Assert.Equal(["a.png", "b.png", "c.png"], reordered.PreviousOrder.Select(f => f.FileName));
        Assert.Equal(["c.png", "a.png", "b.png"], reordered.Order.Select(f => f.FileName));
    }

    [Fact]
    public async Task A_reorder_that_moves_nothing_tells_nobody()
    {
        var (harness, recorder) = Create();
        var product = harness.Media.For(new Product { Id = 5 });

        var a = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png", role: "gallery"));
        var b = await product.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png", role: "gallery"));
        var before = recorder.Changes.Count;

        await product.ReorderAsync("gallery", [a.Id, b.Id]);

        Assert.Equal(before, recorder.Changes.Count);
    }

    [Fact]
    public async Task DeleteAll_detaches_what_others_use_and_deletes_the_rest_with_its_link()
    {
        var (harness, recorder) = Create();
        var one = harness.Media.For(new Product { Id = 1 });

        var mine   = await one.UploadAsync(TestMedia.Upload(TestMedia.Png(), "mine.png"));
        var shared = await one.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "shared.png"));
        await harness.Media.For(new Product { Id = 2 }).AddAsync(shared.Id);
        var before = recorder.Changes.Count;

        await one.DeleteAllAsync();

        var changes = recorder.Changes.Skip(before).ToList();
        Assert.Equal([MediaChangeKind.Detached, MediaChangeKind.Deleted], changes.Select(c => c.Kind));

        Assert.Equal(shared.Id, changes[0].MediaFileId);
        Assert.Equal("1", changes[0].Owner!.Value.EntityId);

        // The sole-held file's link to this product is reported with its delete, not as a detach.
        Assert.Equal(mine.Id, changes[1].MediaFileId);
        Assert.Equal("1", Assert.Single(changes[1].Attachments).Owner.EntityId);

        Assert.Single(changes.Select(c => c.CorrelationId).Distinct());
    }

    [Fact]
    public async Task DeleteAll_that_fails_part_way_leaves_the_undeleted_files_attached()
    {
        var (harness, _) = Create();
        var product = harness.Media.For(new Product { Id = 1 });

        await product.UploadAsync(TestMedia.Upload(TestMedia.Png(), "first.png"));
        await product.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "second.png"));

        harness.Store.FailFileDeletesAfter = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => product.DeleteAllAsync());

        // One went; the other is still this product's, where a retry will find it — not an
        // unattached file nothing points at.
        var left = Assert.Single(await product.ListAsync());
        Assert.Equal(left.Id, Assert.Single(harness.Store.Inner.Attachments).MediaFileId);

        harness.Store.FailFileDeletesAfter = null;
        Assert.Single(await product.DeleteAllAsync());
        Assert.Empty(harness.Store.Inner.Attachments);
    }

    // ── What a refusal does ────────────────────────────────────────────────

    [Fact]
    public async Task A_refused_upload_records_no_row_removes_its_bytes_and_commits_nothing()
    {
        var (harness, _) = Create(failOn: MediaChangeKind.Uploaded);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png())));

        Assert.Equal(0, harness.Store.Inner.Count);
        Assert.Empty(harness.Storage.Paths);
        Assert.Empty(harness.Store.Committed);
        Assert.Empty(harness.Store.Pending);
    }

    [Fact]
    public async Task A_refused_attach_leaves_the_file_unattached()
    {
        var (harness, _) = Create(failOn: MediaChangeKind.Attached);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Media.For(new Product { Id = 1 }).UploadAsync(TestMedia.Upload(TestMedia.Png())));

        // The upload committed before the attach was refused — the documented order — so the file
        // is there, attached to nothing.
        Assert.Equal(1, harness.Store.Inner.Count);
        Assert.Empty(harness.Store.Inner.Attachments);
        Assert.Empty(harness.Store.Pending);
    }

    [Fact]
    public async Task A_refused_delete_leaves_the_file_its_bytes_and_its_links()
    {
        var (harness, _) = Create(failOn: MediaChangeKind.Deleted);

        var file = await harness.Media.For(new Product { Id = 1 }).UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Media.DeleteAsync(file.Id));

        Assert.NotNull(await harness.Media.FindAsync(file.Id));
        Assert.Single(harness.Storage.Paths);
        Assert.Single(harness.Store.Inner.Attachments);
        Assert.Equal(0, harness.Store.FileDeletes);
        Assert.Empty(harness.Store.Pending);
    }

    [Fact]
    public async Task A_refused_edit_changes_nothing()
    {
        var (harness, _) = Create(failOn: MediaChangeKind.Updated);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "keep.png"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Media.UpdateAsync(file.Id, new MediaUpdate { FileName = "lost.png" }));

        Assert.Equal("keep.png", (await harness.Media.GetAsync(file.Id)).FileName);
    }

    [Fact]
    public async Task A_bad_edit_is_refused_before_any_participant_hears_of_it()
    {
        var (harness, recorder) = Create();

        var file   = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        var before = recorder.Changes.Count;

        // A blocked extension: renaming a picture to an executable is refused by validation.
        await Assert.ThrowsAsync<UnsupportedMediaTypeException>(
            () => harness.Media.UpdateAsync(file.Id, new MediaUpdate { FileName = "photo.exe" }));

        Assert.Equal(before, recorder.Changes.Count);
    }

    [Fact]
    public async Task A_store_failure_discards_what_was_enlisted()
    {
        var (harness, _) = Create();
        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        var committed = harness.Store.Committed.Count;

        harness.Store.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id));

        Assert.Empty(harness.Store.Pending);
        Assert.Equal(committed, harness.Store.Committed.Count);
    }

    [Fact]
    public async Task The_first_participant_to_throw_stops_the_rest()
    {
        RecordingParticipant first = null!, second = null!;

        var harness = ParticipantHarness.Create(store =>
        [
            first  = new RecordingParticipant(store, failOn: MediaChangeKind.Uploaded),
            second = new RecordingParticipant(store)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png())));

        Assert.Single(first.Changes);
        Assert.Empty(second.Changes);
        Assert.Empty(harness.Store.Pending);
    }

    // ── Stores that cannot enlist ──────────────────────────────────────────

    [Fact]
    public async Task A_store_that_cannot_enlist_refuses_the_write_rather_than_run_a_participant_without_atomicity()
    {
        var store   = new InMemoryMediaStore();
        var storage = new InMemoryStorageProvider();
        var clock   = TimeProvider.System;

        var media = new MediaManager(
            store,
            new MediaStorageProviderRegistry([storage], defaultProvider: null),
            new DateHierarchyKeyGenerator(clock),
            new ConventionEntityKeyAccessor(),
            MediaRoleRegistry.Empty,
            new MediaOptions(),
            clock,
            [],
            NullLogger<MediaManager>.Instance,
            participants: [new RecordingParticipant()]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => media.UploadAsync(TestMedia.Upload(TestMedia.Png())));

        Assert.Contains("cannot commit what it enlists", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Count);
        Assert.Empty(storage.Paths);
    }

    [Fact]
    public void Build_refuses_a_participant_for_a_store_that_has_not_declared_support()
    {
        var builder = new ServiceCollection().AddMedia()
            .UseCustomStore(_ => new InMemoryMediaStore())
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider())
            .UseWriteParticipant<RecordingParticipant>();

        var error = Assert.Throws<InvalidOperationException>(builder.Build);

        Assert.Contains("does not accept participants", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RecordingParticipant), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_accepts_a_participant_for_a_store_that_declares_support()
    {
        var builder = new ServiceCollection().AddMedia();
        MediaBuilderGuard.EnsureStoreNotConfigured(builder, "Enlisting", supportsWriteParticipants: true);

        builder.Services.AddScoped<IMediaStore>(_ => new EnlistingMediaStore(new InMemoryMediaStore()));

        builder
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider())
            .UseWriteParticipant<RecordingParticipant>()
            .Build();
    }
}
