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
    /// <b>Bump it for ANY change</b> to name resolution, key formatting or the length limits — a
    /// change that is invisible in this library is exactly the kind that silently stops two
    /// libraries addressing the same entity.
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
}
