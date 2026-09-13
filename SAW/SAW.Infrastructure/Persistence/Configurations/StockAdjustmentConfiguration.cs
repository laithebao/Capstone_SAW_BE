using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        builder.ToTable("STOCK_ADJUSTMENT");
        builder.HasKey(x => x.StockAdjustmentId);
        builder.Property(x => x.StockAdjustmentId).HasColumnName("StockAdjustmentID").UseIdentityColumn();
        builder.Property(x => x.AdjustmentCode).HasColumnName("AdjustmentCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.InventoryId).HasColumnName("InventoryID").IsRequired();
        builder.Property(x => x.CreatedByAccountId).HasColumnName("CreatedByAccountID").IsRequired();
        builder.Property(x => x.ReviewedByAccountId).HasColumnName("ReviewedByAccountID");
        builder.Property(x => x.SystemQuantity).HasColumnName("SystemQuantity").HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.ActualQuantity).HasColumnName("ActualQuantity").HasPrecision(18, 3).IsRequired();
        // Computed columns
        builder.Property(x => x.DifferenceQuantity).HasColumnName("DifferenceQuantity").HasPrecision(18, 3).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.VariancePercent).HasColumnName("VariancePercent").HasPrecision(9, 2).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.AdjustmentType).HasColumnName("AdjustmentType").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Reason).HasColumnName("Reason").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.AdjustmentStatus).HasColumnName("AdjustmentStatus").HasMaxLength(20).HasDefaultValue("PENDING");
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.ReviewedAt).HasColumnName("ReviewedAt");
        builder.Property(x => x.ReviewNote).HasColumnName("ReviewNote").HasMaxLength(1000);

        builder.HasIndex(x => x.AdjustmentCode).IsUnique().HasDatabaseName("UQ_STOCK_ADJUSTMENT_Code");

        builder.HasOne(x => x.Inventory)
               .WithMany(i => i.StockAdjustments)
               .HasForeignKey(x => x.InventoryId)
               .HasConstraintName("FK_STOCK_ADJUSTMENT_INVENTORY");

        builder.HasOne(x => x.CreatedByAccount)
               .WithMany()
               .HasForeignKey(x => x.CreatedByAccountId)
               .HasConstraintName("FK_STOCK_ADJUSTMENT_CREATOR");

        builder.HasOne(x => x.ReviewedByAccount)
               .WithMany()
               .HasForeignKey(x => x.ReviewedByAccountId)
               .IsRequired(false)
               .HasConstraintName("FK_STOCK_ADJUSTMENT_REVIEWER");
    }
}
