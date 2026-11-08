namespace SAW.Domain.Entities;

public class SupplierFile
{
    public Guid SupplierFileId { get; set; }
    public int AccountId { get; set; }
    public int? SupplierId { get; set; }
    public long? ProductBatchId { get; set; }
    public string Purpose { get; set; } = "DOCUMENT";
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string StorageName { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public DateTime CreatedAt { get; set; }
}
