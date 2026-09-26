using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.FileSystem;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// The local backend against a real temporary directory. Not a mock: the whole point of these is
/// the file system's own behaviour — traversal, permissions, what happens to a directory once its
/// last file goes.
/// </summary>
public sealed class FileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotnetboost-media-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemStorageProvider _provider;

    public FileSystemTests()
    {
        Directory.CreateDirectory(_root);
        _provider = new FileSystemStorageProvider(new FileSystemOptions { RootPath = _root });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static MediaWrite Write(byte[] content, string path = "a/b/c.png") =>
        new(new MemoryStream(content, writable: false), path, "image/png", ComputeChecksum: false);

    [Fact]
    public async Task Save_writes_the_bytes_and_reports_where()
    {
        var content = new byte[] { 1, 2, 3, 4 };

        var stored = await _provider.SaveAsync(Write(content));

        Assert.Equal("a/b/c.png", stored.Path);
        Assert.Equal(4, stored.Size);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, "a", "b", "c.png")));
    }

    [Fact]
    public async Task Save_creates_the_directories_it_needs()
    {
        await _provider.SaveAsync(Write([1], "deeply/nested/path/x.png"));

        Assert.True(Directory.Exists(Path.Combine(_root, "deeply", "nested", "path")));
    }

    [Fact]
    public async Task OpenRead_returns_what_was_written()
    {
        await _provider.SaveAsync(Write([9, 8, 7]));

        await using var stream = await _provider.OpenReadAsync(_root, "a/b/c.png");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal([9, 8, 7], buffer.ToArray());
    }

    [Fact]
    public async Task OpenRead_reports_a_path_with_nothing_at_it() =>
        await Assert.ThrowsAsync<MediaContentMissingException>(() => _provider.OpenReadAsync(_root, "not/there.png"));

    [Fact]
    public async Task Delete_removes_the_file_and_says_it_did()
    {
        await _provider.SaveAsync(Write([1]));

        Assert.True(await _provider.DeleteAsync(_root, "a/b/c.png"));
        Assert.False(File.Exists(Path.Combine(_root, "a", "b", "c.png")));
    }

    [Fact]
    public async Task Delete_reports_false_for_a_path_with_nothing_at_it() =>
        Assert.False(await _provider.DeleteAsync(_root, "not/there.png"));

    [Fact]
    public async Task Delete_prunes_the_directories_it_emptied()
    {
        await _provider.SaveAsync(Write([1], "2026/09/27/x.png"));
        await _provider.DeleteAsync(_root, "2026/09/27/x.png");

        Assert.False(Directory.Exists(Path.Combine(_root, "2026")));
        // …but not the root itself.
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Delete_leaves_a_directory_that_still_has_something_in_it()
    {
        await _provider.SaveAsync(Write([1], "2026/09/27/x.png"));
        await _provider.SaveAsync(Write([2], "2026/09/27/y.png"));

        await _provider.DeleteAsync(_root, "2026/09/27/x.png");

        Assert.True(Directory.Exists(Path.Combine(_root, "2026", "09", "27")));
    }

    [Fact]
    public async Task Exists_answers_both_ways()
    {
        await _provider.SaveAsync(Write([1]));

        Assert.True(await _provider.ExistsAsync(_root, "a/b/c.png"));
        Assert.False(await _provider.ExistsAsync(_root, "a/b/d.png"));
    }

    [Theory]
    [InlineData("../escaped.png")]
    [InlineData("a/../../escaped.png")]
    [InlineData("/etc/passwd")]
    public async Task A_traversing_path_never_reaches_outside_the_root(string path)
    {
        // "/etc/passwd" is canonicalised to "etc/passwd" and stays inside; the others are rejected
        // outright. Either way nothing outside the root is touched.
        try
        {
            await _provider.SaveAsync(Write([1], path));
        }
        catch (InvalidMediaException)
        {
            return;
        }

        var written = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories);
        Assert.All(written, f => Assert.StartsWith(_root, Path.GetFullPath(f), StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetUrl_returns_null_without_a_public_base_url() =>
        Assert.Null(await _provider.GetUrlAsync(_root, "a/b/c.png", new MediaUrlOptions()));

    [Fact]
    public async Task GetUrl_builds_a_link_under_the_public_base_url()
    {
        var provider = new FileSystemStorageProvider(new FileSystemOptions
        {
            RootPath      = _root,
            // No trailing slash, on purpose: the naive Uri combination drops the last segment.
            PublicBaseUrl = new Uri("https://cdn.example.com/media")
        });

        var url = await provider.GetUrlAsync(_root, "a/b/c.png", new MediaUrlOptions());

        Assert.Equal(new Uri("https://cdn.example.com/media/a/b/c.png"), url);
    }

    [Fact]
    public async Task GetUrl_escapes_each_segment()
    {
        var provider = new FileSystemStorageProvider(new FileSystemOptions
        {
            RootPath      = _root,
            PublicBaseUrl = new Uri("https://cdn.example.com/")
        });

        var url = await provider.GetUrlAsync(_root, "a b/c d.png", new MediaUrlOptions());

        // AbsoluteUri, not ToString(): ToString() un-escapes for display, and what goes on the
        // wire is what matters here.
        Assert.Equal("https://cdn.example.com/a%20b/c%20d.png", url!.AbsoluteUri);
    }

    [Fact]
    public async Task Check_creates_the_root_and_proves_it_is_writable()
    {
        var fresh    = Path.Combine(_root, "created-by-check");
        var provider = new FileSystemStorageProvider(new FileSystemOptions { RootPath = fresh });

        await provider.CheckAsync();

        Assert.True(Directory.Exists(fresh));
        // The probe file it writes is cleaned up again.
        Assert.Empty(Directory.EnumerateFileSystemEntries(fresh));
    }

    [Fact]
    public async Task Check_refuses_a_missing_root_when_it_is_told_not_to_create_one()
    {
        var provider = new FileSystemStorageProvider(new FileSystemOptions
        {
            RootPath            = Path.Combine(_root, "never-created"),
            CreateRootIfMissing = false
        });

        var error = await Assert.ThrowsAsync<MediaStorageException>(() => provider.CheckAsync());

        Assert.Equal("FileSystem", error.StorageProvider);
    }

    [Fact]
    public void A_backend_with_no_root_is_refused() =>
        Assert.Throws<ArgumentException>(() => new FileSystemStorageProvider(new FileSystemOptions()));
}
