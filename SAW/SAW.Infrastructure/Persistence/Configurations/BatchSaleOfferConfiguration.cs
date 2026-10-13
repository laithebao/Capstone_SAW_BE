using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class BatchSaleOfferConfiguration : IEntityTypeConfiguration<BatchSaleOffer>
{
    public void Configure(EntityTypeBuilder<BatchSaleOffer> builder)
    {
        builder.ToTable("BATCH_SALE_OFFER", t => t.HasCheckConstraint("CK_BATCH_SALE_OFFER_Price", "[WholeLotPrice] > 0"));
        builder.HasKey(x => x.ProductBatchId);
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").ValueGeneratedNever();
        builder.Property(x => x.WholeLotPrice).HasPrecision(18, 2);
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasOne(x => x.ProductBatch).WithOne().HasForeignKey<BatchSaleOffer>(x => x.ProductBatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
