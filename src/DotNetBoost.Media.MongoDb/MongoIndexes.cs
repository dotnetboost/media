using DotNetBoost.Media.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DotNetBoost.Media.MongoDb;

/// <summary>
/// Creates the indexes both collections need, for a deployment step or a test that builds the store
/// directly rather than through <c>UseMongoDb(...)</c> — the counterpart of
/// <c>DapperSchema.EnsureCreatedAsync</c> and EF Core's <c>ApplyMedia</c>.
/// <para>
/// They mirror the relational stores' — checksum and category on the files, uniqueness and owner
/// order on the attachments — so the same queries are served the same way whichever backend the
/// application chose. Creating an index that is already there is a no-op, so this is idempotent.
/// </para>
/// <para>
/// The unique index is not decoration. It is what the store's contract rests on when two requests
/// attach the same file to the same entity in the same role at once: the engine checks first, and
/// this is what refuses the loser.
/// </para>
/// </summary>
public static class MongoMediaIndexes
{
    /// <summary>Creates every index this store needs, if it is absent. Idempotent.</summary>
    /// <param name="database">The database holding the collections.</param>
    /// <param name="options">Collection naming. Defaults to <see cref="MongoDbOptions"/>'s own defaults.</param>
    /// <param name="ct">Cancels the operation.</param>
    public static async Task EnsureCreatedAsync(
        IMongoDatabase database,
        MongoDbOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        options ??= new MongoDbOptions();

        var files       = database.GetCollection<MediaFile>(options.MediaFilesCollection);
        var attachments = database.GetCollection<MediaAttachment>(options.MediaAttachmentsCollection);

        var keys  = Builders<MediaFile>.IndexKeys;
        var links = Builders<MediaAttachment>.IndexKeys;

        await files.Indexes.CreateManyAsync(
            [
                // Deduplication and integrity checks. Sparse: a store with checksums off writes
                // none of these fields at all.
                new CreateIndexModel<MediaFile>(
                    keys.Ascending(f => f.Checksum),
                    new CreateIndexOptions { Name = "checksum", Sparse = true }),

                // The default listing, and every category browse.
                new CreateIndexModel<MediaFile>(
                    keys.Ascending(f => f.Category).Descending(f => f.CreatedAt),
                    new CreateIndexOptions { Name = "category_created" })
            ],
            ct).ConfigureAwait(false);

        await attachments.Indexes.CreateManyAsync(
            [
                // One file may hold several roles on one entity — its primary and its seo image —
                // but not the same role twice. Unique on all four fields, which is also what makes
                // attaching idempotent: the engine checks first and this refuses a race.
                new CreateIndexModel<MediaAttachment>(
                    links.Ascending(a => a.MediaFileId).Ascending(a => a.OwnerEntityName)
                         .Ascending(a => a.OwnerEntityId).Ascending(a => a.Role),
                    new CreateIndexOptions { Name = "attachment_unique", Unique = true }),

                // "This entity's files, in the order it put them in" — what For(entity) runs.
                // Leading with the owner and ending on the position lets one index serve the
                // filter, the role narrowing and the sort.
                new CreateIndexModel<MediaAttachment>(
                    links.Ascending(a => a.OwnerEntityName).Ascending(a => a.OwnerEntityId)
                         .Ascending(a => a.Role).Ascending(a => a.SortOrder),
                    new CreateIndexOptions { Name = "attachment_owner" }),

                // The cascade when a file is deleted, the sole-holder test behind DeleteAllAsync,
                // and the distinct that resolves an owner filter into file ids.
                new CreateIndexModel<MediaAttachment>(
                    links.Ascending(a => a.MediaFileId),
                    new CreateIndexOptions { Name = "attachment_file" })
            ],
            ct).ConfigureAwait(false);
    }
}

/// <summary>Creates those indexes as the host starts.</summary>
internal sealed partial class MongoIndexHostedService(
    IServiceProvider services,
    MongoDbOptions options,
    ILogger logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var database    = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();

        try
        {
            await MongoMediaIndexes.EnsureCreatedAsync(database, options, cancellationToken).ConfigureAwait(false);
            LogCreated(logger, options.MediaFilesCollection);
        }
        catch (MongoException ex)
        {
            LogFailed(logger, options.MediaFilesCollection, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 7300, Level = LogLevel.Debug,
        Message = "Media indexes on '{Collection}' are present.")]
    private static partial void LogCreated(ILogger logger, string collection);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Error,
        Message = "Could not create the media indexes on '{Collection}'. The role needs createIndex on the database.")]
    private static partial void LogFailed(ILogger logger, string collection, Exception exception);
}
