namespace SAW.Domain.Entities;

public class QrCode
{
    public long QrCodeId { get; set; }

    public long ProductBatchId { get; set; }
    public string? PackageCode { get; set; }

    public string PublicToken { get; set; } = default!;
    public string TraceabilityUrl { get; set; } = default!;
    public string? QrImageUrl { get; set; }

    public bool IsActive { get; set; }
    public DateTime GeneratedAt { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
}
