using DotNetBoost.Media.Core.Interfaces;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// The backends registered on the builder. Holds them by name and knows which one new uploads go
/// to.
/// <para>
/// Several backends can be registered at once, and that is the point: every row records the
/// backend that wrote it, so an application can switch its default from local disk to Azure and
/// keep serving everything uploaded before the switch — as long as the old backend is still
/// registered. Removing one strands its rows, and <see cref="Get"/> says so rather than returning
/// bytes from the wrong place.
/// </para>
/// </summary>
internal sealed class MediaStorageProviderRegistry : IMediaStorageProviderRegistry
{
    private readonly Dictionary<string, IMediaStorageProvider> _byName;

    public MediaStorageProviderRegistry(IEnumerable<IMediaStorageProvider> providers, string? defaultProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);

        All = [.. providers];

        if (All.Count == 0)
        {
            throw new InvalidOperationException(
                "No media storage backend is configured. Call UseFileSystem(...), UseAzureBlobStorage(...), " +
                "UseAmazonS3(...) or UseCustomStorage<T>() on the builder before Build().");
        }

        _byName = new Dictionary<string, IMediaStorageProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in All)
        {
            if (!_byName.TryAdd(provider.Name, provider))
            {
                throw new InvalidOperationException(
                    $"Two media storage backends are both named '{provider.Name}'. Names are stored in every " +
                    "row they write, so they have to be unique.");
            }
        }

        // No explicit default: the first one registered. With a single backend — the common case
        // — that makes the choice invisible, which is the right amount of ceremony for it.
        Default = defaultProvider is null
            ? All[0]
            : _byName.TryGetValue(defaultProvider, out var named)
                ? named
                : throw new InvalidOperationException(
                    $"'{defaultProvider}' was marked as the default media storage backend, but no backend by " +
                    $"that name is registered. Registered: {string.Join(", ", _byName.Keys)}.");
    }

    public IMediaStorageProvider Default { get; }

    public IReadOnlyList<IMediaStorageProvider> All { get; }

    public IMediaStorageProvider? Find(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : _byName.GetValueOrDefault(name);

    public IMediaStorageProvider Get(string name) =>
        Find(name) ?? throw new MediaStorageException(
            $"No media storage backend named '{name}' is registered, but stored files say they live there. " +
            $"Registered: {string.Join(", ", _byName.Keys)}. Keep the backend registered for as long as rows " +
            "reference it, even after changing which one new uploads go to.",
            name);
}
