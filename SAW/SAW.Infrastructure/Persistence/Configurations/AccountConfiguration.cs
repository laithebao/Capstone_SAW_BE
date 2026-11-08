using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("ACCOUNT");
        builder.HasKey(x => x.AccountId);
        builder.Property(x => x.AccountId).HasColumnName("AccountID").UseIdentityColumn();
        builder.Property(x => x.RoleId).HasColumnName("RoleID").IsRequired();
        builder.Property(x => x.Username).HasColumnName("Username").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Email).HasColumnName("Email").HasMaxLength(255).IsRequired();
        builder.Property(x => x.PasswordHash).HasColumnName("PasswordHash").HasMaxLength(500).IsRequired();
        builder.Property(x => x.FullName).HasColumnName("FullName").HasMaxLength(150).IsRequired();
        builder.Property(x => x.PhoneNumber).HasColumnName("PhoneNumber").HasMaxLength(30);
        builder.Property(x => x.AvatarUrl).HasColumnName("AvatarUrl").HasMaxLength(1000);
        builder.Property(x => x.AccountStatus).HasColumnName("AccountStatus").HasMaxLength(20).HasDefaultValue("PENDING");
        builder.Property(x => x.FailedLoginAttempts).HasColumnName("FailedLoginAttempts").HasDefaultValue(0);
        builder.Property(x => x.LockoutEnd).HasColumnName("LockoutEnd");
        builder.Property(x => x.LastLoginAt).HasColumnName("LastLoginAt");
        builder.Property(x => x.InactiveReviewFlag).HasColumnName("InactiveReviewFlag").HasDefaultValue(false);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");

        builder.HasIndex(x => x.Username).IsUnique().HasDatabaseName("UQ_ACCOUNT_Username");
        builder.HasIndex(x => x.Email).IsUnique().HasDatabaseName("UQ_ACCOUNT_Email");

        builder.HasOne(x => x.Role)
               .WithMany(r => r.Accounts)
               .HasForeignKey(x => x.RoleId)
               .HasConstraintName("FK_ACCOUNT_ROLE");
    }
}
