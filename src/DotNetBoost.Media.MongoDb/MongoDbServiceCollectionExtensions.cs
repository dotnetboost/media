using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.MongoDb;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the MongoDB metadata store.</summary>
    public static class MongoDbMediaBuilderExtensions
    {
        /// <summary>
        /// Records uploaded files in MongoDB, over the <see cref="IMongoDatabase"/> the
        /// application has already registered:
        /// <code>
        /// services.AddSingleton&lt;IMongoClient&gt;(new MongoClient(connectionString));
        /// services.AddSingleton(sp =&gt; sp.GetRequiredService&lt;IMongoClient&gt;().GetDatabase("shop"));
        ///
        /// services.AddMedia()
        ///         .UseMongoDb()
        ///         .UseAzureBlobStorage(storageConnectionString)
        ///         .Build();
        /// </code>
        /// </summary>
        public static MediaBuilder UseMongoDb(this MediaBuilder builder, Action<MongoDbOptions>? configure = null) =>
            builder.UseMongoDb(static sp => sp.GetRequiredService<IMongoDatabase>(), configure);

        /// <summary>
        /// Records uploaded files in MongoDB, over its own client, for an application that does
        /// not register one itself. The client is a singleton, as the driver intends — it owns the
        /// connection pool.
        /// </summary>
        public static MediaBuilder UseMongoDb(
            this MediaBuilder builder,
            string connectionString,
            string databaseName,
            Action<MongoDbOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            builder.Services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

            return builder.UseMongoDb(
                sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName),
                configure);
        }

        /// <summary>
        /// Records uploaded files in the database <paramref name="database"/> resolves — one per
        /// tenant, say, from a scoped accessor.
        /// </summary>
        public static MediaBuilder UseMongoDb(
            this MediaBuilder builder,
            Func<IServiceProvider, IMongoDatabase> database,
            Action<MongoDbOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(database);
            MediaBuilderGuard.EnsureStoreNotConfigured(builder, "MongoDb");

            var options = new MongoDbOptions();
            configure?.Invoke(options);
            builder.Services.TryAddSingleton(options);

            // The driver's class-map registry is global and static, so this happens here — once,
            // at configuration time — rather than in the store's constructor.
            MediaFileClassMap.Register();

            builder.Services.TryAddScoped(database);
            builder.Services.AddScoped<IMediaStore>(sp => new MongoMediaStore(
                sp.GetRequiredService<IMongoDatabase>(), options));

            if (options.CreateIndexes)
            {
                builder.Services.AddSingleton<IHostedService>(sp => new MongoIndexHostedService(
                    sp,
                    options,
                    sp.GetService<ILogger<MongoMediaStore>>() ?? NullLogger<MongoMediaStore>.Instance));
            }

            return builder;
        }
    }
}
