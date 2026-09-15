using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("SUPPLIER", table => table.UseSqlOutputClause(false));
        builder.HasKey(x => x.SupplierId);
        builder.Property(x => x.SupplierId).HasColumnName("SupplierID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID").IsRequired();
        builder.Property(x => x.SupplierCode).HasColumnName("SupplierCode").HasMaxLength(30).IsRequired();
        builder.Property(x => x.SupplierName).HasColumnName("SupplierName").HasMaxLength(200).IsRequired();
        builder.Property(x => x.TaxCode).HasColumnName("TaxCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ContactPerson).HasColumnName("ContactPerson").HasMaxLength(150).IsRequired();
        builder.Property(x => x.PhoneNumber).HasColumnName("PhoneNumber").HasMaxLength(30);
        builder.Property(x => x.Email).HasColumnName("Email").HasMaxLength(255);
        builder.Property(x => x.Address).HasColumnName("Address").HasMaxLength(500).IsRequired();
        builder.Property(x => x.GrowingArea).HasColumnName("GrowingArea").HasMaxLength(300);
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);
        builder.Property(x => x.ProfileStatus).HasColumnName("ProfileStatus").HasMaxLength(20).HasDefaultValue("ACTIVE");
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");

        builder.HasIndex(x => x.AccountId).IsUnique().HasDatabaseName("UQ_SUPPLIER_Account");
        builder.HasIndex(x => x.SupplierCode).IsUnique().HasDatabaseName("UQ_SUPPLIER_Code");
        builder.HasIndex(x => x.TaxCode).IsUnique().HasDatabaseName("UQ_SUPPLIER_TaxCode");

        builder.HasOne(x => x.Account)
               .WithOne(a => a.Supplier)
               .HasForeignKey<Supplier>(x => x.AccountId)
               .HasConstraintName("FK_SUPPLIER_ACCOUNT");
    }
}
