namespace SAW.Domain.Entities;

public class Distributor
{
    public int DistributorId { get; set; }
    public int AccountId { get; set; }

    public string DistributorCode { get; set; } = default!;
    public string DistributorName { get; set; } = default!;
    public string TaxCode { get; set; } = default!;

    public bool HasOverdueBalance { get; set; }
    public string? OverdueNote { get; set; }
    public DateTime? BalanceStatusUpdatedAt { get; set; }

    public string? ContactPerson { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    public string ProfileStatus { get; set; } = "ACTIVE";

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Account Account { get; set; } = default!;
    public ICollection<PurchaseOrder> PurchaseOrders { get; set; } = [];
}
