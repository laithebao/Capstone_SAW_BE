using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class StandardVersionReviewConfiguration : IEntityTypeConfiguration<StandardVersionReview>
{
    public void Configure(EntityTypeBuilder<StandardVersionReview> builder)
    {
        builder.ToTable("STANDARD_VERSION_REVIEW");
        builder.HasKey(x => x.StandardVersionReviewId);
        builder.Property(x => x.StandardVersionReviewId).HasColumnName("StandardVersionReviewID").UseIdentityColumn();
        builder.Property(x => x.InspectionStandardVersionId).HasColumnName("InspectionStandardVersionID").IsRequired();
        builder.Property(x => x.ReviewedByAccountId).HasColumnName("ReviewedByAccountID").IsRequired();
        builder.Property(x => x.ReviewSequence).HasColumnName("ReviewSequence").IsRequired();
        builder.Property(x => x.ReviewDecision).HasColumnName("ReviewDecision").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Comments).HasColumnName("Comments").HasMaxLength(1000);
        builder.Property(x => x.ReviewedAt).HasColumnName("ReviewedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => new { x.InspectionStandardVersionId, x.ReviewSequence })
               .IsUnique().HasDatabaseName("UQ_STANDARD_VERSION_REVIEW_Step");
        builder.HasIndex(x => new { x.InspectionStandardVersionId, x.ReviewedByAccountId })
               .IsUnique().HasDatabaseName("UQ_STANDARD_VERSION_REVIEW_Account");

        builder.HasOne(x => x.InspectionStandardVersion)
               .WithMany(v => v.Reviews)
               .HasForeignKey(x => x.InspectionStandardVersionId)
               .HasConstraintName("FK_STANDARD_VERSION_REVIEW_VERSION");

        builder.HasOne(x => x.ReviewedByAccount)
               .WithMany()
               .HasForeignKey(x => x.ReviewedByAccountId)
               .HasConstraintName("FK_STANDARD_VERSION_REVIEW_ACCOUNT");
    }
}
