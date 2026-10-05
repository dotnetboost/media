using DotNetBoost.Media.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SampleApp.Domain;

namespace SampleApp.Data;

/// <summary>
/// The application's context, with the media tables mapped into it. Sharing a context is what lets
/// an upload and the entity it belongs to commit in one transaction.
/// <para>
/// Nothing but the <c>ApplyMedia()</c> call below is needed for that. There is no interface to
/// implement and no <c>DbSet</c> to declare: the store reaches both tables through
/// <c>Set&lt;T&gt;()</c>, and application code goes through <c>IMediaManager</c> rather than the
/// tables.
/// </para>
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

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
