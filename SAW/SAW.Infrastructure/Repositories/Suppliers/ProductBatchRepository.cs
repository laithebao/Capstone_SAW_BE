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
            .OrderByDescending(b => b.CreatedAt)
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(b => new SupplierBatchItemResponse
            {
                BatchId = b.ProductBatchId,
                BatchCode = b.BatchCode,
                ProductName = b.ProductName,
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
                _context.Set<ProductBatch>().Add(batch);
                await _context.SaveChangesAsync(cancellationToken);

                initialHistory.ProductBatchId = batch.ProductBatchId;
                _context.Set<BatchStatusHistory>().Add(initialHistory);

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
                _context.ProductBatches.Update(batch);
                _context.BatchStatusHistories.Add(statusHistory);

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

    public async Task<ProductBatch?> GetBatchStatusDetailByIdAsync(long batchId, CancellationToken cancellationToken = default)
    {
        return await _context.ProductBatches
            .Include(b => b.CropType)
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