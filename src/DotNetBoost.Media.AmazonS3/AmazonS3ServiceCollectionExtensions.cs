using Amazon.S3;
using DotNetBoost.Media.AmazonS3;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the Amazon S3 storage backend.</summary>
    public static class AmazonS3MediaBuilderExtensions
    {
        /// <summary>
        /// Stores files as S3 objects, over the <see cref="IAmazonS3"/> the application has
        /// already registered — which is how to use an instance role or any of the SDK's other
        /// credential sources:
        /// <code>
        /// services.AddDefaultAWSOptions(config.GetAWSOptions());
        /// services.AddAWSService&lt;IAmazonS3&gt;();
        ///
        /// services.AddMedia()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .UseAmazonS3("my-app-media")
        ///         .Build();
        /// </code>
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="bucketName">The bucket to write to. This backend never creates it.</param>
        /// <param name="isDefault">Whether new uploads go here.</param>
        /// <param name="configure">Key prefix, URL behaviour, storage class and encryption.</param>
        public static MediaBuilder UseAmazonS3(
            this MediaBuilder builder,
            string bucketName,
            bool isDefault = false,
            Action<AmazonS3Options>? configure = null) =>
            builder.UseAmazonS3(static sp => sp.GetRequiredService<IAmazonS3>(), bucketName, isDefault, configure);

        /// <summary>
        /// Stores files as S3 objects, over a client this library creates from
        /// <paramref name="config"/> and the SDK's usual credential chain. Use it for an
        /// S3-compatible store — MinIO, Cloudflare R2, DigitalOcean Spaces — where the service
        /// URL is not Amazon's:
        /// <code>
        /// .UseAmazonS3(new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true }, "media")
        /// </code>
        /// </summary>
        public static MediaBuilder UseAmazonS3(
            this MediaBuilder builder,
            AmazonS3Config config,
            string bucketName,
            bool isDefault = false,
            Action<AmazonS3Options>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            return builder.UseAmazonS3(_ => new AmazonS3Client(config), bucketName, isDefault, configure);
        }

        /// <summary>
        /// Stores files as S3 objects, over the client <paramref name="client"/> resolves — one
        /// account or region per tenant, say.
        /// </summary>
        public static MediaBuilder UseAmazonS3(
            this MediaBuilder builder,
            Func<IServiceProvider, IAmazonS3> client,
            string bucketName,
            bool isDefault = false,
            Action<AmazonS3Options>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);

            var options = new AmazonS3Options { BucketName = bucketName };
            configure?.Invoke(options);

            MediaBuilderGuard.AddStorageBackend(builder, "AmazonS3", isDefault);

            builder.Services.TryAddSingleton(options);
            builder.Services.AddSingleton<IMediaStorageProvider>(sp =>
                new AmazonS3StorageProvider(client(sp), options));

            return builder;
        }
    }
}
