namespace SAW.Domain.Entities;

// Distributor catalog pricing is separate from supplier declarations and QC data.
public sealed class BatchSaleOffer
{
    public long ProductBatchId { get; set; }
    public decimal WholeLotPrice { get; set; }
    public bool IsPublished { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ProductBatch ProductBatch { get; set; } = default!;
}
