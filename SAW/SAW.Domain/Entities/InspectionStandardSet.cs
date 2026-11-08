namespace SAW.Domain.Entities;

public class InspectionStandardSet
{
    public int InspectionStandardSetId { get; set; }
    public int CropTypeId { get; set; }

    public string StandardCode { get; set; } = default!;
    public string StandardName { get; set; } = default!;
    public string? Description { get; set; }

    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public CropType CropType { get; set; } = default!;
    public ICollection<InspectionStandardVersion> Versions { get; set; } = [];
}
