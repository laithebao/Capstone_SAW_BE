namespace SAW.Domain.Entities;

public class PurchaseOrder
{
    public long PurchaseOrderId { get; set; }
    public string OrderCode { get; set; } = default!;
    public int DistributorId { get; set; }

    public string OrderStatus { get; set; } = "PENDING";

    public string DeliveryAddress { get; set; } = default!;
    public string? ContactPhone { get; set; }
    public DateOnly ExpectedDeliveryDate { get; set; }
    public string? OrderNote { get; set; }

    public decimal SubtotalAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public int? ApprovedByAccountId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? CancellationReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Distributor Distributor { get; set; } = default!;
    public Account? ApprovedByAccount { get; set; }
    public ICollection<OrderDetail> OrderDetails { get; set; } = [];
    public ICollection<OrderStatusHistory> StatusHistories { get; set; } = [];
    public ICollection<GoodsIssue> GoodsIssues { get; set; } = [];
}
