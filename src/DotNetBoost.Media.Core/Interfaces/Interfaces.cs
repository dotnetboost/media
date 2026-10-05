using DotNetBoost.Media.Core.Models;

namespace DotNetBoost.Media.Core.Interfaces;

/// <summary>
/// Chooses the path a new upload is stored at. Replace it with
/// <c>UseKeyGenerator&lt;T&gt;()</c> to change how files are laid out in the backend —
/// per tenant, per year, flat.
/// <para>
/// Whatever it returns is the path the bytes go to and the path the row records, so it has two
/// hard requirements: it must be unique per file, and it must not be derivable from anything the
/// uploader controls. The built-in generator satisfies both by building the name from the file's
/// own id.
/// </para>
/// </summary>
public interface IMediaKeyGenerator
{
    /// <summary>
    /// The path for a file, relative to the backend's container, with forward slashes and no
    /// leading one.
    /// </summary>
    /// <param name="id">The id already assigned to the file.</param>
    /// <param name="fileName">The sanitised upload name, for its extension.</param>
    /// <param name="category">The upload's bucket, or <c>null</c>.</param>
    string Generate(Guid id, string fileName, string? category);
}

/// <summary>
/// Reads an entity's primary key as the canonical string stored in
/// <see cref="MediaAttachment.OwnerEntityId"/>. The default implementation discovers keys by convention
/// (<c>[Key]</c>, <c>Id</c>, <c>{TypeName}Id</c>); the EF Core provider replaces it with one that
/// reads the model instead.
/// <para>
/// Both halves are rules of <b>DotNetBoost Entity Addressing v1</b> — see
/// <see cref="Metadata.EntityKey.AddressingVersion"/> — so an application running any library that
/// implements the same version keys its entities one way, and the version constant is what says so
/// rather than a comment.
/// </para>
/// </summary>
public interface IEntityKeyAccessor
{
    /// <summary>
    /// The key of <paramref name="entity"/>, or <c>null</c> when it has not been assigned yet
    /// (a store-generated key before the entity is first saved).
    /// </summary>
    /// <exception cref="InvalidOperationException">The type has no discoverable single key.</exception>
    string? GetKey(object entity);
}

/// <summary>
/// Hashes bytes as they stream past. One instance per upload — it is stateful and not thread-safe.
/// </summary>
public interface IChecksumCalculator
{
    /// <summary>Folds a chunk into the running hash.</summary>
    void Append(ReadOnlySpan<byte> data);

    /// <summary>The final hash, lowercase hex. Call once, after the last chunk.</summary>
    string Finish();
}

/// <summary>
/// Notified after files are deleted, so an application can clean up records this library has no
/// idea exist — per-language alt text, search index entries, CDN purges.
/// <para>
/// <b>Nothing cascades out of this library</b>, and that is correct: a media row is not a foreign
/// key to anything, and guessing at an application's own tables is not a storage provider's job.
/// The cost of that is otherwise paid at every call site, and one forgotten call site strands rows
/// nobody will ever look for again — a file deleted through the REST endpoint rather than through
/// your own code is the one that gets missed, because no application code runs on that path at
/// all. Registering a handler here puts the cleanup where the delete is instead of where the
/// caller is.
/// <code>
/// services.AddMedia()
///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
///         .UseFileSystem("./uploads")
///         .OnFilesDeleted(async (services, ids, ct) =&gt;
///         {
///             var db = services.GetRequiredService&lt;AppDbContext&gt;();
///             await db.MediaAltText.Where(t =&gt; ids.Contains(t.MediaFileId)).ExecuteDeleteAsync(ct);
///         })
///         .Build();
/// </code>
/// </para>
/// <para>
/// Registered implementations are invoked in registration order, and <b>exceptions are logged and
/// swallowed</b>. A handler that cannot reach its own table must not turn a completed delete into
/// a failed one: the bytes are already gone, so the caller would retry something that cannot be
/// done again, and one broken handler would take the others down with it.
/// </para>
/// <para>
/// Raised <b>after</b> the files are gone, so a handler cannot read the rows it is being told
/// about. It is given ids, not <c>MediaFileInfo</c>s, for that reason — the thing they named no
/// longer exists.
/// </para>
/// </summary>
public interface IMediaFilesDeletedHandler
{
    /// <summary>
    /// Called with the files that were just deleted. Never empty.
    /// <para>
    /// <c>IMediaManager.DeleteAsync</c> passes the one file it deleted.
    /// <c>IMediaAccessor.DeleteAllAsync</c> passes the batch it deleted — <b>not</b> the files it
    /// merely detached, which are still in use by somebody else and whose records must survive.
    /// </para>
    /// </summary>
    /// <param name="mediaFileIds">The deleted files' ids.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default);
}
