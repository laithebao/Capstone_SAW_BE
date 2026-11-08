using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class OrderDetailConfiguration : IEntityTypeConfiguration<OrderDetail>
{
    public void Configure(EntityTypeBuilder<OrderDetail> builder)
    {
        builder.ToTable("ORDER_DETAIL");
        builder.HasKey(x => x.OrderDetailId);
        builder.Property(x => x.OrderDetailId).HasColumnName("OrderDetailID").UseIdentityColumn();
        builder.Property(x => x.PurchaseOrderId).HasColumnName("PurchaseOrderID").IsRequired();
        builder.Property(x => x.CropTypeId).HasColumnName("CropTypeID").IsRequired();
        builder.Property(x => x.RequestedProductBatchId).HasColumnName("RequestedProductBatchID");
        builder.Property(x => x.RequestedQuantity).HasColumnName("RequestedQuantity").HasPrecision(18, 3);
        builder.Property(x => x.ApprovedQuantity).HasColumnName("ApprovedQuantity").HasPrecision(18, 3).HasDefaultValue(0m);
        builder.Property(x => x.Unit).HasColumnName("Unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.RequestedWeightKg).HasColumnName("RequestedWeightKg").HasPrecision(18, 3);
        builder.Property(x => x.ApprovedWeightKg).HasColumnName("ApprovedWeightKg").HasPrecision(18, 3).HasDefaultValue(0m);
        builder.Property(x => x.ReservedWeightKg).HasColumnName("ReservedWeightKg").HasPrecision(18, 3).HasDefaultValue(0m);
        builder.Property(x => x.PickedWeightKg).HasColumnName("PickedWeightKg").HasPrecision(18, 3).HasDefaultValue(0m);
        builder.Property(x => x.UnitPrice).HasColumnName("UnitPrice").HasPrecision(18, 2).HasDefaultValue(0m);
        builder.Property(x => x.TaxAmount).HasColumnName("TaxAmount").HasPrecision(18, 2).HasDefaultValue(0m);
        // Computed columns
        builder.Property(x => x.LineSubtotal).HasColumnName("LineSubtotal").HasPrecision(18, 2).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.LineTotal).HasColumnName("LineTotal").HasPrecision(18, 2).ValueGeneratedOnAddOrUpdate();

        builder.HasOne(x => x.PurchaseOrder)
               .WithMany(o => o.OrderDetails)
               .HasForeignKey(x => x.PurchaseOrderId)
               .HasConstraintName("FK_ORDER_DETAIL_ORDER");

        builder.HasOne(x => x.CropType)
               .WithMany(c => c.OrderDetails)
               .HasForeignKey(x => x.CropTypeId)
               .HasConstraintName("FK_ORDER_DETAIL_CROP");

        builder.HasOne(x => x.RequestedProductBatch)
               .WithMany(b => b.OrderDetails)
               .HasForeignKey(x => x.RequestedProductBatchId)
               .IsRequired(false)
               .HasConstraintName("FK_ORDER_DETAIL_REQUESTED_BATCH");
    }
}
