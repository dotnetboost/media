namespace DotNetBoost.Media.Core;

/// <summary>
/// Base for the expected, caller-facing failures of this library. The API package maps each
/// subtype to an HTTP status; applications can catch this one type to handle them all.
/// </summary>
public abstract class MediaException : Exception
{
    /// <summary>Creates the exception with a caller-facing message.</summary>
    protected MediaException(string message) : base(message) { }

    /// <summary>Creates the exception with a caller-facing message and its cause.</summary>
    protected MediaException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// No live file has that id — it never existed, or it has been deleted. The API package answers
/// it with a 404.
/// </summary>
public sealed class MediaNotFoundException : MediaException
{
    /// <summary>The id that was looked up.</summary>
    public Guid Id { get; }

    /// <summary>Creates the exception for <paramref name="id"/>.</summary>
    public MediaNotFoundException(Guid id)
        : base($"Media file '{id}' was not found.")
    {
        Id = id;
    }
}

/// <summary>
/// The row is there but its bytes are not: the object has been removed from the backend behind
/// this library's back, or the row was restored from a backup the storage account was not.
/// A 404 to the caller, and something to look into on the server.
/// </summary>
public sealed class MediaContentMissingException : MediaException
{
    /// <summary>The file whose bytes are missing, when the row was known.</summary>
    public Guid? Id { get; }

    /// <summary>The backend that should hold them.</summary>
    public string StorageProvider { get; }

    /// <summary>The path they should be at.</summary>
    public string StoragePath { get; }

    /// <summary>Creates the exception for bytes that are not where the row says.</summary>
    public MediaContentMissingException(string storageProvider, string storagePath, Guid? id = null)
        : base(id is { } known
            ? $"Media file '{known}' has no content at '{storagePath}' in the '{storageProvider}' backend. " +
              "The row exists but the object does not."
            : $"There is no object at '{storagePath}' in the '{storageProvider}' backend.")
    {
        Id              = id;
        StorageProvider = storageProvider;
        StoragePath     = storagePath;
    }
}

/// <summary>The upload is larger than the configured limit. The API package answers it with a 413.</summary>
public sealed class MediaTooLargeException : MediaException
{
    /// <summary>How large the upload turned out to be, as far as it was read.</summary>
    public long Size { get; }

    /// <summary>The configured limit.</summary>
    public long Limit { get; }

    /// <summary>Creates the exception for an upload over the limit.</summary>
    public MediaTooLargeException(long size, long limit)
        : base($"The upload is {size:N0} bytes; the limit is {limit:N0}.")
    {
        Size  = size;
        Limit = limit;
    }
}

/// <summary>
/// The upload's media type or extension is not on the allowed list, or is on the blocked one.
/// The API package answers it with a 415.
/// </summary>
public sealed class UnsupportedMediaTypeException : MediaException
{
    /// <summary>The media type that was rejected, when the rejection was about the type.</summary>
    public string? ContentType { get; }

    /// <summary>The extension that was rejected, when it was about the extension.</summary>
    public string? Extension { get; }

    /// <summary>Creates the exception with the reason the upload was rejected.</summary>
    /// <param name="message">What was wrong, and what is allowed.</param>
    /// <param name="contentType">The rejected media type, if any.</param>
    /// <param name="extension">The rejected extension, if any.</param>
    public UnsupportedMediaTypeException(string message, string? contentType = null, string? extension = null)
        : base(message)
    {
        ContentType = contentType;
        Extension   = extension;
    }
}

/// <summary>
/// The upload's bytes are not what its declared media type says they are — a <c>.png</c> whose
/// leading bytes are a Windows executable, for instance. Raised only for formats this library
/// recognises by signature, and only when <c>MediaOptions.VerifyContentType</c> is on.
/// The API package answers it with a 415.
/// </summary>
public sealed class MediaContentMismatchException : MediaException
{
    /// <summary>What the upload claimed to be.</summary>
    public string DeclaredContentType { get; }

    /// <summary>What its leading bytes say it is.</summary>
    public string DetectedContentType { get; }

    /// <summary>Creates the exception for a declared type its content contradicts.</summary>
    public MediaContentMismatchException(string declaredContentType, string detectedContentType)
        : base($"The upload declares '{declaredContentType}' but its content is '{detectedContentType}'.")
    {
        DeclaredContentType = declaredContentType;
        DetectedContentType = detectedContentType;
    }
}

/// <summary>
/// The request is well-formed but conflicts with existing state — a duplicate id, an upload
/// whose path is already taken. The API package answers it with a 409.
/// </summary>
public sealed class MediaConflictException : MediaException
{
    /// <summary>Creates the exception with the rule that was broken.</summary>
    public MediaConflictException(string message) : base(message) { }

    /// <summary>Creates the exception with the rule that was broken, and its cause.</summary>
    public MediaConflictException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A storage backend failed, or is not configured the way the stored rows expect. Not the
/// caller's fault: the API package answers it with a 502, because the application reached a
/// dependency and the dependency is what went wrong.
/// </summary>
public sealed class MediaStorageException : MediaException
{
    /// <summary>The backend involved, when one was identified.</summary>
    public string? StorageProvider { get; }

    /// <summary>Creates the exception with what failed.</summary>
    public MediaStorageException(string message, string? storageProvider = null)
        : base(message)
    {
        StorageProvider = storageProvider;
    }

    /// <summary>Creates the exception with what failed and the backend's own error.</summary>
    public MediaStorageException(string message, Exception innerException, string? storageProvider = null)
        : base(message, innerException)
    {
        StorageProvider = storageProvider;
    }
}

/// <summary>
/// Something about the upload itself is unusable — an empty file name, an unreadable stream,
/// a tag with a comma in it. The API package answers it with a 400.
/// </summary>
public sealed class InvalidMediaException : MediaException
{
    /// <summary>Creates the exception with what was wrong.</summary>
    public InvalidMediaException(string message) : base(message) { }

    /// <summary>Creates the exception with what was wrong, and its cause.</summary>
    public InvalidMediaException(string message, Exception innerException) : base(message, innerException) { }
}
