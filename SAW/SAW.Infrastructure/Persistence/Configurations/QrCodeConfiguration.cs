using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class QrCodeConfiguration : IEntityTypeConfiguration<QrCode>
{
    public void Configure(EntityTypeBuilder<QrCode> builder)
    {
        builder.ToTable("QR_CODE");
        builder.HasKey(x => x.QrCodeId);
        builder.Property(x => x.QrCodeId).HasColumnName("QRCodeID").UseIdentityColumn();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.PackageCode).HasColumnName("PackageCode").HasMaxLength(80);
        builder.Property(x => x.PublicToken).HasColumnName("PublicToken").HasMaxLength(200).IsRequired();
        builder.Property(x => x.TraceabilityUrl).HasColumnName("TraceabilityUrl").HasMaxLength(1000).IsRequired();
        builder.Property(x => x.QrImageUrl).HasColumnName("QRImageUrl").HasMaxLength(1000);
        builder.Property(x => x.IsActive).HasColumnName("IsActive").HasDefaultValue(true);
        builder.Property(x => x.GeneratedAt).HasColumnName("GeneratedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => x.PublicToken).IsUnique().HasDatabaseName("UQ_QR_CODE_PublicToken");
        builder.HasIndex(x => x.TraceabilityUrl).IsUnique().HasDatabaseName("UQ_QR_CODE_Url");

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.QrCodes)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_QR_CODE_BATCH");
    }
}
