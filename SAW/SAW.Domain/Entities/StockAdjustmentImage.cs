namespace SAW.Domain.Entities;

public sealed class StockAdjustmentImage
{
    public Guid StockAdjustmentImageId { get; set; }
    public long? StockAdjustmentId { get; set; }
    public int UploadedByAccountId { get; set; }
    public DateTime UploadedAt { get; set; }
    public string AssetId { get; set; } = default!;
    public string ImageUrl { get; set; } = default!;
    public StockAdjustment? StockAdjustment { get; set; }
    public Account UploadedByAccount { get; set; } = default!;
}
