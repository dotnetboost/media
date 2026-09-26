using DotNetBoost.Media.Core.Models;

namespace DotNetBoost.Media.Core.Interfaces;

/// <summary>
/// Persistence contract for the media table. Implement it to put the rows somewhere this library
/// does not ship a provider for. Implementations are deliberately dumb: validation, content-type
/// sniffing, checksums, deduplication and the ordering of "bytes first, row second" all live in
/// the engine behind <see cref="IMediaManager"/>.
/// <para>
/// Nothing here deletes bytes. A store removes rows; the engine removes the bytes through the
/// backend that wrote them, and does so first.
/// </para>
/// </summary>
public interface IMediaStore
{
    /// <summary>
    /// Stores a new row. The id is already assigned — the engine needs it to derive the storage
    /// path before the bytes are written.
    /// </summary>
    /// <exception cref="MediaConflictException">A row with that id already exists.</exception>
    Task AddAsync(MediaFile file, CancellationToken ct = default);

    /// <summary>
    /// The row with this id, or <c>null</c>.
    /// </summary>
    /// <param name="id">The file's id.</param>
    /// <param name="includeDeleted">
    /// Return a soft-deleted row rather than <c>null</c>. The engine passes <c>true</c> only where
    /// it genuinely means to see one.
    /// </param>
    /// <param name="ct">Cancels the operation.</param>
    Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>
    /// Saves changes to an existing row, matched on its id. Every mutable column is written —
    /// the engine has already applied the patch to the instance it read.
    /// </summary>
    /// <exception cref="MediaNotFoundException">No row has that id.</exception>
    Task UpdateAsync(MediaFile file, CancellationToken ct = default);

    /// <summary>Removes the row outright. Succeeds silently when it does not exist.</summary>
    /// <returns><c>true</c> if a row was removed.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>The rows matching <paramref name="query"/>, in its order, over its page.</summary>
    Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default);

    /// <summary>How many rows match <paramref name="query"/>, ignoring its <c>Skip</c> and <c>Take</c>.</summary>
    Task<int> CountAsync(MediaQuery query, CancellationToken ct = default);

    /// <summary>
    /// A live row whose bytes hash to <paramref name="checksum"/> and are exactly
    /// <paramref name="size"/> long, or <c>null</c>. Backs deduplication; the size is compared as
    /// well as the hash so that a hash collision cannot serve one file's bytes as another's.
    /// </summary>
    Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default);
}
