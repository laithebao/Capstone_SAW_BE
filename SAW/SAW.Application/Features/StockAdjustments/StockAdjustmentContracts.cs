using System.Text.Json.Serialization;

namespace SAW.Application.Features.StockAdjustments;

public sealed record StockAdjustmentQuery(string? Search = null, long? ProductBatchId = null, int? WarehouseLocationId = null,
    string? RequestKind = null, string? AdjustmentType = null, string? Status = null, DateOnly? FromDate = null,
    DateOnly? ToDate = null, string SortBy = "createdDesc", int Page = 1, int PageSize = 20);
public sealed record AdjustmentPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record AdjustmentBatch(long Id, string BatchCode, string ProductName, DateOnly? ExpiryDate, decimal QuantityKg);
public sealed record AdjustmentLocation(int Id, string Code);
public sealed record AdjustmentStock(long InventoryId, int WarehouseLocationId, string LocationCode, string LocationStatus,
    decimal SystemQuantityKg, decimal ReservedQuantityKg, DateTime LastUpdatedAt);
public sealed record AdjustmentScope(AdjustmentBatch Batch, IReadOnlyList<AdjustmentStock> Stocks, string Snapshot,
    bool CanRequest, string? LockReason);
public sealed record AdjustmentSummary(long Id, string AdjustmentCode, string RequestKind, string AdjustmentType,
    long ProductBatchId, string BatchCode, string ProductName, string Locations, decimal SystemQuantity,
    decimal ActualQuantity, decimal DifferenceQuantity, string Status, string CreatedBy, DateTime CreatedAt);
public sealed record AdjustmentLine(long InventoryId, int WarehouseLocationId, string LocationCode, decimal SystemQuantity,
    decimal ActualQuantity, decimal DifferenceQuantity, decimal? ReservedQuantity, DateTime? InventoryUpdatedAt);
public sealed record AdjustmentImage(Guid Id, string Url);
public sealed record AdjustmentDetail(AdjustmentSummary Summary, string Reason, string? ReviewedBy, DateTime? ReviewedAt,
    string? ReviewNote, IReadOnlyList<AdjustmentLine> Lines, IReadOnlyList<AdjustmentImage> Images);
public sealed record AdjustmentReservation(long Id, string OrderCode, string Status, decimal ReservedQuantityKg, decimal PickedQuantityKg);
public sealed record AdjustmentReviewDetail(AdjustmentDetail Request, string BatchStatus, IReadOnlyList<AdjustmentStock> CurrentStocks,
    IReadOnlyList<AdjustmentReservation> Reservations, string ReviewSnapshot, bool CanApprove, string? ApprovalLockReason,
    bool CanReject, bool HasOriginalSnapshot);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewStockAdjustmentRequest(Guid RequestId, string Decision, string? ReviewNote, string ExpectedSnapshot);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateStockAdjustmentRequest(Guid RequestId, long ProductBatchId, string RequestKind,
    long? InventoryId, decimal? ActualQuantityKg, string AdjustmentType, string Reason, string ExpectedSnapshot,
    IReadOnlyList<Guid> ImageIds);

public interface IStockAdjustmentRepository
{
    Task<AdjustmentPage<AdjustmentSummary>> SearchAsync(StockAdjustmentQuery query, CancellationToken ct);
    Task<AdjustmentDetail> GetAsync(long id, CancellationToken ct);
    Task<AdjustmentPage<AdjustmentBatch>> BatchesAsync(string? search, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<AdjustmentLocation>> LocationsAsync(CancellationToken ct);
    Task<AdjustmentScope> ScopeAsync(long batchId, string kind, long? inventoryId, CancellationToken ct);
    Task<long> CreateAsync(CreateStockAdjustmentRequest request, int actorId, CancellationToken ct);
    Task<AdjustmentImage> UploadAsync(byte[] content, int actorId, CancellationToken ct);
    Task RemoveUploadAsync(Guid imageId, int actorId, CancellationToken ct);
    Task<AdjustmentReviewDetail> GetReviewAsync(long id, CancellationToken ct);
    Task ReviewAsync(long id, ReviewStockAdjustmentRequest request, int actorId, CancellationToken ct);
}
