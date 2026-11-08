using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class InventoryConfiguration : IEntityTypeConfiguration<Inventory>
{
    public void Configure(EntityTypeBuilder<Inventory> builder)
    {
        builder.ToTable("INVENTORY");
        builder.HasKey(x => x.InventoryId);
        builder.Property(x => x.InventoryId).HasColumnName("InventoryID").UseIdentityColumn();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.WarehouseLocationId).HasColumnName("WarehouseLocationID").IsRequired();
        builder.Property(x => x.QuantityOnHand).HasColumnName("QuantityOnHand").HasPrecision(18, 3).HasDefaultValue(0m);
        builder.Property(x => x.ReservedQuantity).HasColumnName("ReservedQuantity").HasPrecision(18, 3).HasDefaultValue(0m);
        builder.Property(x => x.Unit).HasColumnName("Unit").HasMaxLength(20).HasDefaultValue("kg");
        // AvailableQuantity is a PERSISTED computed column — map as read-only
        builder.Property(x => x.AvailableQuantity)
               .HasColumnName("AvailableQuantity")
               .HasPrecision(18, 3)
               .ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.LastUpdatedAt).HasColumnName("LastUpdatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => new { x.ProductBatchId, x.WarehouseLocationId })
               .IsUnique().HasDatabaseName("UQ_INVENTORY_BatchLocation");

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.Inventories)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_INVENTORY_BATCH");

        builder.HasOne(x => x.WarehouseLocation)
               .WithMany(l => l.Inventories)
               .HasForeignKey(x => x.WarehouseLocationId)
               .HasConstraintName("FK_INVENTORY_LOCATION");
    }
}
