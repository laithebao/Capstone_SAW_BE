using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.Notifications;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class NotificationInboxRepository(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor) : INotificationInboxRepository
{
    public async Task<NotificationInbox> ListAsync(int actorId, int page, int pageSize, CancellationToken ct)
    {
        if (page is < 1 or > 100000 || pageSize is < 1 or > 50) throw new BadRequestException("Phân trang thông báo không hợp lệ.");
        await using var db = new AppDbContext(options, accessor);
        var rows = await VisibleAsync(db, actorId, ct);
        var total = await rows.CountAsync(ct); var unread = await rows.CountAsync(n => !n.IsRead, ct);
        var items = await rows.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.NotificationId).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new InboxItem(n.NotificationId, n.Title, n.Message, n.RelatedEntityType, n.RelatedEntityId, n.IsRead, n.CreatedAt)).ToListAsync(ct);
        // Render older workflow notices as readable content without rewriting their stored evidence.
        var role = await db.Accounts.Where(a => a.AccountId == actorId).Select(a => a.Role.RoleCode).SingleOrDefaultAsync(ct);
        return new(items.Select(item => Readable(item, role)).ToList(), unread, total, page, pageSize);
    }
    private static async Task<IQueryable<Notification>> VisibleAsync(AppDbContext db, int actorId, CancellationToken ct)
    {
        var role = await db.Accounts.Where(a => a.AccountId == actorId && a.AccountStatus == "ACTIVE" && a.Role.IsActive)
            .Select(a => a.Role.RoleCode).SingleOrDefaultAsync(ct);
        var rows = db.Notifications.AsNoTracking().Where(n => n.AccountId == actorId);
        if (role == null) return rows.Where(n => false);
        if (role != "WAREHOUSE_MANAGER") rows = rows.Where(n => n.NotificationType != "STOCK_ADJUSTMENT_CUSTOMER_RESPONSE"
            && n.Title != "Có phiếu điều chỉnh tồn cần xử lý"
            && !(n.RelatedEntityType == "STOCK_ADJUSTMENT" && n.Title == "Khách đã phản hồi đề xuất"));
        if (role == "OPERATION_STAFF") rows = rows.Where(n => n.RelatedEntityType != "DISTRIBUTOR_ORDER" && n.NotificationType != "STOCK_ADJUSTMENT_REVIEW");
        if (role == "DISTRIBUTOR") rows = rows.Where(n => n.RelatedEntityType != "STOCK_ADJUSTMENT" && n.NotificationType != "STOCK_ADJUSTMENT_STAFF_RESULT");
        if (role == "DISTRIBUTOR") rows = rows.Where(n => !((n.NotificationType == "STOCK_ADJUSTMENT_REVIEW"
                || n.NotificationType == "STOCK_ADJUSTMENT" && (n.Title == "Kết quả kiểm tra lô của đơn" || n.Title == "Đơn đã được điều chỉnh"))
            && n.RelatedEntityType == "DISTRIBUTOR_ORDER" && db.PurchaseOrders.Any(o => o.PurchaseOrderId.ToString() == n.RelatedEntityId
                && o.Distributor.AccountId == actorId && o.OrderStatus == "CANCELLED")));
        return rows;
    }
    private static InboxItem Readable(InboxItem item, string? role)
    {
        if (item.EntityType is not ("STOCK_ADJUSTMENT" or "DISTRIBUTOR_ORDER")) return item;
        if (role == "OPERATION_STAFF" && item.Title == "Đã duyệt điều chỉnh tồn" && item.Message.Contains("giá"))
            return item with { Message = "Yêu cầu của bạn đã được duyệt và tồn kho đã được cập nhật. Kiểm tra trạng thái đơn trước khi tiếp tục công việc." };
        if (role == "OPERATION_STAFF" && item.Title == "Phiếu bị từ chối, lô vẫn tạm dừng")
            return item with { Message = "Yêu cầu của bạn bị từ chối. Vui lòng kiểm tra ghi chú và xác minh lại lô hàng." };
        if (role == "DISTRIBUTOR" && item.Title == "Đơn hàng đã hủy" && item.Message.StartsWith("Khách từ chối đề xuất:"))
            return item with { Message = "Bạn đã từ chối đề xuất thay đổi. Đơn hàng của bạn đã được hủy. Thanh toán/hoàn tiền được xử lý ngoài hệ thống." };
        var message = item.Message.Trim();
        var response = System.Text.RegularExpressions.Regex.Match(message, @"^(ACCEPT|DECLINE|WITHDRAW)\s*[·-]\s*\d+$");
        if (response.Success) return item with { Message = response.Groups[1].Value switch {
            "ACCEPT" => "Khách hàng đã đồng ý đề xuất. Yêu cầu đang chờ quản lý kho phê duyệt.",
            "DECLINE" => "Khách hàng không tiếp tục mua. Đơn đã hủy; yêu cầu kiểm đếm vẫn chờ kho xử lý.",
            _ => "Khách hàng đã rút đồng ý. Kho cần xem xét lại đề xuất."
        } };
        if (System.Text.RegularExpressions.Regex.IsMatch(message, @"^SA-[A-Fa-f0-9]{32}(?:\s*·\s*[^\r\n]+)?$"))
            return item with { Message = item.Title.Contains("Có phiếu") ? "Nhân viên kho vừa gửi yêu cầu điều chỉnh tồn. Vui lòng xem xét và xử lý."
                : item.Title.Contains("từ chối") ? "Yêu cầu chưa được chấp thuận. Lô vẫn tạm dừng để kiểm tra lại."
                : "Yêu cầu điều chỉnh tồn đã được xử lý. Mở chi tiết để xem kết quả." };
        return item;
    }
    public async Task ReadAsync(int actorId, long id, CancellationToken ct)
    {
        await using var db = new AppDbContext(options, accessor);
        if (!await db.Accounts.AnyAsync(a => a.AccountId == actorId && a.AccountStatus == "ACTIVE" && a.Role.IsActive, ct)) throw new ForbiddenException("Tài khoản không còn hoạt động.");
        var visible = await VisibleAsync(db, actorId, ct);
        if (!await visible.AnyAsync(n => n.NotificationId == id, ct)) throw new NotFoundException("Không tìm thấy thông báo của bạn.");
        var row = await db.Notifications.SingleOrDefaultAsync(n => n.NotificationId == id && n.AccountId == actorId, ct)
            ?? throw new NotFoundException("Không tìm thấy thông báo của bạn.");
        if (!row.IsRead) { row.IsRead = true; row.ReadAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
    }
}
