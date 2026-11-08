namespace SAW.Domain.Entities;

// One handling workflow per existing adjustment; no second incident document.
public sealed class StockAdjustmentResolution
{
    public long StockAdjustmentId { get; set; }
    public long? CoordinatingStockAdjustmentId { get; set; }
    public long? PurchaseOrderId { get; set; }
    public long? OrderDetailId { get; set; }
    public long? ReplacesStockAdjustmentId { get; set; }
    public string State { get; set; } = "PENDING_MANAGER";
    public string ExpectedInventorySnapshotJson { get; set; } = default!;
    public string? ExpectedOrderSnapshotJson { get; set; }
    public decimal? ResolvedBatchWeightKg { get; set; }
    public int Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; }
    public string? ResolutionReason { get; set; }
    public StockAdjustment StockAdjustment { get; set; } = default!;
    public StockAdjustment? ReplacesStockAdjustment { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public OrderDetail? OrderDetail { get; set; }
    public ICollection<StockAdjustmentProposal> Proposals { get; set; } = [];
}
