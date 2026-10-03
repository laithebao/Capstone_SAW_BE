using System.Text.Json.Serialization;

namespace SAW.Application.Features.GoodsReceipts.Dtos;

public sealed record GoodsReceiptQuery(string? Search = null, int? SupplierId = null,
    int? WarehouseLocationId = null, DateOnly? FromDate = null, DateOnly? ToDate = null,
    string? Status = null, string? SortBy = "receivedAtDesc", int Page = 1, int PageSize = 10);
public sealed record GoodsReceiptPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record GoodsReceiptOption(int Id, string Name);
public sealed record GoodsReceiptLocation(int Id, string Name, bool IsActive, decimal? MaxWeightKg);
public sealed record GoodsReceiptFilters(IReadOnlyList<GoodsReceiptOption> Suppliers,
    IReadOnlyList<GoodsReceiptLocation> Locations);
public sealed record GoodsReceiptBatch(long Id, string BatchCode, string ProductName, int SupplierId,
    string SupplierName, decimal Quantity, string Unit, decimal WeightInKg, string QualityGrade);
public sealed record GoodsReceiptDetail(long Id, string ReceiptCode, long ProductBatchId,
    string BatchCode, string ProductName, int SupplierId, string SupplierName,
    decimal ReceivedQuantity, string Unit, decimal WeightInKg, int WarehouseLocationId,
    string LocationName, DateOnly ReceivedDate, string ReceiptStatus, DateTime? CommittedAt,
    string? Note, string OperationStaffName, GoodsReceiptSnapshot Snapshot);
// Exact DB timestamp (including time/precision) is preserved for optimistic comparison.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoodsReceiptSnapshot(int WarehouseLocationId, DateTime ReceivedAt, string? Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateGoodsReceiptDraftRequest(int WarehouseLocationId, DateOnly ReceivedDate,
    string? Note, GoodsReceiptSnapshot ExpectedSnapshot);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmGoodsReceiptRequest(GoodsReceiptSnapshot ExpectedSnapshot);
// Quantity, unit, weight and actor are deliberately absent: they come from DB/claims.
public sealed record CreateGoodsReceiptRequest(long ProductBatchId, int WarehouseLocationId,
    DateOnly ReceivedDate, string? Note);
