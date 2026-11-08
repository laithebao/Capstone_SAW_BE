using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class SupplierCropTypeConfiguration : IEntityTypeConfiguration<SupplierCropType>
{
    public void Configure(EntityTypeBuilder<SupplierCropType> builder)
    {
        builder.ToTable("SUPPLIER_CROP_TYPE");
        builder.HasKey(x => new { x.SupplierId, x.CropTypeId });
        builder.Property(x => x.SupplierId).HasColumnName("SupplierID");
        builder.Property(x => x.CropTypeId).HasColumnName("CropTypeID");
        builder.Property(x => x.IsActive).HasColumnName("IsActive").HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasOne(x => x.Supplier)
               .WithMany(s => s.SupplierCropTypes)
               .HasForeignKey(x => x.SupplierId)
               .HasConstraintName("FK_SUPPLIER_CROP_TYPE_SUPPLIER");

        builder.HasOne(x => x.CropType)
               .WithMany(c => c.SupplierCropTypes)
               .HasForeignKey(x => x.CropTypeId)
               .HasConstraintName("FK_SUPPLIER_CROP_TYPE_CROP");
    }
}
