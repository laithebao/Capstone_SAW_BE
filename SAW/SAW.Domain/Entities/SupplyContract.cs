namespace SAW.Domain.Entities;

public class SupplyContract
{
    public long SupplyContractId { get; set; }
    public string ContractCode { get; set; } = default!;
    public int WarehouseManagerAccountId { get; set; }
    public int SupplierId { get; set; }
    public int CropTypeId { get; set; }
    public string? QualityGrade { get; set; }
    public decimal DeliveryQuantityKg { get; set; }
    public string DeliveryPeriod { get; set; } = "WEEK";
    public decimal UnitPriceVndPerKg { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Terms { get; set; }
    public string? AttachmentFileName { get; set; }
    public string? AttachmentStorageName { get; set; }
    public string? AttachmentContentType { get; set; }
    public DateTime? AttachmentUploadedAt { get; set; }
    public string ContractStatus { get; set; } = "PENDING_SUPPLIER";
    public int Revision { get; set; } = 1;
    public DateTime? SupplierRespondedAt { get; set; }
    public string? SupplierRejectionReason { get; set; }
    public int? AdminAccountId { get; set; }
    public DateTime? AdminReviewedAt { get; set; }
    public string? AdminRejectionReason { get; set; }
    public DateTime? CancellationRequestedAt { get; set; }
    public string? CancellationRequestReason { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Account WarehouseManagerAccount { get; set; } = default!;
    public Supplier Supplier { get; set; } = default!;
    public CropType CropType { get; set; } = default!;
    public Account? AdminAccount { get; set; }
    public ICollection<ProductBatch> ProductBatches { get; set; } = [];
}
