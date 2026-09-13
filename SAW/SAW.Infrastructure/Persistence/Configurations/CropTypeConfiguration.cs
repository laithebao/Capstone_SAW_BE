using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class CropTypeConfiguration : IEntityTypeConfiguration<CropType>
{
    public void Configure(EntityTypeBuilder<CropType> builder)
    {
        builder.ToTable("CROP_TYPE");
        builder.HasKey(x => x.CropTypeId);
        builder.Property(x => x.CropTypeId).HasColumnName("CropTypeID").UseIdentityColumn();
        builder.Property(x => x.CropCode).HasColumnName("CropCode").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CropName).HasColumnName("CropName").HasMaxLength(150).IsRequired();
        builder.Property(x => x.CategoryName).HasColumnName("CategoryName").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasColumnName("Description").HasMaxLength(1000);
        builder.Property(x => x.ExpectedMinTempC).HasColumnName("ExpectedMinTempC").HasPrecision(6, 2);
        builder.Property(x => x.ExpectedMaxTempC).HasColumnName("ExpectedMaxTempC").HasPrecision(6, 2);
        builder.Property(x => x.ExpectedMinHumidityPct).HasColumnName("ExpectedMinHumidityPct").HasPrecision(6, 2);
        builder.Property(x => x.ExpectedMaxHumidityPct).HasColumnName("ExpectedMaxHumidityPct").HasPrecision(6, 2);
        builder.Property(x => x.ShelfLifeDays).HasColumnName("ShelfLifeDays");
        builder.Property(x => x.SafetyStockLevelKg).HasColumnName("SafetyStockLevelKg").HasPrecision(18, 3);
        builder.Property(x => x.DefaultUnit).HasColumnName("DefaultUnit").HasMaxLength(20).HasDefaultValue("kg");
        builder.Property(x => x.IsActive).HasColumnName("IsActive").HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");

        builder.HasIndex(x => x.CropCode).IsUnique().HasDatabaseName("UQ_CROP_TYPE_Code");
        builder.HasIndex(x => x.CropName).IsUnique().HasDatabaseName("UQ_CROP_TYPE_Name");
    }
}
