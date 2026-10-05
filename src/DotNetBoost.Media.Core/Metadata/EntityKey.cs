using System.Globalization;

namespace DotNetBoost.Media.Core.Metadata;

/// <summary>
/// Formats an entity's primary key as the string stored in <see cref="Models.MediaAttachment.OwnerEntityId"/>.
/// One column serves <c>int</c>, <c>long</c>, <c>Guid</c> and <c>string</c> keys, so the format
/// has to be stable and culture-independent — a key formatted under <c>ar-SA</c> and read back
/// under <c>en-US</c> must be the same string, or the entity's files stop belonging to it.
/// </summary>
public static class EntityKey
{
    /// <summary>
    /// The entity-addressing spec version this implementation conforms to — <b>DotNetBoost Entity
    /// Addressing v1</b>, as the README sets it out.
    /// <para>
    /// This library and DotNetBoost.MultiLingual resolve entity names and format keys the same way,
    /// independently: the <c>[…Entity("…")]</c> attribute or the type name, proxy types unwrapped;
    /// numbers invariant and GUIDs in <c>"D"</c> format; <see cref="MaxLength"/> for a key and
    /// <see cref="MediaEntityNames.MaxLength"/> for a name. That agreement is what lets an
    /// application join a media row to a translation row on the pair of columns — and nothing
    /// enforced it, which left the guarantee resting on two sets of comments that could drift apart
    /// one release at a time.
    /// </para>
    /// <para>
    /// Every library that shares this addressing publishes the same constant under the same name,
    /// so a consumer can assert the two match and fail at startup rather than discover a mismatch
    /// as rows that stopped joining:
    /// <code>
    /// Assert.Equal(
    ///     DotNetBoost.Media.Core.Metadata.EntityKey.AddressingVersion,
    ///     DotNetBoost.MultiLingual.Core.Metadata.EntityKey.AddressingVersion);
    /// </code>
    /// <c>DotNetBoost.Media.TestSupport.EntityAddressingConformance</c> is that assertion and the
    /// rest of the rules, as a theory to run rather than to hand-write.
    /// </para>
    /// <para>
    /// <b>Bump it for ANY change</b> to entity-name resolution, key discovery, key formatting,
    /// unset-key detection, or either length limit — a change that is invisible in this library is
    /// exactly the kind that silently stops two libraries addressing the same entity.
    /// </para>
    /// </summary>
    public const string AddressingVersion = "1.0";

    /// <summary>
    /// How long a stored key may be. 64 comfortably fits an <c>int</c>, a <c>long</c>, or a
    /// <c>Guid</c> in "D" format (36 characters), with room for a composite string key.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// <paramref name="key"/> as its stored string. Numbers are formatted invariantly, GUIDs in
    /// "D" format, everything else through <see cref="IFormattable"/> where it implements it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The formatted key is empty or longer than <see cref="MaxLength"/>.</exception>
    public static string ToKeyString(object key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var text = key switch
        {
            string s      => s,
            Guid g        => g.ToString("D", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _             => key.ToString() ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("An entity key cannot be empty.");

        if (text.Length > MaxLength)
        {
            throw new InvalidOperationException(
                $"Entity key '{text}' is {text.Length} characters; the column holds {MaxLength}.");
        }

        return text;
    }

    /// <summary>
    /// True when <paramref name="value"/> is the CLR default for its key type — an entity whose
    /// store-generated key has not been assigned yet, and so cannot be addressed.
    /// </summary>
    /// <remarks>
    /// Only the key types listed below are recognised. A value of any OTHER type reads as SET,
    /// even at its own default — a strongly-typed id struct, or <c>ObjectId.Empty</c> — because
    /// Core cannot reference a provider's types to know better.
    /// <para>
    /// A provider whose keys use such a type must therefore return <c>null</c> from its
    /// <see cref="Interfaces.IEntityKeyAccessor"/> for the unassigned value: the accessors are the
    /// only callers of this method, so that is the one place the knowledge exists. Left to this
    /// method, an unsaved entity looks addressable and its files are attached under the default
    /// value's string form.
    /// </para>
    /// <para>
    /// The same rule, over the same types, as DotNetBoost Entity Addressing v1 requires — see
    /// <see cref="AddressingVersion"/>.
    /// </para>
    /// </remarks>
    public static bool IsUnset(object? value) => value switch
    {
        null     => true,
        int i    => i == 0,
        long l   => l == 0,
        short s  => s == 0,
        byte b   => b == 0,
        Guid g   => g == Guid.Empty,
        string s => s.Length == 0,
        _        => false
    };
}
