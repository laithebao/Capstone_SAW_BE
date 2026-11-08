using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class StockAdjustmentResolutionConfiguration : IEntityTypeConfiguration<StockAdjustmentResolution>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentResolution> b)
    {
        b.ToTable("STOCK_ADJUSTMENT_RESOLUTION"); b.HasKey(x => x.StockAdjustmentId);
        b.Property(x => x.StockAdjustmentId).ValueGeneratedNever();
        b.Property(x => x.State).HasMaxLength(30).IsRequired();
        b.Property(x => x.ResolvedBatchWeightKg).HasPrecision(18, 3);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.ResolutionReason).HasMaxLength(1000);
        b.HasOne(x => x.StockAdjustment).WithOne(x => x.Resolution).HasForeignKey<StockAdjustmentResolution>(x => x.StockAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ReplacesStockAdjustment).WithMany().HasForeignKey(x => x.ReplacesStockAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockAdjustment>().WithMany().HasForeignKey(x => x.CoordinatingStockAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.OrderDetail).WithMany().HasForeignKey(x => x.OrderDetailId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PurchaseOrderId, x.State });
    }
}

public sealed class StockAdjustmentProposalConfiguration : IEntityTypeConfiguration<StockAdjustmentProposal>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentProposal> b)
    {
        b.ToTable("STOCK_ADJUSTMENT_PROPOSAL", t => { t.UseSqlOutputClause(false); t.HasCheckConstraint("CK_ADJUSTMENT_PROPOSAL_Amounts", "[ProposedWeightKg] > 0 AND [ProposedWholeLotPrice] > 0"); });
        b.HasKey(x => x.StockAdjustmentProposalId); b.Property(x => x.ProposalStatus).HasMaxLength(20);
        b.HasIndex(x => new { x.StockAdjustmentId, x.Revision }).IsUnique();
        b.Property(x => x.OriginalWeightKg).HasPrecision(18, 3); b.Property(x => x.ProposedWeightKg).HasPrecision(18, 3);
        b.Property(x => x.OriginalWholeLotPrice).HasPrecision(18, 2); b.Property(x => x.ProposedWholeLotPrice).HasPrecision(18, 2);
        b.Property(x => x.OriginalOrderTotal).HasPrecision(18, 2); b.Property(x => x.ProposedOrderTotal).HasPrecision(18, 2);
        b.Property(x => x.CustomerMessage).HasMaxLength(1000); b.Property(x => x.ResponseReason).HasMaxLength(1000); b.Property(x => x.WithdrawalReason).HasMaxLength(1000);
        b.HasOne(x => x.Resolution).WithMany(x => x.Proposals).HasForeignKey(x => x.StockAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CreatedByAccount).WithMany().HasForeignKey(x => x.CreatedByAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.RespondedByAccount).WithMany().HasForeignKey(x => x.RespondedByAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OrderAmendmentConfiguration : IEntityTypeConfiguration<OrderAmendment>
{
    public void Configure(EntityTypeBuilder<OrderAmendment> b)
    {
        b.ToTable("ORDER_AMENDMENT", t => t.UseSqlOutputClause(false)); b.HasKey(x => x.OrderAmendmentId);
        b.HasIndex(x => x.StockAdjustmentProposalId).IsUnique();
        b.Property(x => x.PreviousWeightKg).HasPrecision(18, 3); b.Property(x => x.EffectiveWeightKg).HasPrecision(18, 3);
        b.Property(x => x.PreviousWholeLotPrice).HasPrecision(18, 2); b.Property(x => x.EffectiveWholeLotPrice).HasPrecision(18, 2);
        b.Property(x => x.PreviousOrderTotal).HasPrecision(18, 2); b.Property(x => x.EffectiveOrderTotal).HasPrecision(18, 2);
        b.HasOne(x => x.Proposal).WithMany().HasForeignKey(x => x.StockAdjustmentProposalId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.OrderDetail).WithMany().HasForeignKey(x => x.OrderDetailId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ApprovedByAccount).WithMany().HasForeignKey(x => x.ApprovedByAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GoodsIssueInvalidationConfiguration : IEntityTypeConfiguration<GoodsIssueInvalidation>
{
    public void Configure(EntityTypeBuilder<GoodsIssueInvalidation> b)
    {
        b.ToTable("GOODS_ISSUE_INVALIDATION", t => t.UseSqlOutputClause(false)); b.HasKey(x => x.GoodsIssueId);
        b.Property(x => x.GoodsIssueId).ValueGeneratedNever(); b.Property(x => x.Reason).HasMaxLength(1000);
        b.HasOne(x => x.GoodsIssue).WithOne().HasForeignKey<GoodsIssueInvalidation>(x => x.GoodsIssueId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.StockAdjustment).WithMany().HasForeignKey(x => x.StockAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
