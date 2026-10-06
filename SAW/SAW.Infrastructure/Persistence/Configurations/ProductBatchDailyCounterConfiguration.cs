using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class ProductBatchDailyCounterConfiguration : IEntityTypeConfiguration<ProductBatchDailyCounter>
{
    public void Configure(EntityTypeBuilder<ProductBatchDailyCounter> builder)
    {
        builder.ToTable("PRODUCT_BATCH_DAILY_COUNTER", table =>
            table.HasCheckConstraint("CK_PRODUCT_BATCH_DAILY_COUNTER_LastNumber", "[LastNumber] > 0"));
        builder.HasKey(counter => counter.CodeDate);
        builder.Property(counter => counter.CodeDate).HasColumnType("date");
        builder.Property(counter => counter.LastNumber).IsRequired();
    }
}
