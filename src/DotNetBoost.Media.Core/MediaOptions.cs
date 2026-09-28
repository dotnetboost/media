namespace DotNetBoost.Media.Core;

/// <summary>
/// Runtime options for the media engine. Registered as a singleton by <c>services.AddMedia()</c>
/// and configured through <see cref="MediaBuilder"/>.
/// <para>
/// The defaults are the safe ones. Uploads are capped, executables are refused, content types are
/// checked against the bytes, and deletes keep an audit row — an application that wants any of
/// that relaxed has to say so.
/// </para>
/// </summary>
public sealed class MediaOptions
{
    private long _maxFileSizeBytes = 100L * 1024 * 1024;
    private TimeSpan _defaultUrlExpiry = TimeSpan.FromHours(1);

    /// <summary>
    /// The largest upload accepted, in bytes. Default: 100 MB. Enforced while the stream is read,
    /// so an upload that lies about its length is still stopped — at the limit, not after it.
    /// <para>
    /// This is the library's limit. ASP.NET Core has its own (<c>MaxRequestBodySize</c>, 30 MB by
    /// default, and Kestrel's own cap), and the smaller of the two is what actually applies —
    /// raise both or neither. Set via <c>WithMaxFileSize()</c>.
    /// </para>
    /// </summary>
    public long MaxFileSizeBytes
    {
        get => _maxFileSizeBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            _maxFileSizeBytes = value;
        }
    }

    /// <summary>
    /// Media types accepted, e.g. <c>image/png</c>, or a family as <c>image/*</c>. Empty — the
    /// default — accepts anything the extension rules below allow. Set via
    /// <c>AllowContentTypes()</c>.
    /// </summary>
    public ISet<string> AllowedContentTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// File extensions accepted, each with its leading dot. Empty — the default — accepts
    /// anything not in <see cref="BlockedExtensions"/>. Set via <c>AllowExtensions()</c>.
    /// </summary>
    public ISet<string> AllowedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// File extensions always refused, checked before <see cref="AllowedExtensions"/> so an
    /// allow-list cannot accidentally re-admit one. Defaults to the executable and script
    /// extensions that turn a file store into a way to hand users malware.
    /// <para>
    /// This is a floor, not a security boundary: an allow-list of the few types you actually
    /// serve is the stronger control, and content-type verification is what stops a renamed file.
    /// </para>
    /// </summary>
    public ISet<string> BlockedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".scr", ".pif", ".msi", ".msp", ".cpl",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".jar", ".app", ".dmg", ".sh", ".bash", ".zsh", ".run"
    };

    /// <summary>
    /// Whether the bytes are hashed (SHA-256) while they are written. Default: <c>true</c>. The
    /// hash is computed on the stream already passing through, so it costs no extra read; turning
    /// it off saves only the hashing itself, and also disables
    /// <see cref="DeduplicateByChecksum"/>.
    /// </summary>
    public bool ComputeChecksum { get; set; } = true;

    /// <summary>
    /// Whether an upload whose bytes are already stored returns the existing file instead of a
    /// second copy. Default: <c>false</c>.
    /// <para>
    /// It is off by default because the saving is not free: the returned row carries the FIRST
    /// upload's name, category and uploader, so two callers who "uploaded" a file share
    /// one row and one lifetime — deleting it deletes it for both. Turn it on where the files are
    /// genuinely interchangeable (content-addressed assets), not where they are somebody's
    /// document.
    /// </para>
    /// Requires <see cref="ComputeChecksum"/>.
    /// </summary>
    public bool DeduplicateByChecksum { get; set; }

    /// <summary>
    /// Whether a deleted file keeps its row, with <c>DeletedAt</c> set. Default: <c>true</c>.
    /// <para>
    /// The bytes go either way. What soft delete keeps is the record that the file existed — its
    /// name, size and who uploaded it — which is usually what an audit or a support question
    /// needs. It is not a recycle bin.
    /// </para>
    /// <para>
    /// It does not keep the file's attachments. Those are removed outright either way: a
    /// soft-deleted file that still read as attached to a live entity would be worse than no record
    /// at all.
    /// </para>
    /// </summary>
    public bool SoftDelete { get; set; } = true;

    /// <summary>
    /// Whether an upload's declared media type is checked against its leading bytes, for the
    /// formats this library recognises by signature. Default: <c>true</c>.
    /// <para>
    /// This is what stops a <c>.png</c> that is really an executable, which matters the moment a
    /// browser is pointed at the file. An unrecognised format is accepted — the check can only
    /// speak about signatures it knows.
    /// </para>
    /// </summary>
    public bool VerifyContentType { get; set; } = true;

    /// <summary>
    /// How long a generated URL stays valid when the caller does not say. Default: 1 hour.
    /// Applies to the backends that mint signed links; ignored by those that do not.
    /// </summary>
    public TimeSpan DefaultUrlExpiry
    {
        get => _defaultUrlExpiry;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _defaultUrlExpiry = value;
        }
    }

    /// <summary>
    /// Whether the application checks at startup that every configured backend is reachable and
    /// its container exists. Default: <c>true</c>. Turn it off via <c>DisableStartupValidation()</c>,
    /// or run it yourself at a moment you choose with <c>services.ValidateMediaAsync()</c>.
    /// </summary>
    public bool ValidateStorageAtStartup { get; set; } = true;

    /// <summary>
    /// The media type recorded when nothing better can be determined. Default:
    /// <c>application/octet-stream</c> — the honest answer, and the one that makes a browser
    /// download rather than render.
    /// </summary>
    public string FallbackContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Whether <paramref name="contentType"/> passes <see cref="AllowedContentTypes"/>. An empty
    /// list allows everything; <c>image/*</c> allows a family.
    /// </summary>
    public bool IsContentTypeAllowed(string contentType)
    {
        if (AllowedContentTypes.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        if (AllowedContentTypes.Contains(contentType))
            return true;

        var slash = contentType.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && AllowedContentTypes.Contains(string.Concat(contentType.AsSpan(0, slash + 1), "*"));
    }
}
