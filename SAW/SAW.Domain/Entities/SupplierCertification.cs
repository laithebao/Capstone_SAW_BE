namespace SAW.Domain.Entities;

public class SupplierCertification
{
    public long SupplierCertificationId { get; set; }
    public int SupplierId { get; set; }

    public string CertificationName { get; set; } = default!;
    public string? CertificateNumber { get; set; }
    public string? IssuingOrganization { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? EvidenceFileUrl { get; set; }
    public bool IsActive { get; set; }

    // Navigation
    public Supplier Supplier { get; set; } = default!;
}
