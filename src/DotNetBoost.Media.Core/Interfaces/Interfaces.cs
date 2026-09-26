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
/// <see cref="MediaFile.OwnerEntityId"/>. The default implementation discovers keys by convention
/// (<c>[Key]</c>, <c>Id</c>, <c>{TypeName}Id</c>); the EF Core provider replaces it with one that
/// reads the model instead.
/// <para>
/// Deliberately the same contract as DotNetBoost.MultiLingual's, so an application that uses both
/// libraries keys its entities one way.
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
