namespace SAW.Domain.Entities;

// DRAFT is retained as evidence. This durable cancellation record prevents further edits/commit.
public sealed class GoodsIssueInvalidation
{
    public long GoodsIssueId { get; set; }
    public long StockAdjustmentId { get; set; }
    public int AccountId { get; set; }
    public DateTime InvalidatedAt { get; set; }
    public string Reason { get; set; } = default!;
    public GoodsIssue GoodsIssue { get; set; } = default!;
    public StockAdjustment StockAdjustment { get; set; } = default!;
    public Account Account { get; set; } = default!;
}
