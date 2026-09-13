namespace SAW.Domain.Entities;

public class StockAdjustment
{
    public long StockAdjustmentId { get; set; }
    public string AdjustmentCode { get; set; } = default!;

    public long InventoryId { get; set; }

    public int CreatedByAccountId { get; set; }
    public int? ReviewedByAccountId { get; set; }

    public decimal SystemQuantity { get; set; }
    public decimal ActualQuantity { get; set; }

    // Computed columns — read-only
    public decimal DifferenceQuantity { get; set; }
    public decimal? VariancePercent { get; set; }

    public string AdjustmentType { get; set; } = default!;
    public string Reason { get; set; } = default!;

    public string AdjustmentStatus { get; set; } = "PENDING";

    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    // Navigation
    public Inventory Inventory { get; set; } = default!;
    public Account CreatedByAccount { get; set; } = default!;
    public Account? ReviewedByAccount { get; set; }
}
