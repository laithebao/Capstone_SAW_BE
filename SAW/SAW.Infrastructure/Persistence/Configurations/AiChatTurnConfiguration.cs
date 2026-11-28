using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class AiChatTurnConfiguration : IEntityTypeConfiguration<AiChatTurn>
{
    public void Configure(EntityTypeBuilder<AiChatTurn> builder)
    {
        builder.ToTable("AI_CHAT_TURN");
        builder.HasKey(x => x.AiChatTurnId);
        builder.Property(x => x.AiChatTurnId).HasColumnName("AIChatTurnID").UseIdentityColumn();
        builder.Property(x => x.AccountId).HasColumnName("AccountID");
        builder.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Reply).IsRequired();
        builder.Property(x => x.BatchContextCode).HasMaxLength(200);
        builder.HasIndex(x => new { x.AccountId, x.AiChatTurnId });
        builder.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
