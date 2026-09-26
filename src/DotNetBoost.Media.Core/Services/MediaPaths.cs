using DotNetBoost.Media.Core.Interfaces;
using System.Globalization;
using System.Text;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// The default <see cref="IMediaKeyGenerator"/>: <c>{category}/{yyyy}/{MM}/{dd}/{id}{ext}</c>,
/// e.g. <c>avatars/2026/09/27/9f1cb3e0d4a94f7ab0e2c1d5f6a78901.png</c>.
/// <para>
/// The name is the file's own id, not the uploaded name. That is not cosmetic: a path derived
/// from user input is a path the user can collide with, guess, or aim at another directory, and a
/// path derived from an id can be none of those. The original name still reaches the user — it is
/// in the row, and the download sets <c>Content-Disposition</c> from it.
/// </para>
/// <para>
/// The date hierarchy is for the humans and tools that eventually look in the container: it keeps
/// any one directory small, makes "everything from last September" a prefix, and matches how blob
/// lifecycle rules are usually written. The date is UTC, so a file does not move between folders
/// depending on where the server is.
/// </para>
/// </summary>
/// <param name="timeProvider">Supplies the date. Injected so tests can pin it.</param>
public sealed class DateHierarchyKeyGenerator(TimeProvider timeProvider) : IMediaKeyGenerator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc/>
    public string Generate(Guid id, string fileName, string? category)
    {
        var now       = _timeProvider.GetUtcNow();
        var extension = MediaPaths.ExtensionOf(fileName);
        var folder    = MediaPaths.Segment(category);

        var path = new StringBuilder();

        if (folder is not null)
            path.Append(folder).Append('/');

        path.Append(now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture))
            .Append('/')
            .Append(id.ToString("N", CultureInfo.InvariantCulture))
            .Append(extension);

        return path.ToString();
    }
}

/// <summary>
/// Cleans the parts of an upload that came from outside: its file name, its category, and the
/// storage path built from them.
/// <para>
/// Every method here is a boundary. A file name arrives from a browser and may be
/// <c>..\..\web.config</c>; a category arrives from application code and may hold a slash; a path
/// read back from a row is trusted no more than the day it was written. Getting any of them
/// wrong is a path-traversal bug, so they are all sanitised in one place rather than at each call
/// site.
/// </para>
/// </summary>
public static class MediaPaths
{
    /// <summary>The longest a stored file name may be, matching the column.</summary>
    public const int MaxFileNameLength = 260;

    /// <summary>The longest a storage path may be, matching the column.</summary>
    public const int MaxPathLength = 1024;

    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// The safe form of an uploaded file name: no directory component, no control characters, no
    /// characters that are special to a file system, and never empty.
    /// <code>
    /// "../../etc/passwd"     -> "passwd"
    /// "C:\\Users\\a\\x.png"  -> "x.png"
    /// "  report .pdf "       -> "report .pdf"
    /// ""                     -> "file"
    /// </code>
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "file";

        // Take the last segment of whatever was sent, splitting on BOTH separators — a Windows
        // client sends backslashes and a POSIX server would not otherwise treat them as such.
        var lastSeparator = fileName.LastIndexOfAny(Separators);
        var name          = lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;

        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Control characters, the shell and path metacharacters, and the NTFS stream
            // separator. Spaces and dots survive — they are ordinary in real file names.
            if (char.IsControl(c) || c is '"' or '<' or '>' or '|' or ':' or '*' or '?' or '\0')
                continue;

            cleaned.Append(c);
        }

        var result = cleaned.ToString().Trim();

        // "." and ".." survive the loop above intact, and both are directory references rather
        // than names. So does a name of nothing but spaces.
        if (result.Length == 0 || result == "." || result == "..")
            return "file";

        if (result.Length > MaxFileNameLength)
        {
            // Truncate the stem, not the extension: the extension is what a browser and the
            // content-type check both key off.
            var extension = ExtensionOf(result);
            var stemRoom  = MaxFileNameLength - extension.Length;
            result = stemRoom > 0
                ? string.Concat(result.AsSpan(0, stemRoom), extension)
                : result[..MaxFileNameLength];
        }

        return result;
    }

    /// <summary>
    /// The lowercase extension of a file name, with its dot, or an empty string. Anything that is
    /// not a plausible extension — too long, or not alphanumeric — is dropped rather than carried
    /// into a storage path.
    /// </summary>
    public static string ExtensionOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var extension = Path.GetExtension(fileName);

        if (extension.Length is < 2 or > 16)
            return string.Empty;

        foreach (var c in extension.AsSpan(1))
        {
            if (!char.IsLetterOrDigit(c))
                return string.Empty;
        }

        return extension.ToLowerInvariant();
    }

    /// <summary>
    /// A single path segment built from <paramref name="value"/> — lowercase, with anything that
    /// is not a letter, digit, dash or underscore replaced by a dash. <c>null</c> when nothing
    /// usable is left, so a caller can simply omit the segment.
    /// </summary>
    public static string? Segment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var segment = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            segment.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');

        var result = segment.ToString().Trim('-');
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Checks a storage path before it is used against a backend, and returns it in canonical
    /// form: forward slashes, no leading slash, no empty or relative segment.
    /// <para>
    /// Called by every backend on the way in, including for paths read back from the store. A row
    /// is data like any other — it may have been written by an older version, or by something
    /// that got at the database — and the cost of checking it again is a loop over a short string.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidMediaException">The path is empty, too long, absolute, or escapes its container.</exception>
    public static string Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidMediaException("A storage path cannot be empty.");

        var normalized = path.Replace('\\', '/').Trim('/');

        if (normalized.Length == 0)
            throw new InvalidMediaException("A storage path cannot be empty.");

        if (normalized.Length > MaxPathLength)
            throw new InvalidMediaException($"Storage path is {normalized.Length} characters; the column holds {MaxPathLength}.");

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0)
                throw new InvalidMediaException($"Storage path '{path}' has an empty segment.");

            if (segment is "." or "..")
                throw new InvalidMediaException($"Storage path '{path}' contains a relative segment, which could escape the container.");

            foreach (var c in segment)
            {
                if (char.IsControl(c) || c == '\0')
                    throw new InvalidMediaException($"Storage path '{path}' contains a control character.");
            }
        }

        return normalized;
    }
}
