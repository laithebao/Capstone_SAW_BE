using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("NOTIFICATION");
        builder.HasKey(x => x.NotificationId);
        builder.Property(x => x.NotificationId).HasColumnName("NotificationID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID").IsRequired();
        builder.Property(x => x.NotificationType).HasColumnName("NotificationType").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Priority).HasColumnName("Priority").HasMaxLength(20).HasDefaultValue("NORMAL");
        builder.Property(x => x.Title).HasColumnName("Title").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Message).HasColumnName("Message").HasMaxLength(1500).IsRequired();
        builder.Property(x => x.RelatedEntityType).HasColumnName("RelatedEntityType").HasMaxLength(80);
        builder.Property(x => x.RelatedEntityId).HasColumnName("RelatedEntityID").HasMaxLength(100);
        builder.Property(x => x.IsRead).HasColumnName("IsRead").HasDefaultValue(false);
        builder.Property(x => x.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.ReadAt).HasColumnName("ReadAt");

        builder.HasOne(x => x.Account)
               .WithMany(a => a.Notifications)
               .HasForeignKey(x => x.AccountId)
               .HasConstraintName("FK_NOTIFICATION_ACCOUNT");
    }
}
