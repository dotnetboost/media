namespace DotNetBoost.Media.Core.Models;

/// <summary>
/// One file being uploaded. The stream is read once, straight through to the backend — nothing
/// here is buffered in memory, so a multi-gigabyte upload costs the same as a small one.
/// </summary>
public sealed record MediaUpload
{
    /// <summary>
    /// The bytes. Read from its current position to the end, and <b>not</b> disposed — the caller
    /// owns it, because the caller usually got it from a request it is still serving.
    /// </summary>
    public required Stream Content { get; init; }

    /// <summary>
    /// The name to remember the file by, e.g. <c>invoice-2026-09.pdf</c>. Sanitised before it is
    /// stored: any directory component is dropped, so <c>../../etc/passwd</c> becomes
    /// <c>passwd</c>. Its extension seeds the content type when one is not given.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// The media type, when the caller knows it. Omit it — or send the browser's habitual
    /// <c>application/octet-stream</c> — and it is derived from the file's own leading bytes, and
    /// then from the extension.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>The bucket to sort it into: <c>avatars</c>, <c>invoices</c>. Leads the storage path.</summary>
    public string? Category { get; init; }

    /// <summary>Labels to attach. Normalised — trimmed, lower-cased, de-duplicated.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Extra key/values to keep with the file.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Who is uploading, as the application identifies people. Never inferred.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>
    /// The role to attach the file under when it is uploaded through
    /// <c>IMediaManager.For(entity).UploadAsync(...)</c> — <c>primary</c>, <c>gallery</c>, or
    /// whatever the application means. Ignored by a plain <c>UploadAsync</c>, which attaches the
    /// file to nothing.
    /// <para>
    /// There is deliberately no owner here. An upload stores bytes; what uses them is a
    /// <see cref="MediaAttachment"/>, and routing it through the accessor is what keeps the two
    /// from drifting apart.
    /// </para>
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Writes to a named backend instead of the default one — for a single upload that has to
    /// land somewhere specific. Must be a backend that was registered on the builder.
    /// </summary>
    public string? StorageProvider { get; init; }
}

/// <summary>The entity a file is attached to.</summary>
/// <param name="EntityName">Its logical name, e.g. <c>Product</c>.</param>
/// <param name="EntityId">Its key, formatted invariantly.</param>
public readonly record struct MediaOwner(string EntityName, string EntityId);

/// <summary>
/// A change to a stored file's metadata. Every property is optional, and the ones left unset are
/// left alone — this is a patch, not the full replacement <c>LanguageUpdate</c> is in
/// DotNetBoost.MultiLingual, because the fields here are independent of one another.
/// <para>
/// The bytes, their size, their checksum and their location are not here: they are established
/// by the upload and are not editable. Replacing content means a new upload. Nor are attachments —
/// those are rows of their own, added and removed through <c>IMediaManager.For(entity)</c>.
/// </para>
/// </summary>
public sealed record MediaUpdate
{
    /// <summary>A new display name. The stored path does not change.</summary>
    public string? FileName { get; init; }

    /// <summary>A new bucket. Existing bytes are not moved; only the row changes.</summary>
    public string? Category { get; init; }

    /// <summary>Replaces the file's labels outright. Pass an empty list to clear them.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Replaces the file's metadata outright. Pass an empty dictionary to clear it.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    // Attaching is not a metadata patch. It creates or removes a MediaAttachment row, which the
    // accessor returned by IMediaManager.For(entity) owns — see its AddAsync, SetAsync and
    // DetachAsync. Leaving owner fields here would offer a second, quieter way to do it that could
    // only ever express one owner.
}
