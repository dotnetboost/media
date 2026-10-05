using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.TestSupport;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// The conformance suite run against this library itself.
/// <para>
/// Two things at once, and both are the point. It proves the suite a consumer derives from
/// actually runs and actually asserts, rather than passing because every hook was trivially
/// satisfied. And it holds this library to the spec it publishes: if someone changes how a key is
/// formatted without bumping <see cref="EntityKey.AddressingVersion"/>, the suite that is supposed
/// to catch that in a consumer's build catches it here first.
/// </para>
/// </summary>
public sealed class MediaAddressingConformanceTests : EntityAddressingConformance
{
    protected override string Implementation      => "DotNetBoost.Media";
    protected override string ConformsToVersion   => EntityKey.AddressingVersion;
    protected override int    MaxKeyLength        => EntityKey.MaxLength;
    protected override int    MaxEntityNameLength => MediaEntityNames.MaxLength;

    protected override string FormatKey(object key)       => EntityKey.ToKeyString(key);
    protected override string ResolveEntityName(Type type) => MediaEntityNames.For(type);
}

/// <summary>
/// The version constant itself, which is the only thing in this library that another repository
/// has to match by hand.
/// </summary>
public class AddressingVersionTests
{
    /// <summary>
    /// Pinned to a literal on purpose. The constant is what DotNetBoost.MultiLingual publishes a
    /// copy of, so changing it is a cross-repository act — and this test is the line that says so,
    /// in the place a change to the addressing rules would be made.
    /// </summary>
    [Fact]
    public void The_published_addressing_version_is_the_one_the_other_libraries_agreed_to() =>
        Assert.Equal("1.0", EntityKey.AddressingVersion);

    [Fact]
    public void The_lengths_it_covers_are_the_ones_the_columns_are_built_to()
    {
        // The two widths the spec fixes. Mapped from these constants in every store, so a change
        // here is a migration — which is most of why the version exists.
        Assert.Equal(64, EntityKey.MaxLength);
        Assert.Equal(128, MediaEntityNames.MaxLength);
    }
}
