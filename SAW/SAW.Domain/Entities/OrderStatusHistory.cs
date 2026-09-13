namespace SAW.Domain.Entities;

public class OrderStatusHistory
{
    public long OrderStatusHistoryId { get; set; }
    public long PurchaseOrderId { get; set; }

    public string? OldStatus { get; set; }
    public string NewStatus { get; set; } = default!;
    public int? ChangedByAccountId { get; set; }
    public string? ChangeReason { get; set; }
    public DateTime ChangedAt { get; set; }

    // Navigation
    public PurchaseOrder PurchaseOrder { get; set; } = default!;
    public Account? ChangedByAccount { get; set; }
}
