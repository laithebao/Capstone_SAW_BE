using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class InspectionResultDetailConfiguration : IEntityTypeConfiguration<InspectionResultDetail>
{
    public void Configure(EntityTypeBuilder<InspectionResultDetail> builder)
    {
        builder.ToTable("INSPECTION_RESULT_DETAIL", tb =>
        {
            // Khai báo triggers để EF Core dùng OUTPUT INTO (tránh lỗi với SQL Server triggers)
            // Ref: https://aka.ms/efcore-docs-sqlserver-save-changes-and-output-clause
            tb.HasTrigger("TR_INSPECTION_RESULT_DETAIL_VERSION_CHECK");
            tb.HasTrigger("TR_INSPECTION_RESULT_DETAIL_IMMUTABLE");
        });
        builder.HasKey(x => x.InspectionResultDetailId);
        builder.Property(x => x.InspectionResultDetailId).HasColumnName("InspectionResultDetailID").UseIdentityColumn();
        builder.Property(x => x.QcInspectionId).HasColumnName("QCInspectionID").IsRequired();
        builder.Property(x => x.InspectionCriterionId).HasColumnName("InspectionCriterionID").IsRequired();
        builder.Property(x => x.NumericValue).HasColumnName("NumericValue").HasPrecision(18, 6);
        builder.Property(x => x.TextValue).HasColumnName("TextValue").HasMaxLength(500);
        builder.Property(x => x.BooleanValue).HasColumnName("BooleanValue");
        builder.Property(x => x.EvaluatedGrade).HasColumnName("EvaluatedGrade").HasMaxLength(5);
        builder.Property(x => x.IsPassed).HasColumnName("IsPassed").HasDefaultValue(true);
        builder.Property(x => x.Remarks).HasColumnName("Remarks").HasMaxLength(500);

        builder.HasIndex(x => new { x.QcInspectionId, x.InspectionCriterionId })
               .IsUnique().HasDatabaseName("UQ_INSPECTION_RESULT_DETAIL_Criterion");

        builder.HasOne(x => x.QcInspection)
               .WithMany(q => q.ResultDetails)
               .HasForeignKey(x => x.QcInspectionId)
               .HasConstraintName("FK_INSPECTION_RESULT_DETAIL_INSPECTION");

        builder.HasOne(x => x.InspectionCriterion)
               .WithMany(c => c.ResultDetails)
               .HasForeignKey(x => x.InspectionCriterionId)
               .HasConstraintName("FK_INSPECTION_RESULT_DETAIL_CRITERION");
    }
}
