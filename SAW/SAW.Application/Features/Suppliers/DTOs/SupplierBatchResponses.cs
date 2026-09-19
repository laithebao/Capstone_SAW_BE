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
