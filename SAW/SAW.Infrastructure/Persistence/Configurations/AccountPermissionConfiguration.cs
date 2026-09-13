using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class AccountPermissionConfiguration : IEntityTypeConfiguration<AccountPermission>
{
    public void Configure(EntityTypeBuilder<AccountPermission> builder)
    {
        builder.ToTable("ACCOUNT_PERMISSION");
        builder.HasKey(x => new { x.AccountId, x.PermissionId });
        builder.Property(x => x.AccountId).HasColumnName("AccountID");
        builder.Property(x => x.PermissionId).HasColumnName("PermissionID");
        builder.Property(x => x.IsGranted).HasColumnName("IsGranted").HasDefaultValue(true);
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasOne(x => x.Account)
               .WithMany(a => a.AccountPermissions)
               .HasForeignKey(x => x.AccountId)
               .HasConstraintName("FK_ACCOUNT_PERMISSION_ACCOUNT");

        builder.HasOne(x => x.Permission)
               .WithMany(p => p.AccountPermissions)
               .HasForeignKey(x => x.PermissionId)
               .HasConstraintName("FK_ACCOUNT_PERMISSION_PERMISSION");
    }
}
