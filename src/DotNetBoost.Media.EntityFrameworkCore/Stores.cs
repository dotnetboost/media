using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Media.EntityFrameworkCore;

/// <summary>
/// Keeps media rows in the application's own <see cref="DbContext"/>, which is what lets an
/// upload and the entity it belongs to be saved in one transaction: both go through the same
/// context, and the application's <c>SaveChangesAsync</c> is the one that commits.
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

            throw new MediaConflictException(
                $"Could not record media file '{file.Id}'. Another row with that id may already exist.",
                ex);
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

    private Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        _context.Set<MediaFile>().AnyAsync(f => f.Id == id, ct);

    /// <summary>
    /// Applies a <see cref="MediaQuery"/> to a queryable. Shared by the page and the count so the
    /// two cannot drift — a total that does not match the rows it is a total of is worse than no
    /// total at all.
    /// </summary>
    /// <summary>
    /// Applies a query's ordering. Every ordering ends on the id, so two rows that tie on the sort
    /// column page stably rather than repeating one and skipping another.
    /// </summary>
    private static IQueryable<MediaFile> Sort(IQueryable<MediaFile> files, MediaQuery query) =>
        (query.SortBy, query.Descending) switch
        {
            (MediaSortBy.FileName, true)  => files.OrderByDescending(f => f.FileName).ThenByDescending(f => f.Id),
            (MediaSortBy.FileName, false) => files.OrderBy(f => f.FileName).ThenBy(f => f.Id),
            (MediaSortBy.Size, true)      => files.OrderByDescending(f => f.Size).ThenByDescending(f => f.Id),
            (MediaSortBy.Size, false)     => files.OrderBy(f => f.Size).ThenBy(f => f.Id),
            (_, false)                    => files.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            _                             => files.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
        };

    /// <summary>
    /// Neutralises the wildcards in a value used inside a LIKE. Escaping the escape character
    /// first is what keeps the transformation reversible.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static IQueryable<MediaFile> Filter(IQueryable<MediaFile> files, MediaQuery query)
    {
        if (!query.IncludeDeleted)
            files = files.Where(f => f.DeletedAt == null);

        if (query.OwnerEntityName is { } ownerName)
            files = files.Where(f => f.OwnerEntityName == ownerName);

        if (query.OwnerEntityId is { } ownerId)
            files = files.Where(f => f.OwnerEntityId == ownerId);

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
