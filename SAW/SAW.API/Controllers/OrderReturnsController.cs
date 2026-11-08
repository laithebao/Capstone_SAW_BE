using System.Security.Claims;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
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

    // Historical records remain readable; fresh produce is never returned to stock.
    [HttpPost("{orderId:long}")]
    public IActionResult RequestReturn() => StatusCode(StatusCodes.Status410Gone,
        ApiResponse<object>.Failure(StatusCodes.Status410Gone, "Luồng trả hàng đã dừng. Vui lòng dùng chức năng từ chối nhận hàng và đính kèm ảnh bằng chứng."));

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        var ready = await db.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.ORDER_RETURN', N'U') IS NULL THEN 0 ELSE 1 END AS Value").SingleAsync(ct);
        if (ready == 0) throw new ConflictException("Chức năng trả hàng chưa sẵn sàng. Vui lòng liên hệ quản trị hệ thống để cập nhật dữ liệu.");
    }
}

public sealed record ReturnLot(string? BatchCode, string? ProductName, decimal WeightKg, decimal WholeLotPrice);
public sealed record OrderReturnView(long PurchaseOrderId, string OrderCode, string Status, string Reason,
    string? ReviewNote, string? ReceiptNote, DateTime RequestedAt, DateTime? ReviewedAt, DateTime? ReceivedAt, List<ReturnLot> Lots);
