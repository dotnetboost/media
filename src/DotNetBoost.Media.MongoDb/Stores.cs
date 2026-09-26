using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using MongoDB.Driver;
using System.Text.RegularExpressions;

namespace DotNetBoost.Media.MongoDb;

/// <summary>
/// Keeps media rows as documents in one MongoDB collection. The file's own id is the document's
/// <c>_id</c>, so a lookup by id is a primary-key read.
/// <para>
/// There is no ambient transaction to join — MongoDB's are session-scoped and the driver does not
/// hand them out through DI — so an upload's document and an application's own write are separate
/// operations. The engine's ordering still holds: bytes first, document second, and the bytes are
/// removed again if the document cannot be written.
/// </para>
/// </summary>
/// <param name="database">The database holding the collection.</param>
/// <param name="options">Collection naming.</param>
public sealed class MongoMediaStore(IMongoDatabase database, MongoDbOptions options) : IMediaStore
{
    private readonly IMongoCollection<MediaFile> _collection =
        (database ?? throw new ArgumentNullException(nameof(database)))
        .GetCollection<MediaFile>((options ?? throw new ArgumentNullException(nameof(options))).MediaFilesCollection);

    private static readonly FilterDefinitionBuilder<MediaFile> Filter = Builders<MediaFile>.Filter;

    /// <inheritdoc/>
    public async Task AddAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            await _collection.InsertOneAsync(file, options: null, ct).ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new MediaConflictException(
                $"Could not record media file '{file.Id}'. Another document with that id already exists.",
                ex);
        }
    }

    /// <inheritdoc/>
    public async Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default)
    {
        var file = await _collection.Find(Filter.Eq(f => f.Id, id))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return file is null || (!includeDeleted && file.DeletedAt is not null) ? null : file;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Replace rather than a field-by-field update: the engine hands over an instance it has
        // already applied the whole patch to, and a replacement cannot leave one field behind.
        // IsUpsert stays false, so updating a document that is not there is an error, not a
        // silent insert of a file whose row was deleted underneath the caller.
        var result = await _collection
            .ReplaceOneAsync(Filter.Eq(f => f.Id, file.Id), file, new ReplaceOptions { IsUpsert = false }, ct)
            .ConfigureAwait(false);

        if (result.MatchedCount == 0)
            throw new MediaNotFoundException(file.Id);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var result = await _collection.DeleteOneAsync(Filter.Eq(f => f.Id, id), ct).ConfigureAwait(false);
        return result.DeletedCount > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var documents = await _collection
            .Find(Build(query))
            .Sort(Sort(query))
            .Skip(query.Skip)
            .Limit(query.Take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return documents;
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var count = await _collection.CountDocumentsAsync(Build(query), options: null, ct).ConfigureAwait(false);

        // A collection with more than int.MaxValue documents would overflow the contract's int.
        // Saturating is the least wrong answer for a number that only ever drives a page count.
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    /// <inheritdoc/>
    public Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);

        return _collection
            .Find(Filter.And(
                Filter.Eq(f => f.Checksum, checksum),
                Filter.Eq(f => f.Size, size),
                Filter.Eq(f => f.DeletedAt, null)))
            .SortBy(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct)!;
    }

    /// <summary>
    /// A query's ordering. Every ordering ends on the id, so two documents that tie on the sort
    /// field page stably rather than repeating one and skipping another.
    /// </summary>
    private static SortDefinition<MediaFile> Sort(MediaQuery query)
    {
        var sort = Builders<MediaFile>.Sort;

        return (query.SortBy, query.Descending) switch
        {
            (MediaSortBy.FileName, true)  => sort.Descending(f => f.FileName).Descending(f => f.Id),
            (MediaSortBy.FileName, false) => sort.Ascending(f => f.FileName).Ascending(f => f.Id),
            (MediaSortBy.Size, true)      => sort.Descending(f => f.Size).Descending(f => f.Id),
            (MediaSortBy.Size, false)     => sort.Ascending(f => f.Size).Ascending(f => f.Id),
            (_, false)                    => sort.Ascending(f => f.CreatedAt).Ascending(f => f.Id),
            _                             => sort.Descending(f => f.CreatedAt).Descending(f => f.Id)
        };
    }

    /// <summary>
    /// A <see cref="MediaQuery"/> as a filter. Shared by the page and the count so the two cannot
    /// drift — a total that does not match the documents it is a total of is worse than no total.
    /// </summary>
    private static FilterDefinition<MediaFile> Build(MediaQuery query)
    {
        var filters = new List<FilterDefinition<MediaFile>>();

        if (!query.IncludeDeleted)
            filters.Add(Filter.Eq(f => f.DeletedAt, null));

        if (query.OwnerEntityName is { } ownerName)
            filters.Add(Filter.Eq(f => f.OwnerEntityName, ownerName));

        if (query.OwnerEntityId is { } ownerId)
            filters.Add(Filter.Eq(f => f.OwnerEntityId, ownerId));

        if (query.Category is { } category)
            filters.Add(Filter.Eq(f => f.Category, category));

        if (query.UploadedBy is { } uploadedBy)
            filters.Add(Filter.Eq(f => f.UploadedBy, uploadedBy));

        if (query.Checksum is { } checksum)
            filters.Add(Filter.Eq(f => f.Checksum, checksum));

        if (query.CreatedAfter is { } after)
            filters.Add(Filter.Gte(f => f.CreatedAt, after));

        if (query.CreatedBefore is { } before)
            filters.Add(Filter.Lt(f => f.CreatedAt, before));

        if (query.ContentTypePrefix is { } prefix)
        {
            // Anchored, and the caller's value escaped: an unescaped prefix containing regex
            // metacharacters would match more than it should, and an unanchored one could not use
            // an index.
            filters.Add(Filter.Regex(f => f.ContentType, new MongoDB.Bson.BsonRegularExpression("^" + Regex.Escape(prefix))));
        }

        if (query.Search is { Length: > 0 } search)
        {
            // Unanchored and case-insensitive, and the caller's value escaped so that a search for
            // "report.final" does not treat the dot as "any character".
            filters.Add(Filter.Regex(
                f => f.FileName,
                new MongoDB.Bson.BsonRegularExpression(Regex.Escape(search), "i")));
        }

        if (query.Tag is { } tag)
        {
            // Tags are stored comma-surrounded, so ",invoice," matches a whole tag and never a
            // longer one that starts with it.
            filters.Add(Filter.Regex(f => f.Tags, new MongoDB.Bson.BsonRegularExpression(Regex.Escape(MediaTags.Fragment(tag)))));
        }

        return filters.Count == 0 ? Filter.Empty : Filter.And(filters);
    }
}
