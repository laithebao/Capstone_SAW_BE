using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Application.Features.Picking;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    private sealed record ReviewReceipt(Guid RequestId, string PayloadHash, long AdjustmentId, string Decision, object? Values);

    public async Task ReviewAsync(long id, ReviewStockAdjustmentRequest request, int actorId, CancellationToken ct)
    {
        await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db => {
            // Check current DB authority as well as the controller's authenticated role claims.
            if (!await db.Accounts.AnyAsync(a => a.AccountId == actorId && a.AccountStatus == "ACTIVE"
                    && a.Role.RoleCode == "WAREHOUSE_MANAGER" && a.Role.IsActive, ct))
                throw new ForbiddenException("Chỉ quản lý kho đang hoạt động được duyệt hoặc từ chối yêu cầu.");
            var key = "stock-adjustment-review:" + request.RequestId;
            var lockResult = await db.Database.SqlQuery<int>($"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource={key},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r AS Value;").ToListAsync(ct);
            if (lockResult.Single() < 0) throw new ConflictException("Quyết định đang được xử lý. Vui lòng kiểm tra lại kết quả hoặc thử lại cùng dữ liệu.");
            // Same warehouse capacity lock as Confirm Goods Receipt; it also prevents phantom
            // inventory locations and protects against writers without an application lock.
            await db.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX,HOLDLOCK)", ct);
            var reference = await db.StockAdjustments.AsNoTracking().Where(a => a.StockAdjustmentId == id)
                .Select(a => new { a.Inventory.ProductBatchId }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy phiếu điều chỉnh.");
            var batch = await WarehouseOperationTransaction.LockBatchAsync(db, reference.ProductBatchId, ct);
            var a = await db.StockAdjustments.FromSqlInterpolated($"SELECT * FROM STOCK_ADJUSTMENT WITH (UPDLOCK,HOLDLOCK) WHERE StockAdjustmentID={id}").SingleAsync(ct);
            var payload = Hash(JsonSerializer.Serialize(new { Id = id, Request = request }));
            var operationId = request.RequestId.ToString();
            var audits = await db.AuditLogs.AsNoTracking().Where(l => l.EntityName == "STOCK_ADJUSTMENT" && l.ActionType == "REVIEW_STOCK_ADJUSTMENT"
                && l.NewDataJson != null && l.NewDataJson.Contains(operationId)).ToListAsync(ct);
            foreach (var log in audits)
            {
                var receipt = JsonSerializer.Deserialize<ReviewReceipt>(log.NewDataJson!);
                if (receipt?.RequestId != request.RequestId) continue;
                if (log.AccountId != actorId || receipt.PayloadHash != payload || receipt.AdjustmentId != id)
                    throw new ConflictException("Mã thao tác đã được dùng cho quyết định khác.");
                return true;
            }
            if (a.AdjustmentStatus != "PENDING") throw new ConflictException("Yêu cầu đã được người khác xử lý. Vui lòng tải lại kết quả.");
            var stock = await WarehouseOperationTransaction.LockInventoryAsync(db, batch.ProductBatchId, ct);
            foreach (var locationId in stock.Select(i => i.WarehouseLocationId).Distinct().Order())
                await db.WarehouseLocations.FromSqlInterpolated($"SELECT * FROM WAREHOUSE_LOCATION WITH (UPDLOCK,HOLDLOCK) WHERE WarehouseLocationID={locationId}").LoadAsync(ct);
            await db.WarehouseSettings.FromSqlRaw("SELECT * FROM WAREHOUSE_SETTING WITH (UPDLOCK,HOLDLOCK)").LoadAsync(ct);
            var ids = stock.Select(i => i.InventoryId).ToArray();
            await db.InventoryReservations.FromSqlRaw("SELECT * FROM INVENTORY_RESERVATION WITH (UPDLOCK,HOLDLOCK)")
                .Where(r => ids.Contains(r.InventoryId)).OrderBy(r => r.InventoryReservationId).LoadAsync(ct);
            var before = await ReadReviewAsync(db, id, ct);
            var offerBefore = await db.BatchSaleOffers.AsNoTracking().Where(o => o.ProductBatchId == batch.ProductBatchId)
                .Select(o => new { o.WholeLotPrice, o.SourceStockAdjustmentId, o.RepricedForAdjustmentId, o.IsPublished }).SingleOrDefaultAsync(ct);
            if (before.ReviewSnapshot != request.ExpectedSnapshot)
                throw new ConflictException("Yêu cầu hoặc số liệu đang xem đã thay đổi. Vui lòng tải lại và kiểm tra trước khi quyết định.");
            var resolution = await db.StockAdjustmentResolutions.Include(r => r.StockAdjustment).ThenInclude(x => x.Inventory).ThenInclude(x => x.ProductBatch)
                .SingleOrDefaultAsync(r => r.StockAdjustmentId == id, ct);
            var reviewScope = resolution == null ? new List<StockAdjustmentResolution>() : await CountScopeAsync(db, resolution, ct);
            if (resolution?.CoordinatingStockAdjustmentId is { } parentId && parentId != id) throw new ConflictException("Cần xử lý nhóm kiểm đếm tại phiếu #" + parentId);
            if (resolution != null && request.Decision == "APPROVE") {
                foreach (var member in reviewScope) await EnsureResolutionEvidenceAsync(db, member, ct);
                if (resolution.PurchaseOrderId != null && a.RequestKind == "COUNT") {
                    var proposal = await db.StockAdjustmentProposals.Where(p => p.StockAdjustmentId == id && (p.ProposalStatus == "ACCEPTED" || p.ProposalStatus == "DECLINED")).OrderByDescending(p => p.Revision).FirstOrDefaultAsync(ct);
                    await EnsureCountScopeAsync(db, resolution, proposal, ct);
                }
            }
            var now = DateTime.UtcNow;
            if (request.Decision == "APPROVE")
            {
                if (!before.CanApprove) throw new ConflictException(before.ApprovalLockReason!);
                if (resolution?.PurchaseOrderId != null && a.RequestKind == "DISPOSAL") {
                    await CancelResolutionOrderAsync(db, resolution, actorId, "Kho đã xác nhận lô hỏng và duyệt loại bỏ. Đơn hàng không thể tiếp tục.", ct);
                    foreach (var item in stock) await db.Entry(item).ReloadAsync(ct);
                }
                if (resolution?.State == "CUSTOMER_ACCEPTED" && a.RequestKind == "COUNT")
                    await FinalizeAcceptedOrderAsync(db, resolution, actorId, ct);
                var selected = a.RequestKind == "DISPOSAL" ? stock : stock.Where(i => reviewScope.Count > 0 ? reviewScope.Any(x => x.StockAdjustment.InventoryId == i.InventoryId) : i.InventoryId == a.InventoryId).ToList();
                foreach (var i in selected) { i.QuantityOnHand = a.RequestKind == "DISPOSAL" ? 0 : reviewScope.Count > 0 ? reviewScope.Single(x => x.StockAdjustment.InventoryId == i.InventoryId).StockAdjustment.ActualQuantity : a.ActualQuantity; i.LastUpdatedAt = now; }
                if (!PickingRules.Amount(stock.Sum(i => i.QuantityOnHand), true)) throw new ConflictException("Tổng tồn lô vượt giới hạn decimal(18,3).");
                var oldStatus = batch.BatchStatus;
                var historyId = await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId).MaxAsync(h => (long?)h.BatchStatusHistoryId, ct) ?? 0;
                if (a.RequestKind == "DISPOSAL") { batch.BatchStatus = "DISPOSED"; batch.UpdatedAt = now; }
                // Required ordering: persist inventory + DISPOSED BEFORE the request guard
                // observes APPROVED. All saves remain within the same rollback boundary.
                await db.SaveChangesAsync(ct);
                if (a.RequestKind == "DISPOSAL" && !await db.BatchStatusHistories.AnyAsync(h => h.ProductBatchId == batch.ProductBatchId
                        && h.BatchStatusHistoryId > historyId && h.NewStatus == "DISPOSED", ct))
                {
                    db.BatchStatusHistories.Add(new BatchStatusHistory { ProductBatchId = batch.ProductBatchId, OldStatus = oldStatus,
                        NewStatus = "DISPOSED", ChangedByAccountId = actorId, ChangedAt = now, ChangeReason = "Duyệt loại bỏ " + a.AdjustmentCode });
                }
            }
            if (resolution != null) {
                MoveCountScope(reviewScope, resolution, request.Decision == "APPROVE" ? "RESOLVED" : "REJECTED_HELD", true);
                foreach (var member in reviewScope.Where(x => x.StockAdjustmentId != id)) {
                    member.State = request.Decision == "APPROVE" ? "RESOLVED" : "REJECTED_HELD"; member.UpdatedAt = now; member.ResolutionReason = request.ReviewNote;
                    if (request.Decision == "APPROVE") member.ResolvedBatchWeightKg = stock.Sum(i => i.QuantityOnHand);
                    member.StockAdjustment.AdjustmentStatus = request.Decision == "APPROVE" ? "APPROVED" : "REJECTED";
                    member.StockAdjustment.ReviewedByAccountId = actorId; member.StockAdjustment.ReviewedAt = now; member.StockAdjustment.ReviewNote = request.ReviewNote;
                    db.AuditLogs.Add(new AuditLog { AccountId = actorId, EntityName = "STOCK_ADJUSTMENT", EntityId = member.StockAdjustmentId.ToString(), ActionType = "GROUP_REVIEW_STOCK_ADJUSTMENT", CreatedAt = now,
                        Description = "Quyết định cùng phiếu đầu mối #" + id, NewDataJson = JsonSerializer.Serialize(new { request.RequestId, ParentId = id, member.State, member.StockAdjustment.AdjustmentStatus, request.ReviewNote }) });
                }
                resolution.State = request.Decision == "APPROVE" ? "RESOLVED" : "REJECTED_HELD";
                if (request.Decision == "APPROVE" && a.RequestKind == "COUNT" && a.ActualQuantity < a.SystemQuantity) resolution.ResolvedBatchWeightKg = stock.Sum(i => i.QuantityOnHand);
                resolution.Version++; resolution.UpdatedAt = now; resolution.ResolutionReason = request.ReviewNote;
                if (request.Decision == "REJECT") {
                    foreach (var proposal in await db.StockAdjustmentProposals.Where(p => p.StockAdjustmentId == id && (p.ProposalStatus == "SENT" || p.ProposalStatus == "ACCEPTED" || p.ProposalStatus == "DRAFT")).ToListAsync(ct)) {
                        proposal.ProposalStatus = "WITHDRAWN"; proposal.WithdrawalReason = "Kho thu hồi đề xuất để kiểm tra lại số liệu.";
                    }
                }
                var offer = await db.BatchSaleOffers.SingleOrDefaultAsync(o => o.ProductBatchId == batch.ProductBatchId, ct);
                var proposalPrice = request.Decision == "APPROVE" && a.RequestKind == "COUNT"
                    ? await db.StockAdjustmentProposals.Where(p => p.StockAdjustmentId == id && (p.ProposalStatus == "DECLINED" || p.ProposalStatus == "FINALIZED"))
                        .OrderByDescending(p => p.Revision).Select(p => (decimal?)p.ProposedWholeLotPrice).FirstOrDefaultAsync(ct) : null;
                if (offer == null && proposalPrice is > 0) {
                    offer = new BatchSaleOffer { ProductBatchId = batch.ProductBatchId, WholeLotPrice = proposalPrice.Value };
                    db.BatchSaleOffers.Add(offer);
                }
                if (offer != null) { offer.IsPublished = false; offer.UpdatedAt = now;
                    if (request.Decision == "APPROVE" && a.RequestKind == "COUNT") {
                        offer.SourceStockAdjustmentId = id; offer.ApprovedRemainderWeightKg = stock.Sum(i => i.QuantityOnHand); offer.RepricedForAdjustmentId = null;
                        if (proposalPrice is > 0) { offer.WholeLotPrice = proposalPrice.Value; offer.RepricedForAdjustmentId = id; }
                    }
                }
                if (resolution.PurchaseOrderId is { } relatedOrder) {
                    var customer = await db.PurchaseOrders.Where(o => o.PurchaseOrderId == relatedOrder && o.OrderStatus != "CANCELLED")
                        .Select(o => (int?)o.Distributor.AccountId).SingleOrDefaultAsync(ct);
                    if (customer is { } recipient) Notify(db, recipient, id, relatedOrder, "Kết quả kiểm tra lô của đơn",
                        request.Decision == "REJECT" ? "Kho cần kiểm tra lại; đơn chưa được tiếp tục." : "Kho đã hoàn tất xử lý phiếu điều chỉnh tồn.", true, "STOCK_ADJUSTMENT_REVIEW");
                }
                await NotifyWarehouseAsync(db, id, request.Decision == "APPROVE" ? "Đã duyệt điều chỉnh tồn" : "Phiếu bị từ chối, lô vẫn tạm dừng",
                    request.Decision == "REJECT" ? "Yêu cầu chưa được chấp thuận. Lô vẫn tạm dừng cho đến khi được kiểm tra và xử lý lại."
                        : proposalPrice is > 0 ? "Tồn kho và giá lô đã được cập nhật theo đề xuất. Vui lòng mở chi tiết để xem kết quả xử lý."
                        : "Quản lý kho đã duyệt yêu cầu và cập nhật tồn kho. Vui lòng mở chi tiết để xem kết quả.", ct);
            }
            a.AdjustmentStatus = request.Decision == "APPROVE" ? "APPROVED" : "REJECTED";
            a.ReviewedByAccountId = actorId; a.ReviewedAt = now; a.ReviewNote = request.ReviewNote;
            var effectiveOrder = resolution?.PurchaseOrderId is { } oid ? await OrderSnapshotAsync(db, oid, ct) : null;
            var amendments = resolution?.PurchaseOrderId is { } amendedOrder ? await db.OrderAmendments.AsNoTracking().Where(m => m.OrderDetail.PurchaseOrderId == amendedOrder).Select(m => new { m.OrderAmendmentId, m.StockAdjustmentProposalId, m.PreviousWeightKg, m.EffectiveWeightKg, m.PreviousWholeLotPrice, m.EffectiveWholeLotPrice }).ToListAsync(ct) : null;
            var invalidatedDrafts = await db.GoodsIssueInvalidations.AsNoTracking().Where(i => i.StockAdjustmentId == id).Select(i => i.GoodsIssueId).ToListAsync(ct);
            var effectiveOffer = db.BatchSaleOffers.Local.Where(o => o.ProductBatchId == batch.ProductBatchId)
                .Select(o => new { o.WholeLotPrice, o.SourceStockAdjustmentId, o.RepricedForAdjustmentId, o.IsPublished }).SingleOrDefault();
            db.AuditLogs.Add(new AuditLog { AccountId = actorId, EntityName = "STOCK_ADJUSTMENT", EntityId = id.ToString(),
                ActionType = "REVIEW_STOCK_ADJUSTMENT", CreatedAt = now, OldDataJson = JsonSerializer.Serialize(before),
                NewDataJson = JsonSerializer.Serialize(new ReviewReceipt(request.RequestId, payload, id, request.Decision,
                    new { EffectiveOrder = effectiveOrder, Amendments = amendments, InvalidatedDrafts = invalidatedDrafts, SaleOfferBefore = offerBefore, SaleOfferAfter = effectiveOffer, ResolutionState = resolution?.State, a.AdjustmentStatus, a.ReviewedByAccountId, a.ReviewedAt, a.ReviewNote, batch.BatchStatus,
                        Inventory = stock.OrderBy(i => i.InventoryId).Select(i => new { i.InventoryId, i.QuantityOnHand, i.ReservedQuantity, i.LastUpdatedAt }).ToList() })),
                Description = (request.Decision == "REJECT" ? "Từ chối yêu cầu; không thay đổi tồn. " : a.RequestKind == "DISPOSAL" ? "Duyệt loại bỏ toàn bộ lô. " : "Duyệt kiểm đếm; gán tồn bằng số thực đếm. ") + request.ReviewNote });
            await db.SaveChangesAsync(ct); return true;
        }, ct);
    }
}
