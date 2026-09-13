using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class BatchStatusHistoryConfiguration : IEntityTypeConfiguration<BatchStatusHistory>
{
    public void Configure(EntityTypeBuilder<BatchStatusHistory> builder)
    {
        builder.ToTable("BATCH_STATUS_HISTORY");
        builder.HasKey(x => x.BatchStatusHistoryId);
        builder.Property(x => x.BatchStatusHistoryId).HasColumnName("BatchStatusHistoryID").UseIdentityColumn();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.OldStatus).HasColumnName("OldStatus").HasMaxLength(40);
        builder.Property(x => x.NewStatus).HasColumnName("NewStatus").HasMaxLength(40).IsRequired();
        builder.Property(x => x.ChangedByAccountId).HasColumnName("ChangedByAccountID");
        builder.Property(x => x.ChangeReason).HasColumnName("ChangeReason").HasMaxLength(1000);
        builder.Property(x => x.ChangedAt).HasColumnName("ChangedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.StatusHistories)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_BATCH_STATUS_HISTORY_BATCH");

        builder.HasOne(x => x.ChangedByAccount)
               .WithMany()
               .HasForeignKey(x => x.ChangedByAccountId)
               .IsRequired(false)
               .HasConstraintName("FK_BATCH_STATUS_HISTORY_ACCOUNT");
    }
}
