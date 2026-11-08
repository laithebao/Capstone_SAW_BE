using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class WarehouseLocationConfiguration : IEntityTypeConfiguration<WarehouseLocation>
{
    public void Configure(EntityTypeBuilder<WarehouseLocation> builder)
    {
        builder.ToTable("WAREHOUSE_LOCATION");
        builder.HasKey(x => x.WarehouseLocationId);
        builder.Property(x => x.WarehouseLocationId).HasColumnName("WarehouseLocationID").UseIdentityColumn();
        builder.Property(x => x.LocationCode).HasColumnName("LocationCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ZoneName).HasColumnName("ZoneName").HasMaxLength(100).IsRequired();
        builder.Property(x => x.RackName).HasColumnName("RackName").HasMaxLength(100);
        builder.Property(x => x.BinName).HasColumnName("BinName").HasMaxLength(100);
        builder.Property(x => x.MaxWeightKg).HasColumnName("MaxWeightKg").HasPrecision(18, 3);
        builder.Property(x => x.MaxVolumeM3).HasColumnName("MaxVolumeM3").HasPrecision(18, 6);
        builder.Property(x => x.MinTempC).HasColumnName("MinTempC").HasPrecision(6, 2);
        builder.Property(x => x.MaxTempC).HasColumnName("MaxTempC").HasPrecision(6, 2);
        builder.Property(x => x.MinHumidityPct).HasColumnName("MinHumidityPct").HasPrecision(6, 2);
        builder.Property(x => x.MaxHumidityPct).HasColumnName("MaxHumidityPct").HasPrecision(6, 2);
        builder.Property(x => x.LocationStatus).HasColumnName("LocationStatus").HasMaxLength(30).HasDefaultValue("ACTIVE");
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => x.LocationCode).IsUnique().HasDatabaseName("UQ_WAREHOUSE_LOCATION_Code");
    }
}
