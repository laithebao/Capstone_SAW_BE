using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class GoodsIssueDetailConfiguration : IEntityTypeConfiguration<GoodsIssueDetail>
{
    public void Configure(EntityTypeBuilder<GoodsIssueDetail> builder)
    {
        builder.ToTable("GOODS_ISSUE_DETAIL");
        builder.HasKey(x => x.GoodsIssueDetailId);
        builder.Property(x => x.GoodsIssueDetailId).HasColumnName("GoodsIssueDetailID").UseIdentityColumn();
        builder.Property(x => x.GoodsIssueId).HasColumnName("GoodsIssueID").IsRequired();
        builder.Property(x => x.OrderDetailId).HasColumnName("OrderDetailID").IsRequired();
        builder.Property(x => x.InventoryReservationId).HasColumnName("InventoryReservationID").IsRequired();
        builder.Property(x => x.InventoryId).HasColumnName("InventoryID").IsRequired();
        builder.Property(x => x.IssuedQuantity).HasColumnName("IssuedQuantity").HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.Unit).HasColumnName("Unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.WeightInKg).HasColumnName("WeightInKg").HasPrecision(18, 3).IsRequired();

        builder.HasOne(x => x.GoodsIssue)
               .WithMany(g => g.GoodsIssueDetails)
               .HasForeignKey(x => x.GoodsIssueId)
               .HasConstraintName("FK_GOODS_ISSUE_DETAIL_ISSUE");

        builder.HasOne(x => x.OrderDetail)
               .WithMany(od => od.GoodsIssueDetails)
               .HasForeignKey(x => x.OrderDetailId)
               .HasConstraintName("FK_GOODS_ISSUE_DETAIL_ORDER_DETAIL");

        builder.HasOne(x => x.InventoryReservation)
               .WithMany(r => r.GoodsIssueDetails)
               .HasForeignKey(x => x.InventoryReservationId)
               .HasConstraintName("FK_GOODS_ISSUE_DETAIL_RESERVATION");

        builder.HasOne(x => x.Inventory)
               .WithMany(i => i.GoodsIssueDetails)
               .HasForeignKey(x => x.InventoryId)
               .HasConstraintName("FK_GOODS_ISSUE_DETAIL_INVENTORY");
    }
}
