using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DotNetBoost.Media.EntityFrameworkCore;

/// <summary>
/// <b>Nothing requires this, and nothing reads it.</b> It will be removed in the next major
/// version; delete it from your <see cref="DbContext"/> and the two <see cref="DbSet{TEntity}"/>
/// properties with it.
/// <para>
/// It once looked like the way a context opted in. It never was:
/// <c>UseEntityFrameworkCore&lt;TContext&gt;()</c> constrains on <see cref="DbContext"/> alone, and
/// <c>EfCoreMediaStore</c> reaches the two tables through <c>context.Set&lt;T&gt;()</c>, which works
/// on any context whose model has them. Implementing it bought nothing but two properties to
/// declare.
/// </para>
/// <para>
/// What a context does need is the one line that puts the tables in its model:
/// <see cref="MediaModelBuilderExtensions.ApplyMedia(ModelBuilder, Action{MediaModelOptions})"/>
/// at the end of <c>OnModelCreating</c>. That is the whole opt-in.
/// </para>
/// </summary>
[Obsolete(
    "IMediaDbContext is not required and is never read: UseEntityFrameworkCore<TContext> constrains on " +
    "DbContext alone, and the store reaches both tables through context.Set<T>(). Remove it from your " +
    "DbContext along with the two DbSet properties, and keep only the ApplyMedia() call in OnModelCreating. " +
    "This interface will be removed in the next major version.")]
public interface IMediaDbContext
{
    /// <summary>The one table every uploaded file is recorded in.</summary>
    DbSet<MediaFile> MediaFiles { get; }

    /// <summary>The links between those files and the entities using them.</summary>
    DbSet<MediaAttachment> MediaAttachments { get; }
}

/// <summary>Table naming for the two tables this store adds to your model.</summary>
public sealed class MediaModelOptions
{
    /// <summary>Table holding the media rows. Default: <c>MediaFiles</c>.</summary>
    public string MediaFilesTable { get; set; } = "MediaFiles";

    /// <summary>
    /// Table holding the links between files and entities. Default: <c>MediaAttachments</c>.
    /// <para>
    /// Two tables in total, whatever the application attaches media to. This one is why a file can
    /// belong to several entities at once, and adding media to a new entity type still costs no
    /// migration.
    /// </para>
    /// </summary>
    public string MediaAttachmentsTable { get; set; } = "MediaAttachments";

    /// <summary>Schema for the tables. Default: the model's default schema.</summary>
    public string? Schema { get; set; }
}

/// <summary>Entity mapping for <see cref="MediaFile"/>.</summary>
/// <param name="options">Table naming.</param>
public sealed class MediaFileConfiguration(MediaModelOptions options) : IEntityTypeConfiguration<MediaFile>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<MediaFile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(options.MediaFilesTable, options.Schema);

        builder.HasKey(f => f.Id).HasName("PK_" + options.MediaFilesTable);

        // The id is assigned by the engine before the bytes are written — the storage path is
        // derived from it — so the database must not generate one of its own.
        builder.Property(f => f.Id).ValueGeneratedNever();

        builder.Property(f => f.FileName).HasMaxLength(MediaPaths.MaxFileNameLength).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(255).IsRequired();
        builder.Property(f => f.Size).IsRequired();

        builder.Property(f => f.StorageProvider).HasMaxLength(64).IsRequired();
        builder.Property(f => f.StorageContainer).HasMaxLength(512).IsRequired();
        builder.Property(f => f.StoragePath).HasMaxLength(MediaPaths.MaxPathLength).IsRequired();

        // Lowercase hex SHA-256: always 64 characters, so the column is fixed at that.
        builder.Property(f => f.Checksum).HasMaxLength(64);

        builder.Property(f => f.Category).HasMaxLength(MediaCategories.MaxLength);
        builder.Property(f => f.Tags).HasMaxLength(MediaTags.MaxLength);
        builder.Property(f => f.Metadata).HasMaxLength(MediaMetadata.MaxLength);
        builder.Property(f => f.UploadedBy).HasMaxLength(256);

        // Every timestamp this library writes is UTC, and these converters make the database
        // agree on the way in and the way out.
        //
        // On the way in it matters for PostgreSQL, whose `timestamp with time zone` rejects a
        // DateTime that is not Kind.Utc. On the way out it matters for SQLite and SQL Server,
        // which both return Kind.Unspecified — a value that then serializes to JSON without a "Z"
        // and is read by every client as local time, which is an hour or eight wrong.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var nullableUtc = new ValueConverter<DateTime?, DateTime?>(
            v => v == null ? null : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
            v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

        builder.Property(f => f.CreatedAt).HasConversion(utc).IsRequired();
        builder.Property(f => f.UpdatedAt).HasConversion(nullableUtc);
        builder.Property(f => f.DeletedAt).HasConversion(nullableUtc);

        // Computed from DeletedAt. Mapping it would add a column that can disagree with the one
        // it is derived from.
        builder.Ignore(f => f.IsDeleted);

        // Deduplication and integrity checks look files up by hash. Non-unique: two rows may
        // legitimately share a checksum when deduplication is off.
        builder.HasIndex(f => f.Checksum)
            .HasDatabaseName($"IX_{options.MediaFilesTable}_Checksum");

        // Backs the default listing (everything, newest first) and every category browse.
        builder.HasIndex(f => new { f.Category, f.CreatedAt })
            .HasDatabaseName($"IX_{options.MediaFilesTable}_Category_CreatedAt");
    }
}

/// <summary>Entity mapping for <see cref="MediaAttachment"/>.</summary>
/// <param name="options">Table naming.</param>
public sealed class MediaAttachmentConfiguration(MediaModelOptions options) : IEntityTypeConfiguration<MediaAttachment>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<MediaAttachment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(options.MediaAttachmentsTable, options.Schema);

        builder.HasKey(a => a.Id).HasName("PK_" + options.MediaAttachmentsTable);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.MediaFileId).IsRequired();
        builder.Property(a => a.OwnerEntityName).HasMaxLength(MediaEntityNames.MaxLength).IsRequired();
        builder.Property(a => a.OwnerEntityId).HasMaxLength(EntityKey.MaxLength).IsRequired();
        builder.Property(a => a.Role).HasMaxLength(MediaRoles.MaxRoleLength).IsRequired();
        builder.Property(a => a.SortOrder).IsRequired();

        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        builder.Property(a => a.CreatedAt).HasConversion(utc).IsRequired();

        // Deliberately no relationship to MediaFile. A navigation would make EF cascade the delete,
        // and the engine already removes these rows itself, in an order it controls — bytes, then
        // links, then the row. It also keeps this table mappable on its own, which is what a store
        // that never references the application's entities needs.
        //
        // Unique on all four columns rather than three: one file may legitimately hold two roles on
        // one entity — the same image as its primary and its seo picture — and what is forbidden is
        // the same file twice in the same role. This is also what makes attaching idempotent.
        builder.HasIndex(a => new { a.MediaFileId, a.OwnerEntityName, a.OwnerEntityId, a.Role })
            .IsUnique()
            .HasDatabaseName($"IX_{options.MediaAttachmentsTable}_Unique");

        // "This entity's files, in the order it put them in" — what For(entity).ListAsync() runs.
        // Leading with the owner and ending on the position lets one index serve the filter, the
        // role narrowing and the sort together.
        builder.HasIndex(a => new { a.OwnerEntityName, a.OwnerEntityId, a.Role, a.SortOrder })
            .HasDatabaseName($"IX_{options.MediaAttachmentsTable}_Owner");

        // Backs the cascade when a file is deleted, and the sole-holder test behind DeleteAllAsync.
        builder.HasIndex(a => a.MediaFileId)
            .HasDatabaseName($"IX_{options.MediaAttachmentsTable}_MediaFile");
    }
}

/// <summary>Model-building entry points.</summary>
public static class MediaModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>MediaFiles</c> and <c>MediaAttachments</c> tables to the model.
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    ///     modelBuilder.ApplyMedia();
    /// }
    /// </code>
    /// <para>
    /// Nothing about your own entities changes. Attachment rows point AT an entity by name and key,
    /// and there is deliberately no foreign key back — that is what lets two tables serve every
    /// entity in the application, and what makes adding media to a new entity cost no migration.
    /// The price is that deleting an entity does not cascade to its files: call
    /// <c>media.For(entity).DeleteAllAsync()</c> when it goes, which deletes the files that entity
    /// alone was using and detaches the ones others still reference.
    /// </para>
    /// </summary>
    public static ModelBuilder ApplyMedia(this ModelBuilder modelBuilder, Action<MediaModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var options = new MediaModelOptions();
        configure?.Invoke(options);

        modelBuilder.ApplyConfiguration(new MediaFileConfiguration(options));
        modelBuilder.ApplyConfiguration(new MediaAttachmentConfiguration(options));
        return modelBuilder;
    }
}

/// <summary>
/// The one media question a set of known ids cannot answer: which owners have media <b>at all</b>,
/// as a queryable to compose into your own.
/// </summary>
public static class MediaQueryableExtensions
{
    /// <summary>
    /// The formatted ids of every owner of <typeparamref name="TOwner"/> with something attached —
    /// optionally in one role — as an <see cref="IQueryable{T}"/> that has not run yet.
    /// <para>
    /// <c>IMediaManager.ForMany</c> answers "which of <i>these</i> owners have images", which is
    /// everything a listing page needs once it knows its page. Filtering <b>before</b> paging is a
    /// different question — "the first 20 products that have an image" is not a subset of the first
    /// 20 products — and it can only be asked where the two sets meet, which is the database:
    /// <code>
    /// var withImage = db.OwnersWithMedia&lt;Product&gt;("primary");
    ///
    /// var page = await db.Products
    ///     .Where(p =&gt; withImage.Contains(p.Id.ToString()))
    ///     .OrderBy(p =&gt; p.Name)
    ///     .Skip(skip).Take(take)
    ///     .ToListAsync(ct);
    /// </code>
    /// Negate it with <c>!withImage.Contains(...)</c> for the products still missing a photo.
    /// </para>
    /// <para>
    /// Composable because nothing is materialised: it is a <c>SELECT DISTINCT OwnerEntityId</c>
    /// over the attachment table, which EF Core folds into the outer query as a subquery. The
    /// comparison is against the <b>stored</b> key — invariant, GUIDs in <c>"D"</c> format — so
    /// write the owner side as the provider's own string conversion of the key and not as anything
    /// culture-dependent.
    /// </para>
    /// <para>
    /// One statement rather than a join to the files, because an attachment never points at a
    /// deleted file: deleting one removes every attachment of it outright, even under soft delete.
    /// That guarantee is what this method rests on, and it is documented on
    /// <c>IMediaManager.DeleteAsync</c>.
    /// </para>
    /// </summary>
    /// <typeparam name="TOwner">
    /// The entity type, whose stored name comes from <c>[MediaEntity("…")]</c> or the type name —
    /// read exactly as <c>IMediaManager.For(entity)</c> reads it.
    /// </typeparam>
    /// <param name="db">The context holding the media tables.</param>
    /// <param name="role">One role, or <c>null</c> for "anything attached".</param>
    public static IQueryable<string> OwnersWithMedia<TOwner>(this DbContext db, string? role = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var entityName = MediaEntityNames.For(typeof(TOwner));

        // Normalized only when there is one: MediaRoles.Normalize(null) is the "default" role,
        // and null here means every role rather than that one.
        var normalized = role is null ? null : MediaRoles.Normalize(role);

        return db.Set<MediaAttachment>()
            .Where(a => a.OwnerEntityName == entityName)
            .Where(a => normalized == null || a.Role == normalized)
            .Select(a => a.OwnerEntityId)
            .Distinct();
    }
}
