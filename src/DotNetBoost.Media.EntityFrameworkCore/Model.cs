using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DotNetBoost.Media.EntityFrameworkCore;

/// <summary>
/// Implement this on your <see cref="DbContext"/> to let the EF Core store use it, and call
/// <see cref="MediaModelBuilderExtensions.ApplyMedia(ModelBuilder, Action{MediaModelOptions})"/>
/// at the end of <c>OnModelCreating</c>.
/// </summary>
public interface IMediaDbContext
{
    /// <summary>The one table every uploaded file is recorded in.</summary>
    DbSet<MediaFile> MediaFiles { get; }
}

/// <summary>Table naming for the one table this store adds to your model.</summary>
public sealed class MediaModelOptions
{
    /// <summary>Table holding the media rows. Default: <c>MediaFiles</c>.</summary>
    public string MediaFilesTable { get; set; } = "MediaFiles";

    /// <summary>Schema for the table. Default: the model's default schema.</summary>
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

        builder.Property(f => f.OwnerEntityName).HasMaxLength(MediaEntityNames.MaxLength);
        builder.Property(f => f.OwnerEntityId).HasMaxLength(EntityKey.MaxLength);
        builder.Property(f => f.Category).HasMaxLength(64);
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

        // "This entity's files, newest first" is the query the library issues most, and it is what
        // IMediaManager.For(entity).ListAsync() runs. Leading with the owner columns lets the
        // index serve the filter and the sort together.
        builder.HasIndex(f => new { f.OwnerEntityName, f.OwnerEntityId, f.CreatedAt })
            .HasDatabaseName($"IX_{options.MediaFilesTable}_Owner");

        // Deduplication and integrity checks look files up by hash. Non-unique: two rows may
        // legitimately share a checksum when deduplication is off.
        builder.HasIndex(f => f.Checksum)
            .HasDatabaseName($"IX_{options.MediaFilesTable}_Checksum");

        // Backs the default listing (everything, newest first) and every category browse.
        builder.HasIndex(f => new { f.Category, f.CreatedAt })
            .HasDatabaseName($"IX_{options.MediaFilesTable}_Category_CreatedAt");
    }
}

/// <summary>Model-building entry points.</summary>
public static class MediaModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>MediaFiles</c> table to the model.
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    ///     modelBuilder.ApplyMedia();
    /// }
    /// </code>
    /// <para>
    /// Nothing about your own entities changes. Media rows point AT an entity by name and key, and
    /// there is deliberately no foreign key back — that is what lets one table serve every entity
    /// in the application, and what makes adding media to a new entity cost no migration. The
    /// price is that deleting an entity does not cascade to its files: call
    /// <c>media.For(entity).DeleteAllAsync()</c> when it goes.
    /// </para>
    /// </summary>
    public static ModelBuilder ApplyMedia(this ModelBuilder modelBuilder, Action<MediaModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var options = new MediaModelOptions();
        configure?.Invoke(options);

        modelBuilder.ApplyConfiguration(new MediaFileConfiguration(options));
        return modelBuilder;
    }
}
