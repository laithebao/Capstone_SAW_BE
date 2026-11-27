using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class GrowingAreaConfiguration : IEntityTypeConfiguration<GrowingArea>
{
    public void Configure(EntityTypeBuilder<GrowingArea> builder)
    {
        // Tên bảng
        builder.ToTable("GROWING_AREA");

        // Khóa chính
        builder.HasKey(e => e.GrowingAreaId);
        builder.HasOne(e => e.OwnerSupplier).WithMany().HasForeignKey(e => e.OwnerSupplierId).OnDelete(DeleteBehavior.Restrict);

        // Thuộc tính
        builder.Property(e => e.AreaName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Region)
            .IsRequired(false)
            .HasMaxLength(100);

        builder.Property(e => e.Province)
            .IsRequired(false)
            .HasMaxLength(100);

        builder.Property(e => e.District)
            .IsRequired(false)
            .HasMaxLength(100);

        builder.Property(e => e.Ward)
            .IsRequired(false)
            .HasMaxLength(100);

        builder.Property(e => e.Description)
            .HasMaxLength(1000);
    }
}