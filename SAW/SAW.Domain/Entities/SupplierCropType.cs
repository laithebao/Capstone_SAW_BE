namespace SAW.Domain.Entities;

public class SupplierCropType
{
    public int SupplierId { get; set; }
    public int CropTypeId { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Supplier Supplier { get; set; } = default!;
    public CropType CropType { get; set; } = default!;
}
