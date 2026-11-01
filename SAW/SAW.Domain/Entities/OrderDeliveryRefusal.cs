namespace SAW.Domain.Entities;

// Customer evidence for external settlement; no physical return or stock receipt.
public sealed class OrderDeliveryRefusal
{
    public long OrderDeliveryRefusalId { get; set; }
    public long PurchaseOrderId { get; set; }
    public Guid RequestId { get; set; }
    public int CreatedByAccountId { get; set; }
    public string Reason { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = default!;
    public ICollection<OrderDeliveryRefusalImage> Images { get; set; } = [];
    public ICollection<OrderDeliveryRefusalLot> Lots { get; set; } = [];
}

public sealed class OrderDeliveryRefusalLot
{
    public long OrderDetailId { get; set; }
    public long OrderDeliveryRefusalId { get; set; }
    public OrderDetail OrderDetail { get; set; } = default!;
    public OrderDeliveryRefusal OrderDeliveryRefusal { get; set; } = default!;
}

public sealed class OrderDeliveryRefusalImage
{
    public Guid OrderDeliveryRefusalImageId { get; set; }
    public long OrderDeliveryRefusalId { get; set; }
    public string FileName { get; set; } = default!;
    public string StorageName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public string Sha256 { get; set; } = default!;
    public long ByteLength { get; set; }
    public int Position { get; set; }
    public OrderDeliveryRefusal OrderDeliveryRefusal { get; set; } = default!;
}
