using DotNetBoost.Media.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DotNetBoost.Media.MongoDb;

/// <summary>
/// Creates the collection's indexes as the host starts. They mirror the relational stores' —
/// owner, checksum, category — so the same queries are served the same way whichever backend the
/// application chose.
/// </summary>
internal sealed partial class MongoIndexHostedService(
    IServiceProvider services,
    MongoDbOptions options,
    ILogger logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var database    = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
        var collection  = database.GetCollection<MediaFile>(options.MediaFilesCollection);

        var keys = Builders<MediaFile>.IndexKeys;

        try
        {
            await collection.Indexes.CreateManyAsync(
                [
                    // "This entity's files, newest first" — the query the library issues most.
                    // Leading with the owner fields lets one index serve the filter and the sort.
                    new CreateIndexModel<MediaFile>(
                        keys.Ascending(f => f.OwnerEntityName).Ascending(f => f.OwnerEntityId).Descending(f => f.CreatedAt),
                        new CreateIndexOptions { Name = "owner_created", Sparse = true }),

                    // Deduplication and integrity checks. Sparse: a store with checksums off
                    // writes none of these fields at all.
                    new CreateIndexModel<MediaFile>(
                        keys.Ascending(f => f.Checksum),
                        new CreateIndexOptions { Name = "checksum", Sparse = true }),

                    // The default listing, and every category browse.
                    new CreateIndexModel<MediaFile>(
                        keys.Ascending(f => f.Category).Descending(f => f.CreatedAt),
                        new CreateIndexOptions { Name = "category_created" })
                ],
                cancellationToken).ConfigureAwait(false);

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
