using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Media.Core;

/// <summary>
/// Says once, as the host starts, which files-deleted handlers are registered — or that none are.
/// <para>
/// Media cannot know who stores data about its files, so it cannot tell a missing handler from an
/// application that has nothing to clean up. What it can do is make the registered set visible, so
/// "why is this table full of rows for deleted files?" has an answer in the startup log. Debug,
/// not Information: most applications store nothing about files and rightly register none.
/// </para>
/// </summary>
internal sealed partial class MediaDeletedHandlersReport(
    IReadOnlyList<string> handlers,
    ILogger<MediaDeletedHandlersReport>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<MediaDeletedHandlersReport>.Instance;

    /// <summary>The handler names the line reports, in registration order.</summary>
    internal IReadOnlyList<string> Handlers => handlers;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (handlers.Count == 0)
            LogNone(_logger);
        else
            LogRegistered(_logger, handlers.Count, handlers);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 7101, Level = LogLevel.Debug,
        Message = "DotNetBoost.Media: {Count} files-deleted handler(s) registered ({Handlers}).")]
    private static partial void LogRegistered(ILogger logger, int count, IReadOnlyList<string> handlers);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Debug,
        Message = "DotNetBoost.Media: no files-deleted handlers registered. Records the application keeps " +
                  "about a file are not cleaned up when the file is deleted; register OnFilesDeleted(...) if " +
                  "there are any.")]
    private static partial void LogNone(ILogger logger);
}
