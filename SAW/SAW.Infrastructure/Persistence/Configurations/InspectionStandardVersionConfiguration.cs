using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class InspectionStandardVersionConfiguration : IEntityTypeConfiguration<InspectionStandardVersion>
{
    public void Configure(EntityTypeBuilder<InspectionStandardVersion> builder)
    {
        builder.ToTable("INSPECTION_STANDARD_VERSION");
        builder.HasKey(x => x.InspectionStandardVersionId);
        builder.Property(x => x.InspectionStandardVersionId).HasColumnName("InspectionStandardVersionID").UseIdentityColumn();
        builder.Property(x => x.InspectionStandardSetId).HasColumnName("InspectionStandardSetID").IsRequired();
        builder.Property(x => x.VersionNo).HasColumnName("VersionNo").IsRequired();
        builder.Property(x => x.VersionStatus).HasColumnName("VersionStatus").HasMaxLength(20).HasDefaultValue("DRAFT");
        builder.Property(x => x.EffectiveFrom).HasColumnName("EffectiveFrom");
        builder.Property(x => x.EffectiveTo).HasColumnName("EffectiveTo");
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => new { x.InspectionStandardSetId, x.VersionNo })
               .IsUnique().HasDatabaseName("UQ_INSPECTION_STANDARD_VERSION");

        builder.HasOne(x => x.InspectionStandardSet)
               .WithMany(s => s.Versions)
               .HasForeignKey(x => x.InspectionStandardSetId)
               .HasConstraintName("FK_INSPECTION_STANDARD_VERSION_SET");
    }
}
