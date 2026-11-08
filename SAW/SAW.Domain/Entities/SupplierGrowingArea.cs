namespace SAW.Domain.Entities;

public class SupplierGrowingArea
{
    public int SupplierId { get; set; }
    public int GrowingAreaId { get; set; }

    // Thông tin bổ sung cho Supplier tại vùng này (ví dụ: diện tích Supplier khai thác tại đây)
    public double? AreaInHectares { get; set; } 
    public string? SupplierSpecificNote { get; set; }

    public DateTime JoinedAt { get; set; }

    // Navigation
    public Supplier Supplier { get; set; } = default!;
    public GrowingArea GrowingArea { get; set; } = default!;
}