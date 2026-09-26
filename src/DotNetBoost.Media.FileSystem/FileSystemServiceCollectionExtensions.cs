using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.FileSystem;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the local-disk storage backend.</summary>
    public static class FileSystemMediaBuilderExtensions
    {
        /// <summary>
        /// Stores files on the local file system, under <paramref name="rootPath"/>:
        /// <code>
        /// services.AddMedia()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .UseFileSystem(Path.Combine(builder.Environment.ContentRootPath, "uploads"))
        ///         .Build();
        /// </code>
        /// <para>
        /// Files are not reachable over HTTP unless <see cref="FileSystemOptions.PublicBaseUrl"/>
        /// says where a web server serves them from. Without it, <c>GetUrlAsync</c> returns
        /// <c>null</c> and downloads go through the application — which is what you want for
        /// anything that is not public.
        /// </para>
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="rootPath">The directory to write under. Prefer an absolute path.</param>
        /// <param name="isDefault">
        /// Whether new uploads go here. With one backend registered this makes no difference — the
        /// only backend is the default.
        /// </param>
        /// <param name="configure">Public base URL, buffer size, and whether to create the directory.</param>
        public static MediaBuilder UseFileSystem(
            this MediaBuilder builder,
            string rootPath,
            bool isDefault = false,
            Action<FileSystemOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

            var options = new FileSystemOptions { RootPath = rootPath };
            configure?.Invoke(options);

            return builder.UseFileSystem(options, isDefault);
        }

        /// <summary>
        /// Stores files on the local file system, configured entirely by
        /// <paramref name="options"/> — for binding a configuration section straight onto it.
        /// </summary>
        public static MediaBuilder UseFileSystem(
            this MediaBuilder builder,
            FileSystemOptions options,
            bool isDefault = false)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(options);

            var provider = new FileSystemStorageProvider(options);

            MediaBuilderGuard.AddStorageBackend(builder, provider.Name, isDefault);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<IMediaStorageProvider>(provider);

            return builder;
        }
    }
}
