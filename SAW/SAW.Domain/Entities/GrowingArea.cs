namespace SAW.Domain.Entities;

public class GrowingArea
{
    public int GrowingAreaId { get; set; }
    public int? OwnerSupplierId { get; set; }
    public Supplier? OwnerSupplier { get; set; }

    public string AreaName { get; set; } = default!; // Tên vùng trồng
    
    public string? Region { get; set; }   // Miền (Bắc, Trung, Nam)
    public string? Province { get; set; } // Cấp 1: Tỉnh / Thành phố trực thuộc TW
    public string? District { get; set; } // Cấp 2: Quận / Huyện / TP trực thuộc tỉnh
    public string? Ward { get; set; }     // Cấp 3: Xã / Phường / Thị trấn
    
    public string? Description { get; set; } 

    // Navigation
    public ICollection<SupplierGrowingArea> SupplierGrowingAreas { get; set; } = [];
    public ICollection<ProductBatch> ProductBatches { get; set; } = [];
}