using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> builder)
    {
        builder.ToTable("GOODS_RECEIPT");
        builder.HasKey(x => x.GoodsReceiptId);
        builder.Property(x => x.GoodsReceiptId).HasColumnName("GoodsReceiptID").UseIdentityColumn();
        builder.Property(x => x.ReceiptCode).HasColumnName("ReceiptCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.WarehouseLocationId).HasColumnName("WarehouseLocationID").IsRequired();
        builder.Property(x => x.OperationAccountId).HasColumnName("OperationAccountID").IsRequired();
        builder.Property(x => x.ReceivedQuantity).HasColumnName("ReceivedQuantity").HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.Unit).HasColumnName("Unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.WeightInKg).HasColumnName("WeightInKg").HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.ReceiptStatus).HasColumnName("ReceiptStatus").HasMaxLength(20).HasDefaultValue("DRAFT");
        builder.Property(x => x.ReceivedAt).HasColumnName("ReceivedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.CommittedAt).HasColumnName("CommittedAt");
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasIndex(x => x.ReceiptCode).IsUnique().HasDatabaseName("UQ_GOODS_RECEIPT_Code");

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.GoodsReceipts)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_GOODS_RECEIPT_BATCH");

        builder.HasOne(x => x.WarehouseLocation)
               .WithMany(l => l.GoodsReceipts)
               .HasForeignKey(x => x.WarehouseLocationId)
               .HasConstraintName("FK_GOODS_RECEIPT_LOCATION");

        builder.HasOne(x => x.OperationAccount)
               .WithMany()
               .HasForeignKey(x => x.OperationAccountId)
               .HasConstraintName("FK_GOODS_RECEIPT_ACCOUNT");
    }
}
