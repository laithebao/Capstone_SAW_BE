using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class SensoryResultConfiguration : IEntityTypeConfiguration<SensoryResult>
{
    public void Configure(EntityTypeBuilder<SensoryResult> builder)
    {
        builder.ToTable("SENSORY_RESULT");
        builder.HasKey(x => x.SensoryResultId);
        builder.Property(x => x.SensoryResultId).HasColumnName("SensoryResultID").UseIdentityColumn();
        builder.Property(x => x.QcInspectionId).HasColumnName("QCInspectionID").IsRequired();
        builder.Property(x => x.FreshnessScore).HasColumnName("FreshnessScore").HasPrecision(8, 2);
        builder.Property(x => x.SizeScore).HasColumnName("SizeScore").HasPrecision(8, 2);
        builder.Property(x => x.ColorScore).HasColumnName("ColorScore").HasPrecision(8, 2);
        builder.Property(x => x.RipenessScore).HasColumnName("RipenessScore").HasPrecision(8, 2);
        builder.Property(x => x.DamagePercentage).HasColumnName("DamagePercentage").HasPrecision(6, 2);
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasIndex(x => x.QcInspectionId).IsUnique().HasDatabaseName("UQ_SENSORY_RESULT_Inspection");

        builder.HasOne(x => x.QcInspection)
               .WithOne(q => q.SensoryResult)
               .HasForeignKey<SensoryResult>(x => x.QcInspectionId)
               .HasConstraintName("FK_SENSORY_RESULT_INSPECTION");
    }
}
