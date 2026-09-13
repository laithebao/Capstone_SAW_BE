namespace SAW.Domain.Entities;

public class InventoryReservation
{
    public long InventoryReservationId { get; set; }

    public long OrderDetailId { get; set; }
    public long InventoryId { get; set; }

    public decimal ReservedQuantity { get; set; }
    public decimal PickedQuantity { get; set; }

    public string ReservationStatus { get; set; } = "RESERVED";

    public DateTime ReservedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }

    // Navigation
    public OrderDetail OrderDetail { get; set; } = default!;
    public Inventory Inventory { get; set; } = default!;
    public ICollection<PickingHistory> PickingHistories { get; set; } = [];
    public ICollection<GoodsIssueDetail> GoodsIssueDetails { get; set; } = [];
}
