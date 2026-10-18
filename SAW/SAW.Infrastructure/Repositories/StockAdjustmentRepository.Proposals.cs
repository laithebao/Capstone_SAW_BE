using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    public async Task ActAsync(long id, AdjustmentProposalRequest request, int actorId, CancellationToken ct)
    {
        if (id <= 0 || request.ExpectedVersion <= 0 || request.Action is not ("SAVE" or "SEND" or "WITHDRAW" or "CLEAR"))
            throw new BadRequestException("Thao tác hoặc phiên bản không hợp lệ.");
        if (request.Message?.Trim().Length > 1000 || request.Reason?.Trim().Length > 1000)
            throw new BadRequestException("Nội dung tối đa 1000 ký tự.");
        await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db => {
            await EnsureAuthorityAsync(db, actorId, "WAREHOUSE_MANAGER", ct);
            var hash = Hash(JsonSerializer.Serialize(new { id, request }));
            if (await ReplayAsync(db, actorId, request.RequestId, hash, ct)) return true;
            var r = await LockResolutionAsync(db, id, request.ExpectedVersion, ct);
            var a = r.StockAdjustment;
            if (r.CoordinatingStockAdjustmentId is { } coordinator && coordinator != id) throw new ConflictException("Mở phiếu đầu mối #" + coordinator + " để xử lý cả nhóm kiểm đếm.");
            var countScope = await CountScopeAsync(db, r, ct);
            var proposals = await db.StockAdjustmentProposals.Where(p => p.StockAdjustmentId == id).OrderByDescending(p => p.Revision).ToListAsync(ct);
            var before = JsonSerializer.Serialize(new { r.Version, r.State, r.ExpectedInventorySnapshotJson, r.ExpectedOrderSnapshotJson,
                Proposals = proposals.Select(p => new { p.StockAdjustmentProposalId, p.Revision, p.ProposalStatus, p.ProposedWeightKg, p.ProposedWholeLotPrice, p.ProposedOrderTotal, p.CustomerMessage, p.ResponseReason, p.WithdrawalReason }).ToList() });
            if (request.Action == "CLEAR") {
                if (r.State != "REJECTED_HELD" || string.IsNullOrWhiteSpace(request.Reason))
                    throw new ConflictException("Chỉ được xác nhận báo cáo sai sau khi từ chối phiếu và nhập lý do kiểm tra.");
                var batch = a.Inventory.ProductBatch;
                if (batch.ExpiryDate < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)) || batch.BatchStatus != "IN_STOCK")
                    throw new ConflictException("Không thể mở lại lô hết hạn hoặc không đủ điều kiện sử dụng.");
                // Explicit false-report clearance uses a fresh review snapshot, never revives a cancelled order.
                var view = await ReadReviewAsync(db, id, ct);
                if (request.ExpectedSnapshot != view.ReviewSnapshot || a.SystemQuantity != view.CurrentStocks.Sum(x => x.SystemQuantityKg))
                    throw new ConflictException("Tồn đã đổi; cần lập phiếu thay thế để kiểm tra lại.");
                await EnsureResolutionEvidenceAsync(db, r, ct);
                if (await BatchHandlingEligibility.CompletedQcReasonAsync(db, batch.ProductBatchId, batch.QualityGrade, ct) is { } qcReason) throw new ConflictException(qcReason);
                if (countScope.Any(x => x.State != "REJECTED_HELD")) throw new ConflictException("Nhóm phiếu đã có thay thế hoặc thay đổi; cần xử lý các phiếu còn lại.");
                foreach (var member in countScope) await EnsureResolutionEvidenceAsync(db, member, ct);
                MoveCountScope(countScope, r, "CLEARED", true); r.ResolutionReason = request.Reason.Trim();
            }
            else {
                if (a.AdjustmentStatus != "PENDING" || a.RequestKind != "COUNT" || a.ActualQuantity >= a.SystemQuantity || r.PurchaseOrderId == null)
                    throw new ConflictException("Chỉ phiếu hao hụt liên quan đơn đang xử lý mới có đề xuất khách hàng.");
                await EnsureCountScopeAsync(db, r, null, ct);
                if (request.Action == "WITHDRAW") {
                    var old = proposals.SingleOrDefault(p => p.StockAdjustmentProposalId == request.ProposalId && (p.ProposalStatus == "SENT" || p.ProposalStatus == "ACCEPTED" || p.ProposalStatus == "DRAFT"));
                    if (old == null || string.IsNullOrWhiteSpace(request.Reason)) throw new ConflictException("Đề xuất không còn hiệu lực hoặc thiếu lý do rút.");
                    old.ProposalStatus = "WITHDRAWN"; old.WithdrawalReason = request.Reason.Trim(); MoveCountScope(countScope, r, "PENDING_MANAGER", false);
                }
                else {
                    if (r.State != "PENDING_MANAGER" || proposals.Any(p => p.ProposalStatus is "SENT" or "ACCEPTED" or "DECLINED" or "FINALIZED"))
                        throw new ConflictException("Cần thu hồi đề xuất đang chờ trước khi thay đổi nội dung.");
                    if (request.WholeLotPrice is not > 0 || request.WholeLotPrice >= 10000000000000000m || decimal.Round(request.WholeLotPrice.Value, 2) != request.WholeLotPrice
                        || string.IsNullOrWhiteSpace(request.Message)) throw new BadRequestException("Nhập giá nguyên lô hợp lệ (tối đa 2 số lẻ) và nội dung gửi khách.");
                    var line = await db.OrderDetails.SingleAsync(l => l.OrderDetailId == r.OrderDetailId, ct);
                    var order = await db.PurchaseOrders.SingleAsync(o => o.PurchaseOrderId == r.PurchaseOrderId, ct);
                    if (order.OrderStatus is not ("APPROVED" or "RESERVED") || order.ApprovedAt == null) throw new ConflictException("Chỉ gửi đề xuất tiếp tục cho đơn đã duyệt và chưa soạn.");
                    if (order.OrderStatus == "CANCELLED") throw new ConflictException("Đơn đã hủy; không được gửi đề xuất mới.");
                    var view = await ReadReviewAsync(db, id, ct);
                    if (request.ExpectedSnapshot != view.ReviewSnapshot) throw new ConflictException("Phạm vi kiểm đếm đã thay đổi. Tải lại trước khi gửi đề xuất.");
                    var totalWeight = await db.Inventories.Where(i => i.ProductBatchId == a.Inventory.ProductBatchId).SumAsync(i => i.QuantityOnHand, ct) - countScope.Sum(x => x.StockAdjustment.SystemQuantity - x.StockAdjustment.ActualQuantity);
                    if (!PickingRules.Amount(totalWeight) || totalWeight >= line.ApprovedWeightKg && order.OrderStatus != "PENDING") throw new ConflictException("Khối lượng còn lại không hợp lệ cho đề xuất hao hụt.");
                    var draft = proposals.FirstOrDefault(p => p.ProposalStatus == "DRAFT");
                    if (draft == null) { draft = new StockAdjustmentProposal { StockAdjustmentId = id, Revision = proposals.Count == 0 ? 1 : proposals.Max(p => p.Revision) + 1, CreatedByAccountId = actorId, CreatedAt = DateTime.UtcNow }; db.StockAdjustmentProposals.Add(draft); proposals.Add(draft); }
                    draft.ScopeSnapshotJson = CountSnapshot(countScope);
                    draft.OriginalWeightKg = line.ApprovedWeightKg > 0 ? line.ApprovedWeightKg : line.RequestedWeightKg;
                    draft.ProposedWeightKg = totalWeight; draft.OriginalWholeLotPrice = line.UnitPrice; draft.ProposedWholeLotPrice = request.WholeLotPrice.Value;
                    draft.OriginalOrderTotal = order.TotalAmount; draft.ProposedOrderTotal = order.TotalAmount - line.UnitPrice + draft.ProposedWholeLotPrice;
                    if (draft.ProposedOrderTotal <= 0 || draft.ProposedOrderTotal >= 10000000000000000m) throw new BadRequestException("Tổng giá trị đề xuất vượt giới hạn tiền của đơn.");
                    draft.CustomerMessage = request.Message.Trim();
                    if (request.Action == "SEND") { draft.ProposalStatus = "SENT"; draft.SentAt = DateTime.UtcNow; MoveCountScope(countScope, r, "PENDING_CUSTOMER", true); }
                }
                var customer = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == r.PurchaseOrderId).Select(o => o.Distributor.AccountId).SingleAsync(ct);
                if (request.Action != "SAVE") Notify(db, customer, id, r.PurchaseOrderId, request.Action == "SEND" ? "Đề xuất thay đổi đơn hàng" : "Đã rút đề xuất thay đổi đơn", request.Message?.Trim() ?? request.Reason!.Trim(), true);
            }
            r.Version++; r.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); // Obtain proposal identity inside the same rollback boundary.
            RecordResolution(db, id, actorId, request.Action, request.RequestId, hash, before, new { r.Version, r.State, request.WholeLotPrice, request.Message, request.Reason, r.PurchaseOrderId, Proposals = proposals.Select(p => new { p.StockAdjustmentProposalId, p.Revision, p.ProposalStatus, p.ProposedWeightKg, p.ProposedWholeLotPrice }).ToList() });
            await db.SaveChangesAsync(ct); return true;
        }, ct);
    }
    public async Task<IReadOnlyList<AdjustmentResolutionDetail>> ForOrderAsync(long orderId, int actorId, CancellationToken ct)
    {
        if (orderId <= 0) throw new BadRequestException("Mã đơn hàng không hợp lệ.");
        await using var db = Context(); await EnsureAuthorityAsync(db, actorId, "DISTRIBUTOR", ct);
        if (!await db.PurchaseOrders.AnyAsync(o => o.PurchaseOrderId == orderId && o.Distributor.AccountId == actorId && o.Distributor.ProfileStatus == "ACTIVE", ct))
            throw new NotFoundException("Không tìm thấy đơn hàng của bạn.");
        var ids = await db.StockAdjustmentResolutions.Where(r => r.PurchaseOrderId == orderId && (r.CoordinatingStockAdjustmentId == null || r.CoordinatingStockAdjustmentId == r.StockAdjustmentId)).OrderByDescending(r => r.StockAdjustmentId).Select(r => r.StockAdjustmentId).ToListAsync(ct);
        var result = new List<AdjustmentResolutionDetail>();
        foreach (var id in ids) { var item = (await ResolutionDetailAsync(db, id, ct))!;
            result.Add(item with { Proposals = item.Proposals.Where(p => p.Status != "DRAFT").ToList(), Counts = null, CoordinatingAdjustmentId = null, Order = null, ReplacesStockAdjustmentId = null, ReplacementAdjustmentId = null, ResolutionReason = null, UpdatedBy = null }); }
        return result;
    }
    public async Task RespondAsync(long orderId, AdjustmentResponseRequest request, int actorId, CancellationToken ct)
    {
        if (orderId <= 0 || request.ProposalId <= 0 || request.Revision <= 0 || request.ExpectedVersion <= 0 || request.Decision is not ("ACCEPT" or "DECLINE" or "WITHDRAW") || request.Reason?.Trim().Length > 1000
            || request.Decision != "ACCEPT" && string.IsNullOrWhiteSpace(request.Reason)) throw new BadRequestException("Phản hồi không hợp lệ; từ chối/rút đồng ý phải có lý do.");
        await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db => {
            await EnsureAuthorityAsync(db, actorId, "DISTRIBUTOR", ct);
            if (!await db.PurchaseOrders.AnyAsync(o => o.PurchaseOrderId == orderId && o.Distributor.AccountId == actorId && o.Distributor.ProfileStatus == "ACTIVE", ct)) throw new NotFoundException("Không tìm thấy đơn hàng của bạn.");
            var hash = Hash(JsonSerializer.Serialize(new { orderId, request }));
            if (await ReplayAsync(db, actorId, request.RequestId, hash, ct)) return true;
            var proposal = await db.StockAdjustmentProposals.SingleOrDefaultAsync(p => p.StockAdjustmentProposalId == request.ProposalId && p.Resolution.PurchaseOrderId == orderId, ct)
                ?? throw new NotFoundException("Không tìm thấy đề xuất của đơn.");
            var r = await LockResolutionAsync(db, proposal.StockAdjustmentId, request.ExpectedVersion, ct);
            if (request.Revision != proposal.Revision || proposal.ProposalStatus != (request.Decision == "WITHDRAW" ? "ACCEPTED" : "SENT"))
                throw new ConflictException("Đề xuất đã thay đổi hoặc được phản hồi. Vui lòng tải lại.");
            await EnsureCountScopeAsync(db, r, proposal, ct);
            var countScope = await CountScopeAsync(db, r, ct);
            var before = JsonSerializer.Serialize(new { r.Version, r.State, r.ExpectedInventorySnapshotJson, r.ExpectedOrderSnapshotJson,
                proposal.StockAdjustmentProposalId, proposal.Revision, proposal.ProposalStatus, proposal.ResponseReason, proposal.RespondedByAccountId, proposal.RespondedAt, proposal.WithdrawalReason });
            if (request.Decision == "WITHDRAW") { proposal.ProposalStatus = "WITHDRAWN"; proposal.WithdrawalReason = request.Reason!.Trim(); MoveCountScope(countScope, r, "PENDING_MANAGER", false); }
            else {
                proposal.ProposalStatus = request.Decision == "ACCEPT" ? "ACCEPTED" : "DECLINED";
                proposal.RespondedAt = DateTime.UtcNow; proposal.RespondedByAccountId = actorId; proposal.ResponseReason = PickingRules.Text(request.Reason);
                MoveCountScope(countScope, r, request.Decision == "ACCEPT" ? "CUSTOMER_ACCEPTED" : "CUSTOMER_DECLINED", true);
                if (request.Decision == "DECLINE") {
                    await CancelResolutionOrderAsync(db, r, actorId, "Khách từ chối đề xuất: " + request.Reason!.Trim(), ct);
                    foreach (var member in countScope) await RefreshOwnedEvidenceAsync(db, member, ct);
                }
            }
            r.Version++; r.UpdatedAt = DateTime.UtcNow;
            RecordResolution(db, r.StockAdjustmentId, actorId, "CUSTOMER_" + request.Decision, request.RequestId, hash, before, new { proposal.StockAdjustmentProposalId, proposal.Revision, r.State, request.Reason, r.ExpectedInventorySnapshotJson, r.ExpectedOrderSnapshotJson });
            await NotifyWarehouseAsync(db, r.StockAdjustmentId, "Khách đã phản hồi đề xuất", request.Decision switch {
                "ACCEPT" => "Khách hàng đã đồng ý giá và khối lượng đề xuất. Vui lòng duyệt kết quả kiểm đếm để tiếp tục đơn.",
                "DECLINE" => "Khách hàng không tiếp tục mua. Đơn đã hủy; yêu cầu kiểm đếm vẫn chờ quản lý kho xử lý.",
                _ => "Khách hàng đã rút đồng ý. Đơn tiếp tục tạm dừng để kho xem xét lại đề xuất."
            }, ct, managersOnly: true, notificationType: "STOCK_ADJUSTMENT_CUSTOMER_RESPONSE");
            await db.SaveChangesAsync(ct); return true;
        }, ct);
    }
    private static async Task<StockAdjustmentResolution> LockResolutionAsync(AppDbContext db, long id, int version, CancellationToken ct)
    {
        var r = await db.StockAdjustmentResolutions.FromSqlInterpolated($"SELECT * FROM STOCK_ADJUSTMENT_RESOLUTION WITH (UPDLOCK,HOLDLOCK) WHERE StockAdjustmentId={id}")
            .Include(x => x.StockAdjustment).ThenInclude(a => a.Inventory).ThenInclude(i => i.ProductBatch).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Không tìm thấy hồ sơ xử lý điều chỉnh.");
        if (r.Version != version) throw new ConflictException("Hồ sơ đã thay đổi. Vui lòng tải lại trước khi quyết định.");
        return r;
    }
    private static async Task EnsureResolutionEvidenceAsync(AppDbContext db, StockAdjustmentResolution r, CancellationToken ct)
    {
        var a = r.StockAdjustment;
        var stocks = await db.Inventories.AsNoTracking().Include(i => i.WarehouseLocation).Where(i => i.ProductBatchId == a.Inventory.ProductBatchId && (r.PurchaseOrderId != null || a.RequestKind == "DISPOSAL" || i.InventoryId == a.InventoryId)).ToListAsync(ct);
        if (Hash(await SnapshotJsonAsync(db, a.Inventory.ProductBatch, stocks, ct)) != Hash(r.ExpectedInventorySnapshotJson)
            || r.PurchaseOrderId is { } oid && r.ExpectedOrderSnapshotJson != await OrderSnapshotAsync(db, oid, ct))
            throw new ConflictException("Tồn kho, đơn hoặc phần giữ hàng đã thay đổi ngoài hồ sơ. Cần rà soát snapshot.");
        if (r.PurchaseOrderId is { } orderId) {
            var related = await RelatedOrdersAsync(db, a.Inventory.ProductBatchId, ct);
            var reason = await OrderScopeReasonAsync(db, a.Inventory.ProductBatchId, related, ct);
            if (reason != null) throw new ConflictException(reason);
            if (await db.InventoryReservations.AnyAsync(x => x.OrderDetail.PurchaseOrderId == orderId && (x.PickedQuantity > 0 || x.PickingHistories.Any()), ct)
                || await db.GoodsIssues.AnyAsync(x => x.PurchaseOrderId == orderId && x.IssueStatus == "COMMITTED", ct)) throw new ConflictException("Đơn đã soạn/xuất; không được xử lý bằng luồng này.");
        }
    }
    private static async Task RefreshOwnedEvidenceAsync(AppDbContext db, StockAdjustmentResolution r, CancellationToken ct)
    {
        // Only called immediately after this workflow's own reservation release. Original evidence remains immutable.
        var a = r.StockAdjustment;
        var batch = await db.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == a.Inventory.ProductBatchId, ct);
        var stocks = await db.Inventories.AsNoTracking().Include(i => i.WarehouseLocation).Where(i => i.ProductBatchId == batch.ProductBatchId && (r.PurchaseOrderId != null || a.RequestKind == "DISPOSAL" || i.InventoryId == a.InventoryId)).ToListAsync(ct);
        r.ExpectedInventorySnapshotJson = await SnapshotJsonAsync(db, batch, stocks, ct);
        r.ExpectedOrderSnapshotJson = r.PurchaseOrderId is { } oid ? await OrderSnapshotAsync(db, oid, ct) : null;
    }
}
