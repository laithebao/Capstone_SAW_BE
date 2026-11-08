using SAW.Application.Features.Suppliers.DTOs;

namespace SAW.Application.Features.Suppliers.Commands;

public static class SupplierBatchValidation
{
    public static decimal ValidateAndCalculateWeight(DeclareProductBatchRequest r, DateOnly today)
    {
        if (r.CropTypeId <= 0 || r.GrowingAreaId <= 0)
            throw new ArgumentException("Vui lòng chọn nông sản và vùng trồng hợp lệ.");
        if (string.IsNullOrWhiteSpace(r.ProductName) || r.ProductName.Trim().Length > 200)
            throw new ArgumentException("Tên sản phẩm phải có từ 1 đến 200 ký tự.");
        if (r.Note?.Length > 1000 || r.PackagingType?.Length > 100)
            throw new ArgumentException("Ghi chú tối đa 1000 ký tự; quy cách đóng gói tối đa 100 ký tự.");
        if (r.HarvestDate == default || r.HarvestDate > today)
            throw new ArgumentException("Ngày thu hoạch phải hợp lệ và không ở tương lai.");
        if (r.ExpectedDeliveryDate < r.HarvestDate)
            throw new ArgumentException("Ngày giao dự kiến không được trước ngày thu hoạch.");
        if (r.ExpiryDate < r.HarvestDate)
            throw new ArgumentException("Ngày hết hạn không được trước ngày thu hoạch.");
        ValidateDecimal(r.DeclaredQuantity, 18, 3, "Số lượng khai báo", true);
        if (r.PackageCount is <= 0)
            throw new ArgumentException("Số kiện phải là số nguyên dương.");
        ValidateDecimal(r.PackageUnitWeightKg, 18, 3, "Khối lượng mỗi kiện", true);
        ValidateDecimal(r.ExpectedMinTempC, 6, 2, "Nhiệt độ tối thiểu");
        ValidateDecimal(r.ExpectedMaxTempC, 6, 2, "Nhiệt độ tối đa");
        ValidateDecimal(r.ExpectedMinHumidityPct, 6, 2, "Độ ẩm tối thiểu");
        ValidateDecimal(r.ExpectedMaxHumidityPct, 6, 2, "Độ ẩm tối đa");
        if (r.ExpectedMinTempC > r.ExpectedMaxTempC || r.ExpectedMinHumidityPct > r.ExpectedMaxHumidityPct)
            throw new ArgumentException("Giá trị tối thiểu không được lớn hơn giá trị tối đa.");
        if (r.ExpectedMinHumidityPct is < 0 or > 100 || r.ExpectedMaxHumidityPct is < 0 or > 100)
            throw new ArgumentException("Độ ẩm phải từ 0% đến 100%.");
        decimal weight;
        switch (r.Unit?.Trim().ToLowerInvariant())
        {
            case "kg": case "kilogram": weight = r.DeclaredQuantity; break;
            case "tấn": case "tan": case "ton": weight = r.DeclaredQuantity * 1000m; break;
            case "bao": case "thùng":
                if (r.PackageCount is null || r.PackageUnitWeightKg is null)
                    throw new ArgumentException("Bao/Thùng cần số kiện và khối lượng kg mỗi kiện.");
                if (r.DeclaredQuantity != r.PackageCount)
                    throw new ArgumentException("Số lượng khai báo Bao/Thùng phải bằng số kiện.");
                weight = r.PackageCount.Value * r.PackageUnitWeightKg.Value;
                break;
            default: throw new ArgumentException("Đơn vị phải là Kg, Tấn, Bao hoặc Thùng.");
        }
        ValidateDecimal(weight, 18, 3, "Tổng khối lượng kg", true);
        return weight;
    }

    private static void ValidateDecimal(decimal? value, int precision, int scale, string label, bool positive = false)
    {
        if (!value.HasValue) return;
        var limit = precision == 18 ? 1000000000000000m : 10000m;
        if (value <= -limit || value >= limit || decimal.Round(value.Value, scale) != value || (positive && value <= 0))
            throw new ArgumentException($"{label} phải {(positive ? "lớn hơn 0, " : "")}nằm trong decimal({precision},{scale}) và có tối đa {scale} chữ số thập phân.");
    }
}
