using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class QualityImageConfiguration : IEntityTypeConfiguration<QualityImage>
{
    public void Configure(EntityTypeBuilder<QualityImage> builder)
    {
        builder.ToTable("QUALITY_IMAGE");
        builder.HasKey(x => x.QualityImageId);
        builder.Property(x => x.QualityImageId).HasColumnName("QualityImageID").UseIdentityColumn();
        builder.Property(x => x.QcInspectionId).HasColumnName("QCInspectionID").IsRequired();
        builder.Property(x => x.FileName).HasColumnName("FileName").HasMaxLength(255).IsRequired();
        builder.Property(x => x.FileUrl).HasColumnName("FileUrl").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.MimeType).HasColumnName("MimeType").HasMaxLength(100);
        builder.Property(x => x.UploadedByAccountId).HasColumnName("UploadedByAccountID").IsRequired();
        builder.Property(x => x.UploadedAt).HasColumnName("UploadedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasOne(x => x.QcInspection)
               .WithMany(q => q.QualityImages)
               .HasForeignKey(x => x.QcInspectionId)
               .HasConstraintName("FK_QUALITY_IMAGE_INSPECTION");

        builder.HasOne(x => x.UploadedByAccount)
               .WithMany()
               .HasForeignKey(x => x.UploadedByAccountId)
               .HasConstraintName("FK_QUALITY_IMAGE_ACCOUNT");
    }
}
