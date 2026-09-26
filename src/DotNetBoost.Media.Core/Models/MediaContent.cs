namespace DotNetBoost.Media.Core.Models;

/// <summary>
/// An open read over a stored file: the bytes, plus the metadata an HTTP response needs to
/// describe them. Dispose it — that closes the underlying network or file stream.
/// </summary>
public sealed class MediaContent : IAsyncDisposable, IDisposable
{
    /// <summary>Creates a content handle over an open stream.</summary>
    /// <param name="stream">The bytes, positioned at the start. Owned by this instance.</param>
    /// <param name="file">The row the stream came from.</param>
    public MediaContent(Stream stream, MediaFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(file);

        Stream = stream;
        File   = file;
    }

    /// <summary>
    /// The bytes. Forward-only on some backends and seekable on others — do not assume
    /// <see cref="System.IO.Stream.Length"/> works; <see cref="MediaFileInfo.Size"/> always does.
    /// </summary>
    public Stream Stream { get; }

    /// <summary>The file this is the content of.</summary>
    public MediaFileInfo File { get; }

    /// <summary>The media type to serve it as.</summary>
    public string ContentType => File.ContentType;

    /// <summary>The name to offer it under.</summary>
    public string FileName => File.FileName;

    /// <inheritdoc/>
    public void Dispose() => Stream.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// What a storage backend reports after writing the bytes: where they landed, and what it
/// actually measured while writing them.
/// </summary>
/// <param name="Container">The container, bucket or root directory they went into.</param>
/// <param name="Path">The path inside it, with forward slashes.</param>
/// <param name="Size">
/// Bytes written, counted by the backend. This — not anything the caller claimed — is what is
/// stored, so a lying <c>Content-Length</c> cannot produce a row that disagrees with the file.
/// </param>
/// <param name="Checksum">
/// Lowercase hex SHA-256 of what was written, or <c>null</c> when the engine did not ask for one.
/// </param>
public readonly record struct StoredObject(string Container, string Path, long Size, string? Checksum);

/// <summary>What a backend is being asked to write.</summary>
/// <param name="Content">The bytes, read from the current position to the end and not disposed.</param>
/// <param name="Path">The path to write them to, inside the backend's configured container.</param>
/// <param name="ContentType">The media type to record with them, where the backend stores one.</param>
/// <param name="ComputeChecksum">Whether to hash the bytes while writing them.</param>
public readonly record struct MediaWrite(Stream Content, string Path, string ContentType, bool ComputeChecksum);

/// <summary>
/// How a link to a stored file should be handed out. Backends that can mint a time-limited,
/// signed URL (Azure SAS, S3 pre-signed) honour all of it; a backend that only serves files over
/// the application's own routes ignores the expiry, because its link does not carry one.
/// </summary>
public sealed record MediaUrlOptions
{
    /// <summary>
    /// How long the link stays valid. <c>null</c> takes <c>MediaOptions.DefaultUrlExpiry</c>
    /// (1 hour by default).
    /// </summary>
    public TimeSpan? ExpiresIn { get; init; }

    /// <summary>
    /// Ask the browser to download rather than display, under
    /// <see cref="DownloadFileName"/> — a <c>Content-Disposition: attachment</c> on the signed
    /// response where the backend supports overriding it.
    /// </summary>
    public bool AsAttachment { get; init; }

    /// <summary>The name to download under. Defaults to the file's own name.</summary>
    public string? DownloadFileName { get; init; }
}
