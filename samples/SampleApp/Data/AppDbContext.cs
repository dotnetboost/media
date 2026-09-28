using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SampleApp.Domain;

namespace SampleApp.Data;

/// <summary>
/// The application's context, with the media table mapped into it. Sharing a context is what lets
/// an upload and the entity it belongs to commit in one transaction.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IMediaDbContext
{
    public DbSet<Product> Products => Set<Product>();

    /// <summary>Required by <see cref="IMediaDbContext"/>. Application code goes through IMediaManager.</summary>
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    public DbSet<MediaAttachment> MediaAttachments => Set<MediaAttachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Product>(product =>
        {
            product.ToTable("Products");
            product.HasKey(p => p.Id);
            product.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            product.Property(p => p.Name).HasMaxLength(256).IsRequired();
            product.Property(p => p.Price).HasPrecision(18, 2);
            product.HasIndex(p => p.Sku).IsUnique();
        });

        // At the end, after your own configuration — the same place DotNetBoost.MultiLingual's
        // ApplyMultiLingual() goes.
        modelBuilder.ApplyMedia();
    }
}
