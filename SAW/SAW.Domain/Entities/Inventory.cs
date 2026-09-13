namespace SAW.Domain.Entities;

public class Inventory
{
    public long InventoryId { get; set; }
    public long ProductBatchId { get; set; }
    public int WarehouseLocationId { get; set; }

    public decimal QuantityOnHand { get; set; }
    public decimal ReservedQuantity { get; set; }
    public string Unit { get; set; } = "kg";

    // AvailableQuantity is a computed/persisted column — read-only from EF
    public decimal AvailableQuantity { get; set; }

    public DateTime LastUpdatedAt { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
    public WarehouseLocation WarehouseLocation { get; set; } = default!;
    public ICollection<InventoryReservation> Reservations { get; set; } = [];
    public ICollection<StockAdjustment> StockAdjustments { get; set; } = [];
    public ICollection<GoodsIssueDetail> GoodsIssueDetails { get; set; } = [];
}
