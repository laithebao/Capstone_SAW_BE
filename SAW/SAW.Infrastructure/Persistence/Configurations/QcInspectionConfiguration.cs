using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class QcInspectionConfiguration : IEntityTypeConfiguration<QcInspection>
{
    public void Configure(EntityTypeBuilder<QcInspection> builder)
    {
        builder.ToTable("QC_INSPECTION");
        builder.HasKey(x => x.QcInspectionId);
        builder.Property(x => x.QcInspectionId).HasColumnName("QCInspectionID").UseIdentityColumn();
        builder.Property(x => x.InspectionCode).HasColumnName("InspectionCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.InspectionStandardVersionId).HasColumnName("InspectionStandardVersionID").IsRequired();
        builder.Property(x => x.QcAccountId).HasColumnName("QCAccountID").IsRequired();
        builder.Property(x => x.SamplingRatio).HasColumnName("SamplingRatio").HasPrecision(8, 5);
        builder.Property(x => x.SampleSize).HasColumnName("SampleSize").HasPrecision(18, 3);
        builder.Property(x => x.InspectionStatus).HasColumnName("InspectionStatus").HasMaxLength(20).HasDefaultValue("DRAFT");
        builder.Property(x => x.QcResult).HasColumnName("QCResult").HasMaxLength(20);
        builder.Property(x => x.QualityGrade).HasColumnName("QualityGrade").HasMaxLength(5);
        builder.Property(x => x.StartedAt).HasColumnName("StartedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.CompletedAt).HasColumnName("CompletedAt");
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasIndex(x => x.InspectionCode).IsUnique().HasDatabaseName("UQ_QC_INSPECTION_Code");

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.QcInspections)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_QC_INSPECTION_BATCH");

        builder.HasOne(x => x.InspectionStandardVersion)
               .WithMany(v => v.QcInspections)
               .HasForeignKey(x => x.InspectionStandardVersionId)
               .HasConstraintName("FK_QC_INSPECTION_STANDARD_VERSION");

        builder.HasOne(x => x.QcAccount)
               .WithMany()
               .HasForeignKey(x => x.QcAccountId)
               .HasConstraintName("FK_QC_INSPECTION_ACCOUNT");
    }
}
