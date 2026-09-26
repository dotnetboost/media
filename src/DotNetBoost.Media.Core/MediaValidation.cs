using DotNetBoost.Media.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Media.Core;

/// <summary>
/// Checks at startup that every configured storage backend is actually usable. The alternative is
/// finding out from the first upload of the day that a container name is misspelled or a
/// credential has expired.
/// </summary>
public static class MediaValidation
{
    /// <summary>
    /// Probes each registered backend and throws if any of them cannot be reached. What "probe"
    /// means is up to the backend: creating a missing directory, checking a container exists,
    /// a HEAD against a bucket.
    /// </summary>
    /// <exception cref="MediaStorageException">A backend is unreachable or misconfigured.</exception>
    public static async Task ValidateStorageAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var registry    = scope.ServiceProvider.GetService<IMediaStorageProviderRegistry>();

        if (registry is null)
            return;

        foreach (var provider in registry.All)
        {
            if (provider is IMediaStorageHealthCheck check)
                await check.CheckAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Implemented by a storage backend that can say at startup whether it is usable. Optional: a
/// backend that does not implement it is simply not probed.
/// </summary>
public interface IMediaStorageHealthCheck
{
    /// <summary>
    /// Verifies that this backend is reachable and its container exists, creating the container
    /// where the backend is configured to.
    /// </summary>
    /// <exception cref="MediaStorageException">It is not usable.</exception>
    Task CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Runs <see cref="MediaValidation.ValidateStorageAsync"/> once as the host starts, so a
/// misconfigured backend fails the boot instead of the first upload. Registered by
/// <c>Build()</c> unless <c>DisableStartupValidation()</c> was called.
/// </summary>
internal sealed partial class MediaValidationHostedService(
    IServiceProvider services,
    ILogger<MediaValidationHostedService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<MediaValidationHostedService>.Instance;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await MediaValidation.ValidateStorageAsync(services, cancellationToken).ConfigureAwait(false);
        LogValidated(_logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 7100, Level = LogLevel.Debug,
        Message = "Media storage backends validated.")]
    private static partial void LogValidated(ILogger logger);
}
