using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories.Suppliers;

public class ProductBatchRepository : IProductBatchRepository
{
    private readonly AppDbContext _context;

    public ProductBatchRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<SupplierBatchListResponse> GetBatchesBySupplierAccountIdAsync(int accountId, GetSupplierBatchesQueryRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Xác định SupplierId từ AccountId
        var supplier = await _context.Set<Supplier>()
            .FirstOrDefaultAsync(s => s.AccountId == accountId, cancellationToken);

        if (supplier == null)
        {
            throw new KeyNotFoundException("Supplier profile not found.");
        }

        // 2. Base Query
        var query = _context.Set<ProductBatch>()
            .Where(b => b.SupplierId == supplier.SupplierId);

        // 3. Thống kê tổng quan dựa trên BatchStatus
        var summary = new SupplierBatchSummaryResponse
        {
            TotalDeclaredBatches = await query.CountAsync(cancellationToken),
            PendingApprovalBatches = await query.CountAsync(b => b.BatchStatus == "SUBMITTED" || b.BatchStatus == "PENDING_APPROVAL" || b.BatchStatus == "PENDING_PREDECLARATION", cancellationToken),
            PendingQCBatches = await query.CountAsync(b => b.BatchStatus == "PENDING_QC", cancellationToken),
            ApprovedBatches = await query.CountAsync(b => b.BatchStatus == "APPROVED" || b.BatchStatus == "COMMITTED", cancellationToken),
            RejectedBatches = await query.CountAsync(b => b.BatchStatus == "REJECTED", cancellationToken)
        };

        // 4. Áp dụng các bộ lọc (Filter & Search)
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var kw = request.Keyword.Trim().ToLower();
            query = query.Where(b => b.BatchCode.ToLower().Contains(kw) || b.ProductName.ToLower().Contains(kw));
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            query = query.Where(b => b.BatchStatus == request.Status.Trim());
        }

        // ĐÃ SỬA: Lọc theo Province thay vì GrowingAreaId
        if (!string.IsNullOrWhiteSpace(request.Province))
        {
            var p = request.Province.Trim();
            query = query.Where(b => b.GrowingArea != null && b.GrowingArea.Province == p);
        }

        if (request.FromDate.HasValue)
        {
            query = query.Where(b => b.CreatedAt >= request.FromDate.Value);
        }

        if (request.ToDate.HasValue)
        {
            query = query.Where(b => b.CreatedAt <= request.ToDate.Value);
        }

        // 5. Phân trang và Mapping DTO
        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Include(b => b.GrowingArea)
            .OrderByDescending(b => b.CreatedAt)
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(b => new SupplierBatchItemResponse
            {
                BatchId = b.ProductBatchId,
                BatchCode = b.BatchCode,
                ProductName = b.ProductName,
                Note = b.Note,

                // ĐÃ SỬA: Map chi tiết 4 trường địa chỉ khớp với UI
                AreaName = b.GrowingArea != null ? b.GrowingArea.AreaName : null,
                Province = b.GrowingArea != null ? b.GrowingArea.Province : null,
                District = b.GrowingArea != null ? b.GrowingArea.District : null,
                Ward = b.GrowingArea != null ? b.GrowingArea.Ward : null,
                
                QuantityInTons = b.Unit.ToLower().Contains("kg") ? b.DeclaredQuantity / 1000m : b.DeclaredQuantity,
                SubmittedDate = b.CreatedAt,
                Status = b.BatchStatus,
                StatusDisplayName = MapStatusDisplayName(b.BatchStatus)
            })
            .ToListAsync(cancellationToken);

        return new SupplierBatchListResponse
        {
            Summary = summary,
            Batches = new PagingResult<SupplierBatchItemResponse>
            {
                Items = items,
                TotalCount = totalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize
            }
        };
    }

    private static string MapStatusDisplayName(string status) => status switch
    {
        "PENDING_PREDECLARATION" => "Chờ duyệt",
        "SUBMITTED" => "Chờ duyệt",
        "PENDING_APPROVAL" => "Chờ duyệt",
        "PENDING_QC" => "Chờ kiểm định QC",
        "APPROVED" => "Đã duyệt",
        "COMMITTED" => "Đã nhập kho",
        "REJECTED" => "Bị từ chối",
        _ => status
    };

    public async Task<bool> IsCropTypeRegisteredForSupplierAsync(int supplierId, int cropTypeId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<SupplierCropType>()
            .AnyAsync(sct => sct.SupplierId == supplierId && sct.CropTypeId == cropTypeId && sct.IsActive, cancellationToken);
    }

    public async Task<string> GenerateBatchCodeAsync(CancellationToken cancellationToken = default)
    {
        var todayStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"LH-{todayStr}-";

        var lastBatch = await _context.Set<ProductBatch>()
            .Where(b => b.BatchCode.StartsWith(prefix))
            .OrderByDescending(b => b.BatchCode)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastBatch == null)
        {
            return $"{prefix}01";
        }

        var lastSeqStr = lastBatch.BatchCode.Replace(prefix, "");
        if (int.TryParse(lastSeqStr, out int lastSeq))
        {
            return $"{prefix}{(lastSeq + 1):D2}";
        }

        return $"{prefix}{Guid.NewGuid().ToString()[..2].ToUpper()}";
    }

    public async Task AddProductBatchAsync(ProductBatch batch, BatchStatusHistory initialHistory, CancellationToken cancellationToken = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                // 1. Thêm trực tiếp history vào navigation collection của batch
                batch.StatusHistories ??= new List<BatchStatusHistory>();
                batch.StatusHistories.Add(initialHistory);

                // 2. Thêm batch (EF Core sẽ tự chèn cả batch và history trong 1 câu lệnh)
                _context.Set<ProductBatch>().Add(batch);

                // 3. Chỉ SaveChanges DUY NHẤT 1 LẦN
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    public async Task<ProductBatch?> GetBatchByIdAsync(long batchId, CancellationToken cancellationToken = default)
    {
        return await _context.ProductBatches
            .FirstOrDefaultAsync(b => b.ProductBatchId == batchId, cancellationToken);
    }

    public async Task UpdateProductBatchAsync(ProductBatch batch, BatchStatusHistory statusHistory, CancellationToken cancellationToken = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                // 1. Gán ID chính xác
                statusHistory.ProductBatchId = batch.ProductBatchId;

                // 2. Thêm lịch sử mới vào DB
                _context.Set<BatchStatusHistory>().Add(statusHistory);

                // 3. Đánh dấu Batch là Modified (Chỉ update thông tin lô hàng)
                _context.Entry(batch).State = EntityState.Modified;

                // 4. Lưu tất cả thay đổi
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                // Ghi log lỗi ra console/logger để không bị chìm exception
                Console.WriteLine($"[ERROR UpdateProductBatchAsync]: {ex.Message} | Inner: {ex.InnerException?.Message}");
                throw;
            }
        });
    }

    public async Task<ProductBatch?> GetBatchStatusDetailByIdAsync(long batchId, CancellationToken cancellationToken = default)
    {
        return await _context.ProductBatches
            .Include(b => b.CropType)
            .Include(b => b.GrowingArea)
            .Include(b => b.QcInspections)
            .FirstOrDefaultAsync(b => b.ProductBatchId == batchId, cancellationToken);
    }

    public async Task<decimal> GetCommittedReceivedQuantityAsync(long batchId, CancellationToken cancellationToken = default)
    {
        return await _context.GoodsReceipts
            .Where(gr => gr.ProductBatchId == batchId && gr.ReceiptStatus == "COMMITTED")
            .SumAsync(gr => (decimal?)gr.ReceivedQuantity, cancellationToken) ?? 0m;
    }

    public async Task<List<BatchStatusHistory>> GetBatchStatusHistoryAsync(long batchId, CancellationToken cancellationToken = default)
    {
        return await _context.BatchStatusHistories
            .Where(h => h.ProductBatchId == batchId)
            .OrderByDescending(h => h.ChangedAt)
            .ToListAsync(cancellationToken);
    }
}