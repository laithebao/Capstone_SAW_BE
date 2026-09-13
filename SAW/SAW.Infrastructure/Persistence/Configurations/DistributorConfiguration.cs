using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class DistributorConfiguration : IEntityTypeConfiguration<Distributor>
{
    public void Configure(EntityTypeBuilder<Distributor> builder)
    {
        builder.ToTable("DISTRIBUTOR");
        builder.HasKey(x => x.DistributorId);
        builder.Property(x => x.DistributorId).HasColumnName("DistributorID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID").IsRequired();
        builder.Property(x => x.DistributorCode).HasColumnName("DistributorCode").HasMaxLength(30).IsRequired();
        builder.Property(x => x.DistributorName).HasColumnName("DistributorName").HasMaxLength(200).IsRequired();
        builder.Property(x => x.TaxCode).HasColumnName("TaxCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.HasOverdueBalance).HasColumnName("HasOverdueBalance").HasDefaultValue(false);
        builder.Property(x => x.OverdueNote).HasColumnName("OverdueNote").HasMaxLength(500);
        builder.Property(x => x.BalanceStatusUpdatedAt).HasColumnName("BalanceStatusUpdatedAt");
        builder.Property(x => x.ContactPerson).HasColumnName("ContactPerson").HasMaxLength(150);
        builder.Property(x => x.PhoneNumber).HasColumnName("PhoneNumber").HasMaxLength(30);
        builder.Property(x => x.Email).HasColumnName("Email").HasMaxLength(255);
        builder.Property(x => x.Address).HasColumnName("Address").HasMaxLength(500);
        builder.Property(x => x.ProfileStatus).HasColumnName("ProfileStatus").HasMaxLength(20).HasDefaultValue("ACTIVE");
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");

        builder.HasIndex(x => x.AccountId).IsUnique().HasDatabaseName("UQ_DISTRIBUTOR_Account");
        builder.HasIndex(x => x.DistributorCode).IsUnique().HasDatabaseName("UQ_DISTRIBUTOR_Code");
        builder.HasIndex(x => x.TaxCode).IsUnique().HasDatabaseName("UQ_DISTRIBUTOR_TaxCode");

        builder.HasOne(x => x.Account)
               .WithOne(a => a.Distributor)
               .HasForeignKey<Distributor>(x => x.AccountId)
               .HasConstraintName("FK_DISTRIBUTOR_ACCOUNT");
    }
}
