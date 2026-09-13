using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> builder)
    {
        builder.ToTable("STOCK_TRANSFER");
        builder.HasKey(x => x.StockTransferId);
        builder.Property(x => x.StockTransferId).HasColumnName("StockTransferID").UseIdentityColumn();
        builder.Property(x => x.TransferCode).HasColumnName("TransferCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.FromWarehouseLocationId).HasColumnName("FromWarehouseLocationID").IsRequired();
        builder.Property(x => x.ToWarehouseLocationId).HasColumnName("ToWarehouseLocationID").IsRequired();
        builder.Property(x => x.Quantity).HasColumnName("Quantity").HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.Unit).HasColumnName("Unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceTrackingId).HasColumnName("SourceTrackingID").HasMaxLength(100).IsRequired();
        builder.Property(x => x.DestinationTrackingId).HasColumnName("DestinationTrackingID").HasMaxLength(100).IsRequired();
        builder.Property(x => x.OperationAccountId).HasColumnName("OperationAccountID").IsRequired();
        builder.Property(x => x.TransferStatus).HasColumnName("TransferStatus").HasMaxLength(20).HasDefaultValue("COMPLETED");
        builder.Property(x => x.TransferredAt).HasColumnName("TransferredAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasIndex(x => x.TransferCode).IsUnique().HasDatabaseName("UQ_STOCK_TRANSFER_Code");
        builder.HasIndex(x => x.SourceTrackingId).IsUnique().HasDatabaseName("UQ_STOCK_TRANSFER_SourceTracking");
        builder.HasIndex(x => x.DestinationTrackingId).IsUnique().HasDatabaseName("UQ_STOCK_TRANSFER_DestinationTracking");

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.StockTransfers)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_STOCK_TRANSFER_BATCH");

        builder.HasOne(x => x.FromWarehouseLocation)
               .WithMany(l => l.StockTransfersFrom)
               .HasForeignKey(x => x.FromWarehouseLocationId)
               .HasConstraintName("FK_STOCK_TRANSFER_FROM_LOCATION");

        builder.HasOne(x => x.ToWarehouseLocation)
               .WithMany(l => l.StockTransfersTo)
               .HasForeignKey(x => x.ToWarehouseLocationId)
               .HasConstraintName("FK_STOCK_TRANSFER_TO_LOCATION");

        builder.HasOne(x => x.OperationAccount)
               .WithMany()
               .HasForeignKey(x => x.OperationAccountId)
               .HasConstraintName("FK_STOCK_TRANSFER_ACCOUNT");
    }
}
