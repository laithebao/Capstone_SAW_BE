namespace SAW.Domain.Entities;

public class LabResult
{
    public long LabResultId { get; set; }
    public long QcInspectionId { get; set; }

    public string? ChemicalResidueStatus { get; set; }
    public decimal? ResidueValue { get; set; }
    public string? ResidueUnit { get; set; }

    public string? PathogenStatus { get; set; }
    public string? PathogenName { get; set; }

    public string? LabName { get; set; }
    public DateTime? TestedAt { get; set; }
    public string? Note { get; set; }

    // Navigation
    public QcInspection QcInspection { get; set; } = default!;
}
