using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SAW.Application.Features.Suppliers.DTOs;

public class GetSupplierBatchesQueryRequest
{
    public string? Keyword { get; set; }
    public string? Status { get; set; }
    public string? Province { get; set; } // Lọc theo Tỉnh
    public string? ConsumptionStatus { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;
    [Range(1, 100)]
    public int PageSize { get; set; } = 10;
}

public class DeclareProductBatchRequest
{
    [Required(ErrorMessage = "Please fill in all required fields.")]
    [Range(1, int.MaxValue)]
    public int CropTypeId { get; set; }

    [Required(ErrorMessage = "Please select a growing area.")]
    [Range(1, int.MaxValue)]
    public int GrowingAreaId { get; set; } // Đã khớp với DB Entity

    [Required(ErrorMessage = "Please fill in all required fields.")]
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please fill in all required fields.")]
    public DateOnly HarvestDate { get; set; }


    public decimal DeclaredQuantity { get; set; }

    [Required(ErrorMessage = "Please fill in all required fields.")]
    [MaxLength(20)]
    public string Unit { get; set; } = "Tấn";

    [MaxLength(100)]
    public string? PackagingType { get; set; }
    public int? PackageCount { get; set; }
    public decimal? PackageUnitWeightKg { get; set; }

    public decimal? ExpectedMinTempC { get; set; }
    public decimal? ExpectedMaxTempC { get; set; }
    public decimal? ExpectedMinHumidityPct { get; set; }
    public decimal? ExpectedMaxHumidityPct { get; set; }

    public DateOnly? ExpectedDeliveryDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public List<string>? EvidenceDocumentUrls { get; set; }
    [MaxLength(1000)]
    public string? Note { get; set; }
}

public class UpdateProductBatchRequest : DeclareProductBatchRequest
{
    [Required] public DateTime? ExpectedCreatedAt { get; set; }
    public DateTime? ExpectedUpdatedAt { get; set; }
}

public class CancelSupplierBatchRequest
{
    [Required] public DateTime? ExpectedCreatedAt { get; set; }
    public DateTime? ExpectedUpdatedAt { get; set; }
}
