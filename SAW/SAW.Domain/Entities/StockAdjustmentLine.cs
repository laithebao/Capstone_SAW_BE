namespace SAW.Domain.Entities;

public sealed class StockAdjustmentLine
{
    public long StockAdjustmentLineId { get; set; }
    public long StockAdjustmentId { get; set; }
    public long InventoryId { get; set; }
    public decimal SystemQuantity { get; set; }
    public decimal ActualQuantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public DateTime InventoryUpdatedAt { get; set; }
    public string Unit { get; set; } = "kg";
    public string LocationCode { get; set; } = default!;
    public StockAdjustment StockAdjustment { get; set; } = default!;
    public Inventory Inventory { get; set; } = default!;
}
