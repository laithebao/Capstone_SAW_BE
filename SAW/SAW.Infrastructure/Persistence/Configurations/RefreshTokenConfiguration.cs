using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("REFRESH_TOKEN");
        builder.HasKey(x => x.RefreshTokenId);
        builder.Property(x => x.RefreshTokenId).HasColumnName("RefreshTokenID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("TokenHash").HasMaxLength(500).IsRequired();
        builder.Property(x => x.JwtId).HasColumnName("JwtId").HasMaxLength(120);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.ExpiresAt).HasColumnName("ExpiresAt").IsRequired();
        builder.Property(x => x.RevokedAt).HasColumnName("RevokedAt");
        builder.Property(x => x.CreatedByIp).HasColumnName("CreatedByIp").HasMaxLength(64);
        builder.Property(x => x.RevokedByIp).HasColumnName("RevokedByIp").HasMaxLength(64);
        builder.Property(x => x.ReplacedByTokenHash).HasColumnName("ReplacedByTokenHash").HasMaxLength(500);

        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("UQ_REFRESH_TOKEN_Hash");

        builder.HasOne(x => x.Account)
               .WithMany(a => a.RefreshTokens)
               .HasForeignKey(x => x.AccountId)
               .HasConstraintName("FK_REFRESH_TOKEN_ACCOUNT");
    }
}
