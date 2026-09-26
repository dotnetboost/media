using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Entry point for registering DotNetBoost.Media.</summary>
    public static class MediaServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the engine and returns a builder for further configuration. Follow it with a
        /// metadata store, at least one storage backend, and <c>Build()</c>.
        /// </summary>
        public static MediaBuilder AddMedia(this IServiceCollection services) =>
            services.AddMedia(static _ => { });

        /// <summary>
        /// Registers the engine, applies <paramref name="configure"/> to its options, and returns
        /// a builder for further configuration.
        /// <code>
        /// services.AddMedia(o =&gt; o.MaxFileSizeBytes = 25 * 1024 * 1024)
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()   // the table
        ///         .UseFileSystem("./uploads")                 // the bytes
        ///         .Build();
        /// </code>
        /// </summary>
        public static MediaBuilder AddMedia(this IServiceCollection services, Action<MediaOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.TryAddSingleton(TimeProvider.System);

            // Singletons: neither depends on anything scoped, and the key generator is called on
            // every upload.
            services.TryAddSingleton<IMediaKeyGenerator, DateHierarchyKeyGenerator>();
            services.TryAddSingleton<IEntityKeyAccessor, ConventionEntityKeyAccessor>();

            // Scoped: the metadata store is (an EF Core DbContext, a Dapper connection), and the
            // engine is only as long-lived as the store behind it.
            services.TryAddScoped<MediaManager>();
            services.TryAddScoped<IMediaManager>(sp => sp.GetRequiredService<MediaManager>());

            var builder = new MediaBuilder(services);
            configure(builder.Options);
            return builder;
        }
    }

    /// <summary>Builder methods for engine behaviour.</summary>
    public static class MediaBuilderExtensions
    {
        /// <summary>
        /// Caps how large an upload may be (default: 100 MB). Remember that ASP.NET Core has its
        /// own request-body limit, and the smaller of the two applies.
        /// </summary>
        public static MediaBuilder WithMaxFileSize(this MediaBuilder builder, long bytes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bytes, 0);
            builder.Options.MaxFileSizeBytes = bytes;
            return builder;
        }

        /// <summary>
        /// Accepts only these media types. A family is written <c>image/*</c>. Calling it more
        /// than once adds to the list rather than replacing it.
        /// <code>
        /// .AllowContentTypes("image/*", "application/pdf")
        /// </code>
        /// </summary>
        public static MediaBuilder AllowContentTypes(this MediaBuilder builder, params string[] contentTypes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(contentTypes);

            foreach (var contentType in contentTypes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
                builder.Options.AllowedContentTypes.Add(contentType.Trim().ToLowerInvariant());
            }

            return builder;
        }

        /// <summary>
        /// Accepts only these extensions, each with its leading dot. Adds to the list on repeated
        /// calls.
        /// </summary>
        public static MediaBuilder AllowExtensions(this MediaBuilder builder, params string[] extensions)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(extensions);

            foreach (var extension in extensions)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(extension);
                var normalized = extension.Trim().ToLowerInvariant();
                builder.Options.AllowedExtensions.Add(normalized[0] == '.' ? normalized : "." + normalized);
            }

            return builder;
        }

        /// <summary>
        /// Adds extensions to the always-refused list, on top of the built-in executable and
        /// script extensions.
        /// </summary>
        public static MediaBuilder BlockExtensions(this MediaBuilder builder, params string[] extensions)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(extensions);

            foreach (var extension in extensions)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(extension);
                var normalized = extension.Trim().ToLowerInvariant();
                builder.Options.BlockedExtensions.Add(normalized[0] == '.' ? normalized : "." + normalized);
            }

            return builder;
        }

        /// <summary>
        /// Returns the existing file when an upload's bytes are already stored, instead of keeping
        /// a second copy. Requires checksums, which are on by default.
        /// <para>
        /// The shared row carries the FIRST upload's name and owner, and deleting it deletes it
        /// for everyone who "uploaded" those bytes — right for interchangeable assets, wrong for
        /// somebody's document. See <see cref="MediaOptions.DeduplicateByChecksum"/>.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Checksums have been turned off.</exception>
        public static MediaBuilder DeduplicateByChecksum(this MediaBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (!builder.Options.ComputeChecksum)
            {
                throw new InvalidOperationException(
                    "DeduplicateByChecksum() needs checksums, which DisableChecksums() turned off. " +
                    "Deduplication is by content hash; without one there is nothing to compare.");
            }

            builder.Options.DeduplicateByChecksum = true;
            return builder;
        }

        /// <summary>
        /// Stops hashing uploads. Saves the hashing itself and nothing else — the bytes are
        /// already streaming past — and gives up corruption detection and deduplication.
        /// </summary>
        public static MediaBuilder DisableChecksums(this MediaBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.ComputeChecksum       = false;
            builder.Options.DeduplicateByChecksum = false;
            return builder;
        }

        /// <summary>
        /// Deletes the row along with the bytes, instead of keeping it with <c>DeletedAt</c> set.
        /// </summary>
        public static MediaBuilder DisableSoftDelete(this MediaBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.SoftDelete = false;
            return builder;
        }

        /// <summary>
        /// Stops checking an upload's declared media type against its own bytes. Do this only
        /// where the uploads are not user-supplied: it is what keeps an executable renamed to
        /// <c>.png</c> from being stored, and served, as an image.
        /// </summary>
        public static MediaBuilder DisableContentTypeVerification(this MediaBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.VerifyContentType = false;
            return builder;
        }

        /// <summary>Sets how long generated URLs stay valid when the caller does not say (default: 1 hour).</summary>
        public static MediaBuilder WithDefaultUrlExpiry(this MediaBuilder builder, TimeSpan expiry)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, TimeSpan.Zero);
            builder.Options.DefaultUrlExpiry = expiry;
            return builder;
        }

        /// <summary>
        /// Stops the application probing its storage backends at startup. Run the check yourself
        /// with <c>services.ValidateMediaAsync()</c> if you would rather control the moment.
        /// </summary>
        public static MediaBuilder DisableStartupValidation(this MediaBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.ValidateStorageAtStartup = false;
            return builder;
        }

        /// <summary>
        /// Replaces how storage paths are chosen (default:
        /// <c>{category}/{yyyy}/{MM}/{dd}/{id}{ext}</c>). Registered as a singleton.
        /// </summary>
        public static MediaBuilder UseKeyGenerator<TGenerator>(this MediaBuilder builder)
            where TGenerator : class, IMediaKeyGenerator
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Replace(ServiceDescriptor.Singleton<IMediaKeyGenerator, TGenerator>());
            return builder;
        }

        /// <summary>Replaces how entity keys are read (default: <c>[Key]</c>, <c>Id</c>, <c>{TypeName}Id</c>).</summary>
        public static MediaBuilder UseEntityKeyAccessor<TAccessor>(this MediaBuilder builder)
            where TAccessor : class, IEntityKeyAccessor
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Replace(ServiceDescriptor.Scoped<IEntityKeyAccessor, TAccessor>());
            return builder;
        }
    }

    /// <summary>Running the media configuration checks on demand.</summary>
    public static class MediaValidationExtensions
    {
        /// <summary>
        /// Probes every configured storage backend. Call it once the application is ready — after
        /// your own migrations, say — when you would rather control the moment than let the
        /// startup check pick it:
        /// <code>
        /// var app = builder.Build();
        /// await app.Services.ValidateMediaAsync();
        /// </code>
        /// </summary>
        /// <exception cref="DotNetBoost.Media.Core.MediaStorageException">A backend is unreachable or misconfigured.</exception>
        public static Task ValidateMediaAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
            MediaValidation.ValidateStorageAsync(services, cancellationToken);
    }

    /// <summary>Builder methods for plugging in components this library does not ship.</summary>
    public static class CustomMediaBuilderExtensions
    {
        /// <summary>Configures a custom metadata store, registered scoped.</summary>
        public static MediaBuilder UseCustomStore<TStore>(this MediaBuilder builder)
            where TStore : class, IMediaStore
        {
            ArgumentNullException.ThrowIfNull(builder);
            MediaBuilderGuard.EnsureStoreNotConfigured(builder, "Custom");
            builder.Services.AddScoped<IMediaStore, TStore>();
            return builder;
        }

        /// <summary>Configures a custom metadata store created by a factory — e.g. to share one instance.</summary>
        public static MediaBuilder UseCustomStore(this MediaBuilder builder, Func<IServiceProvider, IMediaStore> store)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(store);
            MediaBuilderGuard.EnsureStoreNotConfigured(builder, "Custom");
            builder.Services.AddScoped(store);
            return builder;
        }

        /// <summary>
        /// Configures a custom storage backend, registered as a singleton alongside any others.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">
        /// Its <see cref="IMediaStorageProvider.Name"/>. Passed rather than read off the type,
        /// because the backend is not constructed until the container is built and the builder
        /// needs the name now — to reject a duplicate, and to resolve which backend is the
        /// default. It must match what the instance reports, and <c>Build()</c> fails if it does
        /// not.
        /// </param>
        /// <param name="isDefault">Whether new uploads go to it. The first backend registered is the default when none says so.</param>
        public static MediaBuilder UseCustomStorage<TProvider>(
            this MediaBuilder builder,
            string name,
            bool isDefault = false)
            where TProvider : class, IMediaStorageProvider
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            MediaBuilderGuard.AddStorageBackend(builder, name, isDefault);
            builder.Services.AddSingleton<IMediaStorageProvider, TProvider>();
            return builder;
        }

        /// <summary>
        /// Configures a custom storage backend created by a factory, registered as a singleton
        /// alongside any others.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">Its <see cref="IMediaStorageProvider.Name"/>, which every row it writes records.</param>
        /// <param name="provider">Creates it.</param>
        /// <param name="isDefault">Whether new uploads go to it.</param>
        public static MediaBuilder UseCustomStorage(
            this MediaBuilder builder,
            string name,
            Func<IServiceProvider, IMediaStorageProvider> provider,
            bool isDefault = false)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(provider);

            MediaBuilderGuard.AddStorageBackend(builder, name, isDefault);
            builder.Services.AddSingleton(provider);
            return builder;
        }
    }
}
