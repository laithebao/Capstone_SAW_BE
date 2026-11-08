namespace SAW.Domain.Entities;

public class QualityImage
{
    public long QualityImageId { get; set; }
    public long QcInspectionId { get; set; }

    public string FileName { get; set; } = default!;
    public string FileUrl { get; set; } = default!;
    public string? MimeType { get; set; }

    public int UploadedByAccountId { get; set; }
    public DateTime UploadedAt { get; set; }

    // Navigation
    public QcInspection QcInspection { get; set; } = default!;
    public Account UploadedByAccount { get; set; } = default!;
}
