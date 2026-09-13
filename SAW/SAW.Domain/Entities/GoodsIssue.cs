namespace SAW.Domain.Entities;

public class GoodsIssue
{
    public long GoodsIssueId { get; set; }
    public string IssueCode { get; set; } = default!;

    public long PurchaseOrderId { get; set; }
    public int OperationAccountId { get; set; }

    public string? ReceiverName { get; set; }

    public string IssueStatus { get; set; } = "DRAFT";

    public DateTime IssuedAt { get; set; }
    public DateTime? CommittedAt { get; set; }

    public string? Note { get; set; }

    // Navigation
    public PurchaseOrder PurchaseOrder { get; set; } = default!;
    public Account OperationAccount { get; set; } = default!;
    public ICollection<GoodsIssueDetail> GoodsIssueDetails { get; set; } = [];
}
