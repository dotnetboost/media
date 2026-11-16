using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// The engine's counters. <see cref="MediaTelemetry"/> is the public half: the names an
/// application subscribes to.
/// </summary>
internal sealed class MediaMetrics : IDisposable
{
    // Null when the meter came from a factory, which owns it. Only a meter this type created is a
    // meter this type may dispose.
    private readonly Meter? _owned;

    private readonly Counter<long> _handlerFailures;

    /// <summary>Creates the instruments on <paramref name="factory"/>'s meter, or on one of its own.</summary>
    /// <param name="factory">
    /// The host's meter factory, or <c>null</c>. An ASP.NET Core application has one registered; a
    /// console application that only called <c>AddMedia()</c> does not, and refusing to start over
    /// telemetry nobody subscribed to would be the wrong trade.
    /// </param>
    internal MediaMetrics(IMeterFactory? factory)
    {
        Meter meter;

        if (factory is not null)
        {
            meter = factory.Create(new MeterOptions(MediaTelemetry.MeterName)
            {
                Version = MediaTelemetry.MeterVersion
            });
        }
        else
        {
            meter  = new Meter(MediaTelemetry.MeterName, MediaTelemetry.MeterVersion);
            _owned = meter;
        }

        _handlerFailures = meter.CreateCounter<long>(
            MediaTelemetry.DeletedHandlerFailures,
            "{failure}",
            "Files-deleted handlers that threw. The files they were told about are gone; their own records about them are not.");
    }

    /// <summary>One handler failed one notification.</summary>
    internal void DeletedHandlerFailed(string handler)
    {
        if (!_handlerFailures.Enabled)
            return;

        _handlerFailures.Add(1, new TagList { { MediaTelemetry.TagHandler, handler } });
    }

    /// <inheritdoc/>
    public void Dispose() => _owned?.Dispose();
}
