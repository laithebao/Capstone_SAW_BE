namespace SAW.Domain.Entities;

public class ProductBatchDailyCounter
{
    public DateOnly CodeDate { get; set; }
    public int LastNumber { get; set; }
}
