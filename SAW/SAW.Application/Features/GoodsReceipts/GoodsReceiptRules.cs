using SAW.Application.Exceptions;
using SAW.Domain.Entities;

namespace SAW.Application.Features.GoodsReceipts;

public static class GoodsReceiptRules
{
    public static bool ValidAmount(decimal? value) => value is > 0 and <= 999999999999999.999m
        && decimal.Round(value.Value, 3) == value.Value;

    public static void EnsureEligible(ProductBatch batch, QcInspection? latest)
    {
        if (batch.BatchStatus != "APPROVED_FOR_STORAGE")
            throw new ConflictException("Lô chưa được duyệt nhập kho hoặc đã được nhập kho.");
        if (latest is null || latest.InspectionStatus != "COMPLETED" || latest.CompletedAt is null
            || latest.QcResult != "PASS" || latest.QualityGrade is not ("A" or "B" or "C" or "D")
            || latest.QualityGrade != batch.QualityGrade)
            throw new ConflictException("Phiếu QC mới nhất chưa hoàn tất đạt yêu cầu. Vui lòng kiểm tra lại lô.");
        if (!ValidAmount(batch.VerifiedQuantity) || !ValidAmount(batch.VerifiedWeightInKg))
            throw new ConflictException("Lô thiếu số lượng hoặc khối lượng kiểm nhận hợp lệ.");
        if (string.IsNullOrWhiteSpace(batch.Unit) || batch.Unit.Length > 20 || batch.Unit.Any(char.IsControl))
            throw new ConflictException("Đơn vị của lô không hợp lệ.");
    }

    public static void EnsureCapacity(decimal current, decimal incoming, decimal? maximum, string name)
    {
        if (maximum.HasValue && current + incoming > maximum.Value)
            throw new ConflictException($"{name} không đủ sức chứa cho khối lượng nhập. Vui lòng chọn vị trí hoặc kiểm tra sức chứa.");
    }
}
