using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Controllers;

[ApiController, Route("api/order-delivery-refusals")]
[Authorize(Roles = "DISTRIBUTOR,WAREHOUSE_MANAGER")]
public sealed class OrderDeliveryRefusalsController(AppDbContext db, IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    private int Actor => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
    private string StorageRoot => Path.GetFullPath(configuration["DeliveryRefusalFiles:StoragePath"]
        ?? Path.Combine(environment.ContentRootPath, "App_Data", "DeliveryRefusalFiles"));

    [HttpGet("{orderId:long}")]
    public async Task<IActionResult> Get(long orderId, CancellationToken ct)
    {
        await EnsureReady(ct);
        await EnsureVisible(orderId, ct);
        return Ok(ApiResponse<List<DeliveryRefusalView>>.Success(await Read(orderId, ct)));
    }

    [HttpGet("{orderId:long}/evidence/{imageId:guid}")]
    public async Task<IActionResult> Image(long orderId, Guid imageId, CancellationToken ct)
    {
        await EnsureReady(ct);
        await EnsureVisible(orderId, ct);
        var file = await db.OrderDeliveryRefusalImages.AsNoTracking().SingleOrDefaultAsync(i => i.OrderDeliveryRefusalImageId == imageId
            && i.OrderDeliveryRefusal.PurchaseOrderId == orderId, ct) ?? throw new NotFoundException("Không tìm thấy ảnh bằng chứng.");
        var path = Path.Combine(StorageRoot, file.StorageName);
        if (!System.IO.File.Exists(path)) throw new NotFoundException("Ảnh bằng chứng không còn trong nơi lưu trữ.");
        return PhysicalFile(path, file.ContentType);
    }

    [HttpPost("{orderId:long}"), Authorize(Roles = "DISTRIBUTOR")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(52 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 52 * 1024 * 1024)]
    public async Task<IActionResult> Refuse(long orderId, [FromForm] DeliveryRefusalForm form, CancellationToken ct)
    {
        if (orderId <= 0 || form.RequestId == Guid.Empty) throw new BadRequestException("Mã đơn hoặc mã yêu cầu không hợp lệ.");
        await EnsureReady(ct);
        await EnsureVisible(orderId, ct);
        var reason = DeliveryRefusalRules.ValidateReason(form.Reason);
        if (form.BatchIds is null || form.BatchIds.Count is < 1 or > 20
            || form.BatchIds.Any(id => id <= 0) || form.BatchIds.Distinct().Count() != form.BatchIds.Count)
            throw new BadRequestException("Vui lòng chọn từ 1 đến 20 lô khác nhau thuộc đơn hàng.");
        if (form.Images is null || form.Images.Count is < 1 or > DeliveryRefusalRules.MaxImages)
            throw new BadRequestException("Vui lòng đính kèm từ 1 đến 5 ảnh bằng chứng.");
        var images = new List<BufferedImage>();
        foreach (var file in form.Images)
        {
            if (file.Length is <= 0 or > DeliveryRefusalRules.MaxImageBytes)
                throw new BadRequestException("Mỗi ảnh phải có dữ liệu và không vượt quá 10 MB.");
            await using var input = file.OpenReadStream();
            await using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            if (bytes.Length != file.Length || bytes.Length > DeliveryRefusalRules.MaxImageBytes)
                throw new BadRequestException("Kích thước ảnh không hợp lệ.");
            var type = DeliveryRefusalRules.ImageType(bytes, Path.GetExtension(file.FileName));
            var name = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(name)) throw new BadRequestException("Tên ảnh không hợp lệ.");
            images.Add(new BufferedImage(Guid.NewGuid(), name[..Math.Min(name.Length, 255)], type.Extension, type.ContentType,
                Convert.ToHexString(SHA256.HashData(bytes)), bytes));
        }

        var written = new List<string>();
        try
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var order = await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM PURCHASE_ORDER WITH (UPDLOCK,HOLDLOCK) WHERE PurchaseOrderID={orderId}")
                    .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
                await db.Entry(order).Reference(o => o.Distributor).LoadAsync(ct);
                if (order.Distributor.AccountId != Actor) throw new NotFoundException("Không tìm thấy đơn hàng của bạn.");
                var existing = await db.OrderDeliveryRefusals.AsNoTracking().Include(r => r.Images).Include(r => r.Lots).ThenInclude(l => l.OrderDetail)
                    .SingleOrDefaultAsync(r => r.RequestId == form.RequestId, ct);
                if (existing is not null)
                {
                    if (existing.PurchaseOrderId != orderId || existing.Reason != reason
                        || !existing.Lots.Select(l => l.OrderDetail.RequestedProductBatchId ?? 0).Order().SequenceEqual(form.BatchIds.Order())
                        || !existing.Images.Select(i => i.Sha256).Order().SequenceEqual(images.Select(i => i.Hash).Order()))
                        throw new ConflictException("Mã yêu cầu đã được sử dụng với nội dung khác. Vui lòng tải lại đơn.");
                    await tx.CommitAsync(ct);
                    return;
                }
                DeliveryRefusalRules.EnsureEligible(order.OrderStatus);
                var lines = db.OrderDetails.Where(l => l.PurchaseOrderId == orderId && l.RequestedProductBatchId != null
                    && form.BatchIds.Contains(l.RequestedProductBatchId.Value));
                var selected = await lines.Include(l => l.RequestedProductBatch).ToListAsync(ct);
                if (selected.Count != form.BatchIds.Count) throw new BadRequestException("Có lô không thuộc đơn hàng này.");
                if (await db.OrderDeliveryRefusalLots.AnyAsync(r => r.OrderDetail.PurchaseOrderId == orderId
                    && form.BatchIds.Contains(r.OrderDetail.RequestedProductBatchId ?? 0), ct))
                    throw new ConflictException("Có lô đã được từ chối trước đó. Vui lòng chọn những lô còn lại.");
                if (await lines.AnyAsync(l => l.ApprovedWeightKg <= 0
                    || (l.GoodsIssueDetails.Where(i => i.GoodsIssue.IssueStatus == "COMMITTED").Sum(i => (decimal?)i.WeightInKg) ?? 0) != l.ApprovedWeightKg, ct))
                    throw new ConflictException("Có lô được chọn chưa xuất đủ bằng phiếu xuất hoàn tất.");
                var now = DateTime.UtcNow;
                var refusal = new OrderDeliveryRefusal { PurchaseOrderId = orderId, PurchaseOrder = order, RequestId = form.RequestId,
                    Reason = reason, CreatedByAccountId = Actor, CreatedAt = now };
                foreach (var line in selected) refusal.Lots.Add(new OrderDeliveryRefusalLot { OrderDetailId = line.OrderDetailId, OrderDetail = line });
                Directory.CreateDirectory(StorageRoot);
                foreach (var (image, index) in images.Select((image, index) => (image, index)))
                {
                    var storageName = image.Id.ToString("N") + image.Extension;
                    var path = Path.Combine(StorageRoot, storageName);
                    if (!written.Contains(storageName))
                    {
                        written.Add(storageName);
                        await System.IO.File.WriteAllBytesAsync(path, image.Bytes, ct);
                    }
                    refusal.Images.Add(new OrderDeliveryRefusalImage { OrderDeliveryRefusalImageId = image.Id, FileName = image.Name,
                        StorageName = storageName, ContentType = image.ContentType, Sha256 = image.Hash, ByteLength = image.Bytes.Length, Position = index });
                }
                var previous = order.OrderStatus;
                var hasRemaining = await db.OrderDetails.AnyAsync(l => l.PurchaseOrderId == orderId
                    && !form.BatchIds.Contains(l.RequestedProductBatchId ?? 0)
                    && !db.OrderDeliveryRefusalLots.Any(r => r.OrderDetailId == l.OrderDetailId), ct);
                order.OrderStatus = hasRemaining ? DeliveryRefusalRules.PartialStatus : DeliveryRefusalRules.Status;
                order.UpdatedAt = now;
                db.OrderDeliveryRefusals.Add(refusal);
                await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'AccountID', @value={Actor}", ct);
                try
                {
                    await db.SaveChangesAsync(ct);
                    if (previous != order.OrderStatus && !await db.OrderStatusHistories.AnyAsync(h => h.PurchaseOrderId == orderId && h.NewStatus == order.OrderStatus, ct))
                    {
                        db.OrderStatusHistories.Add(new OrderStatusHistory { PurchaseOrderId = orderId, OldStatus = previous,
                            NewStatus = order.OrderStatus, ChangedByAccountId = Actor, ChangedAt = now,
                            ChangeReason = "Khách từ chối những lô đã chọn; các lô còn lại giữ nguyên. Lý do và ảnh được lưu để xử lý bên ngoài." });
                        await db.SaveChangesAsync(ct);
                    }
                }
                finally { await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL", CancellationToken.None); }
                await tx.CommitAsync(ct);
            });
        }
        catch
        {
            // A commit acknowledgement can be lost. Preserve any files linked to a committed report.
            try
            {
                var linked = await db.OrderDeliveryRefusalImages.AsNoTracking().Where(i => written.Contains(i.StorageName)).Select(i => i.StorageName).ToListAsync(CancellationToken.None);
                foreach (var name in written.Except(linked)) System.IO.File.Delete(Path.Combine(StorageRoot, name));
            }
            catch { /* When commit outcome is unknown, retain evidence rather than risk deleting it. */ }
            throw;
        }
        var reports = await Read(orderId, ct);
        return Ok(ApiResponse<DeliveryRefusalView>.Success(reports.Single(r => r.RequestId == form.RequestId), "Đã ghi nhận từ chối những lô đã chọn. Các bên sẽ xử lý bên ngoài hệ thống."));
    }

    private async Task EnsureReady(CancellationToken ct)
    {
        var ready = await db.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.ORDER_DELIVERY_REFUSAL',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.ORDER_DELIVERY_REFUSAL_IMAGE',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.ORDER_DELIVERY_REFUSAL_LOT',N'U') IS NOT NULL THEN 1 ELSE 0 END AS Value").SingleAsync(ct);
        if (ready == 0) throw new ConflictException("Chức năng từ chối nhận hàng chưa sẵn sàng. Vui lòng cập nhật database và chạy lại API.");
    }

    private async Task EnsureVisible(long orderId, CancellationToken ct)
    {
        var manager = User.IsInRole("WAREHOUSE_MANAGER");
        if (!await db.PurchaseOrders.AnyAsync(o => o.PurchaseOrderId == orderId && (manager || o.Distributor.AccountId == Actor
                && o.Distributor.ProfileStatus == "ACTIVE" && o.Distributor.Account.AccountStatus == "ACTIVE"), ct))
            throw new NotFoundException("Không tìm thấy đơn hàng.");
    }

    private Task<List<DeliveryRefusalView>> Read(long orderId, CancellationToken ct) => db.OrderDeliveryRefusals.AsNoTracking()
        .Where(r => r.PurchaseOrderId == orderId).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.OrderDeliveryRefusalId)
        .Select(r => new DeliveryRefusalView(r.PurchaseOrderId, r.RequestId, r.PurchaseOrder.OrderCode,
            r.Reason, r.CreatedAt, r.Images.OrderBy(i => i.Position).Select(i => new DeliveryRefusalEvidence(i.OrderDeliveryRefusalImageId,
                i.FileName, $"/api/order-delivery-refusals/{orderId}/evidence/{i.OrderDeliveryRefusalImageId}")).ToList(),
            r.Lots.OrderBy(l => l.OrderDetailId).Select(l => new DeliveryRefusalLotView(l.OrderDetail.RequestedProductBatchId ?? 0,
                l.OrderDetail.RequestedProductBatch!.BatchCode, l.OrderDetail.RequestedProductBatch.ProductName, l.OrderDetail.ApprovedWeightKg)).ToList())).ToListAsync(ct);

    private sealed record BufferedImage(Guid Id, string Name, string Extension, string ContentType, string Hash, byte[] Bytes);
}

public sealed class DeliveryRefusalForm
{
    public Guid RequestId { get; set; }
    public string? Reason { get; set; }
    public List<long> BatchIds { get; set; } = [];
    public List<IFormFile> Images { get; set; } = [];
}
public sealed record DeliveryRefusalEvidence(Guid Id, string FileName, string Url);
public sealed record DeliveryRefusalLotView(long BatchId, string BatchCode, string ProductName, decimal WeightKg);
public sealed record DeliveryRefusalView(long PurchaseOrderId, Guid RequestId, string OrderCode, string Reason, DateTime CreatedAt,
    List<DeliveryRefusalEvidence> Images, List<DeliveryRefusalLotView> Lots);
