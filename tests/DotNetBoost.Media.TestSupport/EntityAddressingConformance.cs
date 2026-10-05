using DotNetBoost.Media.Core.Metadata;
using System.Globalization;
using Xunit;

namespace DotNetBoost.Media.TestSupport;

/// <summary>
/// The <b>DotNetBoost Entity Addressing v1</b> conformance suite: everything two libraries have to
/// agree about before an application can join their rows on <c>(EntityName, EntityId)</c>.
/// <para>
/// This library and DotNetBoost.MultiLingual arrive at the same addressing independently — same
/// name resolution, same key format, same two length limits — and that is what lets a media row
/// and a translation row about one product be matched up. Nothing in either library enforced it:
/// the agreement rested on comments in two repositories, which is a guarantee with no test behind
/// it, and a consumer wanting one had to write it by hand.
/// </para>
/// <para>
/// Derive from this in your own test project, point it at the other library, and the agreement is
/// a suite you run rather than a promise you take:
/// <code>
/// public sealed class MultiLingualAddressingTests : EntityAddressingConformance
/// {
///     protected override string Implementation        =&gt; "DotNetBoost.MultiLingual";
///     protected override string ConformsToVersion     =&gt; MultiLingual.Core.Metadata.EntityKey.AddressingVersion;
///     protected override int    MaxKeyLength          =&gt; MultiLingual.Core.Metadata.EntityKey.MaxLength;
///     protected override int    MaxEntityNameLength   =&gt; MultiLingual.Core.Metadata.SubjectNames.MaxLength;
///
///     protected override string FormatKey(object key)      =&gt; MultiLingual.Core.Metadata.EntityKey.ToKeyString(key);
///     protected override string ResolveEntityName(Type t)  =&gt; MultiLingual.Core.Metadata.SubjectNames.For(t);
/// }
/// </code>
/// </para>
/// <para>
/// Every assertion compares the other implementation against this one. It is deliberately not a
/// spec written out a third time — a third copy of the rules is a third thing to drift.
/// </para>
/// </summary>
public abstract class EntityAddressingConformance
{
    /// <summary>What is being checked, for the failure message — e.g. <c>DotNetBoost.MultiLingual</c>.</summary>
    protected abstract string Implementation { get; }

    /// <summary>The other library's published addressing version — its own <c>AddressingVersion</c>.</summary>
    protected abstract string ConformsToVersion { get; }

    /// <summary>The longest key it stores.</summary>
    protected abstract int MaxKeyLength { get; }

    /// <summary>The longest entity name it stores.</summary>
    protected abstract int MaxEntityNameLength { get; }

    /// <summary>Its key formatter.</summary>
    protected abstract string FormatKey(object key);

    /// <summary>Its entity-name resolver.</summary>
    protected abstract string ResolveEntityName(Type type);

    /// <summary>
    /// A key of each type the one column has to serve, including the values whose formatting is
    /// culture-dependent if anyone forgets to pin the culture.
    /// </summary>
    public static TheoryData<object> Keys =>
    [
        42,
        -7,
        0,
        int.MaxValue,
        long.MaxValue,
        (long)-1,
        (short)3,
        (byte)200,
        new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
        Guid.Empty,
        "a-string-key",
        "composite:7:2026"
    ];

    [Fact]
    public void It_publishes_the_same_addressing_version()
    {
        // The one assertion that makes the rest of this suite a contract rather than a snapshot:
        // a library that changes any rule below is required to bump its version, so this fails
        // first and names the problem instead of leaving a consumer to find rows that stopped
        // joining.
        Assert.True(
            string.Equals(EntityKey.AddressingVersion, ConformsToVersion, StringComparison.Ordinal),
            $"{Implementation} declares entity addressing v{ConformsToVersion}; DotNetBoost.Media declares " +
            $"v{EntityKey.AddressingVersion}. The two no longer promise to address an entity the same way, so " +
            "rows keyed by one cannot be joined to rows keyed by the other.");
    }

    [Fact]
    public void It_stores_keys_in_a_column_of_the_same_width() =>
        Assert.True(
            EntityKey.MaxLength == MaxKeyLength,
            $"{Implementation} stores keys up to {MaxKeyLength} characters; DotNetBoost.Media stores " +
            $"{EntityKey.MaxLength}. A key the wider one accepts has no row in the narrower.");

    [Fact]
    public void It_stores_entity_names_in_a_column_of_the_same_width() =>
        Assert.True(
            MediaEntityNames.MaxLength == MaxEntityNameLength,
            $"{Implementation} stores entity names up to {MaxEntityNameLength} characters; " +
            $"DotNetBoost.Media stores {MediaEntityNames.MaxLength}.");

    [Theory]
    [MemberData(nameof(Keys))]
    public void It_formats_a_key_the_same_way(object key) =>
        Assert.Equal(EntityKey.ToKeyString(key), FormatKey(key));

    [Theory]
    [MemberData(nameof(Keys))]
    public void It_formats_a_key_the_same_way_under_a_hostile_culture(object key)
    {
        // ar-SA formats numbers with its own digits and its own calendar, and fa-IR groups them
        // differently. A key formatted under one and read back under another has to be the same
        // string, or an entity's rows stop belonging to it the moment a request arrives with a
        // different Accept-Language.
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var mine  = EntityKey.ToKeyString(key);
            var theirs = FormatKey(key);

            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");

            Assert.Equal(mine, theirs);
            Assert.Equal(mine, FormatKey(key));
            Assert.Equal(mine, EntityKey.ToKeyString(key));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void It_formats_a_guid_in_D_format()
    {
        // Spelled out rather than left to the comparison above, because this is the one key type
        // with several plausible spellings — "N" without dashes, "B" in braces — and two libraries
        // picking differently is the mismatch that would be hardest to spot: both strings look
        // like the same GUID.
        var id = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", FormatKey(id));
    }

    [Fact]
    public void It_resolves_an_unannotated_type_to_its_type_name()
    {
        // No attribute from either library, so both have to fall back the same way. A type each
        // library renames through its own attribute is each library's own business — the shared
        // rule is the fallback, and it is the one every entity in an application relies on.
        Assert.Equal(nameof(PlainEntity), ResolveEntityName(typeof(PlainEntity)));
        Assert.Equal(MediaEntityNames.For(typeof(PlainEntity)), ResolveEntityName(typeof(PlainEntity)));
    }

    [Fact]
    public void It_refuses_an_empty_key()
    {
        // Both refuse; the exception type is each library's own. What matters is that neither
        // stores a row addressed to nothing, which would then be joined to by every other such row.
        Assert.ThrowsAny<Exception>(() => FormatKey(string.Empty));
        Assert.ThrowsAny<Exception>(() => FormatKey("   "));
    }

    [Fact]
    public void It_refuses_a_key_longer_than_the_column()
    {
        var tooLong = new string('k', MaxKeyLength + 1);

        Assert.ThrowsAny<Exception>(() => FormatKey(tooLong));
        Assert.ThrowsAny<Exception>(() => EntityKey.ToKeyString(tooLong));
    }

    [Fact]
    public void It_accepts_a_key_exactly_as_long_as_the_column()
    {
        // The boundary in the other direction, so an off-by-one in either library's check turns up
        // here rather than as a key one library stores and the other rejects.
        var exact = new string('k', MaxKeyLength);

        Assert.Equal(exact, FormatKey(exact));
        Assert.Equal(exact, EntityKey.ToKeyString(exact));
    }

    /// <summary>
    /// An entity neither library has an attribute on, so both must resolve it to its type name.
    /// Nested here so a consumer gets it with the suite rather than declaring one of its own.
    /// </summary>
    public sealed class PlainEntity
    {
        /// <summary>Its key, discovered by the convention both libraries share.</summary>
        public int Id { get; set; }
    }
}
