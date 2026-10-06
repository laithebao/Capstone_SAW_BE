using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class SupplierFileConfiguration : IEntityTypeConfiguration<SupplierFile>
{
    public void Configure(EntityTypeBuilder<SupplierFile> b)
    {
        b.ToTable("SUPPLIER_FILE");
        b.HasKey(f => f.SupplierFileId);
        b.Property(f => f.Purpose).HasMaxLength(20);
        b.Property(f => f.FileName).HasMaxLength(255);
        b.Property(f => f.ContentType).HasMaxLength(100);
        b.Property(f => f.StorageName).HasMaxLength(100);
        b.HasIndex(f => new { f.SupplierId, f.ProductBatchId });
        b.HasOne<Account>().WithMany().HasForeignKey(f => f.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Supplier>().WithMany().HasForeignKey(f => f.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductBatch>().WithMany().HasForeignKey(f => f.ProductBatchId).OnDelete(DeleteBehavior.Restrict);
    }
}
