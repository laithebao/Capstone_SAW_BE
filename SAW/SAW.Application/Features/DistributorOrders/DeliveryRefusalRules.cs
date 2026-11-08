using SAW.Application.Exceptions;

namespace SAW.Application.Features.DistributorOrders;

public static class DeliveryRefusalRules
{
    public const string Status = "DELIVERY_REFUSED";
    public const string PartialStatus = "PARTIALLY_REFUSED";
    public const int MaxImages = 5;
    public const int MaxImageBytes = 10 * 1024 * 1024;

    public static void EnsureEligible(string status)
    {
        if (status is not ("ISSUED" or "DISPATCHED" or "DELIVERED" or PartialStatus))
            throw new ConflictException("Chỉ từ chối lô đã xuất đủ, đang giao hoặc đã xác nhận nhận hàng.");
    }

    public static string ValidateReason(string? value)
    {
        var reason = value?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000
            || reason.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new BadRequestException("Vui lòng nhập lý do từ chối từ 1 đến 1000 ký tự.");
        return reason;
    }

    public static (string Extension, string ContentType) ImageType(ReadOnlySpan<byte> bytes, string extension)
    {
        extension = extension.ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" && bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
            return (".jpg", "image/jpeg");
        if (extension == ".png" && bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (".png", "image/png");
        if (extension == ".webp" && bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return (".webp", "image/webp");
        throw new BadRequestException("Bằng chứng phải là ảnh JPG, PNG hoặc WEBP có nội dung hợp lệ.");
    }
}
