using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;

namespace DotNetBoost.Media.UnitTests;

public class PathTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\a\report.pdf", "report.pdf")]
    [InlineData("/var/log/syslog", "syslog")]
    [InlineData("  report .pdf  ", "report .pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.png", "abcdefgh.png")]
    [InlineData("", "file")]
    [InlineData("   ", "file")]
    [InlineData("..", "file")]
    [InlineData(".", "file")]
    public void SanitizeFileName_keeps_only_a_safe_leaf(string input, string expected) =>
        Assert.Equal(expected, MediaPaths.SanitizeFileName(input));

    [Fact]
    public void SanitizeFileName_truncates_but_keeps_the_extension()
    {
        var long_ = new string('a', 400) + ".png";

        var result = MediaPaths.SanitizeFileName(long_);

        Assert.Equal(MediaPaths.MaxFileNameLength, result.Length);
        Assert.EndsWith(".png", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.PNG", ".png")]
    [InlineData("a.tar.gz", ".gz")]
    [InlineData("noextension", "")]
    [InlineData("a.", "")]
    [InlineData("a.this-is-far-too-long-to-be-an-extension", "")]
    [InlineData("a.pn g", "")]
    public void ExtensionOf_accepts_only_a_plausible_extension(string input, string expected) =>
        Assert.Equal(expected, MediaPaths.ExtensionOf(input));

    [Theory]
    [InlineData("Product Images", "product-images")]
    [InlineData("  avatars  ", "avatars")]
    [InlineData("a/b", "a-b")]
    [InlineData("---", null)]
    [InlineData("", null)]
    public void Segment_makes_one_safe_path_segment(string input, string? expected) =>
        Assert.Equal(expected, MediaPaths.Segment(input));

    [Theory]
    [InlineData("a/b/c.png", "a/b/c.png")]
    [InlineData("/a/b.png", "a/b.png")]
    [InlineData(@"a\b.png", "a/b.png")]
    public void Validate_canonicalises_a_good_path(string input, string expected) =>
        Assert.Equal(expected, MediaPaths.Validate(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/../b.png")]
    [InlineData("../b.png")]
    [InlineData("a//b.png")]
    [InlineData("a/./b.png")]
    public void Validate_refuses_a_path_that_could_escape(string input) =>
        Assert.Throws<InvalidMediaException>(() => MediaPaths.Validate(input));

    [Fact]
    public void Validate_refuses_a_path_over_the_column_length() =>
        Assert.Throws<InvalidMediaException>(() => MediaPaths.Validate(new string('a', MediaPaths.MaxPathLength + 1)));
}

public class ContentTypeTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "application/pdf")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "application/zip")]
    [InlineData(new byte[] { 0x4D, 0x5A }, "application/vnd.microsoft.portable-executable")]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, "application/x-elf")]
    public void Detect_recognises_a_known_signature(byte[] header, string expected) =>
        Assert.Equal(expected, MediaContentTypes.Detect(header));

    [Fact]
    public void Detect_returns_null_for_bytes_it_does_not_know() =>
        Assert.Null(MediaContentTypes.Detect([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]));

    [Fact]
    public void Detect_tells_the_RIFF_families_apart()
    {
        Assert.Equal("image/webp", MediaContentTypes.Detect([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8]));
        Assert.Equal("audio/wav", MediaContentTypes.Detect([.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8]));
        Assert.Equal("video/x-msvideo", MediaContentTypes.Detect([.. "RIFF"u8, 0, 0, 0, 0, .. "AVI "u8]));
    }

    [Theory]
    [InlineData("image/png", "image/png", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip", true)]
    [InlineData("application/vnd.oasis.opendocument.text", "application/zip", true)]
    [InlineData("audio/mp4", "video/mp4", true)]
    [InlineData("image/jpg", "image/jpeg", true)]
    [InlineData("image/png", "application/vnd.microsoft.portable-executable", false)]
    [InlineData("text/plain", "application/zip", false)]
    public void AreCompatible_allows_the_container_formats_and_nothing_else(string declared, string detected, bool expected) =>
        Assert.Equal(expected, MediaContentTypes.AreCompatible(declared, detected));

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.PDF", "application/pdf")]
    [InlineData("a.unknown", null)]
    [InlineData("a", null)]
    public void FromFileName_maps_the_extension(string fileName, string? expected) =>
        Assert.Equal(expected, MediaContentTypes.FromFileName(fileName));
}

public class TagTests
{
    [Fact]
    public void Join_normalises_and_delimits()
    {
        Assert.Equal(",a,b,", MediaTags.Join(["B", " a ", "b"]));
    }

    [Fact]
    public void Join_returns_null_for_nothing_usable()
    {
        Assert.Null(MediaTags.Join([]));
        Assert.Null(MediaTags.Join(["  "]));
        Assert.Null(MediaTags.Join(null));
    }

    [Fact]
    public void Join_refuses_a_tag_containing_the_delimiter() =>
        Assert.Throws<ArgumentException>(() => MediaTags.Join(["a,b"]));

    [Fact]
    public void Join_refuses_an_over_long_tag() =>
        Assert.Throws<ArgumentException>(() => MediaTags.Join([new string('a', MediaTags.MaxTagLength + 1)]));

    [Fact]
    public void Join_refuses_too_many_tags() =>
        Assert.Throws<ArgumentException>(() => MediaTags.Join([.. Enumerable.Range(0, MediaTags.MaxTags + 1).Select(i => $"t{i}")]));

    [Fact]
    public void Split_round_trips_Join() =>
        Assert.Equal(["a", "b"], MediaTags.Split(MediaTags.Join(["b", "a"])));

    [Fact]
    public void Fragment_wraps_a_tag_in_delimiters() =>
        Assert.Equal(",invoice,", MediaTags.Fragment(" Invoice "));

    [Fact]
    public void A_fragment_does_not_match_a_longer_tag()
    {
        var stored = MediaTags.Join(["invoiced"])!;

        Assert.DoesNotContain(MediaTags.Fragment("invoice"), stored, StringComparison.Ordinal);
    }
}

public class MetadataTests
{
    [Fact]
    public void Serialize_returns_null_for_nothing()
    {
        Assert.Null(MediaMetadata.Serialize(null));
        Assert.Null(MediaMetadata.Serialize(new Dictionary<string, string>()));
    }

    [Fact]
    public void Serialize_round_trips()
    {
        var json = MediaMetadata.Serialize(new Dictionary<string, string> { ["a"] = "1" });

        Assert.Equal("1", MediaMetadata.Parse(json)["a"]);
    }

    [Fact]
    public void Parse_treats_unusable_json_as_empty() =>
        Assert.Empty(MediaMetadata.Parse("{not json"));

    [Fact]
    public void Serialize_refuses_a_payload_over_the_column_length()
    {
        var big = new Dictionary<string, string> { ["k"] = new('x', MediaMetadata.MaxLength) };

        Assert.Throws<ArgumentException>(() => MediaMetadata.Serialize(big));
    }
}
