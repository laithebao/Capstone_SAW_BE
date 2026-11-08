using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class StockAdjustmentLineConfiguration : IEntityTypeConfiguration<StockAdjustmentLine>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentLine> b)
    {
        b.ToTable("STOCK_ADJUSTMENT_LINE", t => t.HasCheckConstraint("CK_STOCK_ADJUSTMENT_LINE_Quantities", "[SystemQuantity]>=0 AND [ActualQuantity]>=0 AND [ReservedQuantity]>=0"));
        b.HasKey(x => x.StockAdjustmentLineId);
        b.Property(x => x.StockAdjustmentLineId).HasColumnName("StockAdjustmentLineID");
        b.Property(x => x.StockAdjustmentId).HasColumnName("StockAdjustmentID");
        b.Property(x => x.InventoryId).HasColumnName("InventoryID");
        b.Property(x => x.SystemQuantity).HasPrecision(18, 3);
        b.Property(x => x.ActualQuantity).HasPrecision(18, 3);
        b.Property(x => x.ReservedQuantity).HasPrecision(18, 3);
        b.Property(x => x.Unit).HasMaxLength(20);
        b.Property(x => x.LocationCode).HasMaxLength(50);
        b.HasIndex(x => new { x.StockAdjustmentId, x.InventoryId }).IsUnique();
        b.HasOne(x => x.StockAdjustment).WithMany(x => x.Lines).HasForeignKey(x => x.StockAdjustmentId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Inventory).WithMany().HasForeignKey(x => x.InventoryId).OnDelete(DeleteBehavior.NoAction);
    }
}
