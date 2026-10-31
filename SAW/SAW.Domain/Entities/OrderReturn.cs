namespace SAW.Domain.Entities;

// A whole-order return is physically held apart from saleable inventory.
public sealed class OrderReturn
{
    public long OrderReturnId { get; set; }
    public long PurchaseOrderId { get; set; }
    public string Status { get; set; } = "REQUESTED";
    public string Reason { get; set; } = default!;
    public string? ReviewNote { get; set; }
    public string? ReceiptNote { get; set; }
    public int RequestedByAccountId { get; set; }
    public int? ReviewedByAccountId { get; set; }
    public int? ReceivedByAccountId { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = default!;
}
