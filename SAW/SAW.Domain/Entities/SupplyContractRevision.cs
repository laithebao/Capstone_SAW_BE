namespace SAW.Domain.Entities;
public sealed class SupplyContractRevision
{
    public long Id { get; set; }
    public long SupplyContractId { get; set; }
    public SupplyContract Contract { get; set; } = null!;
    public int Revision { get; set; }
    public string TemplateVersion { get; set; } = "SAW-1";
    public string SnapshotJson { get; set; } = "";
    public string Content { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public DateTime SentAt { get; set; }
}
