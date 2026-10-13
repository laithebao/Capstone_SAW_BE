namespace SAW.Domain.Entities;

public sealed class PurchaseOrderDailyCounter
{
    public DateOnly CodeDate { get; set; }
    public int LastNumber { get; set; }
}
