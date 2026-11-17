using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// The in-memory store with a unit of work bolted on: what a participant enlists is pending until
/// the next write that actually writes something, then committed with it — the same promise the EF
/// Core store makes, kept in lists a test can look at.
/// </summary>
internal sealed class EnlistingMediaStore(InMemoryMediaStore inner) : IEnlistingMediaStore
{
    private readonly List<object> _pending   = [];
    private readonly List<object> _committed = [];

    public InMemoryMediaStore Inner => inner;

    /// <summary>Enlisted and not yet committed or discarded.</summary>
    public IReadOnlyList<object> Pending => _pending;

    /// <summary>Committed with a write, in order.</summary>
    public IReadOnlyList<object> Committed => _committed;

    /// <summary>How many writes committed something enlisted.</summary>
    public int CommittingWrites { get; private set; }

    /// <summary>How many times <see cref="IEnlistingMediaStore.DeleteFileAsync"/> ran.</summary>
    public int FileDeletes { get; private set; }

    /// <summary>Makes every write throw before it writes anything.</summary>
    public bool FailWrites { get; set; }

    public void Enlist(object entity) => _pending.Add(entity);

    public void DiscardEnlisted() => _pending.Clear();

    private void Commit()
    {
        if (_pending.Count == 0)
            return;

        _committed.AddRange(_pending);
        _pending.Clear();
        CommittingWrites++;
    }

    private void ThrowIfFailing()
    {
        if (FailWrites)
            throw new InvalidOperationException("The store was told to fail.");
    }

    public async Task AddAsync(MediaFile file, CancellationToken ct = default)
    {
        ThrowIfFailing();
        await inner.AddAsync(file, ct);
        Commit();
    }

    public Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default) =>
        inner.FindAsync(id, includeDeleted, ct);

    public async Task UpdateAsync(MediaFile file, CancellationToken ct = default)
    {
        ThrowIfFailing();
        await inner.UpdateAsync(file, ct);
        Commit();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ThrowIfFailing();
        var deleted = await inner.DeleteAsync(id, ct);
        if (deleted) Commit();
        return deleted;
    }

    public async Task DeleteFileAsync(MediaFile file, bool softDelete, CancellationToken ct = default)
    {
        ThrowIfFailing();

        if (FailFileDeletesAfter is { } limit && FileDeletes >= limit)
            throw new InvalidOperationException("The store was told to fail this file delete.");

        FileDeletes++;

        await inner.RemoveAttachmentsOfFileAsync(file.Id, ct);

        if (softDelete)
            await inner.UpdateAsync(file, ct);
        else
            await inner.DeleteAsync(file.Id, ct);

        Commit();
    }

    /// <summary>How many times <see cref="IEnlistingMediaStore.ReplaceAttachmentsAsync"/> ran.</summary>
    public int Replacements { get; private set; }

    /// <summary>
    /// Makes the next file delete throw, after this many have succeeded — for a
    /// <c>DeleteAllAsync</c> that fails part-way. Null: never.
    /// </summary>
    public int? FailFileDeletesAfter { get; set; }

    public async Task<int> ReplaceAttachmentsAsync(MediaAttachment link, CancellationToken ct = default)
    {
        ThrowIfFailing();
        Replacements++;

        var displaced = await inner.RemoveAttachmentsAsync(
            new MediaOwner(link.OwnerEntityName, link.OwnerEntityId), link.Role, ct);
        await inner.AddAttachmentAsync(link, ct);

        Commit();
        return displaced;
    }

    public Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default) =>
        inner.QueryAsync(query, ct);

    public Task<int> CountAsync(MediaQuery query, CancellationToken ct = default) => inner.CountAsync(query, ct);

    public Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default) =>
        inner.FindByChecksumAsync(checksum, size, ct);

    public async Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ThrowIfFailing();
        await inner.AddAttachmentAsync(attachment, ct);
        Commit();
    }

    public async Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        ThrowIfFailing();
        var removed = await inner.RemoveAttachmentAsync(attachmentId, ct);
        if (removed) Commit();
        return removed;
    }

    public async Task<int> RemoveAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default)
    {
        ThrowIfFailing();
        var removed = await inner.RemoveAttachmentsAsync(owner, role, ct);
        if (removed > 0) Commit();
        return removed;
    }

    public async Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default)
    {
        ThrowIfFailing();
        var removed = await inner.RemoveAttachmentsOfFileAsync(mediaFileId, ct);
        if (removed > 0) Commit();
        return removed;
    }

    public async Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ThrowIfFailing();
        await inner.UpdateAttachmentAsync(attachment, ct);
        Commit();
    }

    public async Task UpdateAttachmentsAsync(IReadOnlyCollection<MediaAttachment> attachments, CancellationToken ct = default)
    {
        ThrowIfFailing();
        await inner.UpdateAttachmentsAsync(attachments, ct);
        Commit();
    }

    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default) =>
        inner.ListAttachmentsOfFileAsync(mediaFileId, ct);

    public Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(MediaOwner owner, string? role = null, CancellationToken ct = default) =>
        inner.ListAttachmentsAsync(owner, role, ct);

    public Task<IReadOnlyList<MediaOwnerAttachment>> ListAttachmentsForOwnersAsync(
        string entityName, IReadOnlyCollection<string> entityIds, string? role = null, CancellationToken ct = default) =>
        inner.ListAttachmentsForOwnersAsync(entityName, entityIds, role, ct);

    public Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default) =>
        inner.FindSharedAsync(mediaFileIds, excluding, ct);
}

/// <summary>
/// A participant that records every change it is told about and enlists it, so a test can see
/// both what was announced and what committed.
/// </summary>
/// <param name="store">Where it enlists; <c>null</c> to only record.</param>
/// <param name="failOn">Throws on the first change of this kind, after recording it.</param>
internal sealed class RecordingParticipant(EnlistingMediaStore? store = null, MediaChangeKind? failOn = null)
    : IMediaWriteParticipant
{
    private readonly List<MediaChange> _changes = [];

    public IReadOnlyList<MediaChange> Changes => _changes;

    public IEnumerable<MediaChangeKind> Kinds => _changes.Select(c => c.Kind);

    public Task OnWritingAsync(MediaChange change, CancellationToken ct = default)
    {
        _changes.Add(change);
        store?.Enlist(change);

        if (change.Kind == failOn)
            throw new InvalidOperationException($"The participant refused the {change.Kind} change.");

        return Task.CompletedTask;
    }
}

/// <summary>An engine over an <see cref="EnlistingMediaStore"/> and the given participants.</summary>
internal sealed class ParticipantHarness
{
    private ParticipantHarness(IMediaManager media, EnlistingMediaStore store, InMemoryStorageProvider storage)
    {
        Media   = media;
        Store   = store;
        Storage = storage;
    }

    public IMediaManager Media { get; }
    public EnlistingMediaStore Store { get; }
    public InMemoryStorageProvider Storage { get; }

    public static ParticipantHarness Create(
        Func<EnlistingMediaStore, IMediaWriteParticipant[]> participants,
        Action<MediaOptions>? configure = null,
        IMediaRoleRegistry? roles = null)
    {
        var options = new MediaOptions();
        configure?.Invoke(options);

        var store   = new EnlistingMediaStore(new InMemoryMediaStore());
        var storage = new InMemoryStorageProvider();
        var clock   = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));

        var manager = new MediaManager(
            store,
            new MediaStorageProviderRegistry([storage], defaultProvider: null),
            new DateHierarchyKeyGenerator(clock),
            new ConventionEntityKeyAccessor(),
            roles ?? MediaRoleRegistry.Empty,
            options,
            clock,
            [],
            NullLogger<MediaManager>.Instance,
            participants: participants(store));

        return new ParticipantHarness(manager, store, storage);
    }
}
