using Azure.Storage.Blobs;
using DotNetBoost.Media.AzureBlobStorage;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the Azure Blob Storage backend.</summary>
    public static class AzureBlobMediaBuilderExtensions
    {
        /// <summary>
        /// Stores files as blobs, over a connection string:
        /// <code>
        /// services.AddMedia()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .UseAzureBlobStorage(config.GetConnectionString("storage")!, "media")
        ///         .Build();
        /// </code>
        /// A connection string carries an account key, so SAS URLs work — clients download
        /// straight from storage instead of through the application.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="connectionString">An Azure Storage connection string. Also accepts the Azurite development string.</param>
        /// <param name="containerName">The container to write to. Created private at startup unless told otherwise.</param>
        /// <param name="isDefault">Whether new uploads go here.</param>
        /// <param name="configure">Upload tuning, URL behaviour and a CDN base URL.</param>
        public static MediaBuilder UseAzureBlobStorage(
            this MediaBuilder builder,
            string connectionString,
            string containerName = "media",
            bool isDefault = false,
            Action<AzureBlobOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            return builder.UseAzureBlobStorage(
                _ => new BlobServiceClient(connectionString),
                containerName,
                isDefault,
                configure);
        }

        /// <summary>
        /// Stores files as blobs, over the <see cref="BlobServiceClient"/> the application has
        /// already registered — which is how to use a managed identity:
        /// <code>
        /// services.AddSingleton(new BlobServiceClient(new Uri(accountUrl), new DefaultAzureCredential()));
        ///
        /// services.AddMedia()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .UseAzureBlobStorage()
        ///         .Build();
        /// </code>
        /// <para>
        /// A client built on a token credential cannot sign a SAS by itself, so <c>GetUrlAsync</c>
        /// returns <c>null</c> and downloads go through the application. Serve them with
        /// <c>MapMediaEndpoints()</c>, or give the client an account key if direct downloads
        /// matter more.
        /// </para>
        /// </summary>
        public static MediaBuilder UseAzureBlobStorage(
            this MediaBuilder builder,
            string containerName = "media",
            bool isDefault = false,
            Action<AzureBlobOptions>? configure = null) =>
            builder.UseAzureBlobStorage(
                static sp => sp.GetRequiredService<BlobServiceClient>(),
                containerName,
                isDefault,
                configure);

        /// <summary>
        /// Stores files as blobs, over the client <paramref name="client"/> resolves — one account
        /// per tenant, say, from a scoped accessor.
        /// </summary>
        public static MediaBuilder UseAzureBlobStorage(
            this MediaBuilder builder,
            Func<IServiceProvider, BlobServiceClient> client,
            string containerName = "media",
            bool isDefault = false,
            Action<AzureBlobOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

            var options = new AzureBlobOptions { ContainerName = containerName };
            configure?.Invoke(options);

            MediaBuilderGuard.AddStorageBackend(builder, "AzureBlobStorage", isDefault);

            builder.Services.TryAddSingleton(options);
            builder.Services.AddSingleton<IMediaStorageProvider>(sp =>
                new AzureBlobStorageProvider(client(sp), options));

            return builder;
        }
    }
}
