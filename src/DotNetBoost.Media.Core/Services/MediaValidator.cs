using DotNetBoost.Media.Core.Models;
using System.Buffers;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// Decides whether an upload is acceptable, and what media type it is actually stored as.
/// <para>
/// The order matters and is deliberate: the extension is checked first because it is the cheapest
/// and the blocklist is absolute, then the bytes are sniffed, then the sniffed type is reconciled
/// with the declared one, and only then is the result checked against the allowed types. Sniffing
/// before allow-listing is what stops <c>evil.exe</c> renamed to <c>photo.png</c> from being
/// allowed by an <c>image/*</c> rule.
/// </para>
/// </summary>
internal sealed class MediaValidator(MediaOptions options)
{
    private readonly MediaOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Checks what can be checked before a single byte is read: the name and its extension.
    /// </summary>
    /// <returns>The sanitised file name.</returns>
    /// <exception cref="UnsupportedMediaTypeException">Its extension is blocked or not allowed.</exception>
    public string ValidateName(string? fileName)
    {
        var safe      = MediaPaths.SanitizeFileName(fileName);
        var extension = MediaPaths.ExtensionOf(safe);

        if (extension.Length > 0 && _options.BlockedExtensions.Contains(extension))
        {
            throw new UnsupportedMediaTypeException(
                $"Files with the extension '{extension}' are not accepted.",
                extension: extension);
        }

        if (_options.AllowedExtensions.Count > 0 && !_options.AllowedExtensions.Contains(extension))
        {
            throw new UnsupportedMediaTypeException(
                extension.Length == 0
                    ? $"Files without an extension are not accepted. Allowed: {string.Join(", ", _options.AllowedExtensions.Order(StringComparer.Ordinal))}."
                    : $"Files with the extension '{extension}' are not accepted. Allowed: {string.Join(", ", _options.AllowedExtensions.Order(StringComparer.Ordinal))}.",
                extension: extension);
        }

        return safe;
    }

    /// <summary>
    /// Reads enough of <paramref name="content"/> to recognise its format, settles on the media
    /// type to store, and returns a stream that replays what was read.
    /// <para>
    /// The returned stream is what the backend should be given — the original has been advanced
    /// past its header and, being a request body, cannot be rewound.
    /// </para>
    /// </summary>
    /// <exception cref="MediaContentMismatchException">The bytes contradict the declared type.</exception>
    /// <exception cref="UnsupportedMediaTypeException">The resulting type is not allowed.</exception>
    public async Task<(string ContentType, Stream Content)> ResolveContentTypeAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(MediaContentTypes.SignatureLength);
        int headerLength;

        try
        {
            headerLength = await ReadAtLeastAsync(content, header.AsMemory(0, MediaContentTypes.SignatureLength), ct)
                .ConfigureAwait(false);

            var detected = MediaContentTypes.Detect(header.AsSpan(0, headerLength));
            var declared = Normalize(declaredContentType);

            var resolved = Reconcile(declared, detected, fileName);

            if (!_options.IsContentTypeAllowed(resolved))
            {
                throw new UnsupportedMediaTypeException(
                    $"'{resolved}' is not an accepted media type. Allowed: " +
                    $"{string.Join(", ", _options.AllowedContentTypes.Order(StringComparer.Ordinal))}.",
                    contentType: resolved);
            }

            // Copy out of the pooled buffer: the stream outlives this method, and the array goes
            // back to the pool in `finally`.
            return (resolved, new PrefixStream(header.AsSpan(0, headerLength).ToArray(), content));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>
    /// The media type to store, given what the caller said and what the bytes say.
    /// <list type="bullet">
    ///   <item>Nothing recognised in the bytes: take the declared type, or the extension's, or the fallback.</item>
    ///   <item>Recognised and compatible with the declared type: take the declared one — it is the more specific of the two (<c>.docx</c> rather than "a ZIP").</item>
    ///   <item>Recognised and the caller declared nothing: take the detected one.</item>
    ///   <item>Recognised and it contradicts the declared type: refuse, unless verification is off, in which case the DETECTED type wins — storing a lie about the bytes is worse than rejecting them.</item>
    /// </list>
    /// </summary>
    private string Reconcile(string? declared, string? detected, string fileName)
    {
        if (detected is null)
            return declared ?? MediaContentTypes.FromFileName(fileName) ?? _options.FallbackContentType;

        if (declared is null)
            return detected;

        if (MediaContentTypes.AreCompatible(declared, detected))
            return declared;

        if (_options.VerifyContentType)
            throw new MediaContentMismatchException(declared, detected);

        return detected;
    }

    /// <summary>
    /// A declared content type worth using: trimmed, without parameters (<c>; charset=…</c>), and
    /// <c>null</c> for the placeholder browsers send when they have no idea — which is not a
    /// claim about the file and should not be treated as one.
    /// </summary>
    private static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var bare      = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();

        return bare.Length == 0 || bare == MediaContentTypes.Octet ? null : bare;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> as far as the stream allows. <c>ReadAsync</c> may return
    /// fewer bytes than asked for at any time — over a network stream it usually does — and a
    /// single call would leave the signature half-read and the format unrecognised.
    /// </summary>
    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    /// <summary>Normalises and checks the parts of an upload that are neither name nor bytes.</summary>
    /// <exception cref="InvalidMediaException">A tag or the metadata is unusable.</exception>
    public static (string? Tags, string? Metadata) ValidateAnnotations(
        IReadOnlyList<string>? tags,
        IReadOnlyDictionary<string, string>? metadata)
    {
        try
        {
            return (MediaTags.Join(tags), MediaMetadata.Serialize(metadata));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidMediaException(ex.Message, ex);
        }
    }
}
