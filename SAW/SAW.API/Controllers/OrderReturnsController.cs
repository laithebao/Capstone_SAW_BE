using System.Security.Claims;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController, Route("api/order-returns")]
[Authorize(Roles = "DISTRIBUTOR")]
public sealed class OrderReturnsController(AppDbContext db) : ControllerBase
{
    private static readonly Expression<Func<OrderReturn, OrderReturnView>> View = r => new OrderReturnView(
        r.PurchaseOrderId, r.PurchaseOrder.OrderCode, r.Status, r.Reason, r.ReviewNote, r.ReceiptNote, r.RequestedAt, r.ReviewedAt, r.ReceivedAt,
        r.PurchaseOrder.OrderDetails.Select(l => new ReturnLot(l.RequestedProductBatch!.BatchCode, l.RequestedProductBatch.ProductName,
            l.ApprovedWeightKg, l.UnitPrice)).ToList());
    private int Actor => int.Parse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private IQueryable<OrderReturn> Visible => db.OrderReturns.Where(r => r.PurchaseOrder.Distributor.AccountId == Actor);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return Ok(ApiResponse<object>.Success(await Visible.AsNoTracking()
        .OrderBy(r => r.Status == "REQUESTED" || r.Status == "APPROVED" ? 0 : 1).ThenByDescending(r => r.RequestedAt)
        .Select(View).Take(100).ToListAsync(ct)));
    }

    [HttpGet("{orderId:long}")]
    public async Task<IActionResult> Get(long orderId, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        if (!await db.PurchaseOrders.AnyAsync(o => o.PurchaseOrderId == orderId &&
            o.Distributor.AccountId == Actor, ct)) throw new NotFoundException("Không tìm thấy đơn hàng.");
        return Ok(ApiResponse<object?>.Success(await Visible.AsNoTracking().Where(r => r.PurchaseOrderId == orderId)
            .Select(View).SingleOrDefaultAsync(ct)));
    }

    [HttpPost("{orderId:long}"), Authorize(Roles = "DISTRIBUTOR")]
    public Task<IActionResult> RequestReturn(long orderId, ReturnNote request, CancellationToken ct) => Mutate(orderId, async order =>
    {
        if (order.Distributor.AccountId != Actor) throw new NotFoundException("Không tìm thấy đơn hàng của bạn.");
        var reason = OrderReturnRules.RequiredNote(request.Note);
        var existing = await db.OrderReturns.SingleOrDefaultAsync(r => r.PurchaseOrderId == orderId, ct);
        if (existing != null)
        {
            if (existing.RequestedByAccountId == Actor && existing.Reason == reason) return;
            throw new ConflictException("Đơn đã có yêu cầu trả hàng. Vui lòng xem kết quả xử lý.");
        }
        OrderReturnRules.EnsureRequestable(order.OrderStatus);
        if (!await db.GoodsIssues.AnyAsync(i => i.PurchaseOrderId == orderId && i.IssueStatus == "COMMITTED", ct))
            throw new ConflictException("Đơn chưa có phiếu xuất kho hoàn tất.");
        db.OrderReturns.Add(new OrderReturn { PurchaseOrderId = orderId, PurchaseOrder = order, Reason = reason,
            RequestedByAccountId = Actor, RequestedAt = DateTime.UtcNow });
    }, ct);

    private async Task<IActionResult> Mutate(long id, Func<PurchaseOrder, Task> action, CancellationToken ct)
    {
        if (id <= 0) throw new BadRequestException("Mã đơn không hợp lệ.");
        await EnsureReadyAsync(ct);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var order = await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM PURCHASE_ORDER WITH (UPDLOCK,HOLDLOCK) WHERE PurchaseOrderID={id}")
                .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
            await db.Entry(order).Reference(o => o.Distributor).LoadAsync(ct);
            await action(order);
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'AccountID', @value={Actor}", ct);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            finally { await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL", CancellationToken.None); }
            await tx.CommitAsync(ct);
            return (IActionResult)Ok(ApiResponse<object>.Success(new { order.PurchaseOrderId }, "Đã gửi yêu cầu trả hàng. Vui lòng chờ kho xử lý."));
        });
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        var ready = await db.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.ORDER_RETURN', N'U') IS NULL THEN 0 ELSE 1 END AS Value").SingleAsync(ct);
        if (ready == 0) throw new ConflictException("Chức năng trả hàng chưa sẵn sàng. Vui lòng liên hệ quản trị hệ thống để cập nhật dữ liệu.");
    }
}

public sealed record ReturnNote(string? Note);
public sealed record ReturnLot(string? BatchCode, string? ProductName, decimal WeightKg, decimal WholeLotPrice);
public sealed record OrderReturnView(long PurchaseOrderId, string OrderCode, string Status, string Reason,
    string? ReviewNote, string? ReceiptNote, DateTime RequestedAt, DateTime? ReviewedAt, DateTime? ReceivedAt, List<ReturnLot> Lots);
