using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Services;
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
    protected override bool   IsKeyUnset(object? value)    => EntityKey.IsUnset(value);

    // The CONVENTION accessor, not the EF Core one: the shared rule is the fallback used where
    // there is no model to read, and the EF provider is free to be more correct than it.
    protected override string? DiscoverKey(object entity) => Convention.GetKey(entity);

    private static readonly ConventionEntityKeyAccessor Convention = new();
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

    /// <summary>
    /// This library refuses an over-long key as it formats it. Not part of the shared conformance
    /// suite: the spec fixes the LIMIT, not the layer that enforces it, and DotNetBoost.MultiLingual
    /// refuses the same key at the write so it can name the entity as well — conformant, and more
    /// helpful. So each library tests its own refusal, and the suite compares the limits.
    /// </summary>
    [Fact]
    public void A_key_longer_than_the_column_is_refused_as_it_is_formatted()
    {
        var tooLong = new string('k', EntityKey.MaxLength + 1);

        var error = Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString(tooLong));

        Assert.Contains(tooLong.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_or_whitespace_key_is_refused()
    {
        // Whitespace is the one worth pinning: it has a length, so it is not "unset", and a library
        // that let it through would file rows under a key nothing else can address.
        Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString(string.Empty));
        Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString("   "));
    }

    [Fact]
    public void The_lengths_it_covers_are_the_ones_the_columns_are_built_to()
    {
        // The two widths the spec fixes. Mapped from these constants in every store, so a change
        // here is a migration — which is most of why the version exists.
        Assert.Equal(64, EntityKey.MaxLength);
        Assert.Equal(128, MediaEntityNames.MaxLength);
    }
}
