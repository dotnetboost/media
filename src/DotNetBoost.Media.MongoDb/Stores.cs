using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Text.RegularExpressions;

namespace DotNetBoost.Media.MongoDb;

/// <summary>
/// Keeps media rows as documents in one MongoDB collection, and the attachments linking them to
/// entities in another. Each document's own id is its <c>_id</c>, so a lookup by id is a
/// primary-key read.
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

    private readonly IMongoCollection<MediaAttachment> _attachments =
        (database ?? throw new ArgumentNullException(nameof(database)))
        .GetCollection<MediaAttachment>((options ?? throw new ArgumentNullException(nameof(options))).MediaAttachmentsCollection);

    /// <summary>
    /// The files collection's name, for the one read that joins to it by name rather than through
    /// its typed collection — a <c>$lookup</c> stage names its foreign collection as a string.
    /// </summary>
    private readonly string _filesCollectionName = options.MediaFilesCollection;

    private static readonly FilterDefinitionBuilder<MediaFile> Filter = Builders<MediaFile>.Filter;

    private static readonly FilterDefinitionBuilder<MediaAttachment> Link = Builders<MediaAttachment>.Filter;

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

        var filter = await BuildAsync(query, ct).ConfigureAwait(false);

        // The entity's own order lives on the attachment, and MongoDB cannot sort one collection by
        // a field in another. So the ordering keys come from the links that were just read, and the
        // sort happens here — over the matched files only, which for one entity's media is the
        // handful it attached, and never more than MediaQuery.MaxTake after the slice.
        if (query.SortBy == MediaSortBy.SortOrder)
        {
            var keys      = await OrderingKeysAsync(query, ct).ConfigureAwait(false);
            var unordered = await _collection.Find(filter).ToListAsync(ct).ConfigureAwait(false);

            var ordered = query.Descending
                ? unordered
                    .OrderByDescending(f => Key(keys, f.Id).Role, StringComparer.Ordinal)
                    .ThenByDescending(f => Key(keys, f.Id).SortOrder)
                    .ThenByDescending(f => f.Id)
                : unordered
                    .OrderBy(f => Key(keys, f.Id).Role, StringComparer.Ordinal)
                    .ThenBy(f => Key(keys, f.Id).SortOrder)
                    .ThenBy(f => f.Id);

            return [.. ordered.Skip(query.Skip).Take(query.Take)];
        }

        return await _collection
            .Find(filter)
            .Sort(Sort(query))
            .Skip(query.Skip)
            .Limit(query.Take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filter = await BuildAsync(query, ct).ConfigureAwait(false);
        var count  = await _collection.CountDocumentsAsync(filter, options: null, ct).ConfigureAwait(false);

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
    /// <para>
    /// Asynchronous because an owner filter has to be resolved against the attachment collection
    /// first: MongoDB has no join inside a <c>find</c>, so the links are read and their file ids
    /// become an <c>_id $in</c>. Two round trips rather than one, over an index that covers the
    /// first exactly. An <c>$lookup</c> aggregation would make it one, at the price of a pipeline
    /// that cannot reuse the <c>find</c> path every other query here goes through.
    /// </para>
    /// </summary>
    private async Task<FilterDefinition<MediaFile>> BuildAsync(MediaQuery query, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<MediaFile>>();

        if (!query.IncludeDeleted)
            filters.Add(Filter.Eq(f => f.DeletedAt, null));

        // Only when an owner is asked for: without one the attachment collection is never touched,
        // so a file linked to nine entities comes back once and an unattached file comes back at all.
        if (query.OwnerEntityName is not null)
        {
            var attached = await _attachments
                .Distinct<Guid>(new ExpressionFieldDefinition<MediaAttachment, Guid>(a => a.MediaFileId),
                    OwnerFilter(query), options: null, ct)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            filters.Add(Filter.In(f => f.Id, attached));
        }

        if (query.Unattached)
        {
            // Every id any entity holds, which is a covered distinct on the one indexed field.
            var attached = await _attachments
                .Distinct<Guid>(new ExpressionFieldDefinition<MediaAttachment, Guid>(a => a.MediaFileId),
                    Link.Empty, options: null, ct)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            filters.Add(Filter.Nin(f => f.Id, attached));
        }

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

    /// <summary>The links a query's owner filter matches.</summary>
    private static FilterDefinition<MediaAttachment> OwnerFilter(MediaQuery query)
    {
        var filters = new List<FilterDefinition<MediaAttachment>>
        {
            Link.Eq(a => a.OwnerEntityName, query.OwnerEntityName)
        };

        if (query.OwnerEntityId is { } ownerId)
            filters.Add(Link.Eq(a => a.OwnerEntityId, ownerId));

        if (query.Role is { } role)
            filters.Add(Link.Eq(a => a.Role, role));

        return Link.And(filters);
    }

    /// <summary>
    /// Each matched file's place in the owner's order: its lowest role, then its lowest position —
    /// the same definition the relational stores express as two correlated subqueries, so an
    /// application reads the same order whichever backend it is on.
    /// </summary>
    private async Task<Dictionary<Guid, (string Role, int SortOrder)>> OrderingKeysAsync(
        MediaQuery query, CancellationToken ct)
    {
        var links = await _attachments
            .Find(OwnerFilter(query))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return links
            .GroupBy(a => a.MediaFileId)
            .ToDictionary(
                g => g.Key,
                g => (Role: g.Select(a => a.Role).Min(StringComparer.Ordinal)!, SortOrder: g.Min(a => a.SortOrder)));
    }

    private static (string Role, int SortOrder) Key(
        Dictionary<Guid, (string Role, int SortOrder)> keys, Guid id) =>
        keys.TryGetValue(id, out var key) ? key : (string.Empty, 0);

    /// <inheritdoc/>
    public async Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        try
        {
            await _attachments.InsertOneAsync(attachment, options: null, ct).ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Either the unique index on (file, owner, role) or the _id — both mean this link is
            // already recorded, which is what the engine's pre-check normally catches and this
            // backstops under a race.
            throw new MediaConflictException(
                $"Media file '{attachment.MediaFileId}' is already attached to {attachment.OwnerEntityName} " +
                $"{attachment.OwnerEntityId} as '{attachment.Role}'.",
                ex);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var result = await _attachments
            .DeleteOneAsync(Link.Eq(a => a.Id, attachmentId), ct)
            .ConfigureAwait(false);

        return result.DeletedCount > 0;
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default)
    {
        var filters = new List<FilterDefinition<MediaAttachment>>
        {
            Link.Eq(a => a.OwnerEntityName, owner.EntityName),
            Link.Eq(a => a.OwnerEntityId, owner.EntityId)
        };

        if (role is not null)
            filters.Add(Link.Eq(a => a.Role, role));

        var result = await _attachments.DeleteManyAsync(Link.And(filters), ct).ConfigureAwait(false);
        return Saturate(result.DeletedCount);
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default)
    {
        var result = await _attachments
            .DeleteManyAsync(Link.Eq(a => a.MediaFileId, mediaFileId), ct)
            .ConfigureAwait(false);

        return Saturate(result.DeletedCount);
    }

    /// <inheritdoc/>
    public async Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        // Replaced whole, as the file document is, and never upserted: updating a link that is not
        // there is an error, not a silent insert of one something else has already removed.
        var result = await _attachments
            .ReplaceOneAsync(
                Link.Eq(a => a.Id, attachment.Id), attachment, new ReplaceOptions { IsUpsert = false }, ct)
            .ConfigureAwait(false);

        if (result.MatchedCount == 0)
            throw new MediaNotFoundException(attachment.Id);
    }

    /// <inheritdoc/>
    public async Task UpdateAttachmentsAsync(
        IReadOnlyCollection<MediaAttachment> attachments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count == 0)
            return;

        // One BulkWrite, so a reorder is one round trip however long the gallery is. Unordered:
        // these are replacements of distinct documents with no dependency between them, and an
        // unordered batch lets the server apply them in parallel.
        var writes = attachments
            .Select(attachment => new ReplaceOneModel<MediaAttachment>(
                Link.Eq(a => a.Id, attachment.Id), attachment) { IsUpsert = false })
            .ToList();

        var result = await _attachments
            .BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct)
            .ConfigureAwait(false);

        if (result.MatchedCount < attachments.Count)
        {
            // Which one is worth a second look: the caller is holding rows it read a moment ago
            // and needs to know that one of them has since been detached.
            var ids     = attachments.Select(a => a.Id).ToArray();
            var present = await _attachments
                .Find(Link.In(a => a.Id, ids))
                .Project(a => a.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            throw new MediaNotFoundException(ids.Except(present).FirstOrDefault());
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(
        Guid mediaFileId, CancellationToken ct = default) =>
        await _attachments
            .Find(Link.Eq(a => a.MediaFileId, mediaFileId))
            .SortBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default)
    {
        var filters = new List<FilterDefinition<MediaAttachment>>
        {
            Link.Eq(a => a.OwnerEntityName, owner.EntityName),
            Link.Eq(a => a.OwnerEntityId, owner.EntityId)
        };

        if (role is not null)
            filters.Add(Link.Eq(a => a.Role, role));

        return await _attachments
            .Find(Link.And(filters))
            .SortBy(a => a.Role).ThenBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaOwnerAttachment>> ListAttachmentsForOwnersAsync(
        string entityName,
        IReadOnlyCollection<string> entityIds,
        string? role = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(entityIds);

        if (entityIds.Count == 0)
            return [];

        var ids = new BsonArray(entityIds.Distinct(StringComparer.Ordinal));

        var match = new BsonDocument
        {
            { nameof(MediaAttachment.OwnerEntityName), entityName },
            { nameof(MediaAttachment.OwnerEntityId), new BsonDocument("$in", ids) }
        };

        if (role is not null)
            match.Add(nameof(MediaAttachment.Role), role);

        // One aggregation rather than a read per owner — and the file joined in rather than fetched
        // afterwards, so the whole bulk read is a single command however many owners it covers.
        //
        // Written as BSON stages and deserialized through the registered class maps, rather than
        // through the driver's typed $lookup into a joined POCO: that route re-automaps the result
        // type and would lose the Guid serializers this store configures by hand, which are what
        // make a MediaFileId compare equal to the _id it names.
        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$lookup", new BsonDocument
            {
                { "from", _filesCollectionName },
                { "localField", nameof(MediaAttachment.MediaFileId) },
                { "foreignField", "_id" },
                { "as", JoinedFileField }
            }),
            // An inner join, so a link whose file is gone drops out instead of returning half a
            // row. It cannot normally exist — deleting a file removes its links outright, soft or
            // not — and this keeps the read honest if one ever does.
            new BsonDocument("$unwind", "$" + JoinedFileField),
            // Live files only. A live document has no DeletedAt field at all rather than a null
            // one: the file map sets IgnoreIfNull, so both spellings have to be allowed for.
            new BsonDocument("$match", new BsonDocument("$or", new BsonArray
            {
                new BsonDocument($"{JoinedFileField}.{nameof(MediaFile.DeletedAt)}", BsonNull.Value),
                new BsonDocument($"{JoinedFileField}.{nameof(MediaFile.DeletedAt)}", new BsonDocument("$exists", false))
            })),
            new BsonDocument("$sort", new BsonDocument
            {
                { nameof(MediaAttachment.OwnerEntityId), 1 },
                { nameof(MediaAttachment.Role), 1 },
                { nameof(MediaAttachment.SortOrder), 1 },
                { "_id", 1 }
            })
        };

        var documents = await _attachments
            .Aggregate<BsonDocument>(pipeline, options: null, ct)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rows = new List<MediaOwnerAttachment>(documents.Count);

        foreach (var document in documents)
        {
            // Lifted out before the link is deserialized: the joined file sits inside the
            // attachment document, and MediaAttachment's map has no member to put it in.
            var file = document[JoinedFileField].AsBsonDocument;
            document.Remove(JoinedFileField);

            rows.Add(new MediaOwnerAttachment(
                BsonSerializer.Deserialize<MediaAttachment>(document),
                BsonSerializer.Deserialize<MediaFile>(file)));
        }

        return rows;
    }

    /// <summary>Where the joined file document is parked inside the attachment document.</summary>
    private const string JoinedFileField = "joinedMediaFile";

    /// <inheritdoc/>
    public async Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaFileIds);

        if (mediaFileIds.Count == 0)
            return new HashSet<Guid>();

        // One distinct for the whole set, rather than a probe per file: deleting a product with
        // fifty images must not become fifty round trips.
        var shared = await _attachments
            .Distinct<Guid>(
                new ExpressionFieldDefinition<MediaAttachment, Guid>(a => a.MediaFileId),
                Link.And(
                    Link.In(a => a.MediaFileId, mediaFileIds),
                    Link.Or(
                        Link.Ne(a => a.OwnerEntityName, excluding.EntityName),
                        Link.Ne(a => a.OwnerEntityId, excluding.EntityId))),
                options: null,
                ct)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return shared.ToHashSet();
    }

    /// <summary>
    /// A delete count as the contract's <c>int</c>. Saturating is the least wrong answer for a
    /// number this large, and it only ever tells a caller how much went.
    /// </summary>
    private static int Saturate(long count) => count > int.MaxValue ? int.MaxValue : (int)count;
}
