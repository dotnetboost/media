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
