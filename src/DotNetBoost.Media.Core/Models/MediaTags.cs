using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace DotNetBoost.Media.Core.Models;

/// <summary>
/// The stored form of <see cref="MediaFile.Tags"/>. Tags are kept in one column, comma-delimited
/// AND comma-surrounded (<c>,invoice,2026,</c>), which is what makes a whole-tag match a plain
/// <c>LIKE '%,invoice,%'</c> on every engine this library supports — no join table, and no risk
/// of <c>invoice</c> matching <c>invoiced</c>.
/// </summary>
public static class MediaTags
{
    /// <summary>The longest a single tag may be.</summary>
    public const int MaxTagLength = 64;

    /// <summary>The most tags one file may carry.</summary>
    public const int MaxTags = 32;

    /// <summary>The longest the stored column can get, given the two limits above plus delimiters.</summary>
    public const int MaxLength = (MaxTagLength + 1) * MaxTags + 1;

    /// <summary>
    /// The stored form of <paramref name="tags"/>: trimmed, lower-cased, de-duplicated, ordered,
    /// and wrapped in delimiters. <c>null</c> when there are none, so "no tags" is a NULL column
    /// rather than a lone comma.
    /// </summary>
    /// <exception cref="ArgumentException">A tag is empty, too long, or contains a comma.</exception>
    public static string? Join(IEnumerable<string>? tags)
    {
        if (tags is null)
            return null;

        var normalized = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            var trimmed = tag?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (trimmed.Contains(',', StringComparison.Ordinal))
                throw new ArgumentException($"Tag '{trimmed}' contains a comma, which separates tags.", nameof(tags));

            if (trimmed.Length > MaxTagLength)
                throw new ArgumentException($"Tag '{trimmed}' is longer than {MaxTagLength} characters.", nameof(tags));

            normalized.Add(trimmed.ToLowerInvariant());
        }

        if (normalized.Count == 0)
            return null;

        if (normalized.Count > MaxTags)
            throw new ArgumentException($"A file may carry at most {MaxTags} tags; {normalized.Count} were given.", nameof(tags));

        var sb = new StringBuilder(",");
        foreach (var tag in normalized)
            sb.Append(tag).Append(',');

        return sb.ToString();
    }

    /// <summary>The tags in a stored value, or an empty list.</summary>
    public static IReadOnlyList<string> Split(string? stored) =>
        string.IsNullOrEmpty(stored)
            ? []
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The fragment that matches <paramref name="tag"/> as a whole tag inside the stored column,
    /// for a store composing a <c>LIKE</c>. Returns <c>,invoice,</c> for <c>invoice</c>.
    /// </summary>
    public static string Fragment(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return $",{tag.Trim().ToLowerInvariant()},";
    }
}

/// <summary>
/// The stored form of <see cref="MediaFile.Metadata"/>: a flat JSON object of strings. Flat and
/// string-valued on purpose — it is written by applications and read back by this library, and a
/// shape that cannot nest is one that cannot surprise either of them.
/// </summary>
public static class MediaMetadata
{
    /// <summary>The longest the serialized column may get.</summary>
    public const int MaxLength = 4000;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// The stored form of <paramref name="metadata"/>, or <c>null</c> when it is empty.
    /// </summary>
    /// <exception cref="ArgumentException">The serialized object exceeds <see cref="MaxLength"/>.</exception>
    public static string? Serialize(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var json = JsonSerializer.Serialize(metadata, SerializerOptions);

        if (json.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Serialized metadata is {json.Length} characters; the column holds {MaxLength}. " +
                "Keep large payloads in your own table and reference the media id from it.",
                nameof(metadata));
        }

        return json;
    }

    /// <summary>
    /// The dictionary in a stored value, or an empty one. Unparseable JSON reads as empty rather
    /// than throwing: metadata is decoration, and a row written by an older version of an
    /// application should not make the file itself unreadable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return ReadOnlyDictionary<string, string>.Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stored, SerializerOptions);
            return parsed is null
                ? ReadOnlyDictionary<string, string>.Empty
                : new ReadOnlyDictionary<string, string>(parsed);
        }
        catch (JsonException)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }
    }
}
