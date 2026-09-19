using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SAW.Application.Features.Suppliers.DTOs;

public class GetSupplierBatchesQueryRequest
{
    public string? Keyword { get; set; } // Tìm kiếm theo Mã lô hàng hoặc Tên sản phẩm
    public string? Status { get; set; } // SUBMITTED, PENDING_QC, APPROVED, REJECTED...
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    public int PageIndex { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}
