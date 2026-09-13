using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class EnvironmentLogConfiguration : IEntityTypeConfiguration<EnvironmentLog>
{
    public void Configure(EntityTypeBuilder<EnvironmentLog> builder)
    {
        builder.ToTable("ENVIRONMENT_LOG");
        builder.HasKey(x => x.EnvironmentLogId);
        builder.Property(x => x.EnvironmentLogId).HasColumnName("EnvironmentLogID").UseIdentityColumn();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.WarehouseLocationId).HasColumnName("WarehouseLocationID").IsRequired();
        builder.Property(x => x.QcInspectionId).HasColumnName("QCInspectionID");
        builder.Property(x => x.RecordedByAccountId).HasColumnName("RecordedByAccountID");
        builder.Property(x => x.TemperatureC).HasColumnName("TemperatureC").HasPrecision(6, 2).IsRequired();
        builder.Property(x => x.HumidityPct).HasColumnName("HumidityPct").HasPrecision(6, 2);
        builder.Property(x => x.SourceType).HasColumnName("SourceType").HasMaxLength(20).HasDefaultValue("MANUAL");
        builder.Property(x => x.SensorIdentifier).HasColumnName("SensorIdentifier").HasMaxLength(100);
        builder.Property(x => x.RecordedAt).HasColumnName("RecordedAt").IsRequired();
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.EnvironmentLogs)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_ENVIRONMENT_LOG_BATCH");

        builder.HasOne(x => x.WarehouseLocation)
               .WithMany(l => l.EnvironmentLogs)
               .HasForeignKey(x => x.WarehouseLocationId)
               .HasConstraintName("FK_ENVIRONMENT_LOG_LOCATION");

        builder.HasOne(x => x.QcInspection)
               .WithMany(q => q.EnvironmentLogs)
               .HasForeignKey(x => x.QcInspectionId)
               .IsRequired(false)
               .HasConstraintName("FK_ENVIRONMENT_LOG_INSPECTION");

        builder.HasOne(x => x.RecordedByAccount)
               .WithMany()
               .HasForeignKey(x => x.RecordedByAccountId)
               .IsRequired(false)
               .HasConstraintName("FK_ENVIRONMENT_LOG_ACCOUNT");
    }
}
