using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class OrderReturnConfiguration : IEntityTypeConfiguration<OrderReturn>
{
    public void Configure(EntityTypeBuilder<OrderReturn> b)
    {
        b.ToTable("ORDER_RETURN", t => t.HasCheckConstraint("CK_ORDER_RETURN_Status", "[Status] IN ('REQUESTED','APPROVED','REJECTED','RECEIVED')"));
        b.HasKey(x => x.OrderReturnId);
        b.HasIndex(x => x.PurchaseOrderId).IsUnique();
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ReviewNote).HasMaxLength(1000);
        b.Property(x => x.ReceiptNote).HasMaxLength(1000);
        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.RequestedByAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.ReviewedByAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.ReceivedByAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
