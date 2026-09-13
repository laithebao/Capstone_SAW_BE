namespace SAW.Domain.Entities;

public class StockTransfer
{
    public long StockTransferId { get; set; }
    public string TransferCode { get; set; } = default!;

    public long ProductBatchId { get; set; }
    public int FromWarehouseLocationId { get; set; }
    public int ToWarehouseLocationId { get; set; }

    public decimal Quantity { get; set; }
    public string Unit { get; set; } = default!;

    public string SourceTrackingId { get; set; } = default!;
    public string DestinationTrackingId { get; set; } = default!;

    public int OperationAccountId { get; set; }
    public string TransferStatus { get; set; } = "COMPLETED";
    public DateTime TransferredAt { get; set; }
    public string? Note { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
    public WarehouseLocation FromWarehouseLocation { get; set; } = default!;
    public WarehouseLocation ToWarehouseLocation { get; set; } = default!;
    public Account OperationAccount { get; set; } = default!;
}
