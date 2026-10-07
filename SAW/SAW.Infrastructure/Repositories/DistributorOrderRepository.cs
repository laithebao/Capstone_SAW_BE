using System.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class DistributorOrderRepository(DbContextOptions<AppDbContext> options,
    IHttpContextAccessor accessor, TimeProvider? timeProvider = null) : IDistributorOrderRepository
{
    private AppDbContext Context() => new(options, accessor);
    public async Task<DistributorDashboard> DashboardAsync(int accountId, CancellationToken ct)
    {
        await using var db = Context();
        var owner = await OwnerAsync(db, accountId, ct);
        var rows = db.PurchaseOrders.AsNoTracking().Where(o => o.DistributorId == owner.DistributorId);
        // Aggregate all owned orders, independently of list pagination. Receipt confirmation defines success.
        var totals = await rows.GroupBy(o => o.DistributorId).Select(g => new
        {
            Pending = g.Count(o => o.OrderStatus == "PENDING"),
            Successful = g.Count(o => o.OrderStatus == "DELIVERED"),
            Cancelled = g.Count(o => o.OrderStatus == "CANCELLED"),
            Spent = g.Sum(o => o.OrderStatus == "DELIVERED" ? o.TotalAmount : 0m)
        }).SingleOrDefaultAsync(ct);
        var recent = await rows.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.PurchaseOrderId).Take(4)
            .Select(o => new DistributorOrderSummary(o.PurchaseOrderId, o.OrderCode, o.OrderStatus,
                o.OrderDetails.Count, o.TotalAmount, o.CreatedAt, o.ExpectedDeliveryDate)).ToListAsync(ct);
        return new(totals?.Pending ?? 0, totals?.Successful ?? 0, totals?.Cancelled ?? 0, totals?.Spent ?? 0m, recent);
    }
    private static async Task<Distributor> OwnerAsync(AppDbContext db, int accountId, CancellationToken ct) =>
        await db.Distributors.AsNoTracking().SingleOrDefaultAsync(d => d.AccountId == accountId
            && d.ProfileStatus == "ACTIVE" && d.Account.AccountStatus == "ACTIVE", ct)
        ?? throw new ForbiddenException("Hồ sơ nhà phân phối chưa hoạt động.");

    private static IQueryable<BatchSaleOffer> Saleable(AppDbContext db)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        return db.BatchSaleOffers.AsNoTracking().Where(o => o.IsPublished && o.WholeLotPrice > 0
            && o.ProductBatch.BatchStatus == "IN_STOCK"
            && o.ProductBatch.CropType.IsActive
            && (o.ProductBatch.ExpiryDate == null || o.ProductBatch.ExpiryDate >= today)
            && o.ProductBatch.VerifiedWeightInKg > 0
            && db.Inventories.Any(i => i.ProductBatchId == o.ProductBatchId)
            && !db.Inventories.Any(i => i.ProductBatchId == o.ProductBatchId
                && (i.ReservedQuantity != 0 || i.Unit != "kg" || i.WarehouseLocation.LocationStatus != "ACTIVE"))
            && db.Inventories.Where(i => i.ProductBatchId == o.ProductBatchId).Sum(i => i.QuantityOnHand)
                == o.ProductBatch.VerifiedWeightInKg
            // A lot with an order still in progress is temporarily unavailable to every distributor.
            // Rejected/cancelled orders release the lot; all other order states keep it off the catalog.
            && !db.OrderDetails.Any(l => l.RequestedProductBatchId == o.ProductBatchId
                && l.PurchaseOrder.OrderStatus != "REJECTED" && l.PurchaseOrder.OrderStatus != "CANCELLED")
            && !db.GoodsIssueDetails.Any(i => i.Inventory.ProductBatchId == o.ProductBatchId && i.GoodsIssue.IssueStatus == "COMMITTED")
            && db.QcInspections.Where(q => q.ProductBatchId == o.ProductBatchId)
                .OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId).Take(1)
                .Any(q => q.InspectionStatus == "COMPLETED" && q.CompletedAt >= q.StartedAt && q.QcResult == "PASS"
                    && q.QualityGrade == o.ProductBatch.QualityGrade
                    && (q.QualityGrade == "A" || q.QualityGrade == "B" || q.QualityGrade == "C" || q.QualityGrade == "D")));
    }

    public async Task<DistributorPage<CatalogLot>> CatalogAsync(int accountId, DistributorQuery query, CancellationToken ct)
    {
        await using var db = Context();
        await OwnerAsync(db, accountId, ct);
        var rows = Saleable(db);
        if (!string.IsNullOrWhiteSpace(query.Search)) rows = rows.Where(o => o.ProductBatch.BatchCode.Contains(query.Search)
            || o.ProductBatch.ProductName.Contains(query.Search) || o.ProductBatch.CropType.CropName.Contains(query.Search));
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(o => o.UpdatedAt).ThenByDescending(o => o.ProductBatchId)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(o => new CatalogLot(o.ProductBatchId, o.ProductBatch.BatchCode, o.ProductBatch.ProductName,
                o.ProductBatch.CropType.CropName, o.ProductBatch.Supplier.SupplierName, o.ProductBatch.GrowingArea.AreaName,
                o.ProductBatch.QualityGrade, o.ProductBatch.HarvestDate, o.ProductBatch.ExpiryDate,
                o.ProductBatch.VerifiedWeightInKg!.Value, o.WholeLotPrice)).ToListAsync(ct);
        return new(items, total, query.Page, query.PageSize);
    }

    public async Task<DistributorLotDetail> LotAsync(int accountId, long id, CancellationToken ct)
    {
        await using var db = Context();
        var owner = await OwnerAsync(db, accountId, ct);
        var saleable = await Saleable(db).AnyAsync(o => o.ProductBatchId == id, ct);
        // An ordered lot remains readable by its buyer after reservation/issue, but never becomes purchasable again.
        if (!saleable && !await db.OrderDetails.AnyAsync(l => l.RequestedProductBatchId == id
                && l.PurchaseOrder.DistributorId == owner.DistributorId, ct))
            throw new NotFoundException("Lô hàng không còn được chào bán hoặc không thuộc đơn hàng của bạn.");
        var lot = await db.ProductBatches.AsNoTracking().Include(b => b.CropType).Include(b => b.Supplier)
            .Include(b => b.GrowingArea).SingleOrDefaultAsync(b => b.ProductBatchId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy lô hàng.");
        var price = await db.BatchSaleOffers.Where(o => o.ProductBatchId == id).Select(o => (decimal?)o.WholeLotPrice).SingleOrDefaultAsync(ct);
        var qc = await db.QcInspections.AsNoTracking()
            .Include(q => q.InspectionStandardVersion).ThenInclude(v => v.InspectionStandardSet)
            .Where(q => q.ProductBatchId == id)
            .OrderByDescending(q => q.StartedAt).ThenByDescending(q => q.QcInspectionId).FirstOrDefaultAsync(ct);
        return new(lot.ProductBatchId, lot.BatchCode, lot.ProductName, lot.CropType.CropName, lot.CropType.CategoryName,
            lot.Supplier.SupplierName, lot.GrowingArea.AreaName, lot.GrowingArea.Province, lot.GrowingArea.District, lot.GrowingArea.Ward,
            lot.QualityGrade, lot.HarvestDate, lot.ExpiryDate, lot.VerifiedWeightInKg ?? lot.WeightInKg, price, lot.BatchStatus,
            saleable, lot.VerifiedPackagingType ?? lot.PackagingType, lot.VerifiedPackageCount ?? lot.PackageCount,
            lot.VerifiedPackageUnitWeightKg ?? lot.PackageUnitWeightKg, lot.ExpectedMinTempC, lot.ExpectedMaxTempC,
            lot.ExpectedMinHumidityPct, lot.ExpectedMaxHumidityPct, qc?.QcResult,
            qc?.CompletedAt is { } completed ? Utc(completed) : null,
            qc?.InspectionStandardVersion.InspectionStandardSet.StandardName,
            qc?.InspectionStandardVersion.VersionNo);
    }

    public async Task<DistributorPage<DistributorOrderSummary>> ListAsync(int accountId, DistributorQuery query, CancellationToken ct)
    {
        await using var db = Context();
        var owner = await OwnerAsync(db, accountId, ct);
        var rows = db.PurchaseOrders.AsNoTracking().Where(o => o.DistributorId == owner.DistributorId);
        if (!string.IsNullOrWhiteSpace(query.Search)) rows = rows.Where(o => o.OrderCode.Contains(query.Search)
            || o.OrderDetails.Any(l => l.RequestedProductBatch != null &&
                (l.RequestedProductBatch.BatchCode.Contains(query.Search) || l.RequestedProductBatch.ProductName.Contains(query.Search))));
        if (!string.IsNullOrEmpty(query.Status)) rows = rows.Where(o => o.OrderStatus == query.Status);
        // Orders are written in UTC; calendar filters use Vietnam local dates.
        if (query.FromDate is { } from) { var start = from.ToDateTime(TimeOnly.MinValue).AddHours(-7); rows = rows.Where(o => o.CreatedAt >= start); }
        if (query.ToDate is { } to) { var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue).AddHours(-7); rows = rows.Where(o => o.CreatedAt < end); }
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.PurchaseOrderId)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(o => new DistributorOrderSummary(o.PurchaseOrderId, o.OrderCode, o.OrderStatus,
                o.OrderDetails.Count, o.TotalAmount, o.CreatedAt, o.ExpectedDeliveryDate)).ToListAsync(ct);
        return new(items, total, query.Page, query.PageSize);
    }

    public async Task<DistributorOrderDetail> GetAsync(int accountId, long id, CancellationToken ct)
    {
        await using var db = Context();
        var owner = await OwnerAsync(db, accountId, ct);
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.OrderDetails).ThenInclude(l => l.RequestedProductBatch)
            .Include(o => o.StatusHistories).AsSplitQuery()
            .SingleOrDefaultAsync(o => o.PurchaseOrderId == id && o.DistributorId == owner.DistributorId, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn hàng của bạn.");
        var cancel = order.OrderStatus == "PENDING" && !await HasProcessingAsync(db, id, ct);
        var receipt = await CanReceiveAsync(db, order, ct);
        return new(order.PurchaseOrderId, order.OrderCode, order.OrderStatus, order.DeliveryAddress, order.ContactPhone,
            order.ExpectedDeliveryDate, order.OrderNote, order.TotalAmount, Utc(order.CreatedAt),
            order.ApprovedAt is { } at ? Utc(at) : null, order.RejectionReason, order.CancellationReason,
            order.ReceivedAt is { } received ? Utc(received) : null, cancel, receipt,
            order.OrderDetails.OrderBy(l => l.OrderDetailId).Select(l => new DistributorOrderLine(
                l.RequestedProductBatchId ?? 0, l.RequestedProductBatch?.BatchCode ?? "", l.RequestedProductBatch?.ProductName ?? "",
                l.RequestedWeightKg, l.UnitPrice)).ToList(),
            order.StatusHistories.OrderBy(h => h.ChangedAt).ThenBy(h => h.OrderStatusHistoryId)
                .Select(h => new DistributorOrderEvent(h.OldStatus, h.NewStatus, Utc(h.ChangedAt), h.ChangeReason)).ToList());
    }

    public async Task<DistributorOrderDetail> CreateAsync(int accountId, CreateDistributorOrderRequest request, CancellationToken ct)
    {
        var id = await TransactionAsync(accountId, async db =>
        {
            var owner = await OwnerAsync(db, accountId, ct);
            var existing = await db.PurchaseOrders.SingleOrDefaultAsync(o => o.RequestId == request.RequestId && o.DistributorId == owner.DistributorId, ct);
            if (existing is not null) return existing.PurchaseOrderId;
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var order = new PurchaseOrder { RequestId = request.RequestId, DistributorId = owner.DistributorId, OrderStatus = "PENDING",
                DeliveryAddress = request.DeliveryAddress, ContactPhone = request.ContactPhone,
                ExpectedDeliveryDate = request.ExpectedDeliveryDate, OrderNote = request.Note, CreatedAt = now.UtcDateTime };
            foreach (var selected in request.Lots.OrderBy(l => l.BatchId))
            {
                // Lock catalog, stock and eligibility inside the serializable transaction. No reservation at submission.
                await db.BatchSaleOffers.FromSqlInterpolated($"SELECT * FROM BATCH_SALE_OFFER WITH (UPDLOCK, HOLDLOCK) WHERE ProductBatchID={selected.BatchId}").LoadAsync(ct);
                await db.Inventories.FromSqlInterpolated($"SELECT * FROM INVENTORY WITH (UPDLOCK, HOLDLOCK) WHERE ProductBatchID={selected.BatchId}").LoadAsync(ct);
                var lot = await Saleable(db).Include(o => o.ProductBatch).SingleOrDefaultAsync(o => o.ProductBatchId == selected.BatchId, ct)
                    ?? throw new ConflictException("Một lô đã hết khả dụng, hết hạn hoặc ngừng bán. Vui lòng chọn lại lô.");
                if (lot.WholeLotPrice != selected.ExpectedPrice || lot.ProductBatch.VerifiedWeightInKg != selected.ExpectedWeightKg)
                    throw new ConflictException($"Giá hoặc khối lượng lô {lot.ProductBatch.BatchCode} đã thay đổi. Vui lòng tải lại danh sách.");
                order.OrderDetails.Add(new OrderDetail { CropTypeId = lot.ProductBatch.CropTypeId,
                    RequestedProductBatchId = selected.BatchId, RequestedQuantity = 1, Unit = "Lô",
                    RequestedWeightKg = lot.ProductBatch.VerifiedWeightInKg!.Value, UnitPrice = lot.WholeLotPrice });
            }
            order.SubtotalAmount = order.OrderDetails.Sum(l => l.UnitPrice);
            if (order.SubtotalAmount > 9999999999999999.99m) throw new BadRequestException("Tổng giá trị đơn vượt giới hạn lưu trữ.");
            order.TotalAmount = order.SubtotalAmount; // No online payment or tax calculation in these UCs.
            order.OrderCode = await NextOrderCodeAsync(db, DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).DateTime), ct);
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync(ct);
            await EnsureHistoryAsync(db, order.PurchaseOrderId, null, "PENDING", accountId, "Đặt mua nguyên lô.", ct);
            return order.PurchaseOrderId;
        }, ct);
        return await GetAsync(accountId, id, ct);
    }

    private static async Task<string> NextOrderCodeAsync(AppDbContext db, DateOnly date, CancellationToken ct)
    {
        // Date row/range lock is held until the order commits. Failed orders roll back the counter too.
        var numbers = await db.Database.SqlQuery<int>($"""
            DECLARE @number int;
            SELECT @number = LastNumber FROM dbo.PURCHASE_ORDER_DAILY_COUNTER WITH (UPDLOCK,HOLDLOCK) WHERE CodeDate={date};
            IF @number IS NULL
            BEGIN
                SET @number=1;
                INSERT dbo.PURCHASE_ORDER_DAILY_COUNTER (CodeDate,LastNumber) VALUES ({date},@number);
            END
            ELSE
            BEGIN
                IF @number=2147483647 THROW 50310,'Daily purchase order counter exhausted.',1;
                SET @number=@number+1;
                UPDATE dbo.PURCHASE_ORDER_DAILY_COUNTER SET LastNumber=@number WHERE CodeDate={date};
            END;
            SELECT @number AS [Value];
            """).ToListAsync(ct);
        return $"PO-{date:yyyyMMdd}-{numbers.Single():D2}";
    }

    public async Task<DistributorOrderDetail> CancelAsync(int accountId, long id, CancellationToken ct)
    {
        await TransactionAsync(accountId, async db =>
        {
            var order = await LockOwnedAsync(db, accountId, id, ct);
            if (order.OrderStatus == "CANCELLED") return true;
            if (order.OrderStatus != "PENDING" || await HasProcessingAsync(db, id, ct))
                throw new ConflictException("Chỉ được hủy đơn chờ duyệt và chưa được xử lý. Vui lòng tải lại đơn.");
            order.OrderStatus = "CANCELLED"; order.CancellationReason = null; order.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await EnsureHistoryAsync(db, id, "PENDING", "CANCELLED", accountId, null, ct);
            return true;
        }, ct);
        return await GetAsync(accountId, id, ct);
    }

    public async Task<DistributorOrderDetail> ConfirmReceiptAsync(int accountId, long id, CancellationToken ct)
    {
        await TransactionAsync(accountId, async db =>
        {
            var order = await LockOwnedAsync(db, accountId, id, ct);
            if (order.OrderStatus == "DELIVERED" && order.ReceivedAt is not null) return true;
            if (!await CanReceiveAsync(db, order, ct))
                throw new ConflictException("Chỉ được xác nhận nhận hàng khi đơn đã xuất đủ hàng hoặc đang giao.");
            var old = order.OrderStatus;
            order.OrderStatus = "DELIVERED"; order.ReceivedAt = DateTime.UtcNow;
            order.ReceivedByAccountId = accountId; order.UpdatedAt = order.ReceivedAt;
            await db.SaveChangesAsync(ct);
            await EnsureHistoryAsync(db, id, old, "DELIVERED", accountId, "Nhà phân phối xác nhận đã nhận đủ hàng.", ct);
            return true;
        }, ct);
        return await GetAsync(accountId, id, ct);
    }

    private static Task<bool> HasProcessingAsync(AppDbContext db, long id, CancellationToken ct) => db.PurchaseOrders
        .Where(o => o.PurchaseOrderId == id).AnyAsync(o => o.ApprovedAt != null || o.GoodsIssues.Any()
            || o.OrderDetails.Any(l => l.ApprovedQuantity > 0 || l.ApprovedWeightKg > 0 || l.InventoryReservations.Any()), ct);

    private static async Task<bool> CanReceiveAsync(AppDbContext db, PurchaseOrder order, CancellationToken ct)
    {
        if (order.OrderStatus is not ("ISSUED" or "DISPATCHED") || order.ReceivedAt is not null) return false;
        var lines = db.OrderDetails.Where(l => l.PurchaseOrderId == order.PurchaseOrderId);
        return await lines.AnyAsync(ct) && !await lines.AnyAsync(l => l.ApprovedWeightKg <= 0
            || l.ApprovedWeightKg != l.RequestedWeightKg
            || (l.GoodsIssueDetails.Where(i => i.GoodsIssue.IssueStatus == "COMMITTED").Sum(i => (decimal?)i.WeightInKg) ?? 0) != l.RequestedWeightKg, ct);
    }

    private static async Task<PurchaseOrder> LockOwnedAsync(AppDbContext db, int accountId, long id, CancellationToken ct)
    {
        var owner = await OwnerAsync(db, accountId, ct);
        return await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM PURCHASE_ORDER WITH (UPDLOCK, HOLDLOCK) WHERE PurchaseOrderID={id} AND DistributorID={owner.DistributorId}")
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng của bạn.");
    }

    private static async Task EnsureHistoryAsync(AppDbContext db, long id, string? old, string status, int actor, string reason, CancellationToken ct)
    {
        // Production DB already records transitions in a trigger. Do not duplicate its rows.
        if (!await db.OrderStatusHistories.AnyAsync(h => h.PurchaseOrderId == id && h.NewStatus == status, ct))
        {
            db.OrderStatusHistories.Add(new OrderStatusHistory { PurchaseOrderId = id, OldStatus = old,
                NewStatus = status, ChangedByAccountId = actor, ChangedAt = DateTime.UtcNow, ChangeReason = reason });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<T> TransactionAsync<T>(int actorId, Func<AppDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var strategyContext = Context();
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = Context();
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'AccountID', @value={actorId}", ct);
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var result = await action(db);
                await tx.CommitAsync(ct);
                return result;
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL", CancellationToken.None);
                await db.Database.CloseConnectionAsync();
            }
        });
    }
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
