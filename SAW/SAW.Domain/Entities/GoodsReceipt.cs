namespace SAW.Domain.Entities;

public class GoodsReceipt
{
    public long GoodsReceiptId { get; set; }
    public string ReceiptCode { get; set; } = default!;

    public long ProductBatchId { get; set; }
    public int WarehouseLocationId { get; set; }
    public int OperationAccountId { get; set; }

    public decimal ReceivedQuantity { get; set; }
    public string Unit { get; set; } = default!;
    public decimal WeightInKg { get; set; }

    public string ReceiptStatus { get; set; } = "DRAFT";

    public DateTime ReceivedAt { get; set; }
    public DateTime? CommittedAt { get; set; }

    public string? Note { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
    public WarehouseLocation WarehouseLocation { get; set; } = default!;
    public Account OperationAccount { get; set; } = default!;
}
