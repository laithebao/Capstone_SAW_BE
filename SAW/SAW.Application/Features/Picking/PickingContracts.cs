using System.Text.Json.Serialization;

namespace SAW.Application.Features.Picking;

public sealed record PickingQuery(string? Search = null, string? Status = null, string Sort = "approved_desc",
    int Page = 1, int PageSize = 20, DateOnly? FromDate = null, DateOnly? ToDate = null);
public sealed record PickingPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record PickingOrderSummary(long Id, string OrderCode, string DistributorName, DateTime? ApprovedAt,
    DateOnly? ExpectedDeliveryDate, string Status, decimal ApprovedWeightKg, decimal ReservedWeightKg,
    decimal PickedWeightKg, bool CanPick, string? PickingLockReason);
public sealed record PickingHistoryItem(long Id, decimal QuantityKg, DateTime PickedAt, string PickedByName, string? Note);
public sealed record PickingReservation(long Id, long InventoryId, long ProductBatchId, string BatchCode,
    string ProductName, int WarehouseLocationId, string LocationName, DateOnly? ExpiryDate, string Status,
    decimal ReservedQuantityKg, decimal PickedQuantityKg, bool CanPick, string? PickingLockReason,
    IReadOnlyList<PickingHistoryItem> History);
public sealed record PickingOrderLine(long Id, string CropName, long? RequestedProductBatchId, string? RequestedBatchCode, string? ProductName,
    decimal ApprovedWeightKg, decimal ReservedWeightKg, decimal PickedWeightKg, IReadOnlyList<PickingReservation> Reservations);
public sealed record PickingOrderDetail(PickingOrderSummary Order, IReadOnlyList<PickingOrderLine> Lines, IReadOnlyList<PickingAdjustmentWork>? Adjustments = null);
public sealed record PickingAdjustmentWork(long Id, string Code, string State);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmPickingRequest(Guid RequestId, long ReservationId, long OrderDetailId,
    long InventoryId, long ProductBatchId, decimal QuantityKg, decimal ExpectedPickedQuantityKg, string? Note);
public interface IPickingRepository
{
    Task<PickingPage<PickingOrderSummary>> SearchAsync(PickingQuery query, CancellationToken ct);
    Task<PickingOrderDetail> GetAsync(long id, CancellationToken ct);
    Task ConfirmAsync(long orderId, ConfirmPickingRequest request, int actorId, CancellationToken ct);
}
public interface IOrderReservationAllocator
{
    // False means approval is saved, but allocation was not possible; no partial reservation is retained.
    Task<bool> TryReserveAsync(long orderId, int actorId, CancellationToken ct);
}
public interface IPickingService
{
    Task<PickingPage<PickingOrderSummary>> SearchAsync(PickingQuery query, CancellationToken ct);
    Task<PickingOrderDetail> GetAsync(long id, CancellationToken ct);
    Task<PickingOrderDetail> ConfirmAsync(long orderId, ConfirmPickingRequest request, int actorId, CancellationToken ct);
}
