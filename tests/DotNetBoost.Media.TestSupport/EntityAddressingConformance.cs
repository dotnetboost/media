using DotNetBoost.Media.Core.Metadata;
using System.ComponentModel.DataAnnotations;
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
    /// Its unset-key test — the one that decides whether an entity can be addressed at all.
    /// </summary>
    protected abstract bool IsKeyUnset(object? value);

    /// <summary>
    /// Its convention key discovery, returning the formatted key or <c>null</c> for an unset one.
    /// Pass the convention accessor, not a provider's model-reading one: the shared rule is the
    /// fallback both libraries use when there is no model to consult.
    /// </summary>
    protected abstract string? DiscoverKey(object entity);

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

    /// <summary>
    /// The shared rule is the LIMIT, not where each library enforces it. One may refuse an
    /// over-long key as it formats it and another at the write, where it can name the entity too;
    /// both are conformant, and demanding a throw from <see cref="FormatKey"/> would fail the
    /// second for being more helpful. So this compares the published limits and leaves the
    /// refusal to each library's own tests.
    /// </summary>
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
    public void It_accepts_a_key_exactly_as_long_as_the_column()
    {
        // The boundary in the other direction, so an off-by-one in either library's check turns up
        // here rather than as a key one library stores and the other rejects.
        var exact = new string('k', MaxKeyLength);

        Assert.Equal(exact, FormatKey(exact));
        Assert.Equal(exact, EntityKey.ToKeyString(exact));
    }

    [Fact]
    public void It_treats_the_same_values_as_unset()
    {
        // The values the spec calls "not assigned yet". A library that disagrees on ONE of them
        // addresses rows the other addresses nothing for: `byte Id = 0` was exactly that, read as
        // the key "0" by one library and as unassigned by the other.
        foreach (var value in new object?[] { null, 0, 0L, (short)0, (byte)0, Guid.Empty, string.Empty })
        {
            var mine = EntityKey.IsUnset(value);

            Assert.True(mine, $"DotNetBoost.Media should treat {Describe(value)} as unset.");
            Assert.True(
                IsKeyUnset(value),
                $"{Implementation} treats {Describe(value)} as an assigned key; DotNetBoost.Media treats it " +
                "as unset. One of them would address rows the other believes belong to nothing.");
        }
    }

    [Fact]
    public void It_treats_an_assigned_key_as_set()
    {
        // The other direction, so a library that calls everything unset — and therefore silently
        // writes nothing — fails here rather than looking like it agrees.
        foreach (var value in new object?[] { 1, -1, 1L, (short)1, (byte)1, Guid.NewGuid(), "k" })
        {
            Assert.False(EntityKey.IsUnset(value), $"DotNetBoost.Media should treat {Describe(value)} as set.");
            Assert.False(
                IsKeyUnset(value),
                $"{Implementation} treats {Describe(value)} as unset; DotNetBoost.Media treats it as a key.");
        }
    }

    [Fact]
    public void It_discovers_a_key_marked_with_the_Key_attribute() =>
        Assert.Equal("7", DiscoverKey(new AnnotatedKeyEntity { Code = 7 }));

    [Fact]
    public void It_discovers_a_key_named_Id() =>
        Assert.Equal("7", DiscoverKey(new PlainEntity { Id = 7 }));

    [Fact]
    public void It_discovers_a_key_named_TypeNameId() =>
        Assert.Equal("7", DiscoverKey(new SuffixKeyEntity { SuffixKeyEntityId = 7 }));

    [Fact]
    public void It_prefers_the_Key_attribute_over_a_property_named_Id() =>
        // Order matters, not just membership: an entity with both must key on the annotated one in
        // both libraries, or the same entity is addressed by two different values.
        Assert.Equal("7", DiscoverKey(new AnnotatedAndIdEntity { Code = 7, Id = 99 }));

    [Fact]
    public void It_answers_null_for_a_key_that_is_not_assigned_yet() =>
        Assert.Null(DiscoverKey(new PlainEntity()));

    [Fact]
    public void It_refuses_a_composite_key() =>
        // Refused rather than resolved to one of them: reflection does not order properties, so
        // picking the first would key the entity on whichever this runtime returned.
        Assert.ThrowsAny<Exception>(() => DiscoverKey(new CompositeKeyEntity { Left = 1, Right = 2 }));

    [Fact]
    public void It_ignores_a_property_it_cannot_read() =>
        // A write-only `Id` is not a key. Eligible on name alone, so a library matching by name
        // without checking readability would pick it and then fail to read a value from it.
        Assert.Equal("7", DiscoverKey(new WriteOnlyIdEntity { WriteOnlyIdEntityId = 7 }));

    /// <summary>How a key value reads in a failure message, with its type and nulls spelled out.</summary>
    private static string Describe(object? value) =>
        value is null ? "null" : $"({value.GetType().Name}){(value is string s ? $"\"{s}\"" : value)}";

    /// <summary>
    /// An entity neither library has an attribute on, so both must resolve it to its type name.
    /// Nested here so a consumer gets it with the suite rather than declaring one of its own.
    /// </summary>
    public sealed class PlainEntity
    {
        /// <summary>Its key, discovered by the convention both libraries share.</summary>
        public int Id { get; set; }
    }

    /// <summary>Keyed by attribute rather than by name — the first rule of key discovery.</summary>
    public sealed class AnnotatedKeyEntity
    {
        /// <summary>Its key.</summary>
        [Key]
        public int Code { get; set; }
    }

    /// <summary>Carries both, so the ORDER of the rules is asserted and not just their presence.</summary>
    public sealed class AnnotatedAndIdEntity
    {
        /// <summary>Its key, because <c>[Key]</c> outranks the name.</summary>
        [Key]
        public int Code { get; set; }

        /// <summary>Not its key, despite the name.</summary>
        public int Id { get; set; }
    }

    /// <summary>Keyed by the <c>{TypeName}Id</c> fallback — the third rule.</summary>
    public sealed class SuffixKeyEntity
    {
        /// <summary>Its key.</summary>
        public int SuffixKeyEntityId { get; set; }
    }

    /// <summary>Two <c>[Key]</c> properties, which one column cannot hold.</summary>
    public sealed class CompositeKeyEntity
    {
        /// <summary>Half of a composite key.</summary>
        [Key]
        public int Left { get; set; }

        /// <summary>The other half.</summary>
        [Key]
        public int Right { get; set; }
    }

    /// <summary>
    /// Has a property named <c>Id</c> that cannot be read, so key discovery must skip it and fall
    /// through to the <c>{TypeName}Id</c> rule.
    /// </summary>
    public sealed class WriteOnlyIdEntity
    {
        private int _ignored;

        /// <summary>Write-only, and therefore not a key.</summary>
        public int Id { set => _ignored = value; }

        /// <summary>Its actual key.</summary>
        public int WriteOnlyIdEntityId { get; set; }

        /// <summary>Keeps the compiler from warning that the backing field is never read.</summary>
        public int Ignored() => _ignored;
    }
}
