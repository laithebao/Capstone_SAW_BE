namespace SAW.Domain.Entities;

public sealed class StockAdjustmentProposal
{
    public long StockAdjustmentProposalId { get; set; }
    public long StockAdjustmentId { get; set; }
    public string ScopeSnapshotJson { get; set; } = default!;
    public int Revision { get; set; }
    public string ProposalStatus { get; set; } = "DRAFT";
    public decimal OriginalWeightKg { get; set; }
    public decimal ProposedWeightKg { get; set; }
    public decimal OriginalWholeLotPrice { get; set; }
    public decimal ProposedWholeLotPrice { get; set; }
    public decimal OriginalOrderTotal { get; set; }
    public decimal ProposedOrderTotal { get; set; }
    public string CustomerMessage { get; set; } = default!;
    public int CreatedByAccountId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int? RespondedByAccountId { get; set; }
    public DateTime? RespondedAt { get; set; }
    public string? ResponseReason { get; set; }
    public string? WithdrawalReason { get; set; }
    public StockAdjustmentResolution Resolution { get; set; } = default!;
    public Account CreatedByAccount { get; set; } = default!;
    public Account? RespondedByAccount { get; set; }
}
