using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class ProductBatchConfiguration : IEntityTypeConfiguration<ProductBatch>
{
    public void Configure(EntityTypeBuilder<ProductBatch> builder)
    {
        builder.ToTable("PRODUCT_BATCH", tb => tb.UseSqlOutputClause(false));
        builder.HasKey(x => x.ProductBatchId);
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").UseIdentityColumn();
        builder.Property(x => x.BatchCode).HasColumnName("BatchCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.SupplierId).HasColumnName("SupplierID").IsRequired();
        builder.Property(x => x.CropTypeId).HasColumnName("CropTypeID").IsRequired();
        builder.Property(x => x.ProductName).HasColumnName("ProductName").HasMaxLength(200).IsRequired();
        builder.Property(x => x.GrowingAreaId).HasColumnName("GrowingAreaID").IsRequired();
        builder.Property(x => x.HarvestDate).HasColumnName("HarvestDate").IsRequired();
        builder.Property(x => x.DeclaredQuantity).HasColumnName("DeclaredQuantity").HasPrecision(18, 3);
        builder.Property(x => x.Unit).HasColumnName("Unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.WeightInKg).HasColumnName("WeightInKg").HasPrecision(18, 3);
        builder.Property(x => x.VerifiedQuantity).HasColumnName("VerifiedQuantity").HasPrecision(18, 3);
        builder.Property(x => x.VerifiedWeightInKg).HasColumnName("VerifiedWeightInKg").HasPrecision(18, 3);
        builder.Property(x => x.PackagingType).HasColumnName("PackagingType").HasMaxLength(100);
        builder.Property(x => x.PackageCount).HasColumnName("PackageCount");
        builder.Property(x => x.PackageUnitWeightKg).HasColumnName("PackageUnitWeightKg").HasPrecision(18, 3);
        builder.Property(x => x.ExpectedMinTempC).HasColumnName("ExpectedMinTempC").HasPrecision(6, 2);
        builder.Property(x => x.ExpectedMaxTempC).HasColumnName("ExpectedMaxTempC").HasPrecision(6, 2);
        builder.Property(x => x.ExpectedMinHumidityPct).HasColumnName("ExpectedMinHumidityPct").HasPrecision(6, 2);
        builder.Property(x => x.ExpectedMaxHumidityPct).HasColumnName("ExpectedMaxHumidityPct").HasPrecision(6, 2);
        builder.Property(x => x.ShelfLifeDaysSnapshot).HasColumnName("ShelfLifeDaysSnapshot");
        builder.Property(x => x.ExpectedDeliveryDate).HasColumnName("ExpectedDeliveryDate");
        builder.Property(x => x.ExpiryDate).HasColumnName("ExpiryDate");
        builder.Property(x => x.BatchStatus).HasColumnName("BatchStatus").HasMaxLength(40).HasDefaultValue("PENDING_PREDECLARATION");
        builder.Property(x => x.QualityGrade).HasColumnName("QualityGrade").HasMaxLength(5);
        builder.Property(x => x.RejectionReason).HasColumnName("RejectionReason").HasMaxLength(1000);
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");

        builder.HasIndex(x => x.BatchCode).IsUnique().HasDatabaseName("UQ_PRODUCT_BATCH_Code");

        builder.HasOne(x => x.Supplier)
               .WithMany(s => s.ProductBatches)
               .HasForeignKey(x => x.SupplierId)
               .HasConstraintName("FK_PRODUCT_BATCH_SUPPLIER");

        builder.HasOne(x => x.CropType)
               .WithMany(c => c.ProductBatches)
               .HasForeignKey(x => x.CropTypeId)
               .HasConstraintName("FK_PRODUCT_BATCH_CROP");

        builder.HasOne(x => x.GrowingArea)
               .WithMany(g => g.ProductBatches)
               .HasForeignKey(x => x.GrowingAreaId)
               .HasConstraintName("FK_PRODUCT_BATCH_GROWING_AREA");
    }
}
