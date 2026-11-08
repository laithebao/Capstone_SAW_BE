using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("PERMISSION");
        builder.HasKey(x => x.PermissionId);
        builder.Property(x => x.PermissionId).HasColumnName("PermissionID").UseIdentityColumn();
        builder.Property(x => x.PermissionCode).HasColumnName("PermissionCode").HasMaxLength(100).IsRequired();
        builder.Property(x => x.PermissionName).HasColumnName("PermissionName").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasColumnName("Description").HasMaxLength(500);
        builder.Property(x => x.IsActive).HasColumnName("IsActive").HasDefaultValue(true);

        builder.HasIndex(x => x.PermissionCode).IsUnique().HasDatabaseName("UQ_PERMISSION_Code");
    }
}
