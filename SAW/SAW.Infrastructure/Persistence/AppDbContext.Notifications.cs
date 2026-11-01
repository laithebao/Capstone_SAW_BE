using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence;

public partial class AppDbContext
{
    // Notifications are inserted by the same SaveChanges as the state transition.
    // Reads, retries after a committed operation, and read receipts do not emit events.
    private async Task AppendWorkflowNotificationsAsync(CancellationToken ct)
    {
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is ProductBatch or PurchaseOrder or SupplyContract or StockAdjustment
                or StockAdjustmentResolution or StockAdjustmentProposal or OrderReturn or OrderDeliveryRefusal or BatchSaleOffer).ToList();
        if (entries.Count == 0) return;
        var users = await Accounts.AsNoTracking().Where(a => a.AccountStatus == "ACTIVE")
            .Select(a => new { a.AccountId, a.Role.RoleCode }).ToListAsync(ct);
        var emitted = new HashSet<string>();
        void Send(IEnumerable<int> ids, string type, string title, string message, string entity, long id)
        {
            foreach (var account in ids.Distinct().Where(id => users.Any(a => a.AccountId == id)))
            {
                var key = $"{account}:{type}:{entity}:{id}:{message}";
                if (!emitted.Add(key)) continue;
                QueueWorkflowNotification(account, type, title, message, entity, id);
            }
        }
        int[] Roles(params string[] roles) => users.Where(a => roles.Contains(a.RoleCode)).Select(a => a.AccountId).ToArray();
        async Task<int[]> Customer(long orderId) => await PurchaseOrders.Where(o => o.PurchaseOrderId == orderId)
            .Select(o => o.Distributor.AccountId).ToArrayAsync(ct);
        foreach (var e in entries)
        {
            bool Changed(string name) => e.State == EntityState.Added || e.Property(name).IsModified
                && !Equals(e.Property(name).OriginalValue, e.Property(name).CurrentValue);
            switch (e.Entity)
            {
                case ProductBatch b when e.State == EntityState.Deleted || Changed(nameof(ProductBatch.BatchStatus)):
                    var state = e.State == EntityState.Deleted ? "DELETED" : b.BatchStatus;
                    await QueueBatchStatusNotificationsAsync(b, state, ct);
                    break;
                case BatchSaleOffer pricedOffer when pricedOffer.ProductBatchId > 0 && pricedOffer.WholeLotPrice > 0
                    && Changed(nameof(BatchSaleOffer.WholeLotPrice)):
                    var pricedBatchCode = await ProductBatches.Where(b => b.ProductBatchId == pricedOffer.ProductBatchId)
                        .Select(b => b.BatchCode).SingleAsync(ct);
                    var salePrice = pricedOffer.WholeLotPrice.ToString("#,0.##", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));
                    Send(Roles("WAREHOUSE_MANAGER"), "SALE_PRICE_UPDATED", "Đã cập nhật giá bán nguyên lô",
                        $"Lô {pricedBatchCode}: giá bán nguyên lô {salePrice} đ. {(pricedOffer.IsPublished ? "Đang công bố bán." : "Chưa công bố bán.")}",
                        "SALE_OFFER", pricedOffer.ProductBatchId);
                    break;
                case BatchSaleOffer offer when !offer.IsPublished && (Changed(nameof(BatchSaleOffer.IsPublished)) || Changed(nameof(BatchSaleOffer.SourceStockAdjustmentId))):
                    Send(Roles("WAREHOUSE_MANAGER"), "LOT_PRICING", "Lô cần kiểm tra và công bố lại giá",
                        $"Lô #{offer.ProductBatchId} đã dừng bán. Kiểm tra phiếu xử lý và khối lượng trước khi định giá lại.", "SALE_OFFER", offer.ProductBatchId);
                    break;
                case PurchaseOrder o when Changed(nameof(PurchaseOrder.OrderStatus)) && o.OrderStatus is not ("DELIVERY_REFUSED" or "PARTIALLY_REFUSED")
                    || o.OrderStatus == "PARTIALLY_REFUSED" && Changed(nameof(PurchaseOrder.ReceivedAt)):
                    var orderLabel = o.OrderStatus switch { "PENDING" => "Chờ duyệt", "CANCELLED" => "Đã hủy", "APPROVED" => "Đã duyệt", "RESERVED" => "Đã giữ hàng",
                        "PARTIALLY_PICKED" => "Đang soạn hàng", "PICKED" => "Đã soạn đủ hàng", "PREPARED" => "Sẵn sàng giao", "PARTIALLY_ISSUED" => "Đã xuất một phần",
                        "ISSUED" => "Đã xuất kho", "DISPATCHED" => "Đang giao", "DELIVERED" => "Khách đã nhận hàng", "PARTIALLY_REFUSED" => "Khách đã nhận các lô còn lại; có lô bị từ chối", "REJECTED" => "Bị từ chối", "RETURNED" => "Đã nhận hàng trả", _ => o.OrderStatus };
                    var customer = await Distributors.Where(d => d.DistributorId == o.DistributorId).Select(d => d.AccountId).ToArrayAsync(ct);
                    Send(customer, "ORDER_STATUS", $"Đơn {o.OrderCode}: {orderLabel}", $"Đơn {o.OrderCode}: {orderLabel}. {o.RejectionReason ?? o.CancellationReason}", "PURCHASE_ORDER", o.PurchaseOrderId);
                    Send(Roles("WAREHOUSE_MANAGER"), "ORDER_STATUS", $"Đơn {o.OrderCode}: {orderLabel}", $"Đơn {o.OrderCode}: {orderLabel}.", "PURCHASE_ORDER", o.PurchaseOrderId);
                    if (o.OrderStatus is "APPROVED" or "RESERVED" or "PICKED") Send(Roles("OPERATION_STAFF"), "ORDER_FULFILLMENT", "Đơn cần giữ/soạn/xuất hàng", $"Đơn {o.OrderCode}: {orderLabel}. Kiểm tra bước tiếp theo trước khi xuất hàng.", "PICKING_ORDER", o.PurchaseOrderId);
                    break;
                case SupplyContract c when Changed(nameof(SupplyContract.ContractStatus)) || Changed(nameof(SupplyContract.Revision)):
                    var supplierIds = await Suppliers.Where(s => s.SupplierId == c.SupplierId).Select(s => s.AccountId).ToArrayAsync(ct);
                    var contractLabel = c.ContractStatus switch { "PENDING_SUPPLIER" => "Chờ nhà cung cấp xác nhận", "PENDING_ADMIN" => "Chờ Admin duyệt",
                        "ACTIVE" => "Admin đã duyệt", "APPROVED" => "Admin đã duyệt", "ADMIN_REJECTED" => "Admin từ chối", "SUPPLIER_REJECTED" => "Nhà cung cấp từ chối",
                        "CANCELLATION_REQUESTED" => "Yêu cầu hủy hợp đồng", "PENDING_ADMIN_CANCELLATION" => "Chờ Admin duyệt hủy", "CANCELLATION_REJECTED" => "Từ chối yêu cầu hủy", "REJECTED" => "Bị từ chối", "CANCELLED" => "Đã hủy", _ => c.ContractStatus };
                    Send(supplierIds.Concat(Roles("ADMINISTRATOR")).Append(c.WarehouseManagerAccountId), "CONTRACT_STATUS", $"Hợp đồng {c.ContractCode}",
                        $"Hợp đồng {c.ContractCode}: {contractLabel}. {c.SupplierRejectionReason ?? c.AdminRejectionReason ?? c.CancellationReason}", "SUPPLY_CONTRACT", c.SupplyContractId);
                    break;
                case StockAdjustment a when Changed(nameof(StockAdjustment.AdjustmentStatus)):
                    var adjustmentLabel = a.AdjustmentStatus switch { "PENDING" => "Chờ quản lý xử lý", "APPROVED" => "Đã duyệt", "REJECTED" => "Đã từ chối", _ => a.AdjustmentStatus };
                    var adjustmentMessage = $"Phiếu {a.AdjustmentCode} ({(a.RequestKind == "DISPOSAL" ? "loại bỏ lô" : "kiểm đếm/hao hụt")}): {adjustmentLabel}. {a.Reason} {a.ReviewNote}";
                    Send(Roles("WAREHOUSE_MANAGER", "OPERATION_STAFF").Append(a.CreatedByAccountId), "STOCK_ADJUSTMENT", "Lô cần giữ kho / xử lý điều chỉnh", adjustmentMessage, "STOCK_ADJUSTMENT", a.StockAdjustmentId);
                    var batchId = await Inventories.Where(i => i.InventoryId == a.InventoryId).Select(i => i.ProductBatchId).FirstOrDefaultAsync(ct);
                    var affectedCustomers = await OrderDetails.Where(l => l.RequestedProductBatchId == batchId && l.PurchaseOrder.OrderStatus != "CANCELLED" && l.PurchaseOrder.OrderStatus != "REJECTED")
                        .Select(l => l.PurchaseOrder.Distributor.AccountId).Distinct().ToArrayAsync(ct);
                    Send(affectedCustomers, "ORDER_INCIDENT", "Lô trong đơn có sự cố tồn kho", "Kho đang xử lý hao hụt/hư hỏng của lô trong đơn. Vui lòng xem đơn và phản hồi đề xuất nếu có.", "CUSTOMER_ORDERS", 0);
                    break;
                case StockAdjustmentResolution r when Changed(nameof(StockAdjustmentResolution.State)):
                    var creator = await StockAdjustments.Where(a => a.StockAdjustmentId == r.StockAdjustmentId).Select(a => a.CreatedByAccountId).ToArrayAsync(ct);
                    var resolutionLabel = r.State switch { "PENDING_MANAGER" => "Chờ quản lý xử lý", "PENDING_CUSTOMER" => "Chờ khách phản hồi", "CUSTOMER_ACCEPTED" => "Khách đã đồng ý",
                        "CUSTOMER_DECLINED" => "Khách đã từ chối", "RESOLVED" => "Đã xử lý xong", "CLEARED" => "Đã gỡ giữ lô", "REPLACED" => "Đã thay thế phiếu", _ => "Kho đang xử lý" };
                    Send(creator.Concat(Roles("WAREHOUSE_MANAGER", "OPERATION_STAFF")), "ADJUSTMENT_RESOLUTION", "Cập nhật xử lý phiếu điều chỉnh", $"Phiếu #{r.StockAdjustmentId}: {resolutionLabel}. {r.ResolutionReason}", "STOCK_ADJUSTMENT", r.StockAdjustmentId);
                    if (r.PurchaseOrderId is long affectedOrder) Send(await Customer(affectedOrder), "ORDER_INCIDENT", "Cập nhật xử lý sự cố đơn hàng", $"Kho đã cập nhật xử lý sự cố: {resolutionLabel}. Vui lòng mở đơn để xem thông tin.", "PURCHASE_ORDER", affectedOrder);
                    break;
                case StockAdjustmentProposal p when p.SentAt != null && p.ProposalStatus != "DRAFT" && Changed(nameof(StockAdjustmentProposal.ProposalStatus)):
                    var resolution = p.Resolution ?? await StockAdjustmentResolutions.FindAsync([p.StockAdjustmentId], ct);
                    var proposalLabel = p.ProposalStatus switch { "SENT" => "Cần khách phản hồi", "ACCEPTED" => "Khách đã đồng ý", "DECLINED" => "Khách đã từ chối", "WITHDRAWN" => "Kho đã thu hồi đề xuất", "FINALIZED" => "Đã áp dụng điều chỉnh", _ => "Đã cập nhật" };
                    if (resolution?.PurchaseOrderId is long proposedOrder)
                        Send((await Customer(proposedOrder)).Concat(Roles("WAREHOUSE_MANAGER")), "ORDER_PROPOSAL", "Đề xuất xử lý đơn hàng", $"Trạng thái đề xuất: {proposalLabel}. {p.CustomerMessage}", "PURCHASE_ORDER", proposedOrder);
                    break;
                case OrderDeliveryRefusal refusal when e.State == EntityState.Added:
                    Send((await Customer(refusal.PurchaseOrderId)).Concat(Roles("WAREHOUSE_MANAGER")), "DELIVERY_REFUSAL",
                        $"Từ chối lô trong đơn {refusal.PurchaseOrder.OrderCode}",
                        $"Lô bị từ chối: {string.Join(", ", refusal.Lots.Select(l => l.OrderDetail.RequestedProductBatch?.BatchCode ?? l.OrderDetailId.ToString()))}. Lý do: {refusal.Reason}. Có {refusal.Images.Count} ảnh bằng chứng. Các bên xử lý bên ngoài, không nhập hàng lại kho.",
                        "PURCHASE_ORDER", refusal.PurchaseOrderId);
                    break;
                case OrderReturn r when Changed(nameof(OrderReturn.Status)):
                    var recipients = (await Customer(r.PurchaseOrderId)).Concat(Roles("WAREHOUSE_MANAGER"));
                    if (r.Status is "APPROVED" or "RECEIVED") recipients = recipients.Concat(Roles("OPERATION_STAFF"));
                    var returnLabel = r.Status switch { "REQUESTED" => "Chờ quản lý duyệt trả hàng", "APPROVED" => "Đã duyệt trả hàng, chờ staff kiểm nhận", "REJECTED" => "Từ chối trả hàng", "RECEIVED" => "Đã kiểm nhận hàng trả", _ => r.Status };
                    Send(recipients, "ORDER_RETURN", "Cập nhật yêu cầu trả hàng", $"Đơn #{r.PurchaseOrderId}: {returnLabel}. {r.Reason} {r.ReviewNote} {r.ReceiptNote}", "ORDER_RETURN", r.PurchaseOrderId);
                    break;
            }
        }
    }

    // ExecuteUpdate bypasses the change tracker. Its caller queues the same notifications
    // inside the existing transaction, then persists them with its history/audit receipt.
    internal async Task QueueBatchStatusNotificationsAsync(ProductBatch batch, string status, CancellationToken ct)
    {
        var label = status switch { "PENDING_QC" => "Chờ kiểm định", "APPROVED_FOR_STORAGE" => "Đạt QC, chờ nhập kho",
            "IN_STOCK" => "Đã nhập kho", "REJECTED" => "Bị từ chối", "SUBMITTED" => "Chờ kiểm nhận", "PENDING_PREDECLARATION" => "Chờ kiểm nhận",
            "QUARANTINE" => "Tạm giữ, chờ xử lý", "CANCELLED" => "Đã hủy", "ISSUED" => "Đã xuất kho", "PARTIALLY_ISSUED" => "Đã xuất một phần",
            "DELETED" => "Đã xóa", "DISPOSED" => "Đã xử lý loại bỏ", _ => status };
        var recipientRole = status switch
        {
            "PENDING_QC" => "QC_STAFF",
            "APPROVED_FOR_STORAGE" or "SUBMITTED" or "PENDING_PREDECLARATION" or "QUARANTINE" => "OPERATION_STAFF",
            "IN_STOCK" => "WAREHOUSE_MANAGER",
            _ => null
        };
        var supplierAccountId = await Suppliers.Where(s => s.SupplierId == batch.SupplierId)
            .Select(s => (int?)s.AccountId).SingleOrDefaultAsync(ct);
        var recipients = await Accounts.AsNoTracking().Where(a => a.AccountStatus == "ACTIVE"
            && (a.AccountId == supplierAccountId || recipientRole != null && a.Role.RoleCode == recipientRole))
            .Select(a => new { a.AccountId, a.Role.RoleCode }).ToListAsync(ct);
        foreach (var recipient in recipients)
        {
            if (recipient.AccountId == supplierAccountId)
                QueueWorkflowNotification(recipient.AccountId, "BATCH_STATUS", $"Lô {batch.BatchCode}: {label}",
                    $"Lô {batch.BatchCode} – {batch.ProductName}: {label}. {batch.RejectionReason}", "PRODUCT_BATCH", batch.ProductBatchId);
            if (recipient.RoleCode != recipientRole) continue;
            if (status == "PENDING_QC")
                QueueWorkflowNotification(recipient.AccountId, "QC_REQUIRED", "Có lô chờ kiểm định", $"Lô {batch.BatchCode} đang chờ QC.", "QC_QUEUE", batch.ProductBatchId);
            else if (recipientRole == "OPERATION_STAFF")
                QueueWorkflowNotification(recipient.AccountId, "BATCH_ACTION", "Có lô cần xử lý", $"Lô {batch.BatchCode}: {label}.", "PRODUCT_BATCH", batch.ProductBatchId);
            else if (status == "IN_STOCK")
                QueueWorkflowNotification(recipient.AccountId, "LOT_PRICING", "Có lô nhập kho cần kiểm tra giá bán",
                    $"Lô {batch.BatchCode} đã nhập kho. Kiểm tra điều kiện bán và công bố giá nguyên lô.", "SALE_OFFER", batch.ProductBatchId);
        }
    }

    internal async Task QueueGoodsReceiptNotificationAsync(GoodsReceipt receipt, int actorId, CancellationToken ct)
    {
        if (receipt.ReceiptStatus != "COMMITTED") return;
        var batchCode = await ProductBatches.Where(b => b.ProductBatchId == receipt.ProductBatchId)
            .Select(b => b.BatchCode).SingleAsync(ct);
        var weight = receipt.WeightInKg.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));
        await QueueWarehouseDocumentNotificationAsync("GOODS_RECEIPT", receipt.GoodsReceiptId, receipt.OperationAccountId, actorId,
            "GOODS_RECEIPT_COMMITTED", "Đã xác nhận nhập kho",
            $"Phiếu {receipt.ReceiptCode}, lô {batchCode}: đã xác nhận nhập {weight} kg vào kho.", ct);
    }

    internal async Task QueueGoodsIssueNotificationAsync(GoodsIssue issue, int actorId, CancellationToken ct)
    {
        if (issue.IssueStatus != "COMMITTED") return;
        var orderCode = await PurchaseOrders.Where(o => o.PurchaseOrderId == issue.PurchaseOrderId)
            .Select(o => o.OrderCode).SingleAsync(ct);
        await QueueWarehouseDocumentNotificationAsync("GOODS_ISSUE", issue.GoodsIssueId, issue.OperationAccountId, actorId,
            "GOODS_ISSUE_COMMITTED", "Đã xác nhận xuất kho",
            $"Phiếu {issue.IssueCode}, đơn {orderCode}: đã xác nhận xuất kho theo phiếu.", ct);
    }

    private async Task QueueWarehouseDocumentNotificationAsync(string entity, long id, int creatorId, int actorId,
        string type, string title, string message, CancellationToken ct)
    {
        // Called only after the document has an ID, inside the operation's transaction.
        // Drafts notify warehouse participants, never suppliers/distributors as if stock had moved.
        var recipients = await Accounts.AsNoTracking().Where(a => a.AccountStatus == "ACTIVE"
            && (a.AccountId == creatorId || a.AccountId == actorId || a.Role.RoleCode == "WAREHOUSE_MANAGER"))
            .Select(a => a.AccountId).ToArrayAsync(ct);
        foreach (var recipient in recipients) QueueWorkflowNotification(recipient, type, title, message, entity, id);
    }

    private void QueueWorkflowNotification(int accountId, string type, string title, string message, string entity, long id)
    {
        var entityId = id > 0 ? id.ToString() : null;
        message = message.Length > 1500 ? message[..1500] : message;
        if (Notifications.Local.Any(n => Entry(n).State == EntityState.Added && n.AccountId == accountId
            && n.NotificationType == type && n.RelatedEntityType == entity && n.RelatedEntityId == entityId && n.Message == message)) return;
        Notifications.Add(new Notification { AccountId = accountId, NotificationType = type,
            Title = title, Message = message, RelatedEntityType = entity, RelatedEntityId = entityId,
            IsRead = false, CreatedAt = DateTime.UtcNow });
    }
}
