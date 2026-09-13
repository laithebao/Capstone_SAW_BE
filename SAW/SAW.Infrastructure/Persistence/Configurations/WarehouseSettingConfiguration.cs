using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class WarehouseSettingConfiguration : IEntityTypeConfiguration<WarehouseSetting>
{
    public void Configure(EntityTypeBuilder<WarehouseSetting> builder)
    {
        builder.ToTable("WAREHOUSE_SETTING");
        builder.HasKey(x => x.WarehouseSettingId);
        builder.Property(x => x.WarehouseSettingId).HasColumnName("WarehouseSettingID").ValueGeneratedNever();
        builder.Property(x => x.WarehouseName).HasColumnName("WarehouseName").HasMaxLength(200).IsRequired();
        builder.Property(x => x.MaxCapacityKg).HasColumnName("MaxCapacityKg").HasPrecision(18, 3);
        builder.Property(x => x.Address).HasColumnName("Address").HasMaxLength(500);
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").HasDefaultValueSql("SYSDATETIME()");
    }
}
