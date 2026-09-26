using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;

namespace DotNetBoost.Media.UnitTests;

public class LifecycleTests
{
    [Fact]
    public async Task OpenRead_returns_the_bytes_that_were_uploaded()
    {
        var harness = Harness.Create();
        var content = TestMedia.Png(256);
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(content));

        using var read = await harness.Media.OpenReadAsync(file.Id);
        using var buffer = new MemoryStream();
        await read.Stream.CopyToAsync(buffer);

        Assert.Equal(content, buffer.ToArray());
        Assert.Equal("image/png", read.ContentType);
        Assert.Equal("photo.png", read.FileName);
    }

    [Fact]
    public async Task OpenRead_reports_a_row_whose_bytes_have_gone()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        harness.Storage.Corrupt(file.StoragePath);

        var missing = await Assert.ThrowsAsync<MediaContentMissingException>(
            () => harness.Media.OpenReadAsync(file.Id));

        Assert.Equal(file.StoragePath, missing.StoragePath);
    }

    [Fact]
    public async Task Get_throws_for_an_unknown_id()
    {
        var harness = Harness.Create();

        var missing = await Assert.ThrowsAsync<MediaNotFoundException>(
            () => harness.Media.GetAsync(Guid.NewGuid()));

        Assert.NotEqual(Guid.Empty, missing.Id);
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_id()
    {
        var harness = Harness.Create();

        Assert.Null(await harness.Media.FindAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_removes_the_bytes_and_keeps_the_row_by_default()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.True(await harness.Media.DeleteAsync(file.Id));

        // Bytes gone, row kept as the record that the file existed.
        Assert.Empty(harness.Storage.Paths);
        Assert.Equal(1, harness.Store.Count);

        // …and invisible to every ordinary read.
        Assert.Null(await harness.Media.FindAsync(file.Id));
        var page = await harness.Media.ListAsync();
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Delete_removes_the_row_too_when_soft_delete_is_off()
    {
        var harness = Harness.Create(o => o.SoftDelete = false);
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.True(await harness.Media.DeleteAsync(file.Id));

        Assert.Empty(harness.Storage.Paths);
        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task Delete_reports_false_for_a_file_that_is_not_there()
    {
        var harness = Harness.Create();

        Assert.False(await harness.Media.DeleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_twice_is_not_an_error()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.True(await harness.Media.DeleteAsync(file.Id));
        Assert.False(await harness.Media.DeleteAsync(file.Id));
    }

    [Fact]
    public async Task A_soft_deleted_file_is_visible_only_when_asked_for()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));
        await harness.Media.DeleteAsync(file.Id);

        var page = await harness.Media.ListAsync(new MediaQuery { IncludeDeleted = true });

        var found = Assert.Single(page.Items);
        Assert.Equal(file.Id, found.Id);
        Assert.True(found.IsDeleted);
    }

    [Fact]
    public async Task Update_changes_only_what_it_names()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(
            TestMedia.Upload(TestMedia.Png(), "old.png", category: "photos", tags: ["draft"]));

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        var updated = await harness.Media.UpdateAsync(file.Id, new MediaUpdate { FileName = "new.png" });

        Assert.Equal("new.png", updated.FileName);
        Assert.Equal("photos", updated.Category);
        Assert.Equal(["draft"], updated.Tags);
        Assert.Equal(harness.Clock.GetUtcNow().UtcDateTime, updated.UpdatedAt);

        // The bytes and their location are not editable.
        Assert.Equal(file.StoragePath, updated.StoragePath);
        Assert.Equal(file.Size, updated.Size);
        Assert.Equal(file.Checksum, updated.Checksum);
    }

    [Fact]
    public async Task Update_replaces_tags_outright()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), tags: ["a", "b"]));

        var updated = await harness.Media.UpdateAsync(file.Id, new MediaUpdate { Tags = ["c"] });

        Assert.Equal(["c"], updated.Tags);
    }

    [Fact]
    public async Task Update_can_clear_tags()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), tags: ["a"]));

        var updated = await harness.Media.UpdateAsync(file.Id, new MediaUpdate { Tags = [] });

        Assert.Empty(updated.Tags);
    }

    [Fact]
    public async Task Update_throws_for_an_unknown_id()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<MediaNotFoundException>(
            () => harness.Media.UpdateAsync(Guid.NewGuid(), new MediaUpdate { FileName = "x.png" }));
    }

    [Fact]
    public async Task Deduplication_returns_the_existing_file_and_stores_one_copy()
    {
        var harness = Harness.Create(o => o.DeduplicateByChecksum = true);
        var content = TestMedia.Png(512);

        var first  = await harness.Media.UploadAsync(TestMedia.Upload(content, "first.png"));
        var second = await harness.Media.UploadAsync(TestMedia.Upload(content, "second.png"));

        Assert.Equal(first.Id, second.Id);
        // The caller who uploaded "second.png" gets back the first upload's row — which is the
        // trade-off the option documents.
        Assert.Equal("first.png", second.FileName);

        Assert.Equal(1, harness.Store.Count);
        Assert.Single(harness.Storage.Paths);
    }

    [Fact]
    public async Task Without_deduplication_identical_bytes_are_two_files()
    {
        var harness = Harness.Create();
        var content = TestMedia.Png(512);

        var first  = await harness.Media.UploadAsync(TestMedia.Upload(content, "first.png"));
        var second = await harness.Media.UploadAsync(TestMedia.Upload(content, "second.png"));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, harness.Store.Count);
        Assert.Equal(2, harness.Storage.Paths.Count);
    }

    [Fact]
    public async Task GetUrl_returns_null_for_a_backend_with_no_direct_link()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Null(await harness.Media.GetUrlAsync(file.Id));
    }

    [Fact]
    public async Task GetUrl_returns_the_backends_link_when_it_has_one()
    {
        var harness = Harness.Create();
        harness.Storage.Url = new Uri("https://cdn.example.com/x.png");

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Equal(new Uri("https://cdn.example.com/x.png"), await harness.Media.GetUrlAsync(file.Id));
    }
}
