using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class OrderDeliveryRefusalConfiguration : IEntityTypeConfiguration<OrderDeliveryRefusal>
{
    public void Configure(EntityTypeBuilder<OrderDeliveryRefusal> b)
    {
        b.ToTable("ORDER_DELIVERY_REFUSAL");
        b.HasKey(x => x.OrderDeliveryRefusalId);
        b.HasIndex(x => x.PurchaseOrderId);
        b.HasIndex(x => x.RequestId).IsUnique();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.CreatedByAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OrderDeliveryRefusalLotConfiguration : IEntityTypeConfiguration<OrderDeliveryRefusalLot>
{
    public void Configure(EntityTypeBuilder<OrderDeliveryRefusalLot> b)
    {
        b.ToTable("ORDER_DELIVERY_REFUSAL_LOT");
        // A purchased whole lot can be refused only once, even across separate reports.
        b.HasKey(x => x.OrderDetailId);
        b.Property(x => x.OrderDetailId).ValueGeneratedNever();
        b.HasOne(x => x.OrderDetail).WithMany().HasForeignKey(x => x.OrderDetailId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.OrderDeliveryRefusal).WithMany(x => x.Lots)
            .HasForeignKey(x => x.OrderDeliveryRefusalId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OrderDeliveryRefusalImageConfiguration : IEntityTypeConfiguration<OrderDeliveryRefusalImage>
{
    public void Configure(EntityTypeBuilder<OrderDeliveryRefusalImage> b)
    {
        b.ToTable("ORDER_DELIVERY_REFUSAL_IMAGE");
        b.HasKey(x => x.OrderDeliveryRefusalImageId);
        b.Property(x => x.OrderDeliveryRefusalImageId).ValueGeneratedNever();
        b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.StorageName).HasMaxLength(64).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.StorageName).IsUnique();
        b.HasOne(x => x.OrderDeliveryRefusal).WithMany(x => x.Images)
            .HasForeignKey(x => x.OrderDeliveryRefusalId).OnDelete(DeleteBehavior.Restrict);
    }
}
