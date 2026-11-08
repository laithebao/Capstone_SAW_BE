using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        builder.ToTable("ORDER_STATUS_HISTORY");
        builder.HasKey(x => x.OrderStatusHistoryId);
        builder.Property(x => x.OrderStatusHistoryId).HasColumnName("OrderStatusHistoryID").UseIdentityColumn();
        builder.Property(x => x.PurchaseOrderId).HasColumnName("PurchaseOrderID").IsRequired();
        builder.Property(x => x.OldStatus).HasColumnName("OldStatus").HasMaxLength(30);
        builder.Property(x => x.NewStatus).HasColumnName("NewStatus").HasMaxLength(30).IsRequired();
        builder.Property(x => x.ChangedByAccountId).HasColumnName("ChangedByAccountID");
        builder.Property(x => x.ChangeReason).HasColumnName("ChangeReason").HasMaxLength(1000);
        builder.Property(x => x.ChangedAt).HasColumnName("ChangedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasOne(x => x.PurchaseOrder)
               .WithMany(o => o.StatusHistories)
               .HasForeignKey(x => x.PurchaseOrderId)
               .HasConstraintName("FK_ORDER_STATUS_HISTORY_ORDER");

        builder.HasOne(x => x.ChangedByAccount)
               .WithMany()
               .HasForeignKey(x => x.ChangedByAccountId)
               .IsRequired(false)
               .HasConstraintName("FK_ORDER_STATUS_HISTORY_ACCOUNT");
    }
}
