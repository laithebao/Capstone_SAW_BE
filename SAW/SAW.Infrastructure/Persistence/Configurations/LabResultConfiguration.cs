using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class LabResultConfiguration : IEntityTypeConfiguration<LabResult>
{
    public void Configure(EntityTypeBuilder<LabResult> builder)
    {
        builder.ToTable("LAB_RESULT");
        builder.HasKey(x => x.LabResultId);
        builder.Property(x => x.LabResultId).HasColumnName("LabResultID").UseIdentityColumn();
        builder.Property(x => x.QcInspectionId).HasColumnName("QCInspectionID").IsRequired();
        builder.Property(x => x.ChemicalResidueStatus).HasColumnName("ChemicalResidueStatus").HasMaxLength(20);
        builder.Property(x => x.ResidueValue).HasColumnName("ResidueValue").HasPrecision(18, 6);
        builder.Property(x => x.ResidueUnit).HasColumnName("ResidueUnit").HasMaxLength(30);
        builder.Property(x => x.PathogenStatus).HasColumnName("PathogenStatus").HasMaxLength(20);
        builder.Property(x => x.PathogenName).HasColumnName("PathogenName").HasMaxLength(150);
        builder.Property(x => x.LabName).HasColumnName("LabName").HasMaxLength(200);
        builder.Property(x => x.TestedAt).HasColumnName("TestedAt");
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasIndex(x => x.QcInspectionId).IsUnique().HasDatabaseName("UQ_LAB_RESULT_Inspection");

        builder.HasOne(x => x.QcInspection)
               .WithOne(q => q.LabResult)
               .HasForeignKey<LabResult>(x => x.QcInspectionId)
               .HasConstraintName("FK_LAB_RESULT_INSPECTION");
    }
}
