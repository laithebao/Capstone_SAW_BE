using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;
namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("CATEGORY", t => t.HasCheckConstraint("CK_CATEGORY_Parent", "[ParentId] IS NULL OR [ParentId] <> [CategoryId]"));
        b.HasKey(x => x.CategoryId);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}
