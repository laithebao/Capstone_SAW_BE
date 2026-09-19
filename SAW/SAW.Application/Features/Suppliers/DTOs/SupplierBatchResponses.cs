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
    public decimal QuantityInTons { get; set; }
    public DateTime SubmittedDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StatusDisplayName { get; set; } = string.Empty;
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
    public string Origin { get; set; } = string.Empty;
    public DateOnly HarvestDate { get; set; }

    // Khối lượng & Số lượng
    public decimal DeclaredQuantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal ReceivedQuantity { get; set; } // Tính từ GoodsReceipt (COMMITTED)
    public decimal WeightInKg { get; set; }

    // Trạng thái & Kết quả QC
    public string CurrentStatus { get; set; } = string.Empty;
    public string StatusDisplayName { get; set; } = string.Empty;
    public string? QcResult { get; set; } // Passed / Failed / Pending
    public string? QualityGrade { get; set; } // Hạng A, Hạng B,...
    public string? RejectionReason { get; set; } // Lý do từ chối/thất bại QC
    public string? WarehouseNote { get; set; }

    // Ngày tháng liên quan
    public DateOnly? ExpectedDeliveryDate { get; set; }
    public DateTime CreatedAt { get; set; }

    // Lịch sử tiến trình xử lý
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
