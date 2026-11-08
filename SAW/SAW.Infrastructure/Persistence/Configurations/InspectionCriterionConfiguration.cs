using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class InspectionCriterionConfiguration : IEntityTypeConfiguration<InspectionCriterion>
{
    public void Configure(EntityTypeBuilder<InspectionCriterion> builder)
    {
        builder.ToTable("INSPECTION_CRITERION");
        builder.HasKey(x => x.InspectionCriterionId);
        builder.Property(x => x.InspectionCriterionId).HasColumnName("InspectionCriterionID").UseIdentityColumn();
        builder.Property(x => x.InspectionStandardVersionId).HasColumnName("InspectionStandardVersionID").IsRequired();
        builder.Property(x => x.CriterionCode).HasColumnName("CriterionCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.CriterionName).HasColumnName("CriterionName").HasMaxLength(150).IsRequired();
        builder.Property(x => x.CriterionGroup).HasColumnName("CriterionGroup").HasMaxLength(30).IsRequired();
        builder.Property(x => x.DataType).HasColumnName("DataType").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Unit).HasColumnName("Unit").HasMaxLength(50);
        builder.Property(x => x.IsRequired).HasColumnName("IsRequired").HasDefaultValue(true);
        builder.Property(x => x.IsCritical).HasColumnName("IsCritical").HasDefaultValue(false);

        builder.HasIndex(x => new { x.InspectionStandardVersionId, x.CriterionCode })
               .IsUnique().HasDatabaseName("UQ_INSPECTION_CRITERION_Code");

        builder.HasOne(x => x.InspectionStandardVersion)
               .WithMany(v => v.Criteria)
               .HasForeignKey(x => x.InspectionStandardVersionId)
               .HasConstraintName("FK_INSPECTION_CRITERION_VERSION");
    }
}
