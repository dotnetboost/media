namespace DotNetBoost.Media.Core;

/// <summary>
/// The names of the metrics the engine emits, for an application subscribing to them.
/// <code>
/// builder.Services.AddOpenTelemetry()
///        .WithMetrics(metrics =&gt; metrics.AddMeter(MediaTelemetry.MeterName));
/// </code>
/// </summary>
/// <remarks>
/// <b>Nothing is emitted until something subscribes.</b> Each instrument is asked whether it is
/// enabled before any tag is built, so an unsubscribed counter costs a field read and a branch.
/// </remarks>
public static class MediaTelemetry
{
    /// <summary>The meter every instrument below belongs to.</summary>
    public const string MeterName = "DotNetBoost.Media";

    /// <summary>The meter's version, reported to a subscriber alongside the name.</summary>
    public const string MeterVersion = "1.0.0";

    /// <summary>
    /// Files-deleted handlers that threw, tagged <see cref="TagHandler"/>. One per failed
    /// notification, however many files it carried.
    /// <para>
    /// <b>Should stay at zero.</b> A handler's failure is logged and swallowed, because the files
    /// are already gone and failing the delete could not bring them back. That leaves whatever
    /// the handler keeps about those files stale, and this counter is how you find out without
    /// reading every log line. The ids it missed are on the log entry (event 7004).
    /// </para>
    /// </summary>
    public const string DeletedHandlerFailures = "dotnetboost.media.deleted_handler.failures";

    /// <summary>
    /// Which handler failed: its class name, or for an inline handler the name given to
    /// <c>OnFilesDeleted(handler, name)</c>. Bounded by what the application registered, which is
    /// what makes it safe to tag by. File ids are deliberately never a tag.
    /// </summary>
    public const string TagHandler = "media.handler";
}
