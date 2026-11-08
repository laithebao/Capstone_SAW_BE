namespace SAW.Domain.Entities;

public class EnvironmentLog
{
    public long EnvironmentLogId { get; set; }

    public long ProductBatchId { get; set; }
    public int? WarehouseLocationId { get; set; }   // null khi ghi trong phương pháp kiểm định (lô chưa nhập kho)
    public long? QcInspectionId { get; set; }
    public int? RecordedByAccountId { get; set; }

    public decimal TemperatureC { get; set; }
    public decimal? HumidityPct { get; set; }

    public string SourceType { get; set; } = "MANUAL";
    public string? SensorIdentifier { get; set; }

    public DateTime RecordedAt { get; set; }
    public string? Note { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
    public WarehouseLocation? WarehouseLocation { get; set; }
    public QcInspection? QcInspection { get; set; }
    public Account? RecordedByAccount { get; set; }
}
