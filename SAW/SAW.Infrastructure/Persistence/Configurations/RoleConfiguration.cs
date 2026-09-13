using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("ROLE");
        builder.HasKey(x => x.RoleId);
        builder.Property(x => x.RoleId).HasColumnName("RoleID").UseIdentityColumn();
        builder.Property(x => x.RoleCode).HasColumnName("RoleCode").HasMaxLength(40).IsRequired();
        builder.Property(x => x.RoleName).HasColumnName("RoleName").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasColumnName("Description").HasMaxLength(500);
        builder.Property(x => x.IsActive).HasColumnName("IsActive").HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => x.RoleCode).IsUnique().HasDatabaseName("UQ_ROLE_RoleCode");
    }
}
