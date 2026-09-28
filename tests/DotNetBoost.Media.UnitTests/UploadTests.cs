using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;

namespace DotNetBoost.Media.UnitTests;

public class UploadTests
{
    [Fact]
    public async Task Upload_stores_the_bytes_and_records_the_row()
    {
        var harness = Harness.Create();
        var content = TestMedia.Png(128);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(content, "holiday.png"));

        Assert.Equal("holiday.png", file.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(128, file.Size);
        Assert.Equal("InMemory", file.StorageProvider);
        Assert.Equal(harness.Clock.GetUtcNow().UtcDateTime, file.CreatedAt);

        // The row's path is where the bytes actually went.
        Assert.Equal(content, harness.Storage.Read(file.StoragePath));
    }

    [Fact]
    public async Task Upload_measures_the_size_itself_rather_than_trusting_the_caller()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(4096)));

        Assert.Equal(4096, file.Size);
    }

    [Fact]
    public async Task Upload_derives_the_path_from_the_id_and_the_date_not_the_file_name()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "holiday.png", category: "photos"));

        Assert.Equal($"photos/2026/09/27/{file.Id:N}.png", file.StoragePath);
    }

    [Fact]
    public async Task Upload_hashes_the_bytes()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.NotNull(file.Checksum);
        Assert.Equal(64, file.Checksum.Length);
        Assert.Matches("^[0-9a-f]{64}$", file.Checksum);
    }

    [Fact]
    public async Task Upload_can_be_told_not_to_hash()
    {
        var harness = Harness.Create(o => o.ComputeChecksum = false);

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Null(file.Checksum);
    }

    [Fact]
    public async Task Upload_refuses_a_file_over_the_limit()
    {
        var harness = Harness.Create(o => o.MaxFileSizeBytes = 100);

        var tooLarge = await Assert.ThrowsAsync<MediaTooLargeException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(500))));

        Assert.Equal(100, tooLarge.Limit);

        // Nothing is left behind: no row, and no bytes from the part that did get written.
        Assert.Equal(0, harness.Store.Count);
        Assert.Empty(harness.Storage.Paths);
    }

    [Fact]
    public async Task Upload_refuses_an_executable_renamed_to_an_image()
    {
        var harness = Harness.Create();

        var mismatch = await Assert.ThrowsAsync<MediaContentMismatchException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Executable(), "photo.png", "image/png")));

        Assert.Equal("image/png", mismatch.DeclaredContentType);
        Assert.Equal("application/vnd.microsoft.portable-executable", mismatch.DetectedContentType);
        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task Upload_refuses_a_blocked_extension_before_reading_anything()
    {
        var harness = Harness.Create();

        var refused = await Assert.ThrowsAsync<UnsupportedMediaTypeException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Executable(), "setup.exe", contentType: null)));

        Assert.Equal(".exe", refused.Extension);
        Assert.Empty(harness.Storage.Paths);
    }

    [Fact]
    public async Task Upload_refuses_a_type_outside_the_allowed_list()
    {
        var harness = Harness.Create(o => o.AllowedContentTypes.Add("image/*"));

        await Assert.ThrowsAsync<UnsupportedMediaTypeException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Unrecognised(), "notes.txt", "text/plain")));
    }

    [Fact]
    public async Task Upload_accepts_a_family_match_in_the_allowed_list()
    {
        var harness = Harness.Create(o => o.AllowedContentTypes.Add("image/*"));

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        Assert.Equal("image/png", file.ContentType);
    }

    [Fact]
    public async Task Upload_takes_the_content_type_from_the_bytes_when_the_caller_sends_none()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "mystery.bin", contentType: null));

        Assert.Equal("image/png", file.ContentType);
    }

    [Fact]
    public async Task Upload_ignores_the_browsers_octet_stream_placeholder()
    {
        var harness = Harness.Create();

        // application/octet-stream is what a browser sends when it has no idea, which is not a
        // claim about the file and must not be stored as one.
        var file = await harness.Media.UploadAsync(
            TestMedia.Upload(TestMedia.Png(), "photo.png", "application/octet-stream"));

        Assert.Equal("image/png", file.ContentType);
    }

    [Fact]
    public async Task Upload_falls_back_to_the_extension_when_the_bytes_say_nothing()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(
            TestMedia.Upload(TestMedia.Unrecognised(), "notes.txt", contentType: null));

        Assert.Equal("text/plain", file.ContentType);
    }

    [Fact]
    public async Task Upload_strips_the_directory_from_a_traversing_file_name()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(
            TestMedia.Upload(TestMedia.Png(), "../../../etc/passwd.png"));

        Assert.Equal("passwd.png", file.FileName);
        Assert.DoesNotContain("..", file.StoragePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_removes_the_bytes_when_the_row_cannot_be_written()
    {
        var store   = new FailingMediaStore();
        var storage = new InMemoryStorageProvider();
        var media   = Harness.Over(store, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => media.UploadAsync(TestMedia.Upload(TestMedia.Png())));

        // The bytes went in before the row failed. Leaving them would be an object nothing in the
        // system can ever find or delete.
        Assert.Empty(storage.Paths);
    }

    [Fact]
    public async Task Upload_reports_a_backend_failure_as_a_storage_exception()
    {
        var harness = Harness.Create();
        harness.Storage.FailWrites = true;

        await Assert.ThrowsAsync<MediaStorageException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png())));

        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task Upload_normalises_tags()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(
            TestMedia.Upload(TestMedia.Png(), tags: ["  Invoice ", "invoice", "2026"]));

        Assert.Equal(["2026", "invoice"], file.Tags);
    }

    [Fact]
    public async Task Upload_refuses_a_tag_containing_the_delimiter()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), tags: ["a,b"])));
    }

    [Fact]
    public async Task Upload_keeps_metadata_and_reads_it_back()
    {
        var harness = Harness.Create();

        var file = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()) with
        {
            Metadata = new Dictionary<string, string> { ["width"] = "1920", ["height"] = "1080" }
        });

        var read = await harness.Media.GetAsync(file.Id);

        Assert.Equal("1920", read.Metadata["width"]);
        Assert.Equal("1080", read.Metadata["height"]);
    }

    [Fact]
    public async Task A_category_is_lower_cased_so_two_spellings_are_one_bucket()
    {
        var harness = Harness.Create();

        var upper = await harness.Media.UploadAsync(
            TestMedia.Upload(TestMedia.Png(), "a.png", category: "Product-Images"));
        var lower = await harness.Media.UploadAsync(
            TestMedia.Upload(TestMedia.Png(32), "b.png", category: "product-images"));

        Assert.Equal("product-images", upper.Category);
        Assert.Equal("product-images", lower.Category);

        // And the same bucket in the backend, not two directories holding the same kind of file.
        Assert.All(harness.Storage.Paths, path => Assert.StartsWith("product-images/", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_category_filter_matches_however_it_is_spelled()
    {
        var harness = Harness.Create();

        await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png", category: "Invoices"));

        var page = await harness.Media.ListAsync(new MediaQuery { Category = "INVOICES" });

        // The column is lower-cased on write, so a filter that was not would silently match nothing.
        Assert.Equal("a.png", Assert.Single(page.Items).FileName);
    }

    [Fact]
    public async Task An_updated_category_is_lower_cased_too()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), category: "photos"));

        var updated = await harness.Media.UpdateAsync(file.Id, new MediaUpdate { Category = "  Archive  " });

        Assert.Equal("archive", updated.Category);
    }

    [Fact]
    public async Task An_emptied_category_becomes_none_rather_than_an_empty_string()
    {
        var harness = Harness.Create();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), category: "photos"));

        var updated = await harness.Media.UpdateAsync(file.Id, new MediaUpdate { Category = "" });

        Assert.Null(updated.Category);
    }

    [Fact]
    public async Task A_category_longer_than_the_limit_is_refused()
    {
        var harness = Harness.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Media.UploadAsync(
                TestMedia.Upload(TestMedia.Png(), category: new string('x', MediaCategories.MaxLength + 1))));
    }
}
