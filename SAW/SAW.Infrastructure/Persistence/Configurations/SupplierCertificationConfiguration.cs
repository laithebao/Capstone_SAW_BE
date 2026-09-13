using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class SupplierCertificationConfiguration : IEntityTypeConfiguration<SupplierCertification>
{
    public void Configure(EntityTypeBuilder<SupplierCertification> builder)
    {
        builder.ToTable("SUPPLIER_CERTIFICATION");
        builder.HasKey(x => x.SupplierCertificationId);
        builder.Property(x => x.SupplierCertificationId).HasColumnName("SupplierCertificationID").UseIdentityColumn();
        builder.Property(x => x.SupplierId).HasColumnName("SupplierID").IsRequired();
        builder.Property(x => x.CertificationName).HasColumnName("CertificationName").HasMaxLength(150).IsRequired();
        builder.Property(x => x.CertificateNumber).HasColumnName("CertificateNumber").HasMaxLength(100);
        builder.Property(x => x.IssuingOrganization).HasColumnName("IssuingOrganization").HasMaxLength(200);
        builder.Property(x => x.IssueDate).HasColumnName("IssueDate");
        builder.Property(x => x.ExpiryDate).HasColumnName("ExpiryDate");
        builder.Property(x => x.EvidenceFileUrl).HasColumnName("EvidenceFileUrl").HasMaxLength(1000);
        builder.Property(x => x.IsActive).HasColumnName("IsActive").HasDefaultValue(true);

        builder.HasOne(x => x.Supplier)
               .WithMany(s => s.SupplierCertifications)
               .HasForeignKey(x => x.SupplierId)
               .HasConstraintName("FK_SUPPLIER_CERTIFICATION_SUPPLIER");
    }
}
