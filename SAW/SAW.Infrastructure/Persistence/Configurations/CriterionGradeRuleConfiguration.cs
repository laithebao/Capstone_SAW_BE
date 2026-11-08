using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class CriterionGradeRuleConfiguration : IEntityTypeConfiguration<CriterionGradeRule>
{
    public void Configure(EntityTypeBuilder<CriterionGradeRule> builder)
    {
        builder.ToTable("CRITERION_GRADE_RULE");
        builder.HasKey(x => x.CriterionGradeRuleId);
        builder.Property(x => x.CriterionGradeRuleId).HasColumnName("CriterionGradeRuleID").UseIdentityColumn();
        builder.Property(x => x.InspectionCriterionId).HasColumnName("InspectionCriterionID").IsRequired();
        builder.Property(x => x.Grade).HasColumnName("Grade").HasMaxLength(5).IsRequired();
        builder.Property(x => x.MinValue).HasColumnName("MinValue").HasPrecision(18, 6);
        builder.Property(x => x.MaxValue).HasColumnName("MaxValue").HasPrecision(18, 6);
        builder.Property(x => x.RequiredTextValue).HasColumnName("RequiredTextValue").HasMaxLength(100);
        builder.Property(x => x.IsFailRule).HasColumnName("IsFailRule").HasDefaultValue(false);

        builder.HasOne(x => x.InspectionCriterion)
               .WithMany(c => c.GradeRules)
               .HasForeignKey(x => x.InspectionCriterionId)
               .HasConstraintName("FK_CRITERION_GRADE_RULE_CRITERION");
    }
}
