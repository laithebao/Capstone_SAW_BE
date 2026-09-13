using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AUDIT_LOG");
        builder.HasKey(x => x.AuditLogId);
        builder.Property(x => x.AuditLogId).HasColumnName("AuditLogID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID");
        builder.Property(x => x.ActionType).HasColumnName("ActionType").HasMaxLength(80).IsRequired();
        builder.Property(x => x.EntityName).HasColumnName("EntityName").HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("EntityID").HasMaxLength(100);
        builder.Property(x => x.OldDataJson).HasColumnName("OldDataJson").HasColumnType("nvarchar(max)");
        builder.Property(x => x.NewDataJson).HasColumnName("NewDataJson").HasColumnType("nvarchar(max)");
        builder.Property(x => x.Description).HasColumnName("Description").HasMaxLength(1500);
        builder.Property(x => x.IpAddress).HasColumnName("IpAddress").HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasColumnName("UserAgent").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasOne(x => x.Account)
               .WithMany(a => a.AuditLogs)
               .HasForeignKey(x => x.AccountId)
               .IsRequired(false)
               .HasConstraintName("FK_AUDIT_LOG_ACCOUNT");
    }
}
