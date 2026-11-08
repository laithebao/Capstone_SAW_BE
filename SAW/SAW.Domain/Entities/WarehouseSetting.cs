namespace SAW.Domain.Entities;

public class WarehouseSetting
{
    public byte WarehouseSettingId { get; set; }
    public string WarehouseName { get; set; } = default!;
    public decimal? MaxCapacityKg { get; set; }
    public string? Address { get; set; }
    public DateTime UpdatedAt { get; set; }
}
