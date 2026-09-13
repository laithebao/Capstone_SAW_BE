using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PASSWORD_RESET_TOKEN");
        builder.HasKey(x => x.PasswordResetTokenId);
        builder.Property(x => x.PasswordResetTokenId).HasColumnName("PasswordResetTokenID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("TokenHash").HasMaxLength(500).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.ExpiresAt).HasColumnName("ExpiresAt").IsRequired();
        builder.Property(x => x.UsedAt).HasColumnName("UsedAt");

        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("UQ_PASSWORD_RESET_TOKEN_Hash");

        builder.HasOne(x => x.Account)
               .WithMany(a => a.PasswordResetTokens)
               .HasForeignKey(x => x.AccountId)
               .HasConstraintName("FK_PASSWORD_RESET_TOKEN_ACCOUNT");
    }
}
