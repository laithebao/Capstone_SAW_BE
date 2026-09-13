using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("PURCHASE_ORDER");
        builder.HasKey(x => x.PurchaseOrderId);
        builder.Property(x => x.PurchaseOrderId).HasColumnName("PurchaseOrderID").UseIdentityColumn();
        builder.Property(x => x.OrderCode).HasColumnName("OrderCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.DistributorId).HasColumnName("DistributorID").IsRequired();
        builder.Property(x => x.OrderStatus).HasColumnName("OrderStatus").HasMaxLength(30).HasDefaultValue("PENDING");
        builder.Property(x => x.DeliveryAddress).HasColumnName("DeliveryAddress").HasMaxLength(500).IsRequired();
        builder.Property(x => x.ContactPhone).HasColumnName("ContactPhone").HasMaxLength(30);
        builder.Property(x => x.ExpectedDeliveryDate).HasColumnName("ExpectedDeliveryDate").IsRequired();
        builder.Property(x => x.OrderNote).HasColumnName("OrderNote").HasMaxLength(1000);
        builder.Property(x => x.SubtotalAmount).HasColumnName("SubtotalAmount").HasPrecision(18, 2).HasDefaultValue(0m);
        builder.Property(x => x.TaxAmount).HasColumnName("TaxAmount").HasPrecision(18, 2).HasDefaultValue(0m);
        builder.Property(x => x.TotalAmount).HasColumnName("TotalAmount").HasPrecision(18, 2).HasDefaultValue(0m);
        builder.Property(x => x.ApprovedByAccountId).HasColumnName("ApprovedByAccountID");
        builder.Property(x => x.ApprovedAt).HasColumnName("ApprovedAt");
        builder.Property(x => x.RejectedAt).HasColumnName("RejectedAt");
        builder.Property(x => x.RejectionReason).HasColumnName("RejectionReason").HasMaxLength(1000);
        builder.Property(x => x.CancellationReason).HasColumnName("CancellationReason").HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");

        builder.HasIndex(x => x.OrderCode).IsUnique().HasDatabaseName("UQ_PURCHASE_ORDER_Code");

        builder.HasOne(x => x.Distributor)
               .WithMany(d => d.PurchaseOrders)
               .HasForeignKey(x => x.DistributorId)
               .HasConstraintName("FK_PURCHASE_ORDER_DISTRIBUTOR");

        builder.HasOne(x => x.ApprovedByAccount)
               .WithMany()
               .HasForeignKey(x => x.ApprovedByAccountId)
               .IsRequired(false)
               .HasConstraintName("FK_PURCHASE_ORDER_APPROVER");
    }
}
