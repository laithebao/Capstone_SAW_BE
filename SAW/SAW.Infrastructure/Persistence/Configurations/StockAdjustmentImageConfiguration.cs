using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class StockAdjustmentImageConfiguration : IEntityTypeConfiguration<StockAdjustmentImage>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentImage> b)
    {
        b.ToTable("STOCK_ADJUSTMENT_IMAGE");
        b.HasKey(x => x.StockAdjustmentImageId);
        b.Property(x => x.StockAdjustmentImageId).ValueGeneratedNever();
        b.Property(x => x.AssetId).HasMaxLength(200);
        b.Property(x => x.ImageUrl).HasMaxLength(1000);
        b.HasIndex(x => x.AssetId).IsUnique();
        b.HasOne(x => x.StockAdjustment).WithMany(x => x.Images).HasForeignKey(x => x.StockAdjustmentId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.UploadedByAccount).WithMany().HasForeignKey(x => x.UploadedByAccountId).OnDelete(DeleteBehavior.NoAction);
    }
}
