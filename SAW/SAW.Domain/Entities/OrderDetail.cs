namespace SAW.Domain.Entities;

public class OrderDetail
{
    public long OrderDetailId { get; set; }
    public long PurchaseOrderId { get; set; }

    public int CropTypeId { get; set; }
    public long? RequestedProductBatchId { get; set; }

    public decimal RequestedQuantity { get; set; }
    public decimal ApprovedQuantity { get; set; }
    public string Unit { get; set; } = default!;

    public decimal RequestedWeightKg { get; set; }
    public decimal ApprovedWeightKg { get; set; }
    public decimal ReservedWeightKg { get; set; }
    public decimal PickedWeightKg { get; set; }

    public decimal UnitPrice { get; set; }
    public decimal TaxAmount { get; set; }

    // Computed columns — read-only
    public decimal LineSubtotal { get; set; }
    public decimal LineTotal { get; set; }

    // Navigation
    public PurchaseOrder PurchaseOrder { get; set; } = default!;
    public CropType CropType { get; set; } = default!;
    public ProductBatch? RequestedProductBatch { get; set; }
    public ICollection<InventoryReservation> InventoryReservations { get; set; } = [];
    public ICollection<GoodsIssueDetail> GoodsIssueDetails { get; set; } = [];
}
