using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Services;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// A media engine over the in-memory doubles, with a pinned clock. Built directly rather than
/// through DI: these tests are about the engine's behaviour, and a container in the way only
/// makes a failure harder to read.
/// </summary>
internal sealed class Harness
{
    private Harness(MediaManager manager, InMemoryMediaStore store, InMemoryStorageProvider storage, MediaOptions options, FakeTimeProvider clock)
    {
        Media   = manager;
        Store   = store;
        Storage = storage;
        Options = options;
        Clock   = clock;
    }

    public IMediaManager Media { get; }
    public InMemoryMediaStore Store { get; }
    public InMemoryStorageProvider Storage { get; }
    public MediaOptions Options { get; }
    public FakeTimeProvider Clock { get; }

    /// <summary>An engine over stores the caller supplies — for the failure doubles.</summary>
    public static IMediaManager Over(IMediaStore store, params IMediaStorageProvider[] backends)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));

        return new MediaManager(
            store,
            new MediaStorageProviderRegistry(backends, defaultProvider: null),
            new DateHierarchyKeyGenerator(clock),
            new ConventionEntityKeyAccessor(),
            MediaRoleRegistry.Empty,
            new MediaOptions(),
            clock,
            [],
            NullLogger<MediaManager>.Instance);
    }

    /// <param name="configure">Changes to the options before the engine is built.</param>
    /// <param name="roles">
    /// What each entity declares its roles to be. Left null, nothing is declared and nothing is
    /// checked — which is what every test that is not about roles wants.
    /// </param>
    /// <param name="extraBackends">Further backends to register, for the multi-backend cases.</param>
    /// <param name="deletedHandlers">
    /// Files-deleted handlers, in the order the container would hand them over. Empty for every
    /// test that is not about the notification.
    /// </param>
    public static Harness Create(
        Action<MediaOptions>? configure = null,
        IMediaRoleRegistry? roles = null,
        IEnumerable<IMediaFilesDeletedHandler>? deletedHandlers = null,
        params InMemoryStorageProvider[] extraBackends)
    {
        var options = new MediaOptions();
        configure?.Invoke(options);

        var store   = new InMemoryMediaStore();
        var storage = new InMemoryStorageProvider();
        var clock   = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));

        var registry = new MediaStorageProviderRegistry([storage, .. extraBackends], defaultProvider: null);

        var manager = new MediaManager(
            store,
            registry,
            new DateHierarchyKeyGenerator(clock),
            new ConventionEntityKeyAccessor(),
            roles ?? MediaRoleRegistry.Empty,
            options,
            clock,
            deletedHandlers ?? [],
            NullLogger<MediaManager>.Instance);

        return new Harness(manager, store, storage, options, clock);
    }
}

/// <summary>
/// A clock the tests move by hand. <c>TimeProvider</c> has no built-in fake outside the
/// Microsoft.Extensions.TimeProvider.Testing package, and the two methods used here do not justify
/// taking a dependency on it.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>A metadata store whose inserts always fail, for the "bytes written, row lost" path.</summary>
internal sealed class FailingMediaStore : IMediaStore
{
    public Task AddAsync(MediaFile file, CancellationToken ct = default) =>
        throw new InvalidOperationException("The store was told to fail.");

    public Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default) =>
        Task.FromResult<MediaFile?>(null);

    public Task UpdateAsync(MediaFile file, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaFile>>([]);

    public Task<int> CountAsync(MediaQuery query, CancellationToken ct = default) => Task.FromResult(0);

    public Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default) =>
        Task.FromResult<MediaFile?>(null);

    // The attachment half of the contract is inert: this double exists for the "bytes written, row
    // lost" path, which fails at AddAsync before anything is ever attached.
    public Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default) =>
        throw new InvalidOperationException("The store was told to fail.");

    public Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<int> RemoveAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateAttachmentsAsync(IReadOnlyCollection<MediaAttachment> attachments, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaAttachment>>([]);

    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaAttachment>>([]);

    public Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public Task<IReadOnlyList<MediaOwnerAttachment>> ListAttachmentsForOwnersAsync(
        string entityName,
        IReadOnlyCollection<string> entityIds,
        string? role = null,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaOwnerAttachment>>([]);
}

/// <summary>
/// A files-deleted handler that records what it was told, for the tests about the notification.
/// </summary>
/// <param name="name">Names it in an assertion about the order several ran in.</param>
/// <param name="fail">Throws instead of recording, for the "logged and swallowed" case.</param>
internal sealed class RecordingDeletedHandler(string name = "recorder", bool fail = false) : IMediaFilesDeletedHandler
{
    private readonly List<IReadOnlyCollection<Guid>> _calls = [];

    public string Name => name;

    /// <summary>One entry per notification, holding the ids that notification carried.</summary>
    public IReadOnlyList<IReadOnlyCollection<Guid>> Calls => _calls;

    /// <summary>The ids of every notification run together, for the usual single-call assertion.</summary>
    public IReadOnlyList<Guid> Ids => [.. _calls.SelectMany(ids => ids)];

    public Task OnDeletedAsync(IReadOnlyCollection<Guid> mediaFileIds, CancellationToken ct = default)
    {
        if (fail)
            throw new InvalidOperationException($"Handler '{name}' was told to fail.");

        _calls.Add([.. mediaFileIds]);
        return Task.CompletedTask;
    }
}
