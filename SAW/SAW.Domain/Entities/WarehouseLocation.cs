namespace SAW.Domain.Entities;

public class WarehouseLocation
{
    public int WarehouseLocationId { get; set; }

    public string LocationCode { get; set; } = default!;
    public string ZoneName { get; set; } = default!;
    public string? RackName { get; set; }
    public string? BinName { get; set; }

    public decimal? MaxWeightKg { get; set; }
    public decimal? MaxVolumeM3 { get; set; }

    public decimal? MinTempC { get; set; }
    public decimal? MaxTempC { get; set; }
    public decimal? MinHumidityPct { get; set; }
    public decimal? MaxHumidityPct { get; set; }

    public string LocationStatus { get; set; } = "ACTIVE";
    public DateTime CreatedAt { get; set; }

    // Navigation
    public ICollection<Inventory> Inventories { get; set; } = [];
    public ICollection<GoodsReceipt> GoodsReceipts { get; set; } = [];
    public ICollection<EnvironmentLog> EnvironmentLogs { get; set; } = [];
    public ICollection<StockTransfer> StockTransfersFrom { get; set; } = [];
    public ICollection<StockTransfer> StockTransfersTo { get; set; } = [];
}
