using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class EmailVerificationTokenConfiguration : IEntityTypeConfiguration<EmailVerificationToken>
{
    public void Configure(EntityTypeBuilder<EmailVerificationToken> builder)
    {
        builder.ToTable("EMAIL_VERIFICATION_TOKEN");
        builder.HasKey(x => x.EmailVerificationTokenId);
        builder.Property(x => x.EmailVerificationTokenId).HasColumnName("EmailVerificationTokenID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("TokenHash").HasMaxLength(500).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.ExpiresAt).HasColumnName("ExpiresAt").IsRequired();
        builder.Property(x => x.UsedAt).HasColumnName("UsedAt");

        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("UQ_EMAIL_VERIFICATION_TOKEN_Hash");

        builder.HasOne(x => x.Account)
               .WithMany(a => a.EmailVerificationTokens)
               .HasForeignKey(x => x.AccountId)
               .HasConstraintName("FK_EMAIL_VERIFICATION_TOKEN_ACCOUNT");
    }
}
