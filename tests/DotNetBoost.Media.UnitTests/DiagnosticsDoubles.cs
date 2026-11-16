using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace DotNetBoost.Media.UnitTests;

/// <summary>One captured log entry, with its structured properties.</summary>
internal sealed record LogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    Exception? Exception,
    string Message,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Records every log entry at every level, structured properties included, so a test can assert on
/// what a failure leaves behind rather than only on the formatted text.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILoggerFactory CreateFactory() =>
        LoggerFactory.Create(b => b.AddProvider(this).SetMinimumLevel(LogLevel.Trace));

    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, _entries);

    public void Dispose() { }

    private sealed class Capturing(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();

            entries.Enqueue(new LogEntry(category, logLevel, eventId, exception, formatter(state, exception), properties));
        }
    }
}

/// <summary>
/// A meter factory whose meters this test alone listens to. The meter name is process-wide, so a
/// listener keyed by name alone would also hear every other test running in parallel.
/// </summary>
internal sealed class RecordingMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags)> _measurements = new();

    public RecordingMeterFactory()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            lock (_meters)
            {
                if (_meters.Contains(instrument.Meter))
                    listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;

            _measurements.Enqueue((instrument.Name, value, copy));
        });

        _listener.Start();
    }

    public IReadOnlyList<(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags)> Measurements =>
        [.. _measurements];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        lock (_meters)
            _meters.Add(meter);

        return meter;
    }

    public void Dispose()
    {
        _listener.Dispose();

        lock (_meters)
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}

/// <summary>
/// The in-memory store with one fault added: <see cref="FindAsync"/> throws on a chosen call, for
/// the "DeleteAllAsync fails part-way through the batch" path.
/// </summary>
internal sealed class FailingFindStore(InMemoryMediaStore inner, int failOnCall, Exception failure) : IMediaStore
{
    private int _finds;

    public Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default) =>
        ++_finds == failOnCall ? throw failure : inner.FindAsync(id, includeDeleted, ct);

    public Task AddAsync(MediaFile file, CancellationToken ct = default) => inner.AddAsync(file, ct);
    public Task UpdateAsync(MediaFile file, CancellationToken ct = default) => inner.UpdateAsync(file, ct);
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    public Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default) => inner.QueryAsync(query, ct);
    public Task<int> CountAsync(MediaQuery query, CancellationToken ct = default) => inner.CountAsync(query, ct);

    public Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default) =>
        inner.FindByChecksumAsync(checksum, size, ct);

    public Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default) =>
        inner.AddAttachmentAsync(attachment, ct);

    public Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default) =>
        inner.RemoveAttachmentAsync(attachmentId, ct);

    public Task<int> RemoveAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default) =>
        inner.RemoveAttachmentsAsync(owner, role, ct);

    public Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default) =>
        inner.RemoveAttachmentsOfFileAsync(mediaFileId, ct);

    public Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default) =>
        inner.UpdateAttachmentAsync(attachment, ct);

    public Task UpdateAttachmentsAsync(IReadOnlyCollection<MediaAttachment> attachments, CancellationToken ct = default) =>
        inner.UpdateAttachmentsAsync(attachments, ct);

    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default) =>
        inner.ListAttachmentsOfFileAsync(mediaFileId, ct);

    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default) =>
        inner.ListAttachmentsAsync(owner, role, ct);

    public Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default) =>
        inner.FindSharedAsync(mediaFileIds, excluding, ct);

    public Task<IReadOnlyList<MediaOwnerAttachment>> ListAttachmentsForOwnersAsync(
        string entityName, IReadOnlyCollection<string> entityIds, string? role = null, CancellationToken ct = default) =>
        inner.ListAttachmentsForOwnersAsync(entityName, entityIds, role, ct);
}

/// <summary>Records the token each notification arrived with.</summary>
internal sealed class TokenRecordingHandler : IMediaFilesDeletedHandler
{
    public List<CancellationToken> Tokens { get; } = [];

    public Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default)
    {
        Tokens.Add(ct);
        return Task.CompletedTask;
    }
}
