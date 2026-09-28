using System.Data.Common;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Media.EntityFrameworkCore;

/// <summary>
/// Keeps media rows and the attachments linking them to entities in the application's own
/// <see cref="DbContext"/>, which is what lets an upload and the entity it belongs to be saved in
/// one transaction: both go through the same context, and the application's
/// <c>SaveChangesAsync</c> is the one that commits.
/// <para>
/// Nothing here calls <c>SaveChanges</c> on its own except the writes the engine needs to be
/// durable immediately — an upload's row, which must exist before the method returns, since its
/// bytes already do.
/// </para>
/// </summary>
/// <param name="context">The context holding the table.</param>
public sealed class EfCoreMediaStore(DbContext context) : IMediaStore
{
    private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public async Task AddAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            _context.Set<MediaFile>().Add(file);
        }
        catch (InvalidOperationException ex)
        {
            // The change tracker already holds a different instance under this id, and refuses to
            // track a second. It throws here, before any SQL runs, so this is a separate catch
            // from the one below rather than the same failure arriving twice.
            throw new MediaConflictException(
                $"Could not record media file '{file.Id}'. Another row with that id is already being tracked.",
                ex);
        }

        try
        {
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // The row is gone from the change tracker's point of view either way; leaving a failed
            // Added entity behind would make the application's own next SaveChanges fail too, for
            // a reason it has nothing to do with.
            _context.Entry(file).State = EntityState.Detached;

            // Only a duplicate id is a conflict. A missing table, a dropped connection, a column
            // the model and the schema disagree about — those are reported as themselves, because
            // calling them a conflict sends the caller after a row that is not the problem, and
            // makes the API answer 409 to something no retry will ever fix.
            if (!await RowExistsAsync(file.Id, ct).ConfigureAwait(false))
                throw;

            throw new MediaConflictException(
                $"Could not record media file '{file.Id}'. Another row with that id already exists.",
                ex);
        }
    }

    /// <summary>
    /// Whether a row with this id is already there, asked after an insert has failed. It is a
    /// query rather than a look at the provider's error code because this assembly deliberately
    /// references no provider, and EF Core surfaces a unique violation only as the provider's own
    /// code. A probe that cannot run answers false, leaving the original write error to stand.
    /// </summary>
    private async Task<bool> RowExistsAsync(Guid id, CancellationToken ct)
    {
        try
        {
            return await _context.Set<MediaFile>()
                .AsNoTracking()
                .AnyAsync(f => f.Id == id, ct)
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<MediaFile?> FindAsync(Guid id, bool includeDeleted = false, CancellationToken ct = default)
    {
        var file = await _context.Set<MediaFile>()
            .FirstOrDefaultAsync(f => f.Id == id, ct)
            .ConfigureAwait(false);

        return file is null || (!includeDeleted && file.DeletedAt is not null) ? null : file;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(MediaFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        // The engine reads a row and mutates it, so it is usually already tracked and this is a
        // no-op. Attach covers the case where it is not — a row that came from a no-tracking query.
        if (_context.Entry(file).State == EntityState.Detached)
            _context.Set<MediaFile>().Update(file);

        try
        {
            var written = await _context.SaveChangesAsync(ct).ConfigureAwait(false);

            // Nothing was written and nothing is there: the row was deleted underneath the caller.
            // (Zero rows written with the row present just means nothing actually changed.)
            if (written == 0 && !await ExistsAsync(file.Id, ct).ConfigureAwait(false))
                throw new MediaNotFoundException(file.Id);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // EF issues an UPDATE, sees it affected no rows, and reports a concurrency conflict.
            // For a store keyed only on its id there is only one thing that means.
            _context.Entry(file).State = EntityState.Detached;
            throw new MediaNotFoundException(file.Id) { Source = ex.Source };
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var file = await _context.Set<MediaFile>().FirstOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
        if (file is null)
            return false;

        _context.Set<MediaFile>().Remove(file);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaFile>> QueryAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = Sort(Filter(_context.Set<MediaFile>().AsQueryable(), query), query);

        return await filtered
            .Skip(query.Skip)
            .Take(query.Take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Filter(_context.Set<MediaFile>().AsQueryable(), query).CountAsync(ct);
    }

    /// <inheritdoc/>
    public Task<MediaFile?> FindByChecksumAsync(string checksum, long size, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);

        return _context.Set<MediaFile>()
            .Where(f => f.Checksum == checksum && f.Size == size && f.DeletedAt == null)
            .OrderBy(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public async Task AddAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        _context.Set<MediaAttachment>().Add(attachment);

        try
        {
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _context.Entry(attachment).State = EntityState.Detached;

            // Only the uniqueness of (file, owner, role) is a conflict, and it is asked as a query
            // for the same reason the file insert asks: this assembly references no provider, so a
            // unique violation reaches it only as the provider's own error code. Anything else — a
            // missing table, a dropped connection — is reported as itself rather than as a 409 no
            // retry can clear.
            if (!await AttachmentExistsAsync(attachment, ct).ConfigureAwait(false))
                throw;

            throw new MediaConflictException(
                $"Media file '{attachment.MediaFileId}' is already attached to {attachment.OwnerEntityName} " +
                $"{attachment.OwnerEntityId} as '{attachment.Role}'.",
                ex);
        }
    }

    /// <summary>
    /// Whether that exact link is already there, asked after an insert has failed. A probe that
    /// cannot run answers false, leaving the original write error to stand.
    /// </summary>
    private async Task<bool> AttachmentExistsAsync(MediaAttachment attachment, CancellationToken ct)
    {
        try
        {
            return await _context.Set<MediaAttachment>()
                .AsNoTracking()
                .AnyAsync(
                    a => a.MediaFileId == attachment.MediaFileId
                         && a.OwnerEntityName == attachment.OwnerEntityName
                         && a.OwnerEntityId == attachment.OwnerEntityId
                         && a.Role == attachment.Role,
                    ct)
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await _context.Set<MediaAttachment>()
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct)
            .ConfigureAwait(false);

        if (attachment is null)
            return false;

        _context.Set<MediaAttachment>().Remove(attachment);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default)
    {
        var doomed = await _context.Set<MediaAttachment>()
            .Where(a => a.OwnerEntityName == owner.EntityName && a.OwnerEntityId == owner.EntityId)
            .Where(a => role == null || a.Role == role)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await RemoveRangeAsync(doomed, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAttachmentsOfFileAsync(Guid mediaFileId, CancellationToken ct = default)
    {
        var doomed = await _context.Set<MediaAttachment>()
            .Where(a => a.MediaFileId == mediaFileId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await RemoveRangeAsync(doomed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a set of links in one SaveChanges. Loaded and removed rather than deleted in bulk by
    /// ExecuteDelete, because this store shares the application's context: ExecuteDelete runs
    /// outside the change tracker and outside any transaction the application has open, which would
    /// make "delete the entity and its media together" stop being one unit of work.
    /// </summary>
    private async Task<int> RemoveRangeAsync(List<MediaAttachment> doomed, CancellationToken ct)
    {
        if (doomed.Count == 0)
            return 0;

        _context.Set<MediaAttachment>().RemoveRange(doomed);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        return doomed.Count;
    }

    /// <inheritdoc/>
    public async Task UpdateAttachmentAsync(MediaAttachment attachment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (_context.Entry(attachment).State == EntityState.Detached)
            _context.Set<MediaAttachment>().Update(attachment);

        try
        {
            var written = await _context.SaveChangesAsync(ct).ConfigureAwait(false);

            if (written == 0 && !await AttachmentExistsAsync(attachment.Id, ct).ConfigureAwait(false))
                throw new MediaNotFoundException(attachment.Id);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _context.Entry(attachment).State = EntityState.Detached;
            throw new MediaNotFoundException(attachment.Id) { Source = ex.Source };
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAttachmentsAsync(
        IReadOnlyCollection<MediaAttachment> attachments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count == 0)
            return;

        foreach (var attachment in attachments)
        {
            if (_context.Entry(attachment).State == EntityState.Detached)
                _context.Set<MediaAttachment>().Update(attachment);
        }

        try
        {
            // One SaveChanges for the lot, which EF sends as one batched command inside one
            // transaction. That is the whole point: a reorder either takes or it does not.
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // EF expected each UPDATE to touch a row and one of them touched none, which for rows
            // with no concurrency token means the row is gone rather than that it was changed
            // underneath us.
            var missing = ex.Entries
                .Select(entry => entry.Entity)
                .OfType<MediaAttachment>()
                .FirstOrDefault();

            foreach (var entry in ex.Entries)
                entry.State = EntityState.Detached;

            throw new MediaNotFoundException(missing?.Id ?? Guid.Empty) { Source = ex.Source };
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaAttachment>> ListAttachmentsOfFileAsync(
        Guid mediaFileId, CancellationToken ct = default) =>
        await _context.Set<MediaAttachment>()
            .Where(a => a.MediaFileId == mediaFileId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaAttachment>> ListAttachmentsAsync(
        MediaOwner owner, string? role = null, CancellationToken ct = default) =>
        await _context.Set<MediaAttachment>()
            .Where(a => a.OwnerEntityName == owner.EntityName && a.OwnerEntityId == owner.EntityId)
            .Where(a => role == null || a.Role == role)
            .OrderBy(a => a.Role).ThenBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlySet<Guid>> FindSharedAsync(
        IReadOnlyCollection<Guid> mediaFileIds, MediaOwner excluding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaFileIds);

        if (mediaFileIds.Count == 0)
            return new HashSet<Guid>();

        // One statement for the whole set. The alternative — asking per file whether anyone else
        // holds it — turns deleting a product with fifty images into fifty round trips.
        var shared = await _context.Set<MediaAttachment>()
            .Where(a => mediaFileIds.Contains(a.MediaFileId))
            .Where(a => a.OwnerEntityName != excluding.EntityName || a.OwnerEntityId != excluding.EntityId)
            .Select(a => a.MediaFileId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return shared.ToHashSet();
    }

    private Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        _context.Set<MediaFile>().AnyAsync(f => f.Id == id, ct);

    private Task<bool> AttachmentExistsAsync(Guid id, CancellationToken ct) =>
        _context.Set<MediaAttachment>().AnyAsync(a => a.Id == id, ct);

    /// <summary>
    /// Applies a <see cref="MediaQuery"/> to a queryable. Shared by the page and the count so the
    /// two cannot drift — a total that does not match the rows it is a total of is worse than no
    /// total at all.
    /// </summary>
    /// <summary>
    /// Applies a query's ordering. Every ordering ends on the id, so two rows that tie on the sort
    /// column page stably rather than repeating one and skipping another.
    /// </summary>
    private IQueryable<MediaFile> Sort(IQueryable<MediaFile> files, MediaQuery query)
    {
        // The entity's own order lives on the attachment, so the key is two correlated subqueries
        // over the links this query matched: the lowest role, then the lowest position. Narrowed to
        // one role — the ordering anyone actually asks for — the first is constant and this is
        // simply that role's SortOrder.
        if (query.SortBy == MediaSortBy.SortOrder)
        {
            var attachments = Attachments(query);

            return query.Descending
                ? files
                    .OrderByDescending(f => attachments.Where(a => a.MediaFileId == f.Id).Min(a => a.Role))
                    .ThenByDescending(f => attachments.Where(a => a.MediaFileId == f.Id).Min(a => a.SortOrder))
                    .ThenByDescending(f => f.Id)
                : files
                    .OrderBy(f => attachments.Where(a => a.MediaFileId == f.Id).Min(a => a.Role))
                    .ThenBy(f => attachments.Where(a => a.MediaFileId == f.Id).Min(a => a.SortOrder))
                    .ThenBy(f => f.Id);
        }

        return (query.SortBy, query.Descending) switch
        {
            (MediaSortBy.FileName, true)  => files.OrderByDescending(f => f.FileName).ThenByDescending(f => f.Id),
            (MediaSortBy.FileName, false) => files.OrderBy(f => f.FileName).ThenBy(f => f.Id),
            (MediaSortBy.Size, true)      => files.OrderByDescending(f => f.Size).ThenByDescending(f => f.Id),
            (MediaSortBy.Size, false)     => files.OrderBy(f => f.Size).ThenBy(f => f.Id),
            (_, false)                    => files.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            _                             => files.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
        };
    }

    /// <summary>
    /// The attachments a query's owner filter matches, as a queryable the file query correlates
    /// against. Composed once so the join and the ordering cannot disagree about which links they
    /// are talking about.
    /// </summary>
    private IQueryable<MediaAttachment> Attachments(MediaQuery query)
    {
        var attachments = _context.Set<MediaAttachment>().AsQueryable();

        if (query.OwnerEntityName is { } ownerName)
            attachments = attachments.Where(a => a.OwnerEntityName == ownerName);

        if (query.OwnerEntityId is { } ownerId)
            attachments = attachments.Where(a => a.OwnerEntityId == ownerId);

        if (query.Role is { } role)
            attachments = attachments.Where(a => a.Role == role);

        return attachments;
    }

    /// <summary>
    /// Neutralises the wildcards in a value used inside a LIKE. Escaping the escape character
    /// first is what keeps the transformation reversible.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private IQueryable<MediaFile> Filter(IQueryable<MediaFile> files, MediaQuery query)
    {
        if (!query.IncludeDeleted)
            files = files.Where(f => f.DeletedAt == null);

        // The join, as EXISTS rather than a JOIN: a file linked to nine of this entity's roles must
        // come back once, and EXISTS says that without a DISTINCT over every selected column.
        //
        // Only when an owner is asked for. An unfiltered listing never touches the attachment table
        // at all, which is what keeps unattached files — uploads waiting for the form that will
        // claim them — in the library rather than quietly out of it.
        if (query.OwnerEntityName is not null)
        {
            var attachments = Attachments(query);
            files = files.Where(f => attachments.Any(a => a.MediaFileId == f.Id));
        }

        if (query.Unattached)
        {
            var all = _context.Set<MediaAttachment>();
            files = files.Where(f => !all.Any(a => a.MediaFileId == f.Id));
        }

        if (query.Category is { } category)
            files = files.Where(f => f.Category == category);

        if (query.UploadedBy is { } uploadedBy)
            files = files.Where(f => f.UploadedBy == uploadedBy);

        if (query.Checksum is { } checksum)
            files = files.Where(f => f.Checksum == checksum);

        if (query.CreatedAfter is { } after)
            files = files.Where(f => f.CreatedAt >= after);

        if (query.CreatedBefore is { } before)
            files = files.Where(f => f.CreatedAt < before);

        if (query.ContentTypePrefix is { } prefix)
            files = files.Where(f => EF.Functions.Like(f.ContentType, Escape(prefix) + "%", "\\"));

        if (query.Search is { Length: > 0 } search)
        {
            // Both sides lowered, because LIKE is case-SENSITIVE on PostgreSQL — matching the
            // column's collation would make "case-insensitive" true on SQL Server and false on
            // Postgres, which is worse than either. Unanchored, so it matches anywhere in the
            // name, and escaped so a search for "report_final" does not also match "reportxfinal".
            // No index is lost: an unanchored LIKE could not use one anyway.
            var pattern = "%" + Escape(search.ToLowerInvariant()) + "%";

            // CA1304/CA1311 want ToLower(CultureInfo). This ToLower() is never executed by .NET —
            // it is an expression tree that EF translates to the database's own LOWER(), and the
            // culture-taking overload has no translation at all.
#pragma warning disable CA1304, CA1311
            files = files.Where(f => EF.Functions.Like(f.FileName.ToLower(), pattern, "\\"));
#pragma warning restore CA1304, CA1311
        }

        if (query.Tag is { } tag)
        {
            // Tags are stored comma-surrounded, so the fragment ",invoice," matches a whole tag
            // and never a longer one that starts with it. EF.Functions.Like keeps this in the
            // database; Contains on a nullable column would translate differently per provider.
            // Escaped, with an explicit ESCAPE character: a tag may legitimately contain "_" or
            // "%", and unescaped those are LIKE wildcards that would match far more than was asked
            // for. The overload taking an escape character is the only way to say so through EF.
            var fragment = Escape(MediaTags.Fragment(tag));
            files = files.Where(f => f.Tags != null && EF.Functions.Like(f.Tags, "%" + fragment + "%", "\\"));
        }

        return files;
    }
}

/// <summary>
/// Reads entity keys from the EF Core model rather than by convention, so keys this library
/// could not otherwise see — configured fluently, or a shadow property — are found.
/// </summary>
/// <param name="context">The context whose model defines the keys.</param>
public sealed class EfCoreEntityKeyAccessor(DbContext context) : IEntityKeyAccessor
{
    private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly ConventionEntityKeyAccessorFallback _fallback = new();

    /// <inheritdoc/>
    public string? GetKey(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entityType = _context.Model.FindEntityType(entity.GetType());
        var key        = entityType?.FindPrimaryKey();

        // Not in this context's model — a DTO, or an entity from another context. The convention
        // accessor may still know what to do with it, and saying "no key" here would fail an
        // attach that would otherwise work.
        if (key is null)
            return _fallback.GetKey(entity);

        if (key.Properties.Count > 1)
        {
            throw new InvalidOperationException(
                $"{entity.GetType().Name} has a composite primary key ({string.Join(", ", key.Properties.Select(p => p.Name))}). " +
                "Media rows record a single key, so attach files with media.For(name, id) and a key you " +
                "compose yourself, or register your own IEntityKeyAccessor.");
        }

        var property = key.Properties[0];
        var value    = _context.Entry(entity).Property(property.Name).CurrentValue;

        return value is null || IsDefault(value) ? null : EntityKey.ToKeyString(value);
    }

    private static bool IsDefault(object value) => value switch
    {
        int i    => i == 0,
        long l   => l == 0,
        short s  => s == 0,
        Guid g   => g == Guid.Empty,
        string s => s.Length == 0,
        _        => false
    };
}

/// <summary>
/// The Core convention accessor, reached from the EF Core one for types the model does not
/// contain. A thin alias so this file does not depend on the Core type being public forever.
/// </summary>
internal sealed class ConventionEntityKeyAccessorFallback : IEntityKeyAccessor
{
    private readonly Core.Services.ConventionEntityKeyAccessor _inner = new();

    public string? GetKey(object entity) => _inner.GetKey(entity);
}
