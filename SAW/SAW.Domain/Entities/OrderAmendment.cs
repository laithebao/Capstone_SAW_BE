namespace SAW.Domain.Entities;

public sealed class OrderAmendment
{
    public long OrderAmendmentId { get; set; }
    public long StockAdjustmentProposalId { get; set; }
    public long OrderDetailId { get; set; }
    public decimal PreviousWeightKg { get; set; }
    public decimal EffectiveWeightKg { get; set; }
    public decimal PreviousWholeLotPrice { get; set; }
    public decimal EffectiveWholeLotPrice { get; set; }
    public decimal PreviousOrderTotal { get; set; }
    public decimal EffectiveOrderTotal { get; set; }
    public int ApprovedByAccountId { get; set; }
    public DateTime ApprovedAt { get; set; }
    public StockAdjustmentProposal Proposal { get; set; } = default!;
    public OrderDetail OrderDetail { get; set; } = default!;
    public Account ApprovedByAccount { get; set; } = default!;
}
