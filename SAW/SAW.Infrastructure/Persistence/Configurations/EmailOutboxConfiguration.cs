using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;
namespace SAW.Infrastructure.Persistence.Configurations;
public sealed class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutbox>
{
    public void Configure(EntityTypeBuilder<EmailOutbox> b)
    {
        b.ToTable("EMAIL_OUTBOX"); b.HasKey(x => x.Id);
        b.Property(x => x.EventKey).HasMaxLength(180);
        b.Property(x => x.Recipient).HasMaxLength(255); b.Property(x => x.Subject).HasMaxLength(250); b.Property(x => x.LastError).HasMaxLength(200);
        b.HasIndex(x => new { x.EventKey, x.AccountId }).IsUnique();
        b.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
