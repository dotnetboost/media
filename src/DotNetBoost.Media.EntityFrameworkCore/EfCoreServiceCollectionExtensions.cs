using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the EF Core metadata store.</summary>
    public static class EntityFrameworkMediaBuilderExtensions
    {
        /// <summary>
        /// Records uploaded files in <typeparamref name="TContext"/>:
        /// <code>
        /// services.AddDbContext&lt;AppDbContext&gt;(o =&gt; o.UseNpgsql(connectionString));
        ///
        /// services.AddMedia()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .UseFileSystem("./uploads")
        ///         .Build();
        /// </code>
        /// <para>
        /// Add the table to the model with <c>modelBuilder.ApplyMedia()</c> in
        /// <c>OnModelCreating</c>, and generate a migration for it as you would any other table.
        /// </para>
        /// <para>
        /// Because the rows live in the application's own context, an upload and the entity it
        /// belongs to can be saved together — take an explicit transaction on the context, upload,
        /// save your entity, commit.
        /// </para>
        /// </summary>
        public static MediaBuilder UseEntityFrameworkCore<TContext>(this MediaBuilder builder)
            where TContext : DbContext
        {
            ArgumentNullException.ThrowIfNull(builder);
            MediaBuilderGuard.EnsureStoreNotConfigured(builder, "EntityFrameworkCore");

            var services = builder.Services;

            services.AddScoped<IMediaStore>(sp => new EfCoreMediaStore(sp.GetRequiredService<TContext>()));

            // The model knows more about keys than the convention accessor can work out, so it
            // replaces it rather than merely being registered alongside.
            services.Replace(ServiceDescriptor.Scoped<IEntityKeyAccessor>(
                sp => new EfCoreEntityKeyAccessor(sp.GetRequiredService<TContext>())));

            return builder;
        }
    }
}
