using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;
namespace SAW.Infrastructure.Persistence.Configurations;
public sealed class SupplyContractRevisionConfiguration : IEntityTypeConfiguration<SupplyContractRevision>
{
    public void Configure(EntityTypeBuilder<SupplyContractRevision> b)
    {
        b.ToTable("SUPPLY_CONTRACT_REVISION"); b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.SupplyContractId, x.Revision }).IsUnique();
        b.Property(x => x.TemplateVersion).HasMaxLength(30); b.Property(x => x.ContentHash).HasMaxLength(64);
        b.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.SupplyContractId).OnDelete(DeleteBehavior.Restrict);
    }
}
