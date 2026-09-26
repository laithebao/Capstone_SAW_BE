namespace SAW.Domain.Entities;

public class ProductBatch
{
    public long ProductBatchId { get; set; }
    public string BatchCode { get; set; } = default!;

    public int SupplierId { get; set; }
    public int CropTypeId { get; set; }
    
    // THÊM MỚI
    public int GrowingAreaId { get; set; }

    public string ProductName { get; set; } = default!;
    
    // ĐÃ XÓA: public string Origin { get; set; } = default!;
    
    public DateOnly HarvestDate { get; set; }

    public decimal DeclaredQuantity { get; set; }
    public string Unit { get; set; } = default!;
    public decimal WeightInKg { get; set; }
    public decimal? VerifiedQuantity { get; set; }
    public decimal? VerifiedWeightInKg { get; set; }

    public string? PackagingType { get; set; }
    public int? PackageCount { get; set; }
    public decimal? PackageUnitWeightKg { get; set; }

    public decimal? ExpectedMinTempC { get; set; }
    public decimal? ExpectedMaxTempC { get; set; }
    public decimal? ExpectedMinHumidityPct { get; set; }
    public decimal? ExpectedMaxHumidityPct { get; set; }
    public int? ShelfLifeDaysSnapshot { get; set; }

    public DateOnly? ExpectedDeliveryDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }

    public string BatchStatus { get; set; } = "PENDING_PREDECLARATION";

    public string? QualityGrade { get; set; }
    public string? RejectionReason { get; set; }
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Supplier Supplier { get; set; } = default!;
    public CropType CropType { get; set; } = default!;
    
    // THÊM MỚI
    public GrowingArea GrowingArea { get; set; } = default!;
    
    public ICollection<BatchStatusHistory> StatusHistories { get; set; } = [];
    public ICollection<QcInspection> QcInspections { get; set; } = [];
    public ICollection<Inventory> Inventories { get; set; } = [];
    public ICollection<EnvironmentLog> EnvironmentLogs { get; set; } = [];
    public ICollection<GoodsReceipt> GoodsReceipts { get; set; } = [];
    public ICollection<QrCode> QrCodes { get; set; } = [];
    public ICollection<AiRiskForecast> AiRiskForecasts { get; set; } = [];
    public ICollection<StockTransfer> StockTransfers { get; set; } = [];
    public ICollection<OrderDetail> OrderDetails { get; set; } = [];
}
