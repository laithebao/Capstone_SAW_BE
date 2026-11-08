using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class PurchaseOrderDailyCounterConfiguration : IEntityTypeConfiguration<PurchaseOrderDailyCounter>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderDailyCounter> builder)
    {
        builder.ToTable("PURCHASE_ORDER_DAILY_COUNTER", t => t.HasCheckConstraint("CK_PURCHASE_ORDER_DAILY_COUNTER_Number", "[LastNumber] > 0"));
        builder.HasKey(x => x.CodeDate);
        builder.Property(x => x.CodeDate).HasColumnType("date");
    }
}
