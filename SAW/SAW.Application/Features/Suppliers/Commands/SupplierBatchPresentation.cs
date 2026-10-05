using System.Linq.Expressions;
using SAW.Domain.Entities;

namespace SAW.Application.Features.Suppliers.Commands;

// This projection is used both by EF queries and the supplier detail response.
// It never changes the warehouse's persisted batch status.
public static class SupplierBatchPresentation
{
    public static readonly string[] StoredStatuses = ["IN_STOCK", "RESERVED", "PARTIALLY_ISSUED", "ISSUED"];
    public static readonly string[] VisibleStatuses = SupplierBatchStatuses.All
        .Except(["RESERVED", "PARTIALLY_ISSUED", "ISSUED"]).ToArray();
    public static readonly Expression<Func<ProductBatch, SupplierBatchSnapshot>> Projection = batch => new SupplierBatchSnapshot
    {
        Batch = batch,
        Status = StoredStatuses.Contains(batch.BatchStatus)
            || batch.GoodsReceipts.Any(receipt => receipt.ReceiptStatus == "COMMITTED")
            || batch.StatusHistories.Any(history => StoredStatuses.Contains(history.NewStatus)
                || (history.OldStatus != null && StoredStatuses.Contains(history.OldStatus)))
            ? "IN_STOCK" : batch.BatchStatus
    };
    private static readonly Func<ProductBatch, SupplierBatchSnapshot> Present = Projection.Compile();
    public static SupplierBatchSnapshot Get(ProductBatch batch) => Present(batch);

    public static List<BatchStatusHistory> VisibleHistory(List<BatchStatusHistory> history, DateTime? warehousedAt)
    {
        var chronological = history.OrderBy(item => item.ChangedAt).ThenBy(item => item.BatchStatusHistoryId).ToList();
        var storedIndex = chronological.FindIndex(item => StoredStatuses.Contains(item.NewStatus)
            || (item.OldStatus != null && StoredStatuses.Contains(item.OldStatus)));
        return chronological.Where((item, index) =>
                (storedIndex < 0 || index <= storedIndex)
                && !StoredStatuses.Skip(1).Contains(item.NewStatus)
                && (item.OldStatus == null || !StoredStatuses.Contains(item.OldStatus))
                && (warehousedAt == null || item.ChangedAt <= warehousedAt))
            .OrderByDescending(item => item.ChangedAt).ThenByDescending(item => item.BatchStatusHistoryId).ToList();
    }
}

public class SupplierBatchSnapshot
{
    public ProductBatch Batch { get; init; } = null!;
    public string Status { get; init; } = string.Empty;
}
