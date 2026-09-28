using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace DotNetBoost.Media.Core;

/// <summary>Fluent builder returned by <c>services.AddMedia()</c>.</summary>
public sealed class MediaBuilder
{
    private readonly List<string> _storageBackends = [];
    private readonly List<Assembly> _roleAssemblies = [];
    private readonly Dictionary<string, IReadOnlyList<MediaEntityRole>> _declaredRoles = new(StringComparer.Ordinal);

    /// <summary>The service collection being configured. Providers and backends register here.</summary>
    public IServiceCollection Services { get; }

    internal bool    StoreConfigured { get; set; }
    internal string? StoreName       { get; set; }
    internal string? DefaultStorage  { get; set; }

    /// <summary>
    /// The options instance handed to the engine through DI. Builder methods mutate this object
    /// rather than a private copy, so configuration applied anywhere in the chain takes effect.
    /// </summary>
    internal MediaOptions Options { get; }

    /// <summary>Creates a builder over <paramref name="services"/> and registers its options.</summary>
    public MediaBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Options  = new MediaOptions();
        Services.AddSingleton(Options);
    }

    /// <summary>The names of the storage backends registered so far, in registration order.</summary>
    internal IReadOnlyList<string> StorageBackends => _storageBackends;

    /// <summary>Records an assembly to scan for <c>[MediaRole]</c> at <see cref="Build"/>.</summary>
    internal void AddRoleAssembly(Assembly assembly) => _roleAssemblies.Add(assembly);

    /// <summary>Records roles declared directly on the builder, for a type that carries no attribute.</summary>
    internal void AddDeclaredRoles(string entityName, IReadOnlyList<MediaEntityRole> roles) =>
        Declare(entityName, roles, "declared on the builder");

    /// <summary>
    /// Records a storage backend. Called by each <c>Use…()</c> backend method; several backends
    /// may be registered, unlike the metadata store, of which there is exactly one.
    /// </summary>
    /// <param name="name">The backend's <see cref="IMediaStorageProvider.Name"/>.</param>
    /// <param name="isDefault">Whether new uploads go here.</param>
    /// <exception cref="InvalidOperationException">The name is taken, or a default is already set.</exception>
    internal void AddStorageBackend(string name, bool isDefault)
    {
        if (_storageBackends.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"A media storage backend named '{name}' is already configured. Names are stored in every row " +
                "they write, so each backend can only be registered once.");
        }

        _storageBackends.Add(name);

        if (!isDefault)
            return;

        if (DefaultStorage is not null)
        {
            throw new InvalidOperationException(
                $"Both '{DefaultStorage}' and '{name}' are marked as the default media storage backend. " +
                "Exactly one backend receives new uploads.");
        }

        DefaultStorage = name;
    }

    /// <summary>
    /// Validates the configuration and returns the service collection. Call once at the end of
    /// the chain.
    /// </summary>
    /// <exception cref="InvalidOperationException">No metadata store or no storage backend is configured.</exception>
    public IServiceCollection Build()
    {
        if (!StoreConfigured)
        {
            throw new InvalidOperationException(
                "No media metadata store configured. Call UseEntityFrameworkCore<TContext>(), UseDapper(...), " +
                "UseMongoDb(...) or UseCustomStore<TStore>() first — the table is where uploaded files are " +
                "recorded, and the library cannot find a file without it.");
        }

        if (_storageBackends.Count == 0)
        {
            throw new InvalidOperationException(
                "No media storage backend configured. Call UseFileSystem(...), UseAzureBlobStorage(...), " +
                "UseAmazonS3(...) or UseCustomStorage<TProvider>() — the store records where a file is, and " +
                "a backend is what actually holds it.");
        }

        Services.AddSingleton<IMediaStorageProviderRegistry>(sp => new MediaStorageProviderRegistry(
            sp.GetServices<IMediaStorageProvider>(),
            DefaultStorage));

        // Read here, not when DeclareRolesFrom was called. The name a type's media is stored under is
        // whatever the rest of the builder settles on, so scanning eagerly would file roles under
        // names that a later line could still change — and the registry would come back empty at
        // attach time, silently switching the checking off. Deferring makes the order of these two
        // lines irrelevant rather than merely documented.
        foreach (var assembly in _roleAssemblies)
        {
            foreach (var (entityName, roles) in MediaEntityRoles.Scan(assembly))
                Declare(entityName, roles, $"declared by [MediaRole] in {assembly.GetName().Name}");
        }

        Services.AddSingleton<IMediaRoleRegistry>(new MediaRoleRegistry(_declaredRoles));

        if (Options.ValidateStorageAtStartup)
            Services.AddHostedService<MediaValidationHostedService>();

        return Services;
    }

    /// <summary>
    /// Files one entity's roles, refusing a second set for the same name. Two sources declaring the
    /// same entity differently is a configuration mistake with no sensible winner — and silently
    /// picking one would leave the other's roles quietly unenforced.
    /// </summary>
    private void Declare(string entityName, IReadOnlyList<MediaEntityRole> roles, string source)
    {
        if (roles.Count == 0)
            return;

        if (_declaredRoles.TryGetValue(entityName, out var existing))
        {
            throw new InvalidOperationException(
                $"Media roles for '{entityName}' are declared twice: once as [{string.Join(", ", existing.Select(r => r.Name))}], " +
                $"and again {source} as [{string.Join(", ", roles.Select(r => r.Name))}]. Declare each entity's " +
                "roles once — two types sharing a stored name is usually the cause, and [MediaEntity(\"…\")] " +
                "is how to tell them apart.");
        }

        _declaredRoles[entityName] = roles;
    }
}

/// <summary>Guards against configuring more than one metadata store on a builder.</summary>
public static class MediaBuilderGuard
{
    /// <summary>Records <paramref name="storeName"/> as the builder's metadata store.</summary>
    /// <exception cref="InvalidOperationException">A store is already configured.</exception>
    public static void EnsureStoreNotConfigured(MediaBuilder builder, string storeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        if (builder.StoreConfigured)
        {
            throw new InvalidOperationException(
                $"A media metadata store ('{builder.StoreName}') is already configured. " +
                $"Cannot also configure '{storeName}'.");
        }

        builder.StoreConfigured = true;
        builder.StoreName       = storeName;
    }

    /// <summary>
    /// Registers a storage backend on the builder. Backend packages call this from their own
    /// <c>Use…()</c> method; several backends can coexist.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="name">The backend's <see cref="IMediaStorageProvider.Name"/>.</param>
    /// <param name="isDefault">Whether new uploads go to it.</param>
    /// <exception cref="InvalidOperationException">The name is taken, or a default is already set.</exception>
    public static void AddStorageBackend(MediaBuilder builder, string name, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddStorageBackend(name, isDefault);
    }
}
