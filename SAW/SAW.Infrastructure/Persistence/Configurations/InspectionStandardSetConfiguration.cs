using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class InspectionStandardSetConfiguration : IEntityTypeConfiguration<InspectionStandardSet>
{
    public void Configure(EntityTypeBuilder<InspectionStandardSet> builder)
    {
        builder.ToTable("INSPECTION_STANDARD_SET");
        builder.HasKey(x => x.InspectionStandardSetId);
        builder.Property(x => x.InspectionStandardSetId).HasColumnName("InspectionStandardSetID").UseIdentityColumn();
        builder.Property(x => x.CropTypeId).HasColumnName("CropTypeID").IsRequired();
        builder.Property(x => x.StandardCode).HasColumnName("StandardCode").HasMaxLength(40).IsRequired();
        builder.Property(x => x.StandardName).HasColumnName("StandardName").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasColumnName("Description").HasMaxLength(1000);
        builder.Property(x => x.IsActive).HasColumnName("IsActive").HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => x.StandardCode).IsUnique().HasDatabaseName("UQ_INSPECTION_STANDARD_SET_Code");

        builder.HasOne(x => x.CropType)
               .WithMany(c => c.InspectionStandardSets)
               .HasForeignKey(x => x.CropTypeId)
               .HasConstraintName("FK_INSPECTION_STANDARD_SET_CROP");
    }
}
