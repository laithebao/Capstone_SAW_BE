namespace SAW.Domain.Entities;

public class CropType
{
    public int CropTypeId { get; set; }

    public string CropCode { get; set; } = default!;
    public string CropName { get; set; } = default!;

    public string CategoryName { get; set; } = default!;
    public string? Description { get; set; }

    public decimal? ExpectedMinTempC { get; set; }
    public decimal? ExpectedMaxTempC { get; set; }
    public decimal? ExpectedMinHumidityPct { get; set; }
    public decimal? ExpectedMaxHumidityPct { get; set; }

    public int? ShelfLifeDays { get; set; }
    public decimal? SafetyStockLevelKg { get; set; }

    public string DefaultUnit { get; set; } = "kg";
    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public ICollection<SupplierCropType> SupplierCropTypes { get; set; } = [];
    public ICollection<InspectionStandardSet> InspectionStandardSets { get; set; } = [];
    public ICollection<ProductBatch> ProductBatches { get; set; } = [];
    public ICollection<OrderDetail> OrderDetails { get; set; } = [];
}
