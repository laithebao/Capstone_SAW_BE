using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class SupplierGrowingAreaConfiguration : IEntityTypeConfiguration<SupplierGrowingArea>
{
    public void Configure(EntityTypeBuilder<SupplierGrowingArea> builder)
    {
        // Tên bảng
        builder.ToTable("SUPPLIER_GROWING_AREA");

        // Khóa chính kép
        builder.HasKey(e => new { e.SupplierId, e.GrowingAreaId });

        // Thuộc tính
        builder.Property(e => e.AreaInHectares)
            .HasColumnType("DECIMAL(18,2)"); // Hoặc kiểu số phù hợp với DB của bạn

        builder.Property(e => e.SupplierSpecificNote)
            .HasMaxLength(1000);

        builder.Property(e => e.JoinedAt)
            .IsRequired()
            .HasColumnType("DATETIME2(0)")
            .HasDefaultValueSql("SYSDATETIME()");

        // Quan hệ Many-to-Many
        builder.HasOne(sga => sga.Supplier)
            .WithMany(s => s.SupplierGrowingAreas)
            .HasForeignKey(sga => sga.SupplierId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SUPPLIER_GROWING_AREA_SUPPLIER");

        builder.HasOne(sga => sga.GrowingArea)
            .WithMany(ga => ga.SupplierGrowingAreas)
            .HasForeignKey(sga => sga.GrowingAreaId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SUPPLIER_GROWING_AREA_AREA");
    }
}