using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Entities;
using SkiaSharp;

namespace SAW.Infrastructure.Repositories;

public sealed partial class StockAdjustmentRepository
{
    public static byte[] ValidateImage(byte[] content)
    {
        if (content.Length is 0 or > 10485760) throw new BadRequestException("Mỗi ảnh tối đa 10 MB.");
        try
        {
            using var data = SKData.CreateCopy(content);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp)
                || (long)codec.Info.Width * codec.Info.Height > 20000000 || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                throw new BadRequestException("Chỉ nhận ảnh PNG, JPEG, WEBP tối đa 20 triệu điểm ảnh.");
            // Fully decode and re-encode: a forged extension/MIME/header is insufficient.
            using var bitmap = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new BadRequestException("Nội dung ảnh không đầy đủ hoặc không thể giải mã.");
            using var image = SKImage.FromBitmap(bitmap);
            using var output = image.Encode(SKEncodedImageFormat.Png, 100);
            if (output.Size > 10485760) throw new BadRequestException("Ảnh sau xử lý vượt 10 MB. Vui lòng dùng ảnh nhỏ hơn.");
            return output.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        { throw new BadRequestException("Nội dung ảnh không hợp lệ hoặc không thể giải mã."); }
    }
    public async Task<AdjustmentImage> UploadAsync(byte[] content, int actorId, CancellationToken ct)
    {
        var png = ValidateImage(content);
        await using var db = Context();
        await EnsureAuthorityAsync(db, actorId, "OPERATION_STAFF", ct);
        // Recover abandoned uploads (closed browser/lost upload response). Recheck each ticket
        // under its SQL lock; never delete an asset that has since been linked to a request.
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var abandoned = await db.StockAdjustmentImages.AsNoTracking()
            .Where(i => i.UploadedByAccountId == actorId && i.StockAdjustmentId == null && (i.DeletePending || i.UploadedAt < cutoff))
            .OrderBy(i => i.UploadedAt).Take(20).Select(i => i.StockAdjustmentImageId).ToListAsync(ct);
        foreach (var abandonedId in abandoned)
        {
            try { await RemoveUploadAsync(abandonedId, actorId, ct); }
            catch (Exception e) when (e is ConflictException or NotFoundException or HttpRequestException) { /* Retain failed cleanup for a later upload attempt. */ }
        }
        if (await db.StockAdjustmentImages.CountAsync(i => i.UploadedByAccountId == actorId && i.StockAdjustmentId == null, ct) >= 20)
            throw new ConflictException("Bạn có quá nhiều ảnh chưa gắn hồ sơ. Hãy bỏ ảnh không dùng trước khi tải thêm.");
        var id = Guid.NewGuid(); var asset = "saw-stock-adjustments/" + id.ToString("N");
        string url;
        try { url = await storage.UploadAsync(asset, png, ct); }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        { throw new ConflictException("Tải ảnh bằng chứng thất bại. Giữ form và thử lại hoặc bỏ ảnh."); }
        try
        {
            db.StockAdjustmentImages.Add(new StockAdjustmentImage { StockAdjustmentImageId = id, UploadedByAccountId = actorId,
                UploadedAt = DateTime.UtcNow, AssetId = asset, ImageUrl = url });
            await db.SaveChangesAsync(ct); return new(id, url);
        }
        catch { await storage.DeleteAsync(asset, CancellationToken.None); throw; }
    }
    public async Task RemoveUploadAsync(Guid imageId, int actorId, CancellationToken ct)
    {
        var asset = await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db => {
            await EnsureAuthorityAsync(db, actorId, "OPERATION_STAFF", ct);
            var image = await db.StockAdjustmentImages.FromSqlInterpolated($"SELECT * FROM STOCK_ADJUSTMENT_IMAGE WITH (UPDLOCK,HOLDLOCK) WHERE StockAdjustmentImageId={imageId}").SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy ảnh.");
            if (image.UploadedByAccountId != actorId) throw new ForbiddenException("Ảnh không thuộc tài khoản hiện tại.");
            if (image.StockAdjustmentId != null) throw new ConflictException("Không được xóa ảnh đang được hồ sơ tham chiếu.");
            image.DeletePending = true; await db.SaveChangesAsync(ct); return image.AssetId;
        }, ct);
        // Durable deletion ticket prevents linking while HTTP storage cleanup runs outside SQL.
        // Failed HTTP cleanup leaves the ticket available for retry / abandoned-upload cleanup.
        await storage.DeleteAsync(asset, ct);
        await WarehouseOperationTransaction.ExecuteAsync(options, accessor, actorId, async db => {
            var image = await db.StockAdjustmentImages.SingleOrDefaultAsync(i => i.StockAdjustmentImageId == imageId, ct);
            if (image != null && image.DeletePending && image.StockAdjustmentId == null) { db.StockAdjustmentImages.Remove(image); await db.SaveChangesAsync(ct); }
            return true;
        }, ct);
    }
}
