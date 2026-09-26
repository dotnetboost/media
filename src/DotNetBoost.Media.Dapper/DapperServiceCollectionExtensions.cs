using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Dapper;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the Dapper metadata store.</summary>
    public static class DapperMediaBuilderExtensions
    {
        /// <summary>
        /// Records uploaded files through Dapper, over a connection created per scope:
        /// <code>
        /// services.AddMedia()
        ///         .UseDapper(_ =&gt; new NpgsqlConnection(connectionString), migrateSchema: true)
        ///         .UseFileSystem("./uploads")
        ///         .Build();
        /// </code>
        /// <para>
        /// The connection is registered as <see cref="IDbConnection"/> in the scope, so the
        /// container disposes it and application code can inject the same one — begin a
        /// transaction on it, write your entity, and hand the transaction to
        /// <see cref="DapperMediaStore.WithTransaction"/> so both commit together.
        /// </para>
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="connectionFactory">
        /// Creates the connection for each scope. Its runtime type selects the SQL dialect —
        /// SQL Server, PostgreSQL or SQLite — unless <see cref="DapperOptions.Dialect"/> says
        /// otherwise.
        /// </param>
        /// <param name="migrateSchema">
        /// When <c>true</c>, a hosted service creates the <c>MediaFiles</c> table at startup if it
        /// is absent, in the shape the EF Core store maps. Leave it off — the default — when the
        /// schema is owned by migrations or a deployment step; it only ever creates, and will not
        /// migrate a table whose shape has changed.
        /// </param>
        /// <param name="configure">Table and schema naming, and an explicit dialect.</param>
        /// <exception cref="InvalidOperationException">A metadata store is already configured on this builder.</exception>
        public static MediaBuilder UseDapper(
            this MediaBuilder builder,
            Func<IServiceProvider, IDbConnection> connectionFactory,
            bool migrateSchema = false,
            Action<DapperOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(connectionFactory);
            MediaBuilderGuard.EnsureStoreNotConfigured(builder, "Dapper");

            var options = new DapperOptions();
            configure?.Invoke(options);
            builder.Services.TryAddSingleton(options);

            // Scoped and container-owned: the connection is disposed with the scope, and anything
            // else in the scope that asks for IDbConnection gets the very one the store uses.
            builder.Services.AddScoped(connectionFactory);

            // Scoped too, so a transaction the application puts in the scope is the one the store
            // enlists in — an entity and its media rows then commit or roll back together.
            builder.Services.AddScoped<IMediaStore>(sp => new DapperMediaStore(
                sp.GetRequiredService<IDbConnection>(), options, sp.GetService<IDbTransaction>()));

            // IEntityKeyAccessor is deliberately left alone: Dapper maps plain objects, so the
            // Core convention ([Key], Id, {Type}Id) is already the best available answer.

            if (migrateSchema)
            {
                builder.Services.AddSingleton<IHostedService>(sp => new DapperMigrationHostedService(
                    sp,
                    options,
                    sp.GetService<ILogger<DapperMediaStore>>() ?? NullLogger<DapperMediaStore>.Instance));
            }

            return builder;
        }
    }
}
