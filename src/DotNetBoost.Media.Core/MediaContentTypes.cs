namespace DotNetBoost.Media.Core;

/// <summary>
/// Maps between file extensions and media types, and recognises a file's real format from its
/// leading bytes.
/// <para>
/// A small, curated table rather than the system MIME registry: the registry differs between
/// machines and container images, so the same upload would get a different stored content type in
/// development and in production. This one is part of the library and cannot drift.
/// </para>
/// </summary>
public static class MediaContentTypes
{
    /// <summary>The media type used when nothing is known about the bytes.</summary>
    public const string Octet = "application/octet-stream";

    /// <summary>
    /// How many leading bytes <see cref="Detect"/> needs. The engine buffers exactly this much of
    /// every upload before streaming the rest — it is deliberately tiny.
    /// </summary>
    public const int SignatureLength = 32;

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".jpg"] = "image/jpeg",  [".jpeg"] = "image/jpeg", [".png"]  = "image/png",
        [".gif"] = "image/gif",   [".webp"] = "image/webp", [".bmp"]  = "image/bmp",
        [".tif"] = "image/tiff",  [".tiff"] = "image/tiff", [".svg"]  = "image/svg+xml",
        [".ico"] = "image/x-icon", [".avif"] = "image/avif", [".heic"] = "image/heic",

        // Documents
        [".pdf"]  = "application/pdf",
        [".doc"]  = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"]  = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"]  = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"]  = "application/vnd.oasis.opendocument.text",
        [".ods"]  = "application/vnd.oasis.opendocument.spreadsheet",
        [".rtf"]  = "application/rtf",

        // Text and data
        [".txt"] = "text/plain",   [".csv"]  = "text/csv",   [".md"]  = "text/markdown",
        [".htm"] = "text/html",    [".html"] = "text/html",  [".css"] = "text/css",
        [".json"] = "application/json", [".xml"] = "application/xml", [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",

        // Audio and video
        [".mp3"] = "audio/mpeg",  [".wav"]  = "audio/wav",   [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",   [".flac"] = "audio/flac",  [".aac"] = "audio/aac",
        [".mp4"] = "video/mp4",   [".m4v"]  = "video/mp4",   [".webm"] = "video/webm",
        [".mov"] = "video/quicktime", [".avi"] = "video/x-msvideo", [".mkv"] = "video/x-matroska",

        // Archives
        [".zip"] = "application/zip", [".gz"] = "application/gzip", [".tar"] = "application/x-tar",
        [".7z"]  = "application/x-7z-compressed", [".rar"] = "application/vnd.rar",

        // Fonts
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf"
    };

    /// <summary>
    /// Magic numbers, longest first so that a more specific signature wins over a prefix it
    /// shares with a shorter one. Only formats with a signature distinctive enough to be worth
    /// asserting are here — "unrecognised" is a perfectly good answer, and a better one than a
    /// guess the caller would then be held to.
    /// </summary>
    private static readonly (byte[] Signature, int Offset, string ContentType)[] Signatures =
    [
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, "image/png"),
        ([0x47, 0x49, 0x46, 0x38, 0x37, 0x61],             0, "image/gif"),
        ([0x47, 0x49, 0x46, 0x38, 0x39, 0x61],             0, "image/gif"),
        ([0x52, 0x49, 0x46, 0x46],                         0, "image/webp"),   // refined below
        ([0x25, 0x50, 0x44, 0x46, 0x2D],                   0, "application/pdf"),
        ([0xFF, 0xD8, 0xFF],                               0, "image/jpeg"),
        ([0x42, 0x4D],                                     0, "image/bmp"),
        ([0x49, 0x49, 0x2A, 0x00],                         0, "image/tiff"),
        ([0x4D, 0x4D, 0x00, 0x2A],                         0, "image/tiff"),
        ([0x00, 0x00, 0x01, 0x00],                         0, "image/x-icon"),
        ([0x1F, 0x8B],                                     0, "application/gzip"),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C],             0, "application/x-7z-compressed"),
        ([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07],             0, "application/vnd.rar"),
        ([0x4F, 0x67, 0x67, 0x53],                         0, "application/ogg"),
        ([0x66, 0x4C, 0x61, 0x43],                         0, "audio/flac"),
        ([0x49, 0x44, 0x33],                               0, "audio/mpeg"),
        ([0x77, 0x4F, 0x46, 0x46],                         0, "font/woff"),
        ([0x77, 0x4F, 0x46, 0x32],                         0, "font/woff2"),
        ([0x66, 0x74, 0x79, 0x70],                         4, "video/mp4"),    // ISO base media
        ([0x1A, 0x45, 0xDF, 0xA3],                         0, "video/x-matroska"),
        ([0x50, 0x4B, 0x03, 0x04],                         0, "application/zip"),
        // Windows PE ("MZ") and ELF. Never served as themselves — recognising them is what lets
        // a ".png" that is really an executable be refused.
        ([0x4D, 0x5A],                                     0, "application/vnd.microsoft.portable-executable"),
        ([0x7F, 0x45, 0x4C, 0x46],                         0, "application/x-elf")
    ];

    /// <summary>
    /// The media type for a file name's extension, or <c>null</c> when the extension is unknown
    /// or absent.
    /// </summary>
    public static string? FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension) ? null : FromExtension(extension);
    }

    /// <summary>The media type for an extension (with or without its leading dot), or <c>null</c>.</summary>
    public static string? FromExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var key = extension[0] == '.' ? extension : "." + extension;
        return ByExtension.TryGetValue(key, out var contentType) ? contentType : null;
    }

    /// <summary>
    /// The media type <paramref name="header"/>'s bytes actually are, or <c>null</c> when no
    /// known signature matches. Pass at least <see cref="SignatureLength"/> bytes where the file
    /// is that long.
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        foreach (var (signature, offset, contentType) in Signatures)
        {
            if (header.Length < offset + signature.Length)
                continue;

            if (!header.Slice(offset, signature.Length).SequenceEqual(signature))
                continue;

            // RIFF is a container: WEBP, WAV and AVI all start with it, and the form type at
            // byte 8 is what tells them apart. Without this a .wav would be detected as a
            // .webp and refused for "mismatching" its own correct content type.
            if (contentType == "image/webp")
                return RiffForm(header);

            return contentType;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="declared"/> and <paramref name="detected"/> describe the same
    /// thing. Not string equality: a ZIP signature covers every OOXML and OpenDocument format,
    /// which really are ZIPs, and an ISO base-media signature covers MP4 audio as well as video.
    /// Being strict here would reject correct uploads.
    /// </summary>
    public static bool AreCompatible(string declared, string detected)
    {
        if (string.Equals(declared, detected, StringComparison.OrdinalIgnoreCase))
            return true;

        return detected switch
        {
            "application/zip"  => IsZipContainer(declared),
            "video/mp4"        => declared.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                                  || declared.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
            "application/ogg"  => declared is "audio/ogg" or "video/ogg" or "application/ogg",
            // JPEG's own APP marker varies; some tools write image/jpg.
            "image/jpeg"       => declared is "image/jpg",
            _                  => false
        };
    }

    /// <summary>
    /// Media types a browser will run script from when it renders one as a page of its own.
    /// <para>
    /// This is the set that turns a file store into a stored-XSS vector. Serve a user's upload
    /// inline from your own origin as any of these and its script runs <b>in that origin</b>,
    /// with the session cookie of whoever opened the link — which is why
    /// <c>MapMediaEndpoints()</c> sends them as downloads rather than inline. SVG is on the list
    /// and often surprises people: it is an XML document that may carry a <c>&lt;script&gt;</c>,
    /// not merely a picture.
    /// </para>
    /// <para>
    /// A download disposition does not stop an <c>&lt;img src&gt;</c> or a <c>&lt;video&gt;</c>
    /// from rendering the file — browsers honour <c>Content-Disposition</c> for navigations, not
    /// for embedded resources — so an SVG logo still displays where a page asks for it. What it
    /// stops is the file being opened as a document in its own right.
    /// </para>
    /// <para>
    /// Public because an application serving stored files from its own routes needs the same
    /// answer, and a hand-written list of these is exactly the kind that goes stale.
    /// </para>
    /// </summary>
    public static bool IsActiveContent(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var bare      = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();

        return bare.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
            // XML renders as a document and can pull in an XSLT stylesheet, which scripts.
            || bare.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("text/xsl", StringComparison.OrdinalIgnoreCase)
            // Not script itself: .js is blocked by extension, but the type can still arrive on a
            // file named something else, and a served script is a script.
            || bare.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("application/javascript", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a media type is a format that really is a ZIP archive underneath.</summary>
    private static bool IsZipContainer(string contentType) =>
        contentType.StartsWith("application/vnd.openxmlformats-officedocument.", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("application/vnd.oasis.opendocument.", StringComparison.OrdinalIgnoreCase)
        || contentType is "application/zip"
                       or "application/epub+zip"
                       or "application/java-archive"
                       or "application/vnd.android.package-archive";

    private static string? RiffForm(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
            return null;

        var form = header.Slice(8, 4);

        if (form.SequenceEqual("WEBP"u8)) return "image/webp";
        if (form.SequenceEqual("WAVE"u8)) return "audio/wav";
        if (form.SequenceEqual("AVI "u8)) return "video/x-msvideo";

        return null;
    }
}
