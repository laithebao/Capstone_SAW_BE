using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SAW.Application.Features.Suppliers.DTOs;

public class SupplierBatchItemResponse
{
    public long BatchId { get; set; }
    public string BatchCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    
    // Tách chi tiết Vùng trồng khớp với FE
    public string? AreaName { get; set; } 
    public string? Province { get; set; } 
    public string? District { get; set; }
    public string? Ward { get; set; }
    
    public string? Note { get; set; } 
    public decimal QuantityInTons { get; set; }
    public DateTime SubmittedDate { get; set; } 
    public DateTime? CompletedDate { get; set; } 
    public string Status { get; set; } = string.Empty;
    public string StatusDisplayName { get; set; } = string.Empty;
    public string? ConsumptionStatus { get; set; } = "IN_STOCK"; 
    public string? ConsumptionStatusDisplayName { get; set; } = "Tồn kho";
}

public class SupplierBatchSummaryResponse
{
    public int TotalDeclaredBatches { get; set; }
    public int PendingApprovalBatches { get; set; }
    public int PendingQCBatches { get; set; }
    public int ApprovedBatches { get; set; }
    public int RejectedBatches { get; set; }
}

public class PagingResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageIndex { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public class SupplierBatchListResponse
{
    public SupplierBatchSummaryResponse Summary { get; set; } = new();
    public PagingResult<SupplierBatchItemResponse> Batches { get; set; } = new();
}

public class SupplierBatchStatusResponse
{
    public long BatchId { get; set; }
    public string BatchCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string CropTypeName { get; set; } = string.Empty;
    
    // Tách chi tiết Vùng trồng
    public string AreaName { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string Ward { get; set; } = string.Empty;
    
    public DateOnly HarvestDate { get; set; }

    public decimal DeclaredQuantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal ReceivedQuantity { get; set; } 
    public decimal WeightInKg { get; set; }

    public string CurrentStatus { get; set; } = string.Empty;
    public string StatusDisplayName { get; set; } = string.Empty;
    public string? QcResult { get; set; } 
    public string? QualityGrade { get; set; } 
    public string? RejectionReason { get; set; } 
    public string? WarehouseNote { get; set; }

    public DateOnly? ExpectedDeliveryDate { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<BatchStatusHistoryDto> StatusHistory { get; set; } = new();
}

public class BatchStatusHistoryDto
{
    public string OldStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public string? ChangeReason { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
}