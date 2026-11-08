using System.Text.Json.Serialization;

namespace SAW.Application.Features.GoodsIssues;

public sealed record GoodsIssueQuery(string? Search = null, string? Status = null, int? WarehouseLocationId = null,
    DateOnly? FromDate = null, DateOnly? ToDate = null, string SortBy = "issuedAtDesc", int Page = 1, int PageSize = 20);
public sealed record GoodsIssuePage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record GoodsIssueLocation(int Id, string Name);
public sealed record GoodsIssueSummary(long Id, string IssueCode, long PurchaseOrderId, string OrderCode,
    string DistributorName, decimal WeightInKg, DateOnly IssuedDate, string OperationStaffName, string IssueStatus);
public sealed record GoodsIssueAllocation(long ReservationId, long OrderDetailId, long InventoryId, long ProductBatchId,
    string BatchCode, string ProductName, int WarehouseLocationId, string LocationName, DateOnly? ExpiryDate,
    decimal ApprovedWeightKg, decimal ReservedWeightKg, decimal PickedWeightKg, decimal IssuedWeightKg,
    decimal RemainingWeightKg, decimal QuantityOnHandKg, bool CanIssue, string? LockReason);
public sealed record GoodsIssueOrder(long Id, string OrderCode, string DistributorName, string Status,
    IReadOnlyList<GoodsIssueAllocation> Allocations);
public sealed record GoodsIssueLine(long Id, long ReservationId, long OrderDetailId, long InventoryId,
    long ProductBatchId, string BatchCode, string ProductName, int WarehouseLocationId, string LocationName,
    decimal IssuedQuantity, string Unit, decimal WeightInKg);
public sealed record GoodsIssueHistory(long Id, string Action, DateTime RecordedAt, string? ActorName, string? Description);
public sealed record GoodsIssueDetail(GoodsIssueSummary Summary, string? ReceiverName, string? Note,
    DateTime? CommittedAt, string Snapshot, bool CanEdit, string? LockReason,
    IReadOnlyList<GoodsIssueLine> Lines, IReadOnlyList<GoodsIssueHistory> History);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoodsIssueLineRequest(long ReservationId, decimal WeightInKg);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateGoodsIssueRequest(Guid RequestId, long PurchaseOrderId, DateOnly IssuedDate,
    string? ReceiverName, string? Note, IReadOnlyList<GoodsIssueLineRequest> Lines);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateGoodsIssueDraftRequest(Guid RequestId, string ExpectedSnapshot, DateOnly IssuedDate,
    string? ReceiverName, string? Note, IReadOnlyList<GoodsIssueLineRequest> Lines);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmGoodsIssueRequest(Guid RequestId, string ExpectedSnapshot);

public interface IGoodsIssueRepository
{
    Task<GoodsIssuePage<GoodsIssueSummary>> SearchAsync(GoodsIssueQuery query, CancellationToken ct);
    Task<GoodsIssueDetail> GetAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<GoodsIssueLocation>> LocationsAsync(CancellationToken ct);
    Task<GoodsIssuePage<GoodsIssueOrder>> EligibleAsync(string? search, int page, int pageSize, CancellationToken ct);
    Task<GoodsIssueOrder> OrderAsync(long id, CancellationToken ct);
    Task<long> CreateAsync(CreateGoodsIssueRequest request, int actorId, CancellationToken ct);
    Task UpdateDraftAsync(long id, UpdateGoodsIssueDraftRequest request, int actorId, CancellationToken ct);
    Task ConfirmAsync(long id, ConfirmGoodsIssueRequest request, int actorId, CancellationToken ct);
}
