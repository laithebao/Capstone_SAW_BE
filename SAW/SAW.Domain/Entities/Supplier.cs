namespace SAW.Domain.Entities;

public class Supplier
{
    public int SupplierId { get; set; }
    public int AccountId { get; set; }

    public string SupplierCode { get; set; } = default!;
    public string SupplierName { get; set; } = default!;
    public string TaxCode { get; set; } = default!;

    public string ContactPerson { get; set; } = default!;
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string Address { get; set; } = default!;
    public string? GrowingArea { get; set; }
    public string? Note { get; set; }

    public string ProfileStatus { get; set; } = "ACTIVE";

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Account Account { get; set; } = default!;
    public ICollection<SupplierCropType> SupplierCropTypes { get; set; } = [];
    public ICollection<SupplierCertification> SupplierCertifications { get; set; } = [];
    public ICollection<ProductBatch> ProductBatches { get; set; } = [];
}
