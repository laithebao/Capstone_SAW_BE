using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class GoodsIssueConfiguration : IEntityTypeConfiguration<GoodsIssue>
{
    public void Configure(EntityTypeBuilder<GoodsIssue> builder)
    {
        builder.ToTable("GOODS_ISSUE");
        builder.HasKey(x => x.GoodsIssueId);
        builder.Property(x => x.GoodsIssueId).HasColumnName("GoodsIssueID").UseIdentityColumn();
        builder.Property(x => x.IssueCode).HasColumnName("IssueCode").HasMaxLength(50).IsRequired();
        builder.Property(x => x.PurchaseOrderId).HasColumnName("PurchaseOrderID").IsRequired();
        builder.Property(x => x.OperationAccountId).HasColumnName("OperationAccountID").IsRequired();
        builder.Property(x => x.ReceiverName).HasColumnName("ReceiverName").HasMaxLength(200);
        builder.Property(x => x.IssueStatus).HasColumnName("IssueStatus").HasMaxLength(20).HasDefaultValue("DRAFT");
        builder.Property(x => x.IssuedAt).HasColumnName("IssuedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.CommittedAt).HasColumnName("CommittedAt");
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasIndex(x => x.IssueCode).IsUnique().HasDatabaseName("UQ_GOODS_ISSUE_Code");

        builder.HasOne(x => x.PurchaseOrder)
               .WithMany(o => o.GoodsIssues)
               .HasForeignKey(x => x.PurchaseOrderId)
               .HasConstraintName("FK_GOODS_ISSUE_ORDER");

        builder.HasOne(x => x.OperationAccount)
               .WithMany()
               .HasForeignKey(x => x.OperationAccountId)
               .HasConstraintName("FK_GOODS_ISSUE_ACCOUNT");
    }
}
