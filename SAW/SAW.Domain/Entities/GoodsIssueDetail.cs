namespace SAW.Domain.Entities;

public class GoodsIssueDetail
{
    public long GoodsIssueDetailId { get; set; }

    public long GoodsIssueId { get; set; }
    public long OrderDetailId { get; set; }
    public long InventoryReservationId { get; set; }
    public long InventoryId { get; set; }

    public decimal IssuedQuantity { get; set; }
    public string Unit { get; set; } = default!;
    public decimal WeightInKg { get; set; }

    // Navigation
    public GoodsIssue GoodsIssue { get; set; } = default!;
    public OrderDetail OrderDetail { get; set; } = default!;
    public InventoryReservation InventoryReservation { get; set; } = default!;
    public Inventory Inventory { get; set; } = default!;
}
