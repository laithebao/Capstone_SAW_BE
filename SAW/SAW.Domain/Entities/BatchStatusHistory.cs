namespace SAW.Domain.Entities;

public class BatchStatusHistory
{
    public long BatchStatusHistoryId { get; set; }
    public long ProductBatchId { get; set; }

    public string? OldStatus { get; set; }
    public string NewStatus { get; set; } = default!;
    public int? ChangedByAccountId { get; set; }
    public string? ChangeReason { get; set; }
    public DateTime ChangedAt { get; set; }

    // Navigation
    public ProductBatch ProductBatch { get; set; } = default!;
    public Account? ChangedByAccount { get; set; }
}
